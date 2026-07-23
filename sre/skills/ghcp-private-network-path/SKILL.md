# GHCP private-network path troubleshooting

## Step 0 — FIRST confirm the deployment is actually private (do NOT skip this)

This skill only applies when the deployment uses **private networking**. Many deployments are
**public** (`use_private_networking = false`), and then there are no private endpoints, no private
DNS zones, and no VNet-integration egress — the whole diagnostic below is irrelevant. **Ground your
answer in the real config before reciting any of it.** Confirm the mode:

```bash
# Public if this returns Enabled; also, a public deployment has NO pe-* private endpoints.
az sql server show -g <app-rg> -n <sql-server> --query "publicNetworkAccess"
az network private-endpoint list -g <app-rg> --query "[].name" -o tsv    # empty ⇒ public
```

**If the deployment is PUBLIC:** say so plainly — "this deployment is public networking, so there's
no private endpoint/DNS path to diagnose; the app reaches SQL/Key Vault/GitHub over public endpoints"
— and stop. Don't walk the operator through private-endpoint checks that don't exist. (For a public
app the equivalent failure is a Key Vault firewall/RBAC or a plain outbound issue — a much shorter path.)

## When it IS private

In private mode there are **no public endpoints** — the app reaches SQL and Key Vault over private
endpoints, resolves them via private DNS, and egresses to `api.github.com` through VNet integration.
Failures present three layers away from their cause (a Key Vault DNS failure looks like a GitHub
401), so triage in this fixed order.

## Symptom -> most likely layer

| Symptom | Start at |
|---|---|
| `ghcp.github.token_resolved` = 0 | Key Vault reference / private DNS (step 2) |
| Snapshot `Cannot open server` / SQL timeouts | SQL private endpoint / DNS (step 1) |
| GitHub 401 but token_resolved = 1 | Outbound egress to GitHub (step 3) |

## Step 1 — SQL private endpoint + DNS

```
az network private-endpoint show -g <app-rg> -n pe-sql-<base> --query "customDnsConfigs"
az network private-dns record-set a list -g <app-rg> -z privatelink.database.windows.net -o table
```

The A record for the SQL server must resolve to the private-endpoint NIC IP. If the zone is empty or
the record is missing, the DNS zone link is the problem — common with **bring-your-own-VNet**
(`custom_network_mode = true`, `create_private_dns_zones = false`) pointing at a centralized hub zone
that wasn't wired.

## Step 2 — Key Vault reference resolution

```
az webapp config appsettings list -g <app-rg> -n <app-name> \
  --query "[?name=='GitHub__Token'].value"
```

If the value still shows the literal `@Microsoft.KeyVault(...)` string, App Service failed to resolve
it — the app's managed identity can't reach the vault, or the vault's private DNS
(`privatelink.vaultcore.azure.net`) isn't resolving. Verify:

```
az keyvault show -n <kv-name> --query "properties.privateEndpointConnections"
az role assignment list --scope <kv-id> --query "[?roleDefinitionName=='Key Vault Secrets User']"
```

The app identity needs **Key Vault Secrets User**. Note the timing trap: if the app started *before*
the `github-pat` secret existed, App Service cached the failed resolution — a restart fixes it once
the secret is present.

## Step 3 — Outbound egress to GitHub

`vnet_route_all_enabled = true` forces ALL outbound through the VNet. **This stack provisions no NAT
gateway.** In a landing-zone VNet with a forced-tunnel UDR to a firewall, outbound `api.github.com`
dies while SQL/Key Vault (private endpoints) still work — so the app looks healthy but every GitHub
call fails.

```
az network vnet subnet show -g <net-rg> --vnet-name <vnet> -n snet-app --query "routeTable"
```

If a route table is attached, confirm `api.github.com` (or its IP ranges) is allowed through the
firewall. No NAT gateway + forced tunnel + blocked GitHub = this exact failure.

## Rule of thumb
Resolution order is DNS -> identity/RBAC -> egress. A Key Vault or SQL DNS failure masquerades as a
downstream GitHub/auth error every time — always confirm `token_resolved` and DNS before touching the
GitHub or SQL config itself.
