# Infrastructure — GHCP AI Credit Visibility (Azure)

Terraform to deploy the dashboard **privately** with **Entra ID authentication**, **Key Vault** for the GitHub PAT, **Azure SQL** for ≥3-month persistence, and full **private networking**.

> Validated with `terraform validate` on Terraform 1.11 / azurerm ~> 4.20, and apply-tested end-to-end against a live Azure subscription.

## Preflight: capacity + region check (run this first)

Constrained subscriptions (personal/MSDN, and internal/hybrid subs) frequently lack App Service quota by default or restrict SQL by region. **Run the precheck before deploying** — for testing *and* before any production rollout. It's built into the one script:

```powershell
./deploy.ps1 -Task preflight -Location eastus2                          # gate one region
./deploy.ps1 -Task preflight -Location eastus2,eastus,westus3,uksouth   # scan to pick a viable region
./deploy.ps1 -Task preflight -Location eastus2 -Register                # also register any missing resource providers
```

It checks, per region: **resource-provider registration**, **App Service compute quota**, and **Azure SQL availability**, then prints a deployable/not-deployable summary. A full `./deploy.ps1` runs it automatically (Phase 1) and stops before spending money if the target region fails; override with `-SkipPreflight`, or pick another region with `-Location <region>`.

**App Service quota is checked per TIER via `Microsoft.Web/locations/{region}/usages`.** Each entry's `localizedValue` is the App Service **tier** (Basic, Standard, Premium v2, Premium v3, Premium v4, Isolated…) and `limit > 0` means that tier is deployable in that region for your subscription. This is the real gate: e.g. an `S1` (**Standard**) create fails with *"Total VMs: 0"* when the Standard tier shows `0/0`, even though **Premium v3** shows `0/360` — so the fix is to pick a SKU in a tier that has quota, not to change the family. The precheck prints every tier `[+]` (has quota) / `[x]` (zero), maps your configured `app_service_sku` to its tier, and if that tier is 0 it lists the tiers that **do** have quota so you can switch (e.g. `app_service_sku = "P1v3"`). Request more quota at <https://aka.ms/antquotahelp>.

> Note: a subscription-level **`ProvisioningDisabled`** region restriction (seen on some internal subs for Azure SQL) can only be detected at deploy time — if you hit it, re-run `deploy.ps1 -Location <another-region>`.

## Jump box + Azure Bastion (test the private network end-to-end)

Only relevant when `use_private_networking = true` **and** `custom_network_mode = false` (this
stack must own the VNet to add the extra subnets). Set `enable_jumpbox = true` to create:
- A **`snet-jumpbox`** subnet + a small **Windows Server 2022** VM (`Standard_D2s_v6` by default, no public IP)
- An **`AzureBastionSubnet`** (`/26` minimum) + **Azure Bastion** host with a Standard SKU public IP

`./deploy.ps1 -Task configure` asks about this interactively (right after the VNet/subnet-source
question, and only when you're on the self-created-VNet path) — answer "yes" and it writes
`enable_jumpbox`/`jumpbox_vm_size` into `terraform.tfvars` for you. Or set it by hand in
`terraform.tfvars`:

```hcl
enable_jumpbox = true
# jumpbox_vm_size        = "Standard_D2s_v6"   # default
# jumpbox_admin_username = "jumpboxadmin"      # default
# jumpbox_admin_password = ""                  # default: auto-generated, retrieve with `terraform output -raw jumpbox_admin_password`
# bastion_sku            = "Standard"          # default; "Basic" is cheaper but portal-RDP only (no native client/tunnel)
# subnet_bastion_prefix  = "10.60.0.128/26"    # default — fits the default /24 VNet alongside snet-pe/snet-app
# subnet_jumpbox_prefix  = "10.60.0.192/27"    # default
```

This gives you (or a tester) a way to **RDP into the VNet through the portal via Bastion** — no VM
public IP, no NSG hole open to the internet — and from there browse the private web app URL or
resolve the private DNS names. You typically **won't need to RDP in just for the SQL grant, the
PAT, or a health check** though: `deploy.ps1 -Task grant-sql` and `-Task set-pat` offer the jump
box as just *one* of several access-mode choices (alongside trying direct access, if you're
already on the VNet some other way, and a temporary-public-access escape hatch) — see
[Post-deploy](#post-deploy) and [Going live against real GitHub data](#going-live-against-real-github-data)
for the full menu. `-Task status`'s health check tries a direct connection first and falls back
to the jump box automatically if one exists (no menu — it's read-only, so there's no
temporary-public-access option for it; checking from outside the VNet would otherwise show a
platform-level 403, since the web app's public access is disabled by design, not a real signal).

Connect: Azure Portal → the `vm-jumpbox` VM → **Connect → Bastion** → sign in with
`jumpbox_admin_username` / `terraform output -raw jumpbox_admin_password`. Tear it down again by
setting `enable_jumpbox = false` and re-applying (Bastion + its public IP are the main ongoing cost —
see [Cost notes](#cost-notes)).

## Public vs. private networking (switch)

`use_private_networking` (default `true`) flips the whole pattern:

| | `true` — Private (production) | `false` — Public (personal-tenant / Easy Auth testing) |
|---|---|---|
| Web app | Public access **disabled**, VNet-integrated, reached via **inbound private endpoint** | Public access **enabled** — internet-reachable, still **Entra/Easy-Auth gated** and browsable |
| Key Vault / SQL | Public access disabled, **private endpoints**, private DNS | Public access enabled; SQL gets an **AllowAzureServices firewall rule**; KV network ACL allows (RBAC still gates) |
| VNet / subnets / PEs | Created | **Not created** |
| Private DNS zones | **Not created by default** (landing zone/DINE owns DNS); created only if `create_private_dns_zones = true` when there's no central DNS/DINE policy | **Not created** |
| Plan size | ~22–25 resources (varies with DNS zone creation) | ~17 resources |

Use **public** in a personal tenant to validate Easy Auth end-to-end (you can browse the site), then deploy **private** to the customer environment. Verified: `terraform plan` resolves cleanly in both modes.

## VNet source — this stack creates one, or bring your own

Only relevant when `use_private_networking = true`. `custom_network_mode` picks the source:

| | `false` (default) — this stack creates the VNet | `true` — bring your own existing VNet |
|---|---|---|
| VNet + subnets | Created (`vnet_address_space`, `subnet_pe_prefix`, `subnet_app_prefix`) | Must already exist — this stack only reads them via `data` sources, never creates or modifies them |
| App subnet delegation | Created automatically (`Microsoft.Web/serverFarms`) | Must already be delegated by you before deploying |
| Use when | Standalone subscription, no central IPAM | Your organization's IPAM/landing zone controls VNet creation and address space assignment |
| Required variables | none (defaults are fine) | `existing_vnet_resource_group_name`, `existing_vnet_name`, `existing_subnet_pe_name`, `existing_subnet_app_name` |

Minimum subnet sizes: the private-endpoint subnet needs at least a `/27` (this stack places three private endpoints — app, Key Vault, SQL — one IP each, plus Azure reserves 5 per subnet); the App Service delegated subnet needs at least a `/27` per Azure's VNet-integration requirements. `deploy.ps1`'s interactive configure step asks for both modes; for a non-interactive/scripted setup, set the variables directly in `terraform.tfvars` (see `terraform.tfvars.example`).

> **Private DNS default (important):** `create_private_dns_zones` defaults to **`false`** — the enterprise/CAF-safe choice. A private deployment does **not** create private DNS zones; the landing zone wires DNS centrally (hub zones + DeployIfNotExists), or you supply hub zone IDs. When there's **no central DNS/DINE policy** in this subscription, set `create_private_dns_zones = true` so the stack creates its own local zones. See *Reusing a centralized Private DNS* below.

## Identity model (flip switch): `identity_mode`

The SQL/identity wiring flips with one variable so the same code serves both the shared-tenant **test** and the customer **prod** deployment:

| | `system_assigned` *(default — CUSTOMER / prod)* | `user_assigned_selfadmin` *(TEST — hybrid/shared tenant)* |
|---|---|---|
| Web app identity | App Service **system-assigned** MI (the normal model) | A **user-assigned** MI (`id-…`) |
| SQL Entra admin | An **external** Entra group/user (`sql_admin_object_id`) | **The app's UAMI itself** |
| Schema provisioning | App MI granted via one-time T-SQL (`terraform output post_deploy_sql_grant`) incl. **`db_ddladmin`** so **EF migrations** apply on startup | **App applies EF migrations** on startup (`Database.Migrate()`; the UAMI is the SQL admin) — **no grant** |
| Your identity needed as SQL admin? | Yes (must live in the SQL server's tenant) | **No** — solves the cross-tenant problem |
| Best for | Simplified single-tenant customer deploy | Testing where your identity isn't in the subscription's tenant |

**Why the test mode exists:** Azure SQL's Entra auth only trusts principals in the **subscription's home tenant**. If you deploy into a subscription whose tenant differs from your own sign-in identity (common in hybrid/managed subscriptions), your identity can't be the SQL admin or run the grant. Making the app's *own* user-assigned MI the SQL admin sidesteps this entirely while still exercising the real managed-identity path you'll ship to the customer.

> Customer hand-off = just set `identity_mode = "system_assigned"` (the default), provide `sql_admin_object_id`, and set `use_private_networking = true`. Nothing else changes.

## Test flow (public + self-admin + in-cloud image build)

**Easiest:** run `./deploy.ps1` from the repo root — **one** guided, colorized script that does the whole journey: prereqs → preflight → **configure** (interactive `terraform.tfvars`, incl. "who is admin — Myself/Group") → provision → in-cloud image build → SQL grant → PAT seed → health. Run a single phase with `-Task` (e.g. `-Task configure`, `-Task grant-sql`, `-Task set-pat`, `-Task status`), or the whole thing with no args. `-DryRun` previews without changing anything.

Or copy `terraform.tfvars.example` to `terraform.tfvars` and set `identity_mode = "user_assigned_selfadmin"`, `use_private_networking = false`, `create_acr = true` by hand.

```bash
# 0) Auth to the target subscription's tenant so both azurerm + azuread target it.
az login --tenant <tenant-id>
az account set --subscription <subscription-id>

cd infra
terraform init
terraform apply                       # creates ACR + app (running the placeholder image)

# 1) Build THIS app's image in the cloud (no local Docker) and push to the new ACR.
ACR=$(terraform output -raw acr_login_server)
az acr build -r ${ACR%%.*} -t ghcp-credit-visibility:latest ../GhcpCreditVisibility

# 2) Point the web app at the image and re-apply.
#    Set in terraform.tfvars:  container_image = "<acr_login_server>/ghcp-credit-visibility:latest"
terraform apply

# 3) Browse it — you'll be redirected to Entra; sign in with an account in the
#    subscription's tenant. An Entra "Admin" role holder sees all mock data and can
#    configure group/user mappings at /Admin/Mappings.
terraform output web_app_url
```

> If your tenant blocks **multi-tenant** app registrations (`SignInAudienceNotAllowedAsPerAppPolicy`), you don't need them: the app registration is single-tenant (`AzureADMyOrg`), so any account homed in (or a guest/member of) the subscription's tenant can sign in.

## App registration ownership (so it's always cleanable)

The Entra **app registration and service principal are created with explicit `owners`** = the deploying principal (plus any `additional_app_owner_object_ids` you set). This is deliberate: an app created **without** an owner cannot be deleted later by a non-privileged user (Graph returns `403`), leaving orphaned registrations that only a tenant admin can remove. Setting owners at creation guarantees the creator can always delete it via `terraform destroy` or `az ad app delete`.

For shared environments, add a durable team owner so cleanup never depends on one person:
```hcl
additional_app_owner_object_ids = ["<platform-team-user-or-sp-objectId>"]   # users/SPs only, not groups
```
> If you have pre-existing **ownerless** app registrations from an earlier run, only a tenant admin (Application Administrator / Cloud Application Administrator) can delete them — `terraform destroy` here can't, because they weren't created with an owner.

## The admin console (`DbGroupMapping`)

This is the app's built-in access model. The **Entra group → GitHub cost center** mapping is stored in the database and managed in-app at **`/Admin/Mappings`**.

Why this exists: Entra owns group **membership**, GitHub owns **cost centers**, but nothing connects the two. The admin console is that glue — and because scope is resolved **per request**, mapping changes take effect on the user's next page load (no re-login, no redeploy).

What an admin does at `/Admin/Mappings`:
- **Map a principal → a GitHub cost center** — a principal is an Entra security **group** (a team) or an individual **user** object ID (for a lone manager with no group). Cost centers are picked from those discovered in the snapshot data.
- **Designate administrators** — a group or user who sees all data and can manage the console (self-service).
- **Set the organization display name** (header/title) — an admin setting, not Terraform.
- View **their own user + group object IDs** (from the token) to make mapping/testing easy.

Access (admin) is granted by **either** the Entra **`Admin`** app role (bootstrap — assign yourself once so you can sign in and configure) **or** a principal (group or user) you add as an administrator in the console. Tables `PrincipalCostCenterMapping`, `AdminPrincipal`, and `AppSetting` are created/updated via **EF Core migrations** on startup.

Test flow with a peer:
```
1. Deploy the app.
2. Assign yourself the Entra "Admin" app role -> sign in -> open /Admin/Mappings.
3. Create an Entra security group (e.g. SG-Finance), add your peer.
4. In the console: map SG-Finance -> Cost Center A.
5. Peer signs in -> sees only Cost Center A. You (admin) -> see all. Change the mapping any time; no re-login.
```



## What it creates
| Resource | Notes |
|---|---|
| VNet + 2 subnets | `snet-pe` (private endpoints), `snet-app` (App Service VNet integration, delegated) — **only when `custom_network_mode = false`** (default). Set `custom_network_mode = true` to use your own existing VNet/subnets instead — see [VNet source](#vnet-source--this-stack-creates-one-or-bring-your-own) above |
| Private DNS zones | `vaultcore`, `database.windows.net`, `azurewebsites.net` — **only when `create_private_dns_zones = true`** (no central DNS/DINE policy). Default is to reuse a hub / DINE — see below |
| App Service Plan (Premium v3) + Linux Web App (container) | Public access **disabled**, VNet-integrated, **inbound private endpoint**, **Easy Auth (Entra)** |
| Entra app registration | App Roles `Admin`/`Manager`/`Viewer`, group claims, Easy Auth redirect URI |
| Key Vault | RBAC, **public access disabled**, private endpoint; optional `github-pat` secret |
| Azure SQL server + DB (serverless) | **Entra-only auth**, public access disabled, private endpoint |
| Log Analytics + App Insights | telemetry / resilience signals |
| Jump box VM + Azure Bastion | **only when `enable_jumpbox = true`** (self-created-VNet path only) — Windows Server VM (`Standard_D2s_v6` default) with no public IP, reached via Bastion, for testing the private network — see [Jump box + Azure Bastion](#jump-box--azure-bastion-test-the-private-network-end-to-end) |

## Prerequisites
- `az login` into the target subscription's tenant.
- Terraform ≥ 1.9. Permissions to create the above + an Entra app registration.
- An Entra group/user object ID to be the **SQL Entra admin** (only for `identity_mode = system_assigned`).

## Deploy
```bash
cd infra
cp terraform.tfvars.example terraform.tfvars   # then edit
terraform init
terraform apply
```

Minimum variables to set in `terraform.tfvars`:
```hcl
subscription_id      = "<subscription-guid>"
sql_admin_group_name = "SG-GHCP-SQL-Admins"     # display name
sql_admin_object_id  = "<group-or-user-object-id>"
# use_mock_data      = true   # default — no PAT/Copilot needed
```

### Reusing a centralized Private DNS (Private Link at Scale)
This app is compatible with the CAF *"Private Link and DNS integration at scale"* model, where a
landing-zone policy **denies private DNS zone creation** and a separate **DeployIfNotExists (DINE)**
policy wires the private endpoint to the central hub zones via remediation. This is the **default**
(`create_private_dns_zones = false`) — **we never create a `Microsoft.Network/privateDnsZones`** (which the
deny policy would block), so a private `terraform apply` works in a governed landing zone as-is. Then pick one:

**Option A — platform DINE does the DNS (recommended for that model):** leave `existing_private_dns_zone_ids = {}`.
We create the private endpoints **without a `privateDnsZoneGroup`**, and the central policy remediates the zone
group + A records. Nothing else required from you.
```hcl
create_private_dns_zones      = false
existing_private_dns_zone_ids = {}   # rely entirely on the central DINE policy
```

**Option B — attach the hub zones explicitly** (if you have Reader/Network-Contributor on them):
```hcl
create_private_dns_zones      = false
existing_private_dns_zone_ids = {
  vault = "/subscriptions/.../privatelink.vaultcore.azure.net"
  sql   = "/subscriptions/.../privatelink.database.windows.net"
  sites = "/subscriptions/.../privatelink.azurewebsites.net"
}
```
When there's **no central DNS/DINE policy** in this subscription, set `create_private_dns_zones = true` to create local zones (with matching VNet links) so name resolution works without a hub.

> **Timing / warm-up (Option A):** DINE remediation is **asynchronous** — after `terraform apply` creates the
> private endpoints, there's a short window (usually ~1–2 min, occasionally longer if a remediation task must run)
> before the hub A-records resolve. During that window the app can't resolve the SQL/Key Vault private FQDNs.
> **The app is built to ride this out:** the migrator and snapshot jobs retry with backoff (5→60s, indefinitely)
> and complete automatically once DNS resolves — **no crash-loop, no manual restart**. Entra sign-in is unaffected
> (its secret is a direct app setting, not a Key Vault reference). Data pages may show a transient error until DNS
> is live. `terraform apply` itself does **not** wait on DNS, so it won't fail for this reason.
>
> One caveat in real-data + private mode: Key Vault **references** (the `GitHub__Token` app setting) are resolved by
> App Service and, if the vault is unreachable at first resolution, may only refresh on the platform's schedule or a
> restart. Since the PAT is used only by the background job (which retries), this self-corrects — but if you deploy
> real-data + private in one shot, a single Web App restart after DNS is confirmed guarantees the reference resolves.

## Post-deploy

**`identity_mode = user_assigned_selfadmin` (self-admin test): only step 2 applies** — the app applies its EF migrations on startup (the UAMI is the SQL admin, so no SQL grant is needed).

**`identity_mode = system_assigned` (customer/prod): both steps.**

1. **Grant the web app's managed identity access to SQL.** Run **`./deploy.ps1 -Task grant-sql`** from the repo root — it reads the server/DB/app names from Terraform outputs, uses your `az login` (you must be the Entra SQL admin), and applies an idempotent grant (`db_ddladmin` + read/write) so the app can run its migrations. No manual SQL needed if you don't want it, no restart (the app retries migrations and picks it up within ~30s). **On a private deployment**, the SQL server's public access is disabled, so your workstation may not have a path to it — `deploy.ps1` walks you through **how** to run the grant:
   - **Try direct access** — works if you're already on the VNet somehow (VPN/ExpressRoute/peering, common in real landing-zone environments). Rather than a separate network probe (Azure SQL's gateway accepts the TCP connection either way and only denies access during the login handshake, so a bare port check can't tell you in advance whether this will work), `deploy.ps1` just attempts the grant directly and interprets the real result.
   - **Use the jump box** (only offered when `enable_jumpbox = true`) — via **Azure Run Command**, no RDP needed. Your SQL access token is passed in as a `--protected-parameters` value (never logged or persisted), and the grant executes inside the VNet.
   - **Temporarily allow public access** — an explicit, opt-in escape hatch: briefly re-enables the SQL server's public endpoint with a firewall rule scoped to your IP, runs the grant, then reverts and verifies. Even if the revert step itself somehow failed, the next `terraform apply` re-asserts `public_network_access_enabled = false` for private mode regardless, so this can't silently leave the door open long-term.
   - **Skip — do it manually** — prints the T-SQL (`terraform output post_deploy_sql_grant`) for you to run from wherever you do have access (an existing bastion, a self-hosted CI/CD agent already in the VNet, etc.). When run as part of the full `./deploy.ps1` sequence, this pauses (Enter to continue) since later steps assume the grant is done — but never blocks under `-Yes`/`-DryRun` or when run standalone (`-Task grant-sql`).

   If Direct access fails, `deploy.ps1` offers the jump box (if one exists) as the first fallback, then the temporary-public-access escape hatch, before falling through to manual. The temporary-public-access option is also directly selectable from the menu on its own, without trying Direct first.
2. **Assign app roles / groups.** In Entra, assign bootstrap administrators to the app's `Admin` role, then use `/Admin/Mappings` to manage which Entra groups or users can see which cost centers.

## Going live against real GitHub data
1. Set `use_mock_data = false` and `github_enterprise_slug = "<your-enterprise>"`.
2. Provide the enterprise PAT as Key Vault secret **`github-pat`** (pick one):
   - **Out-of-band (recommended):** run **`./deploy.ps1 -Task set-pat`** (prompts for the PAT, masked, and stores it in the `github-pat` secret). On a **private** vault, `deploy.ps1` offers the same four access-mode choices as the SQL grant above: try direct access (attempts the write directly — Key Vault's front-end also accepts the TLS connection either way and only denies at the HTTP layer, so it's interpreted from the real result rather than a separate probe), use the jump box (its own managed identity, granted `Key Vault Secrets Officer` on just this vault — no RDP), temporarily allow public access (an IP-scoped network rule, auto-reverted), or skip and set it manually from wherever you have access. The PAT value itself is only requested once you've picked a mode that actually needs it — the manual option never asks this script to hold it at all.
   - **Terraform-seeded (turnkey):** set `github_pat_secret_value` in `terraform.tfvars` before apply. Same private-vault caveat — the **apply host** must reach the private endpoint.
3. The app's **`GitHub__Token`** app setting is a Key Vault reference to `github-pat` — **wired automatically whenever `use_mock_data = false`** (independent of how the secret was seeded) — resolved at runtime by the app's managed identity (`Key Vault Secrets User`). The secret value never lands in app settings or Terraform state.

> The PAT is read **only by the background snapshot job**, never on a user request. Control-plane operations (creating the vault, RBAC, app settings) work from anywhere; only the **secret value read/write** is gated by the private endpoint.

## Cost notes
- App Service defaults to **Standard (S1)** with a **CPU-based autoscale** rule (1→3 instances) for production headroom — Standard is the minimum tier that supports autoscale. It also covers the inbound private endpoint, VNet integration, Always On, and deployment slots. **Premium is not required** (private endpoints are supported on Basic/Standard/PremiumV2+); step up to P-series only for higher scale-out or Premium-only features. For a cheaper truly-private option, **Azure Container Apps** (internal ingress, scale-to-zero) is an alternative since the app is already containerized.
- Azure SQL is **serverless (GP_S_Gen5_1)**, but **auto-pause is disabled by default** (`sql_auto_pause_minutes = -1`, always-warm — the production default, since a paused DB adds first-hit resume latency for the SQL Entra admin grant/migrations/snapshot job). For a dev/test subscription where idle cost matters more than latency, set `sql_auto_pause_minutes` to a positive value (e.g. `60`) to auto-pause after that many idle minutes. Swap to Table Storage if pure key/value persistence is preferred (cheaper, but the trend/query code assumes SQL).
- **Jump box + Bastion** (`enable_jumpbox = true`) adds an always-on VM + Bastion host + Standard public IP — meaningful ongoing cost (roughly similar to a small always-on VM plus Bastion's hourly charge). Turn it off (`enable_jumpbox = false`) once you're done testing the private network path.
- Validate exact figures in the Azure Pricing Calculator before quoting.
