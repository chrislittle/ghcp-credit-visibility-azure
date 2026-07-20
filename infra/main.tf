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
# use_uami = true  (identity_mode = "user_assigned_selfadmin"):
#   A user-assigned MI is BOTH the web app identity AND the SQL Entra admin.
#   The app applies its EF Core migrations on startup (Database.Migrate) — no human
#   grant, no cross-tenant admin. Use this to TEST in a hybrid/shared tenant
#   (e.g. a hybrid/shared tenant) where your identity can't be the SQL admin.
# use_uami = false (identity_mode = "system_assigned"):
#   Normal App Service system-assigned MI + an external Entra SQL admin
#   (sql_admin_object_id) that runs the one-time grant. This is the simplified
#   single-tenant CUSTOMER model.
locals {
  use_uami = var.identity_mode == "user_assigned_selfadmin"

  # The identity object ID used for RBAC (Key Vault, ACR) and the SQL admin.
  app_principal_id = local.use_uami ? azurerm_user_assigned_identity.app[0].principal_id : azurerm_linux_web_app.app.identity[0].principal_id
}
