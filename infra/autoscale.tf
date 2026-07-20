# ── Autoscale for the App Service Plan (Standard+ required) ──────
# CPU-based scale-out/in with a floor of autoscale_min so the snapshot
# BackgroundService always has at least one Always-On instance.
resource "azurerm_monitor_autoscale_setting" "plan" {
  name                = "autoscale-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  target_resource_id  = azurerm_service_plan.plan.id
  tags                = var.tags

  profile {
    name = "cpu-based"

    capacity {
      minimum = tostring(var.autoscale_min)
      maximum = tostring(var.autoscale_max)
      default = tostring(var.autoscale_default)
    }

    # Scale OUT when average CPU > 70% over 5 minutes.
    rule {
      metric_trigger {
        metric_name        = "CpuPercentage"
        metric_resource_id = azurerm_service_plan.plan.id
        time_grain         = "PT1M"
        statistic          = "Average"
        time_window        = "PT5M"
        time_aggregation   = "Average"
        operator           = "GreaterThan"
        threshold          = 70
      }
      scale_action {
        direction = "Increase"
        type      = "ChangeCount"
        value     = "1"
        cooldown  = "PT5M"
      }
    }

    # Scale IN when average CPU < 30% over 10 minutes.
    rule {
      metric_trigger {
        metric_name        = "CpuPercentage"
        metric_resource_id = azurerm_service_plan.plan.id
        time_grain         = "PT1M"
        statistic          = "Average"
        time_window        = "PT10M"
        time_aggregation   = "Average"
        operator           = "LessThan"
        threshold          = 30
      }
      scale_action {
        direction = "Decrease"
        type      = "ChangeCount"
        value     = "1"
        cooldown  = "PT10M"
      }
    }
  }
}
