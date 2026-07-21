variable "subscription_id" {
  type        = string
  description = "Azure subscription ID to deploy into."
}

# ── Identity model (flip switch) ──────────────────────────────
variable "identity_mode" {
  type        = string
  description = "system_assigned = normal App Service system-assigned MI + external Entra SQL admin + one-time grant (incl. db_ddladmin so EF migrations apply on startup; simplified single-tenant CUSTOMER model). user_assigned_selfadmin = a user-assigned MI is both the web app identity AND the SQL Entra admin so the app applies its EF migrations on startup with no human grant (TEST model for hybrid/shared tenants where your identity can't be the SQL admin)."
  default     = "system_assigned"

  validation {
    condition     = contains(["system_assigned", "user_assigned_selfadmin"], var.identity_mode)
    error_message = "identity_mode must be \"system_assigned\" or \"user_assigned_selfadmin\"."
  }
}

variable "create_acr" {
  type        = bool
  description = "Create a Basic ACR and grant the app AcrPull — convenient for the test flow (build the image in-cloud with `az acr build`, no local Docker). Customer/prod: leave false and point container_image at the customer's own registry."
  default     = false
}

variable "location" {
  type        = string
  description = "Azure region."
  default     = "eastus2"
}

variable "name_prefix" {
  type        = string
  description = "Short prefix for resource names (lowercase, 3-10 chars)."
  default     = "ghcpcv"

  validation {
    condition     = can(regex("^[a-z0-9]{3,10}$", var.name_prefix))
    error_message = "name_prefix must be 3-10 lowercase alphanumeric characters."
  }
}

variable "tags" {
  type        = map(string)
  description = "Tags applied to all resources. Empty by default — set your own in terraform.tfvars if you want tagging."
  default     = {}
}

# ── Networking ────────────────────────────────────────────────
variable "use_private_networking" {
  type        = bool
  description = "true = PRIVATE pattern (VNet + private endpoints + private DNS, public access disabled on app/KV/SQL). false = PUBLIC pattern (public endpoints + firewall rules, no VNet/PE, web app publicly reachable but still Entra-gated) — convenient for personal-tenant Easy Auth testing."
  default     = true
}

variable "vnet_address_space" {
  type        = string
  default     = "10.60.0.0/24"
  description = "Only used when custom_network_mode = false — this stack creates and sizes its own VNet. Advanced: override this (and the two subnet prefixes below) to fit your own IP plan."

  validation {
    condition     = can(cidrnetmask(var.vnet_address_space))
    error_message = "vnet_address_space must be valid CIDR notation, e.g. 10.60.0.0/24."
  }
}

variable "subnet_pe_prefix" {
  type        = string
  description = "Subnet for private endpoints (app inbound, Key Vault, SQL). Only used when custom_network_mode = false. Minimum /27 (three private endpoints are placed here); must fall entirely within vnet_address_space and must not overlap subnet_app_prefix."
  default     = "10.60.0.0/26"

  validation {
    condition     = can(cidrnetmask(var.subnet_pe_prefix))
    error_message = "subnet_pe_prefix must be valid CIDR notation, e.g. 10.60.0.0/26."
  }
  validation {
    condition     = !can(cidrnetmask(var.subnet_pe_prefix)) || tonumber(split("/", var.subnet_pe_prefix)[1]) <= 27
    error_message = "subnet_pe_prefix must be at least /27 in size (prefix length <= 27) — this subnet hosts three private endpoints (app, Key Vault, SQL)."
  }
}

variable "subnet_app_prefix" {
  type        = string
  description = "Delegated subnet for App Service regional VNet integration. Only used when custom_network_mode = false. Minimum /27 per Azure App Service VNet-integration requirements; must fall entirely within vnet_address_space and must not overlap subnet_pe_prefix."
  default     = "10.60.0.64/26"

  validation {
    condition     = can(cidrnetmask(var.subnet_app_prefix))
    error_message = "subnet_app_prefix must be valid CIDR notation, e.g. 10.60.0.64/26."
  }
  validation {
    condition     = !can(cidrnetmask(var.subnet_app_prefix)) || tonumber(split("/", var.subnet_app_prefix)[1]) <= 27
    error_message = "subnet_app_prefix must be at least /27 in size (prefix length <= 27) per Azure App Service VNet-integration requirements."
  }
}

variable "custom_network_mode" {
  type        = bool
  default     = false
  description = "Only relevant when use_private_networking = true. false (default) = this stack creates its own VNet + two subnets (sized from vnet_address_space/subnet_pe_prefix/subnet_app_prefix). true = bring your own existing VNet and subnets — use this when your organization's IPAM/landing zone controls VNet creation and address space assignment. When true, set existing_vnet_resource_group_name, existing_vnet_name, existing_subnet_pe_name, and existing_subnet_app_name below; the vnet_address_space/subnet_*_prefix variables are ignored."
}

variable "existing_vnet_resource_group_name" {
  type        = string
  default     = ""
  description = "Resource group containing the existing VNet. Required when custom_network_mode = true."
}

variable "existing_vnet_name" {
  type        = string
  default     = ""
  description = "Name of the existing VNet to deploy into. Required when custom_network_mode = true."
}

variable "existing_subnet_pe_name" {
  type        = string
  default     = ""
  description = "Name of an existing, empty (non-delegated) subnet for private endpoints (Key Vault, SQL, and the web app's inbound endpoint). Required when custom_network_mode = true. Minimum recommended size /27 (a private endpoint uses one IP per linked resource — this stack creates three)."
}

variable "existing_subnet_app_name" {
  type        = string
  default     = ""
  description = "Name of an existing subnet, already delegated to Microsoft.Web/serverFarms, for App Service regional VNet integration (outbound). Required when custom_network_mode = true. Minimum size /27 per Azure App Service VNet integration requirements."

  validation {
    # Cross-variable checks (Terraform >= 1.9): only enforced when custom_network_mode = true,
    # so public/default deployments that never set these are unaffected.
    condition = !var.custom_network_mode || (
      var.existing_vnet_resource_group_name != "" &&
      var.existing_vnet_name != "" &&
      var.existing_subnet_pe_name != "" &&
      var.existing_subnet_app_name != ""
    )
    error_message = "custom_network_mode = true requires existing_vnet_resource_group_name, existing_vnet_name, existing_subnet_pe_name, and existing_subnet_app_name to all be set."
  }
}

# ── Jump box + Bastion (advanced networking, self-created VNet only) ──
# Lets a tester reach the private app/Key Vault/SQL endpoints without opening any public
# access — RDP into a small Windows VM over Azure Bastion (no VM public IP, no NSG hole for
# 3389 from the internet). Only supported when this stack owns the VNet (custom_network_mode
# = false); bring-your-own-VNet deployments should ask their platform team for equivalent
# jump-box/Bastion access instead of having this stack modify a VNet it doesn't own.
variable "enable_jumpbox" {
  type        = bool
  description = "Create a Windows jump-box VM + Azure Bastion inside the private VNet, so you can test the private app/Key Vault/SQL private endpoints end-to-end. Only valid when use_private_networking = true AND custom_network_mode = false (this stack must own the VNet to add the extra subnets)."
  default     = false

  validation {
    condition     = !var.enable_jumpbox || !var.custom_network_mode
    error_message = "enable_jumpbox = true requires custom_network_mode = false (jump box/Bastion are only supported in the self-created-VNet path, not bring-your-own-VNet)."
  }
}

variable "jumpbox_vm_size" {
  type        = string
  description = "VM size for the jump box. D2s_v6 is a modest, low-cost default sized for RDP + a browser to test the private app."
  default     = "Standard_D2s_v6"
}

variable "jumpbox_admin_username" {
  type        = string
  description = "Local administrator username for the jump-box VM."
  default     = "jumpboxadmin"
}

variable "jumpbox_admin_password" {
  type        = string
  description = "Local administrator password for the jump-box VM. Leave empty to auto-generate a strong random password (retrieve it after apply with `terraform output -raw jumpbox_admin_password`)."
  default     = ""
  sensitive   = true
}

variable "bastion_sku" {
  type        = string
  description = "Azure Bastion SKU. Standard adds native-client (RDP/SSH from your own client, not just the portal) and IP-based connection; Basic is the cheapest option (portal-only browser RDP)."
  default     = "Standard"

  validation {
    condition     = contains(["Basic", "Standard"], var.bastion_sku)
    error_message = "bastion_sku must be \"Basic\" or \"Standard\"."
  }
}

variable "subnet_bastion_prefix" {
  type        = string
  description = "Subnet for Azure Bastion. Only used when enable_jumpbox = true. Must be named AzureBastionSubnet (enforced automatically) and be at least /26 per Azure Bastion requirements; must fall entirely within vnet_address_space and not overlap the other subnets."
  default     = "10.60.0.128/26"

  validation {
    condition     = can(cidrnetmask(var.subnet_bastion_prefix))
    error_message = "subnet_bastion_prefix must be valid CIDR notation, e.g. 10.60.0.128/26."
  }
  validation {
    condition     = !can(cidrnetmask(var.subnet_bastion_prefix)) || tonumber(split("/", var.subnet_bastion_prefix)[1]) <= 26
    error_message = "subnet_bastion_prefix must be at least /26 in size (prefix length <= 26) — required by Azure Bastion."
  }
}

variable "subnet_jumpbox_prefix" {
  type        = string
  description = "Subnet for the jump-box VM's NIC. Only used when enable_jumpbox = true. Must fall entirely within vnet_address_space and not overlap the other subnets."
  default     = "10.60.0.192/27"

  validation {
    condition     = can(cidrnetmask(var.subnet_jumpbox_prefix))
    error_message = "subnet_jumpbox_prefix must be valid CIDR notation, e.g. 10.60.0.192/27."
  }
}

variable "admin_client_ip" {
  type        = string
  description = "PUBLIC pattern only: your current public IP, added as a temporary Azure SQL firewall rule (\"AllowDeployerIP\") so deploy.ps1's grant-sql phase can run T-SQL as the Entra SQL admin. Leave empty to skip (no rule created). deploy.ps1 sets this automatically before Phase-GrantSql."
  default     = ""
}

variable "create_private_dns_zones" {
  type        = bool
  description = "Whether THIS deployment creates the private DNS zones. Default FALSE = enterprise/CAF-safe: in landing zones the platform owns DNS (hub zones + DeployIfNotExists remediation) and typically DENIES zone creation, so we create bare private endpoints and let the central policy wire DNS (or pass hub IDs via existing_private_dns_zone_ids). Set TRUE only when there is NO central DNS/DINE policy in this subscription, so this stack must create its own local zones. Only relevant when use_private_networking=true."
  default     = false
}

variable "existing_private_dns_zone_ids" {
  type        = map(string)
  description = "When create_private_dns_zones=false, map of zone key -> existing (hub) zone resource ID. Keys: vault, sql, sites. Leave EMPTY to rely entirely on a central DeployIfNotExists policy to attach the private DNS zone group (CAF 'Private Link and DNS at scale' model)."
  default     = {}
}

# ── App Service ───────────────────────────────────────────────
variable "app_service_sku" {
  type        = string
  description = "App Service Plan SKU. S1 (Standard) is the production minimum here: supports inbound private endpoint, VNet integration, Always On, deployment slots, AND autoscale. Basic (B1) works but cannot autoscale. NOTE: App Service quota is enforced PER TIER (Basic/Standard/Premium v2/v3/v4) per region and defaults to 0 on constrained sub types (MSDN/Visual Studio, hybrid/internal) — check it with ./preflight.ps1 (reads Microsoft.Web/locations/{region}/usages; localizedValue = tier). If your tier shows 0, pick a SKU in a tier that has quota (e.g. P1v3 for Premium v3) or request quota at https://aka.ms/antquotahelp."
  default     = "S1"
}

variable "autoscale_min" {
  type        = number
  description = "Minimum App Service Plan instances."
  default     = 1
}

variable "autoscale_max" {
  type        = number
  description = "Maximum App Service Plan instances (Standard supports up to 10)."
  default     = 3
}

variable "autoscale_default" {
  type        = number
  description = "Default instance count autoscale falls back to."
  default     = 1
}

variable "container_image" {
  type        = string
  description = "Fully-qualified container image for the web app (e.g., myacr.azurecr.io/ghcp-visibility:latest). Leave default to deploy the quickstart image until the real image is pushed."
  default     = "mcr.microsoft.com/dotnet/samples:aspnetapp"
}

# ── Entra / auth ──────────────────────────────────────────────
variable "app_display_name" {
  type        = string
  description = "Display name for the Entra app registration."
  default     = "GHCP AI Credit Visibility"
}

variable "admin_principal_object_id" {
  type        = string
  description = "Optional object ID of a USER or GROUP granted the Admin app role at deploy time (e.g. yourself — configure.ps1 can resolve this from your az login — or an Entra admins group). Empty = assign the Admin role manually later. Note: group-based app-role assignment needs Entra ID P1+; assigning a single user does not."
  default     = ""
}

variable "additional_app_owner_object_ids" {
  type        = list(string)
  description = "Optional extra owner object IDs (users or service principals — NOT groups) added to the Entra app registration + service principal, in addition to the deploying principal. Use to give a platform/team account co-ownership so the app can always be cleaned up even if the original creator is unavailable. The deploying principal is ALWAYS added as owner automatically."
  default     = []
}

variable "enable_easy_auth" {
  type        = bool
  description = "Create the Entra app registration + wire App Service built-in (Easy Auth) authentication. Set false for infra-only deploys in tenants where you lack app-registration/service-principal rights (e.g., some hybrid/managed tenants)."
  default     = true
}

# ── SQL ───────────────────────────────────────────────────────
variable "sql_admin_group_name" {
  type        = string
  description = "Display name of the Entra group/user to set as SQL Entra administrator. Required only when identity_mode=system_assigned. Ignored when identity_mode=user_assigned_selfadmin (the UAMI is the admin)."
  default     = ""
}

variable "sql_admin_object_id" {
  type        = string
  description = "Object ID of the Entra group/user to set as SQL Entra administrator. Required only when identity_mode=system_assigned. Ignored when identity_mode=user_assigned_selfadmin."
  default     = ""
}

variable "sql_database_sku" {
  type        = string
  description = "Azure SQL DB SKU. GP_S_Gen5_1 = serverless (cheapest for a POC) — NOTE: auto-pause is OFF by default (see sql_auto_pause_minutes, default -1); opt in to a positive sql_auto_pause_minutes if you want it to pause after idle time. For guaranteed always-warm production latency, use a provisioned tier instead (e.g., GP_Gen5_2)."
  default     = "GP_S_Gen5_1"
}

variable "sql_auto_pause_minutes" {
  type        = number
  description = "Serverless auto-pause delay in minutes. -1 disables auto-pause (always warm — production default). Positive values pause after idle to save cost (adds first-hit resume latency). Only applies to GP_S_* serverless SKUs."
  default     = -1
}

variable "sql_alert_email_addresses" {
  type        = list(string)
  description = "Email addresses notified by the SQL CPU/memory/storage metric alerts (>80%) via the ag-sql-* action group. deploy.ps1 defaults this to your signed-in az account's UPN; override with a shared DL if you want the whole team notified. Leave empty to create the alerts with no notification target."
  default     = []
}

# ── GitHub / app behaviour ────────────────────────────────────
variable "github_enterprise_slug" {
  type        = string
  description = "GitHub enterprise slug to report on. Ignored when use_mock_data=true."
  default     = "your-enterprise"
}

variable "use_mock_data" {
  type        = bool
  description = "When true, the app serves synthetic data (no PAT / no GitHub Copilot needed). Set false + populate the Key Vault PAT secret to use the real GitHub billing API."
  default     = true
}

variable "github_pat_secret_value" {
  type        = string
  description = "Optional: seed the GitHub PAT into Key Vault at deploy time (secret name 'github-pat'). Prefer leaving empty and setting the secret out-of-band — the app's GitHub__Token setting references 'github-pat' whenever use_mock_data=false regardless. NOTE: writing the secret is a Key Vault data-plane op, so with a PRIVATE vault this must be done from a host on the VNet. Sensitive."
  default     = ""
  sensitive   = true
}

variable "retention_months" {
  type        = number
  description = "Months of usage snapshots to retain before purge. Default 12 gives a full year of reporting history; raise for longer horizons (min 3)."
  default     = 12

  validation {
    condition     = var.retention_months >= 3
    error_message = "retention_months must be at least 3."
  }
}
