# ─────────────────────────────────────────────────────────────────────────────
#  Azure SRE Agent (optional) — Microsoft.App/agents via azapi
# ─────────────────────────────────────────────────────────────────────────────
#  An AI reliability agent scoped to this deployment's resource group. It reads through ARM,
#  Log Analytics, and Application Insights — all global control-plane endpoints — so it can run
#  in a DIFFERENT region from the app (Microsoft.App/agents isn't available in Germany West
#  Central) and still monitor it. Everything here is gated on var.enable_sre_agent.
#
#  PREREQUISITE: the Microsoft.App resource provider must be registered in the subscription:
#      az provider register -n Microsoft.App
#  (deploy.ps1 -Task provision does this automatically when enable_sre_agent = true.)
#
#  There is no azurerm resource for this yet, so the agent itself is provisioned with azapi.
#  Skills, custom agents, and hooks are NOT ARM resources — they are data-plane config synced
#  separately (see sre/ and deploy.ps1 -Task sre-sync). Only the agent is Terraform-managed.
# ─────────────────────────────────────────────────────────────────────────────

locals {
  sre = var.enable_sre_agent ? 1 : 0
}

# Dedicated resource group so the agent, its telemetry, and its identity are cleanly separable
# from the app — and so deleting the agent (the only way to stop its always-on cost) is a
# contained operation.
resource "azurerm_resource_group" "sre" {
  count    = local.sre
  name     = "rg-sre-${local.base}"
  location = var.sre_agent_location
  tags     = var.tags
}

# Separate Application Insights for the agent's own operational telemetry (tool calls, hook
# activations, investigation traces) so it doesn't skew the app's metrics. Shares the app's
# Log Analytics workspace to keep query/cost surface in one place.
resource "azurerm_application_insights" "sre" {
  count               = local.sre
  name                = "appi-sre-${local.base}"
  resource_group_name = azurerm_resource_group.sre[0].name
  location            = azurerm_resource_group.sre[0].location
  workspace_id        = azurerm_log_analytics_workspace.law.id
  application_type    = "web"
  tags                = var.tags
}

# User-assigned identity the agent uses to read the managed resources. A UAMI (not the agent's
# system-assigned identity) is REQUIRED: knowledgeGraphConfiguration.identity / actionConfiguration
# .identity must reference an identity that already exists when the agent is created — a
# system-assigned identity doesn't exist until after creation (chicken-and-egg), which is exactly
# the "InvalidIdentity: the referenced managed identity must be set in the agent" 400 you get
# otherwise. This mirrors the official microsoft/sre-agent Terraform module.
resource "azurerm_user_assigned_identity" "sre" {
  count               = local.sre
  name                = "id-sre-${local.base}"
  resource_group_name = azurerm_resource_group.sre[0].name
  location            = azurerm_resource_group.sre[0].location
  tags                = var.tags
}

resource "azapi_resource" "sre_agent" {
  count     = local.sre
  type      = "Microsoft.App/agents@2026-01-01"
  name      = "sre-ghcp-${local.suffix}"
  parent_id = azurerm_resource_group.sre[0].id
  location  = var.sre_agent_location
  tags      = var.tags

  identity {
    type         = "SystemAssigned, UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.sre[0].id]
  }

  body = {
    properties = merge(
      {
        actionConfiguration = {
          mode        = var.sre_agent_mode
          accessLevel = var.sre_agent_access_level
          identity    = azurerm_user_assigned_identity.sre[0].id
        }
        defaultModel = {
          provider = var.sre_agent_model_provider
          name     = var.sre_agent_model_name
        }
        # Resource groups the agent is responsible for (full ARM IDs). The app's RG — NOT the agent's
        # own. `identity` is the UAMI that reads them; required, or the create fails with InvalidIdentity.
        knowledgeGraphConfiguration = {
          identity         = azurerm_user_assigned_identity.sre[0].id
          managedResources = [azurerm_resource_group.rg.id]
        }
        logConfiguration = {
          applicationInsightsConfiguration = {
            # The API requires appId and connectionString together.
            appId            = azurerm_application_insights.sre[0].app_id
            connectionString = azurerm_application_insights.sre[0].connection_string
          }
        }
        upgradeChannel = "Stable"
      },
      # OBO elevation is only wired when a sponsor group is supplied; otherwise the agent stays
      # strictly read-only (the recommended posture for this app).
      var.sre_agent_sponsor_group_id != "" ? {
        agentIdentity = {
          initialSponsorGroupId = var.sre_agent_sponsor_group_id
        }
      } : {}
    )
  }

  # The connection string is sensitive; don't echo the whole body to plan output.
  response_export_values = ["identity.principalId"]
}

# ── RBAC for the agent's managed identity ─────────────────────────
# Reader-tier on the APP's resource group (view resources, query logs/metrics) + Monitoring
# Contributor at subscription scope (acknowledge/close Azure Monitor alerts). This is the
# read-only diagnostic baseline; any write action goes through Review-mode approval / OBO.
#
# These target the agent's USER-ASSIGNED identity — the one it uses to read your resources (and the
# one referenced by knowledgeGraphConfiguration.identity). Assigning to the UAMI (not the agent's
# system-assigned identity) also breaks a dependency cycle: the role assignments no longer depend on
# the agent resource itself, so they can be created alongside it.
locals {
  sre_principal_id = local.sre == 1 ? azurerm_user_assigned_identity.sre[0].principal_id : null
}

resource "azurerm_role_assignment" "sre_reader" {
  count                = local.sre
  scope                = azurerm_resource_group.rg.id
  role_definition_name = "Reader"
  principal_id         = local.sre_principal_id
}

resource "azurerm_role_assignment" "sre_monitoring_reader" {
  count                = local.sre
  scope                = azurerm_resource_group.rg.id
  role_definition_name = "Monitoring Reader"
  principal_id         = local.sre_principal_id
}

resource "azurerm_role_assignment" "sre_log_analytics_reader" {
  count                = local.sre
  scope                = azurerm_resource_group.rg.id
  role_definition_name = "Log Analytics Reader"
  principal_id         = local.sre_principal_id
}

resource "azurerm_role_assignment" "sre_monitoring_contributor" {
  count                = local.sre
  scope                = "/subscriptions/${data.azurerm_client_config.current.subscription_id}"
  role_definition_name = "Monitoring Contributor"
  principal_id         = local.sre_principal_id
}

# Control-plane-only view of Key Vault (config, RBAC, private-endpoint state) — NEVER a
# data-plane role. The agent must not be able to read the github-pat secret value; the Phase 4
# hook is the second line of defence, this scope is the first.
resource "azurerm_role_assignment" "sre_kv_reader" {
  count                = local.sre
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Reader"
  principal_id         = local.sre_principal_id
}

# ── Operator access to the agent's DATA PLANE ─────────────────────
# `deploy.ps1 -Task sre-sync` pushes skills/agents/hooks/knowledge over the agent's data-plane API,
# which needs a token for audience https://azuresre.dev — and that requires the dedicated
# "SRE Agent Administrator" role ON THE AGENT. Subscription Owner is NOT sufficient (the data plane
# has its own RBAC). Granting it to the deploying principal here makes sre-sync work with no manual
# step; without it you get a one-time 403 (sre-sync detects that and prints this exact assignment as
# a fallback). var.sre_agent_admin_object_id lets you grant it to a different operator/group instead.
resource "azurerm_role_assignment" "sre_admin_operator" {
  count                = local.sre
  scope                = azapi_resource.sre_agent[0].id
  role_definition_name = "SRE Agent Administrator"
  principal_id         = var.sre_agent_admin_object_id != "" ? var.sre_agent_admin_object_id : data.azurerm_client_config.current.object_id
}

# ── Data connectors: the "Logs" source ────────────────────────────
# These tell the agent WHICH Application Insights + Log Analytics to query, so the skills' KQL can
# actually run (without them the agent knows the resource group but has no telemetry data source —
# the portal shows "Logs: Not configured"). ARM sub-resources; identity = "system" means they query
# as the agent's SYSTEM-ASSIGNED identity, so that identity needs its own reader roles (below) —
# the UAMI roles above cover the knowledge graph, these cover the log/metric connectors.
locals {
  # Always defined (references resources that always exist); the for_each below gates creation on
  # local.sre, which keeps the conditional out of the map itself (Terraform requires both branches
  # of a ?: to share a type, which an empty {} can't).
  sre_connectors = {
    app-insights = {
      dataConnectorType = "AppInsights"
      dataSource        = azurerm_application_insights.appi.id
      extendedProperties = {
        armResourceId = azurerm_application_insights.appi.id
        resource      = { name = azurerm_application_insights.appi.name }
        appId         = azurerm_application_insights.appi.app_id
      }
      identity = "system"
    }
    log-analytics = {
      dataConnectorType = "LogAnalytics"
      dataSource        = azurerm_log_analytics_workspace.law.id
      extendedProperties = {
        armResourceId = azurerm_log_analytics_workspace.law.id
        resource      = { name = azurerm_log_analytics_workspace.law.name }
      }
      identity = "system"
    }
  }

  # The agent's system-assigned identity (used by the connectors above; distinct from the UAMI).
  sre_system_principal_id = local.sre == 1 ? azapi_resource.sre_agent[0].identity[0].principal_id : null
}

resource "azapi_resource" "sre_connector" {
  # for-comprehension (not a ?: ) so it's always a map — empty when the agent is disabled — which
  # sidesteps the "inconsistent conditional result types" the two differently-shaped values cause.
  for_each                  = { for k, v in local.sre_connectors : k => v if local.sre == 1 }
  schema_validation_enabled = false
  type                      = "Microsoft.App/agents/connectors@2025-05-01-preview"
  name                      = each.key
  parent_id                 = azapi_resource.sre_agent[0].id
  body                      = { properties = each.value }

  # A connector PUT triggers a background extension install that can take 10-30 min. The timeout
  # just caps how long `terraform apply` waits — a timeout is SAFE, the connector finishes
  # provisioning in the background and the next apply reconciles state.
  timeouts {
    create = "15m"
    update = "15m"
    delete = "15m"
  }
}

# Reader roles for the SYSTEM-ASSIGNED identity so the connectors can query logs/metrics.
resource "azurerm_role_assignment" "sre_system_la_reader" {
  count                = local.sre
  scope                = azurerm_resource_group.rg.id
  role_definition_name = "Log Analytics Reader"
  principal_id         = local.sre_system_principal_id
}

resource "azurerm_role_assignment" "sre_system_monitoring_reader" {
  count                = local.sre
  scope                = azurerm_resource_group.rg.id
  role_definition_name = "Monitoring Reader"
  principal_id         = local.sre_system_principal_id
}

resource "azurerm_role_assignment" "sre_system_reader" {
  count                = local.sre
  scope                = azurerm_resource_group.rg.id
  role_definition_name = "Reader"
  principal_id         = local.sre_system_principal_id
}

# ─────────────────────────────────────────────────────────────────────────────
#  Phase 5 — alert rules routed to the agent
# ─────────────────────────────────────────────────────────────────────────────
#  Log-search alerts over the app's Application Insights, firing on the custom metrics/events
#  emitted by SreDiagnosticsPublisher / SnapshotService (Phase 0). These DETECT; the agent
#  DIAGNOSES — keeping detection in cheap alert rules (not agent polling) is the biggest cost
#  lever in the plan.
#
#  KQL note: these query the classic customMetrics/customEvents schema on the App Insights
#  component. Validate each rule against real telemetry once it is flowing (Phase 0 must be
#  deployed and a snapshot must have run) — table/column shapes are the one thing that can't be
#  checked at plan time.
# ─────────────────────────────────────────────────────────────────────────────

resource "azurerm_monitor_action_group" "sre" {
  count               = local.sre
  name                = "ag-sre-${local.base}"
  resource_group_name = azurerm_resource_group.sre[0].name
  short_name          = "srealert"
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

locals {
  # Each alert: a KQL query returning an "AggregatedValue" column, a threshold, and a severity.
  #
  # This App Insights is WORKSPACE-BASED, so telemetry lands in the Log Analytics workspace under the
  # App* tables (AppMetrics/AppEvents) with the workspace schema — the classic customMetrics/
  # customEvents tables are EMPTY. Verified against live data 2026-07. So the rules scope to the
  # workspace (see `scopes` below) and query App* with the workspace columns: custom-metric values
  # arrive as an aggregate (Min/Max/Sum), and event fields live under Properties/Measurements.
  sre_alerts = {
    snapshot_stale = {
      severity    = 1
      description = "Snapshot data is stale (>26h). The 12h snapshot job has likely stopped; the dashboard is serving old numbers."
      query       = <<-KQL
        AppMetrics
        | where Name == "ghcp.snapshot.age_hours"
        | summarize AggregatedValue = max(Max) by bin(TimeGenerated, 15m)
      KQL
      operator    = "GreaterThan"
      threshold   = 26
    }
    token_unresolved = {
      severity    = 1
      description = "The GitHub PAT Key Vault reference did not resolve (token_resolved=0). GitHub calls will 401 until fixed."
      query       = <<-KQL
        AppMetrics
        | where Name == "ghcp.github.token_resolved"
        | summarize AggregatedValue = min(Min) by bin(TimeGenerated, 15m)
      KQL
      operator    = "LessThan"
      threshold   = 1
    }
    snapshot_failed = {
      severity    = 2
      description = "A snapshot run failed (SnapshotFailed event). See the event's error property for the cause."
      query       = <<-KQL
        AppEvents
        | where Name == "SnapshotFailed"
        | summarize AggregatedValue = count() by bin(TimeGenerated, 15m)
      KQL
      operator    = "GreaterThan"
      threshold   = 0
    }
    zero_rows = {
      severity    = 2
      description = "A snapshot completed but wrote 0 rows — likely an empty GitHub user list (bad enterprise slug or PAT scope)."
      query       = <<-KQL
        AppEvents
        | where Name == "SnapshotRunCompleted"
        | extend rows = toreal(Measurements["rowsWritten"])
        | where rows == 0
        | summarize AggregatedValue = count() by bin(TimeGenerated, 30m)
      KQL
      operator    = "GreaterThan"
      threshold   = 0
    }
    rate_limit_low = {
      severity    = 3
      description = "GitHub rate-limit remaining is low (<200). The per-user snapshot calls may start getting throttled."
      query       = <<-KQL
        AppMetrics
        | where Name == "ghcp.github.rate_limit_remaining"
        | summarize AggregatedValue = min(Min) by bin(TimeGenerated, 15m)
      KQL
      operator    = "LessThan"
      threshold   = 200
    }
    pending_migrations = {
      severity    = 2
      description = "Schema migrations have been pending for an extended period — the DDL grant may be missing (system_assigned mode)."
      query       = <<-KQL
        AppMetrics
        | where Name == "ghcp.db.pending_migrations"
        | summarize AggregatedValue = max(Max) by bin(TimeGenerated, 15m)
      KQL
      operator    = "GreaterThan"
      threshold   = 0
    }
  }
}

resource "azurerm_monitor_scheduled_query_rules_alert_v2" "sre" {
  for_each = local.sre == 1 ? local.sre_alerts : {}

  name                = "alert-sre-${each.key}-${local.base}"
  resource_group_name = azurerm_resource_group.sre[0].name
  location            = var.sre_agent_location
  description         = each.value.description
  severity            = each.value.severity
  # Workspace-based App Insights → query the Log Analytics workspace (App* tables), not the AI
  # resource (whose classic customMetrics/customEvents tables are empty). See the sre_alerts note.
  scopes = [azurerm_log_analytics_workspace.law.id]
  tags   = var.tags

  evaluation_frequency = "PT15M"
  window_duration      = "PT1H"

  criteria {
    query                   = each.value.query
    time_aggregation_method = "Maximum"
    metric_measure_column   = "AggregatedValue"
    operator                = each.value.operator
    threshold               = each.value.threshold

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.sre[0].id]
  }
}

# ── Outputs ───────────────────────────────────────────────────────
output "sre_agent_name" {
  value       = local.sre == 1 ? azapi_resource.sre_agent[0].name : null
  description = "Name of the SRE Agent (empty when disabled). Open it at https://sre.azure.com."
}

output "sre_agent_principal_id" {
  value       = local.sre_principal_id
  description = "The agent identity's principal ID — grant it db_datareader + VIEW DATABASE STATE via deploy.ps1 -Task grant-sre-sql."
}

output "sre_agent_id" {
  value       = local.sre == 1 ? azapi_resource.sre_agent[0].id : null
  description = "ARM resource ID of the agent — used by deploy.ps1 -Task sre-sync to resolve the data-plane endpoint."
}
