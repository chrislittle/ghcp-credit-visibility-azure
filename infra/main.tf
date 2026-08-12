locals {
  suffix   = random_string.suffix.result
  base     = "${var.name_prefix}-${local.suffix}"
  base_st  = lower(replace("${var.name_prefix}${local.suffix}", "-", "")) # storage/kv style (no dashes)
  rg_name  = "rg-${local.base}"
  law_name = "log-${local.base}"
}

resource "random_string" "suffix" {
  length  = 5
  special = false
  upper   = false
  numeric = true
}

resource "azurerm_resource_group" "rg" {
  name     = local.rg_name
  location = var.location
  tags     = var.tags
}

# ── Observability (addresses monitoring for §4.4 resilience signals) ──
resource "azurerm_log_analytics_workspace" "law" {
  name                = local.law_name
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = var.tags
}

resource "azurerm_application_insights" "appi" {
  name                = "appi-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  workspace_id        = azurerm_log_analytics_workspace.law.id
  application_type    = "web"
  tags                = var.tags
}

# ── Identity-model switch ────────────────────────────────────────
# Chooses the WEB APP'S IDENTITY TYPE only. Both are production-supported and, for the app itself,
# equivalent — this is all one Terraform state, so neither identity outlives the deployment. Who
# administers SQL is decided separately by sql_admin_group_name/object_id (local.sql_admin_external).
#
# The one structural difference is ORDER OF CREATION. A system-assigned identity does not exist
# until the web app does, and the web app depends on the SQL connection string -> SQL server ->
# whose admin would be that identity: a cycle. A standalone user-assigned identity is created before
# both, which is why only that mode can make the app its own SQL admin.
#
# use_uami = true  (identity_mode = "user_assigned" / legacy "user_assigned_selfadmin")
# use_uami = false (identity_mode = "system_assigned")
locals {
  # Both spellings accepted: "user_assigned_selfadmin" was the original name, given for the SQL
  # behaviour that used to be welded to this switch. Renaming without an alias would break an
  # existing tfvars for no benefit.
  use_uami = var.identity_mode != "system_assigned"

  # The identity object ID used for RBAC (Key Vault, ACR) and the SQL admin.
  app_principal_id = local.use_uami ? azurerm_user_assigned_identity.app[0].principal_id : azurerm_linux_web_app.app.identity[0].principal_id

  # ── SQL admin, decided INDEPENDENTLY of the identity model ───────
  # identity_mode used to decide both "which identity does the app run as" and "who is the SQL
  # Entra admin", which are unrelated questions. Welding them together made
  # "user-assigned identity AND a human admin" inexpressible — and since Azure SQL allows exactly
  # ONE Entra admin, choosing the UAMI meant no person could query the database at all. Every
  # operational task (the SRE agent grant, ad-hoc queries, the SQL runbooks) then needed the admin
  # temporarily taken away from the app and handed back.
  #
  # Now: name an admin and you get it, in either identity mode. Leave it blank in
  # user_assigned_selfadmin and the old self-provisioning behaviour stands, which is the point of
  # that mode — it exists for tenants where your identity CANNOT be the SQL admin.
  sql_admin_external = var.sql_admin_object_id != "" && var.sql_admin_group_name != ""

  # Whether the app's identity needs an explicit database grant. It does whenever it is not itself
  # the admin — that grant is what lets Database.Migrate() apply EF migrations on startup.
  app_needs_sql_grant = !local.use_uami || local.sql_admin_external

  # The DB principal name to grant. Under a user-assigned identity the token is presented by the
  # UAMI, so CREATE USER must name the IDENTITY, not the web app — naming the web app there creates
  # a principal that never authenticates and leaves migrations failing with a login error.
  app_sql_principal_name = local.use_uami ? azurerm_user_assigned_identity.app[0].name : azurerm_linux_web_app.app.name
}
