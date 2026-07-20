resource "azurerm_service_plan" "plan" {
  name                = "asp-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  os_type             = "Linux"
  sku_name            = var.app_service_sku
  tags                = var.tags
}

# Split the container image into registry URL + image:tag for site_config.
locals {
  image_has_registry = length(split("/", var.container_image)) > 1 && length(regexall("\\.", split("/", var.container_image)[0])) > 0
  registry_url       = local.image_has_registry ? "https://${split("/", var.container_image)[0]}" : "https://mcr.microsoft.com"
  docker_image_name  = local.image_has_registry ? join("/", slice(split("/", var.container_image), 1, length(split("/", var.container_image)))) : var.container_image
}

# User-assigned identity (created only in user_assigned_selfadmin mode). Standalone
# resource — avoids the web-app<->SQL dependency cycle and lets a single principal be
# both the app identity and the SQL Entra admin.
resource "azurerm_user_assigned_identity" "app" {
  count               = local.use_uami ? 1 : 0
  name                = "id-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  tags                = var.tags
}

resource "azurerm_linux_web_app" "app" {
  name                = local.app_name
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  service_plan_id     = azurerm_service_plan.plan.id
  https_only          = true

  # PRIVATE: public access off (reached via inbound private endpoint) + VNet integration.
  # PUBLIC:  public access on (browsable directly, still Entra-gated); no VNet integration.
  public_network_access_enabled = !var.use_private_networking
  virtual_network_subnet_id     = local.subnet_app_id

  tags = var.tags

  # Container stdout/stderr → filesystem, so `az webapp log tail` shows real app exceptions
  # instead of "Logging is not enabled for this container."
  logs {
    application_logs {
      file_system_level = "Information"
    }
    http_logs {
      file_system {
        retention_in_days = 7
        retention_in_mb   = 35
      }
    }
  }

  # SystemAssigned (customer/prod) or UserAssigned (self-admin test) per identity_mode.
  identity {
    type         = local.use_uami ? "UserAssigned" : "SystemAssigned"
    identity_ids = local.use_uami ? [azurerm_user_assigned_identity.app[0].id] : null
  }

  site_config {
    vnet_route_all_enabled = var.use_private_networking
    always_on              = true
    ftps_state             = "Disabled"
    http2_enabled          = true
    minimum_tls_version    = "1.2"
    # Platform monitors liveness and recycles unhealthy instances. Uses /health/live (always 200
    # while the process is up) — NOT readiness, so instances aren't pulled during DNS/schema warm-up.
    health_check_path                       = "/health/live"
    health_check_eviction_time_in_min       = 2
    container_registry_use_managed_identity = local.image_has_registry
    # For a user-assigned identity, tell App Service which identity to use for ACR pull.
    container_registry_managed_identity_client_id = local.use_uami && local.image_has_registry ? azurerm_user_assigned_identity.app[0].client_id : null

    application_stack {
      docker_registry_url = local.registry_url
      docker_image_name   = local.docker_image_name
    }
  }

  app_settings = merge(
    {
      "APPLICATIONINSIGHTS_CONNECTION_STRING" = azurerm_application_insights.appi.connection_string
      "WEBSITES_PORT"                         = "8080"
      "GitHub__Enterprise"                    = var.github_enterprise_slug
      "GitHub__UseMock"                       = tostring(var.use_mock_data)
      "Retention__Months"                     = tostring(var.retention_months)
      # Managed-identity SQL connection. In user_assigned_selfadmin mode we must name the
      # identity via "User Id=<UAMI clientId>"; system-assigned needs no User Id.
      "ConnectionStrings__BillingDb" = "Server=tcp:${azurerm_mssql_server.sql.fully_qualified_domain_name},1433;Database=${azurerm_mssql_database.db.name};Authentication=Active Directory Managed Identity;${local.use_uami ? "User Id=${azurerm_user_assigned_identity.app[0].client_id};" : ""}Encrypt=True;TrustServerCertificate=False;"
    },
    var.enable_easy_auth ? {
      "MICROSOFT_PROVIDER_AUTHENTICATION_SECRET" = azuread_application_password.secret[0].value
    } : {},
    !var.use_mock_data ? {
      # Key Vault reference, resolved at RUNTIME by the app's managed identity (Key Vault Secrets User).
      # Built from the vault URI + the fixed secret name so it works regardless of HOW the PAT was
      # provided — Terraform-seeded (github_pat_secret_value) OR set out-of-band (recommended, and
      # required for a PRIVATE Key Vault where the apply host can't reach the data plane).
      "GitHub__Token" = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault.kv.vault_uri}secrets/github-pat)"
    } : {}
  )

  dynamic "auth_settings_v2" {
    for_each = var.enable_easy_auth ? [1] : []
    content {
      auth_enabled           = true
      require_authentication = true
      unauthenticated_action = "RedirectToLoginPage"
      default_provider       = "azureactivedirectory"
      # Health probes bypass Easy Auth so the platform (and ops) can reach them anonymously.
      excluded_paths = ["/health/live", "/health/ready"]

      active_directory_v2 {
        client_id                  = azuread_application.app[0].client_id
        tenant_auth_endpoint       = "https://login.microsoftonline.com/${data.azurerm_client_config.current.tenant_id}/v2.0"
        client_secret_setting_name = "MICROSOFT_PROVIDER_AUTHENTICATION_SECRET"
      }

      login {
        token_store_enabled = true
      }
    }
  }
}

# App identity can read the PAT secret from Key Vault.
resource "azurerm_role_assignment" "app_kv_secrets_user" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = local.app_principal_id
}

# Stream App Service platform logs/metrics to Log Analytics (complements App Insights).
resource "azurerm_monitor_diagnostic_setting" "app" {
  name                       = "diag-app"
  target_resource_id         = azurerm_linux_web_app.app.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.law.id

  enabled_log {
    category_group = "allLogs"
  }
  enabled_metric {
    category = "AllMetrics"
  }
}

# Inbound private endpoint for the web app (private pattern only).
resource "azurerm_private_endpoint" "app" {
  count               = local.pne
  name                = "pe-app-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  subnet_id           = local.subnet_pe_id
  tags                = var.tags

  private_service_connection {
    name                           = "psc-app"
    private_connection_resource_id = azurerm_linux_web_app.app.id
    subresource_names              = ["sites"]
    is_manual_connection           = false
  }

  dynamic "private_dns_zone_group" {
    for_each = lookup(local.private_dns_zone_ids, "sites", null) != null ? [1] : []
    content {
      name                 = "sites"
      private_dns_zone_ids = [local.private_dns_zone_ids["sites"]]
    }
  }
}
