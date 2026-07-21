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

# Azure SQL has a well-documented race condition: a freshly-created (or just-updated) logical
# server needs a few seconds before it's internally ready to accept firewall-rule changes, even
# though Terraform's dependency graph already waited for the server resource's own apply to
# report success — the ARM "creation succeeded" signal doesn't guarantee the SQL control plane
# has finished propagating internally. Without this, the firewall rules below can intermittently
# fail with "DenyPublicEndpointEnabled: Unable to create or modify firewall rules when public
# network interface for the server is disabled" on a freshly-created server, even in PUBLIC mode
# (public_network_access_enabled = true). Only relevant in public mode — private mode never
# creates these firewall rules at all (see counts below).
resource "time_sleep" "sql_server_ready" {
  count           = var.use_private_networking ? 0 : 1
  depends_on      = [azurerm_mssql_server.sql]
  create_duration = "30s"
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

  # Explicit geo-redundant backup storage (GRS) — this is already Azure's default for new
  # databases, but we pin it in code so it's an intentional, reviewable choice rather than an
  # implicit platform default. Applies to both PITR (short-term, 7-day default — unchanged)
  # and would apply to LTR backups if that were ever enabled (it isn't here).
  storage_account_type = "Geo"

  tags = var.tags
}

# ── Monitoring & alerting: CPU / memory / storage pressure ────────
# Notifies when the database is approaching the limits of its current tier, so you know to
# scale up (CPU/memory) or increase max_size_gb (storage) before it becomes an outage.
resource "azurerm_monitor_action_group" "sql_alerts" {
  name                = "ag-sql-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  short_name          = "sqlalert"
  tags                = var.tags

  dynamic "email_receiver" {
    for_each = var.sql_alert_email_addresses
    content {
      name                    = "email-${index(var.sql_alert_email_addresses, email_receiver.value)}"
      email_address           = email_receiver.value
      use_common_alert_schema = true
    }
  }
}

# Stream SQL DB resource logs/metrics to the existing Log Analytics workspace (same pattern
# as the App Service diagnostic setting) — enables KQL queries, SQLInsights, and history
# beyond the Azure Monitor metrics retention used by the alerts above.
resource "azurerm_monitor_diagnostic_setting" "sql_db" {
  name                       = "diag-sql-db"
  target_resource_id         = azurerm_mssql_database.db.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id

  enabled_log {
    category_group = "allLogs"
  }
  enabled_metric {
    category = "Basic"
  }
}

resource "azurerm_monitor_metric_alert" "sql_cpu" {
  name                = "alert-sql-cpu-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  scopes              = [azurerm_mssql_database.db.id]
  description         = "Azure SQL Database CPU usage is above 80% — consider scaling up the SKU or optimizing queries."
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.Sql/servers/databases"
    metric_name      = "cpu_percent"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = 80
  }

  action {
    action_group_id = azurerm_monitor_action_group.sql_alerts.id
  }
}

resource "azurerm_monitor_metric_alert" "sql_memory" {
  name                = "alert-sql-memory-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  scopes              = [azurerm_mssql_database.db.id]
  description         = "Azure SQL Database memory usage is above 80% — consider scaling up the SKU."
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.Sql/servers/databases"
    metric_name      = "sql_instance_memory_percent"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = 80
  }

  action {
    action_group_id = azurerm_monitor_action_group.sql_alerts.id
  }
}

resource "azurerm_monitor_metric_alert" "sql_storage" {
  name                = "alert-sql-storage-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  scopes              = [azurerm_mssql_database.db.id]
  description         = "Azure SQL Database storage usage is above 80% of max_size_gb — increase max_size_gb or clean up data before it fills up."
  severity            = 1
  frequency           = "PT15M"
  window_size         = "PT1H"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.Sql/servers/databases"
    metric_name      = "storage_percent"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = 80
  }

  action {
    action_group_id = azurerm_monitor_action_group.sql_alerts.id
  }
}

# PUBLIC pattern: allow Azure services (App Service outbound) to reach SQL.
# 0.0.0.0 start+end is the special "Allow Azure services" rule.
resource "azurerm_mssql_firewall_rule" "allow_azure" {
  count            = var.use_private_networking ? 0 : 1
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.sql.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
  depends_on       = [time_sleep.sql_server_ready]
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
  depends_on       = [time_sleep.sql_server_ready]
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
