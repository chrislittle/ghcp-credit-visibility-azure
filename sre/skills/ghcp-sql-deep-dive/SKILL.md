# GHCP Azure SQL deep dive

## Executing SQL from this agent — read this FIRST

Your sandbox has **no `sqlcmd`, no ODBC/pymssql drivers, and pip installs are blocked** — and
`az sql db query` **does not exist** as a command. All of this was proven in a live session that
burned five tool calls discovering it. Do NOT attempt any of those. The read-only SQL grant
(`db_datareader` + `VIEW DATABASE STATE`) exists so a SQL execution tool can use it **when one is
available in your session**; when none is:

1. Answer from telemetry first — **Monitor Workspace Log Query** over `AppMetrics`/`AppEvents`
   (`ghcp.*` series, per-enterprise via `Properties["enterprise"]`) and `AzureMetrics`.
2. `GET /health/diag` on the app (authenticated) returns per-enterprise JSON diagnostics.
3. For anything that genuinely needs T-SQL, OUTPUT the exact query below, clearly labeled
   **"run this as the operator"** (Azure Portal query editor, SSMS, or `Invoke-Sqlcmd`) — do not
   attempt to run it yourself.

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
keeps it bounded, but a misconfigured retention setting or a purge that's been failing lets it grow.

**`DailyUsageSnapshots` is the dominant table — size the database around it, not `UsageSnapshots`.**
It holds one row per user/model/sku per DAY (intra-month history), versus one per MONTH in
`UsageSnapshots` — roughly **30x the rows**. A 5,000-user enterprise runs ~25k monthly rows against
~750k daily rows per month; at six months' retention that is single-digit GB, which **can exceed a
2 GB `max_size_gb`**. If a deployment is near the cap, check this table's size FIRST:

```sql
SELECT t.name AS table_name, SUM(p.rows) AS row_count,
       CAST(SUM(a.total_pages) * 8.0 / 1024 AS DECIMAL(10,2)) AS mb
FROM sys.tables t
JOIN sys.indexes i ON t.object_id = i.object_id
JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
JOIN sys.allocation_units a ON p.partition_id = a.container_id
WHERE i.index_id < 2
GROUP BY t.name ORDER BY mb DESC;
```

`OrgUsageSnapshots` (organization/repository attribution) also lives under the DAILY window. It is
much smaller than `DailyUsageSnapshots` — one row per org/repo/sku per day rather than per USER —
but it grows on two axes worth knowing about: repository count, and **backfill**, which walks
backwards a few months per cycle until it reaches the retention floor. A newly deployed enterprise
therefore shows this table growing for several runs and then stopping. That is the backfill
completing, not a leak.

Two retention knobs now, not one: `Retention__Months` (monthly) and `Retention__DailyMonths`, which
defaults to the monthly value and clamps to the same floor of 3. Shortening the daily window is the
correct lever for a size problem — it reclaims ~30x more per month than shortening the monthly one,
and costs only intra-month detail rather than the trend history finance depends on.

**Retention also caps backfill depth.** Org backfill never fetches past the retention floor, because
the purge would delete those rows on the same run. Raising `Retention__DailyMonths` (up to GitHub's
24-month limit) is what deepens org history; the two settings cannot be reasoned about separately. Note it is not
in `infra/appservice.tf`, so it has to be set as an app setting directly.

Raising `max_size_gb` is usually the better answer regardless: storage is roughly $0.115/GB/month, so
a few GB is cents, while purged history is expensive to get back. GitHub does serve a rolling
**24-month** window (its usage endpoints take optional `year`/`month`/`day`), so a purge inside that
window is recoverable in principle — but **only by a backfill job the app does not have**, and not at
all once the data ages past two years. Buy the storage rather than the recovery project.

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
