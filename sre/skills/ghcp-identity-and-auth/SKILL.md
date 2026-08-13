# GHCP identity & auth troubleshooting

Sign-in and identity failures in this app cluster into three causes. The first is the one that will
actually page you.

## 1. Entra app client-secret expiry (tenant-wide sign-in outage)

The app authenticates users via Easy Auth against an Entra app registration whose **client secret has
an expiry** (`azuread_application_password`). Nothing in the stack monitors it. When it lapses, EVERY
user's sign-in breaks at once, with no warning.

```
az ad app credential list --id <entra-app-client-id> \
  --query "[].{displayName:displayName, endDateTime:endDateTime}" -o table
```

If `endDateTime` is past (or within ~30 days), that's the cause / imminent cause. Symptom: users get
redirected to login and back in a loop, or see an AADSTS7000222 (expired secret) error. Fix is a
rotated secret + updated `MICROSOFT_PROVIDER_AUTHENTICATION_SECRET` app setting — flag for the
operator; this is a change action, not a read.

**Proactive:** the daily scheduled sweep should alert when `endDateTime` is < 30 days out.

## 2. Easy Auth / EasyAuthEnabled consistency

The container derives user identity — including the `Admin` role — from the `X-MS-CLIENT-PRINCIPAL`
header. That header is only trustworthy because the Easy Auth platform module strips inbound copies.
There is a matching app setting, `Auth__EasyAuthEnabled`, and a security fix (`bdceb2a`) that makes
the app **refuse to serve** if the platform module is absent while the setting claims it's on.

Check they agree:

```
az webapp auth show -g <app-rg> -n <app-name> --query "platform.enabled"
az webapp config appsettings list -g <app-rg> -n <app-name> \
  --query "[?name=='Auth__EasyAuthEnabled'].value"
```

Platform auth `enabled=true` and `Auth__EasyAuthEnabled=true` must match. If platform auth is off but
the setting says true (or vice versa), that's the misconfiguration the security fix guards — the app
will 5xx by design rather than trust an unauthenticated header. **Never "fix" this by setting
`enable_easy_auth = false` on an internet-facing deployment** — that's the exact vuln the fix closed.

## 3. Managed-identity RBAC grant propagation

On a fresh deploy the app identity needs: **Key Vault Secrets User** (read the PAT), **AcrPull**
(pull the image), and the SQL grant (db_datareader/writer/ddladmin). RBAC can take a few minutes to
propagate; a just-deployed app may 401/403 transiently.

```
az role assignment list --assignee <app-principal-id> -o table
```

For SQL specifically, the grant is T-SQL not RBAC — if the app logs `Login failed for user`, run
`./deploy.ps1 -Task grant-sql` (system_assigned mode). See `ghcp-snapshot-pipeline` step 3.

## 4. "I can sign in but the dashboard is empty"

**Since 2026-08-13, administering the console and seeing data are SEPARATE grants.** This is the most
likely cause of an empty dashboard from an otherwise healthy deployment, and it is not a fault.

| Role | Table | Grants |
|---|---|---|
| Administrator | `AdminPrincipals` | manage the console. **No data access.** |
| Enterprise Reader | `PrincipalEnterpriseGrants` | all cost centers, org breakdown, enterprise budgets — for the enterprises granted |
| Cost center manager | `PrincipalCostCenterMappings` | only the mapped (enterprise, cost center) pairs |

The Entra `Admin` **app role** still grants see-all, as a bootstrap so a fresh deployment is never
locked out. A DB-designated admin principal does not.

`EnterpriseId IS NULL` on a grant means ALL enterprises, including any registered later — that is why
the resolver collapses it to see-all rather than expanding it into today's enterprise ids.

```sql
-- Who can see what? (admins with no reader grant are the ones who will complain)
SELECT a.PrincipalObjectId, a.PrincipalDisplayName, 'admin, NO read access' AS problem
FROM AdminPrincipals a
WHERE NOT EXISTS (SELECT 1 FROM PrincipalEnterpriseGrants g
                  WHERE g.PrincipalType = a.PrincipalType AND g.PrincipalObjectId = a.PrincipalObjectId);

SELECT g.PrincipalDisplayName, g.PrincipalType, g.PrincipalObjectId,
       COALESCE(e.Slug, '** ALL ENTERPRISES **') AS grant_covers
FROM PrincipalEnterpriseGrants g
LEFT JOIN Enterprises e ON e.Id = g.EnterpriseId
ORDER BY g.PrincipalDisplayName;
```

The app states this case explicitly on the dashboard ("you administer this deployment but hold no
Enterprise Reader grant"), so a user reporting a *blank* screen with no such message has a different
problem — check their group claims reached the token (section 2) before looking at grants.

**Fix by granting Enterprise Reader on the admin console, never by adding an administrator.** Making
someone an admin to fix a reporting complaint rebuilds exactly the conflation this split removed.
