# ── Azure SQL: Entra-only auth, serverless ───────────────────────
resource "azurerm_mssql_server" "sql" {
  name                          = "sql-${local.base}"
  resource_group_name           = azurerm_resource_group.rg.name
  location                      = azurerm_resource_group.rg.location
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  public_network_access_enabled = !var.use_private_networking

  # user_assigned_selfadmin: the web app's UAMI IS the SQL Entra admin (app applies its EF
  #   migrations on startup via Database.Migrate(); no human grant; works when your corp
  #   identity isn't in this tenant).
  # system_assigned: an external Entra group/user is the admin and later grants the
  #   system-assigned MI db_ddladmin/read/write (see post_deploy_sql_grant output) so the
  #   app can apply migrations on deploy.
  azuread_administrator {
    login_username              = local.use_uami ? azurerm_user_assigned_identity.app[0].name : var.sql_admin_group_name
    object_id                   = local.use_uami ? azurerm_user_assigned_identity.app[0].principal_id : var.sql_admin_object_id
    tenant_id                   = data.azurerm_client_config.current.tenant_id
    azuread_authentication_only = true
  }

  lifecycle {
    precondition {
      condition     = local.use_uami || (var.sql_admin_object_id != "" && var.sql_admin_group_name != "")
      error_message = "identity_mode=\"system_assigned\" requires sql_admin_group_name and sql_admin_object_id (the external Entra SQL admin that grants the app's managed identity). Use identity_mode=\"user_assigned_selfadmin\" to self-provision without them."
    }
  }

  tags = var.tags
}

resource "azurerm_mssql_database" "db" {
  name        = "ghcpvisibility"
  server_id   = azurerm_mssql_server.sql.id
  sku_name    = var.sql_database_sku
  collation   = "SQL_Latin1_General_CP1_CI_AS"
  max_size_gb = 2

  # Serverless auto-pause (GP_S_* SKUs). -1 = never pause (always warm).
  auto_pause_delay_in_minutes = var.sql_auto_pause_minutes
  min_capacity                = 0.5

  tags = var.tags
}

# PUBLIC pattern: allow Azure services (App Service outbound) to reach SQL.
# 0.0.0.0 start+end is the special "Allow Azure services" rule.
resource "azurerm_mssql_firewall_rule" "allow_azure" {
  count            = var.use_private_networking ? 0 : 1
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.sql.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# PUBLIC pattern: temporary rule opening the deployer's own IP so deploy.ps1's grant-sql
# phase (running T-SQL as the Entra SQL admin from your workstation) isn't blocked by the
# server firewall. Only created when admin_client_ip is set; harmless to leave in place
# (re-runs of deploy.ps1 refresh it to your current IP if it changes).
resource "azurerm_mssql_firewall_rule" "allow_admin_ip" {
  count            = (var.use_private_networking || var.admin_client_ip == "") ? 0 : 1
  name             = "AllowDeployerIP"
  server_id        = azurerm_mssql_server.sql.id
  start_ip_address = var.admin_client_ip
  end_ip_address   = var.admin_client_ip
}

# Private endpoint for SQL (private pattern only).
resource "azurerm_private_endpoint" "sql" {
  count               = local.pne
  name                = "pe-sql-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  subnet_id           = local.subnet_pe_id
  tags                = var.tags

  private_service_connection {
    name                           = "psc-sql"
    private_connection_resource_id = azurerm_mssql_server.sql.id
    subresource_names              = ["sqlServer"]
    is_manual_connection           = false
  }

  dynamic "private_dns_zone_group" {
    for_each = lookup(local.private_dns_zone_ids, "sql", null) != null ? [1] : []
    content {
      name                 = "sql"
      private_dns_zone_ids = [local.private_dns_zone_ids["sql"]]
    }
  }
}
