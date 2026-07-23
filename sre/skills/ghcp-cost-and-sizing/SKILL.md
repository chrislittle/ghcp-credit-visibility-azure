# GHCP cost & sizing

Settle sizing arguments with data, and flag standing artifacts that quietly cost money or widen
exposure.

## Serverless vs provisioned SQL — settle it with real paused hours

The README argues serverless is only cheaper if the DB idles a meaningful fraction of the time
(serverless is billed ~3.4x the provisioned per-vCore-hour rate). Prove it:

```kusto
AzureMetrics
| where ResourceProvider == "MICROSOFT.SQL" and MetricName == "app_cpu_billed"
| summarize billedMinutes = countif(Average > 0), pausedMinutes = countif(Average == 0)
            by bin(TimeGenerated, 1d)
```

If paused time is small (the DB is effectively always-on), recommend a **provisioned** SKU
(`GP_Gen5_2`) — cheaper and predictable. If it idles a large fraction (dev/test, off-hours), keep
serverless (`GP_S_Gen5_1`). Give the crossover in real numbers, not the doc's rule of thumb.

## App Service SKU & autoscale fit

```
az monitor autoscale show -g <app-rg> -n autoscale-<base> --query "profiles[0].capacity"
```

```kusto
AzureMetrics
| where MetricName == "CpuPercentage" and ResourceProvider == "MICROSOFT.WEB"
| summarize avg(Average), max(Maximum) by bin(TimeGenerated, 1h)
```

If it never scales out (CPU consistently < 70%) and min = 1, the plan is right-sized. If it's pinned
at max, the SKU is too small. **Watch for a leftover 2-instance test override** (`autoscale_min = 2`)
that was meant to be temporary — that's a standing ~$73/mo you may not want.

## Standing artifacts worth flagging (quarterly)

1. **Leftover deploy firewall rules.** `deploy.ps1` opens `AllowDeployerIP` for the grant/PAT steps.
   The README calls them harmless to leave, which is true — but they're a standing exception:
   ```
   az sql server firewall-rule list -g <app-rg> -s <sql-server> -o table
   ```
   Flag any `AllowDeployerIP` / temporary rule still present long after deploy.

2. **Public network access.** In the private pattern these should be Disabled. If a temporary-public
   escape hatch (used for a private-KV PAT set) wasn't reverted:
   ```
   az sql server show -g <app-rg> -n <sql-server> --query "publicNetworkAccess"
   az keyvault show -n <kv-name> --query "properties.publicNetworkAccess"
   ```

3. **The optional jump box.** `enable_jumpbox = true` leaves an always-on VM + Bastion running
   (meaningful cost). If the private path is validated, recommend `enable_jumpbox = false`.

## Framing
Deliver a recommendation with a number and the query that produced it, not a generic "consider
scaling." The point of this skill is to replace opinion with the actual utilization data.
