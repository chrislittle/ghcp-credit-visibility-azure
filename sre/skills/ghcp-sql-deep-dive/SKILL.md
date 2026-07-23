# GHCP Azure SQL deep dive

The database is `ghcpvisibility` on `sql-<base>`. It may be **serverless** (`GP_S_*` SKU) with
auto-pause, or **provisioned** (`GP_Gen5_*`). The serverless case changes how you read "the DB is
down," so establish which you're on first:

```
az sql db show -g <app-rg> -s <sql-server> -n ghcpvisibility \
  --query "{sku:currentServiceObjectiveName, autoPause:autoPauseDelay, minCap:minCapacity}"
```

## The #1 false alarm: serverless auto-pause resume

If `autoPause` is set (default 60 min), an idle DB **pauses**. The next connection triggers a resume
that takes ~30–60s and surfaces as SQL error **40613** ("Database is not currently available"). During
that window `/health/ready` legitimately returns 503 and the app's EF retry policy
(`EnableRetryOnFailure`) transparently recovers. **This is not an incident.** Do not page on a single
40613 or a brief readiness blip that self-clears within a minute.

It IS an incident if: readiness stays 503 for >10 min, or 40613s repeat after the resume window.

## Correlate CPU/DTU to snapshot windows

Snapshot runs (every 12h, plus startup) are the main write load. Spikes outside those windows are
worth investigating:

```kusto
AzureMetrics
| where ResourceProvider == "MICROSOFT.SQL" and MetricName in ("cpu_percent", "app_cpu_percent")
| summarize avg(Average), max(Maximum) by bin(TimeGenerated, 5m), MetricName
| order by bin_TimeGenerated desc
```

Cross-reference with `AppEvents | where Name == "SnapshotRunCompleted"` timestamps (workspace-based
App Insights → App* tables in the Log Analytics workspace, not classic customEvents).

## Query Store regressions (needs VIEW DATABASE STATE)

The agent's SQL grant (`deploy.ps1 -Task grant-sre-sql`) includes `VIEW DATABASE STATE`, enabling:

```sql
-- Top regressed queries by CPU in the last day
SELECT TOP 10 qsq.query_id, qt.query_sql_text,
       rs.avg_cpu_time, rs.avg_duration, rs.count_executions
FROM sys.query_store_query qsq
JOIN sys.query_store_query_text qt ON qsq.query_text_id = qt.query_text_id
JOIN sys.query_store_plan qsp ON qsq.query_id = qsp.query_id
JOIN sys.query_store_runtime_stats rs ON qsp.plan_id = rs.plan_id
ORDER BY rs.avg_cpu_time DESC;
```

Also useful live: `sys.dm_exec_requests`, `sys.dm_db_wait_stats`.

## Storage growth vs the cap

`max_size_gb` is small (2 GB by default). The retention purge (`ExecuteDeleteAsync`, one transaction)
keeps it bounded, but a misconfigured `Retention__Months` or a purge that's been failing lets it grow:

```
az sql db show -g <app-rg> -s <sql-server> -n ghcpvisibility --query "maxSizeBytes"
```

```kusto
AppMetrics | where Name == "ghcp.data.months_with_data" | summarize arg_max(TimeGenerated, Max)
```

If months-with-data keeps climbing past the retention window, the purge isn't running — check
`SnapshotRunCompleted.rowsPurged`.

## Cost note
A serverless DB that never pauses is billed ~3.4x the provisioned per-vCore-hour rate. If it's not
idling a meaningful fraction of the time, recommend a provisioned SKU (`GP_Gen5_2`) —
see `ghcp-cost-and-sizing` to settle it with data.
