# GHCP snapshot pipeline troubleshooting

The dashboard **never calls GitHub live** — a background job (`SnapshotHostedService`, every 12h)
writes usage into Azure SQL, and the UI only reads the DB. So "the numbers are wrong/old" is almost
always a snapshot-pipeline problem, not a UI problem. Work this in order.

## How to query the telemetry (IMPORTANT — tool + schema)

This App Insights is **workspace-based**, so the data lives in the **Log Analytics workspace** under
the `App*` tables — `AppMetrics` and `AppEvents`. The classic `customMetrics` / `customEvents` tables
are **empty here**.

**Use the "Monitor Workspace Log Query" tool** to run the KQL below — it is backed by the Log
Analytics connector and works directly. Do NOT use "Monitor **Resource** Log Query" (it 403s here),
and prefer the tool over shelling out to `az`.

If you must use the CLI as a fallback, the exact invocation is fiddly — get it right or it wastes
attempts:
- `-w` takes the workspace **GUID (customerId)**, NOT the workspace name and NOT its ARM resource ID.
  Get it with: `az monitor log-analytics workspace show -g <app-rg> -n <workspace-name> --query customerId -o tsv`
- there is no `-g` parameter on `az monitor log-analytics query`.
- it needs the extension: `az extension add -n log-analytics`.
- then: `az monitor log-analytics query -w <customerId-GUID> --analytics-query "<KQL>"`

Column mapping vs. classic: metric value → `Max`/`Min`/`Sum` (not `value`); event fields → `Properties`
and `Measurements` (not `customDimensions`/`customMeasurements`); time → `TimeGenerated`.

## Telemetry you have (Phase 0)

| Signal (AppMetrics.Name / AppEvents.Name) | Meaning |
|---|---|
| `ghcp.snapshot.age_hours` | Hours since the last run. Job runs every 12h; **>26h = broken, not slow.** |
| `ghcp.snapshot.rows_written` | Rows the last run wrote. **0 on a success = silent failure.** |
| `ghcp.github.token_resolved` | 0 = the Key Vault PAT reference did not resolve (check this BEFORE blaming GitHub). |
| `ghcp.github.rate_limit_remaining` | GitHub budget left. |
| `SnapshotRunCompleted` (event) | `Measurements`: rowsWritten, rowsPurged, durationMs; `Properties`: instanceId, status. |
| `SnapshotFailed` (event) | `Properties.error` has the exception message — **branch on it (below).** |

## Step 1 — Is data actually stale?

```kusto
AppMetrics
| where Name == "ghcp.snapshot.age_hours"
| summarize arg_max(TimeGenerated, Max)
```

`Max > 26` → the job has stopped. Continue. `Max <= 26` → data is current; the complaint is
probably about *correctness*, not freshness — hand off to `ghcp-data-integrity`.

## Step 2 — Did it fail, or succeed with zero rows?

```kusto
AppEvents
| where Name in ("SnapshotRunCompleted", "SnapshotFailed")
| order by TimeGenerated desc
| take 20
| project TimeGenerated, Name, error = tostring(Properties.error),
          rows = toreal(Measurements.rowsWritten), instance = tostring(Properties.instanceId)
```

- **Succeeded, rows == 0** → GitHub returned an empty user list. Cause is the **enterprise slug or
  PAT scope**, not the DB. Check `GitHub__Enterprise` app setting and the PAT's
  `read:enterprise` / `manage_billing:enterprise` scopes. Do NOT touch SQL.
- **SnapshotFailed** → read `error` and branch:

## Step 3 — Branch on the error string

| Error contains | Real cause | Action |
|---|---|---|
| `401 (Unauthorized)` | PAT expired **or** Key Vault reference unresolved | Check `ghcp.github.token_resolved` FIRST — if 0, it's Key Vault/DNS, not GitHub → hand off to `ghcp-private-network-path`. If 1, the PAT itself is bad/expired. |
| `403` / `429` | Rate limit | The client calls the usage API **once per user, sequentially** — at N users that's N calls/run. Report the `ghcp.github.rate_limit_remaining` trend; this is expected pressure at enterprise scale, not a bug. |
| `Login failed for user` | The one-time SQL grant never ran (system_assigned mode) | Output: `./deploy.ps1 -Task grant-sql`. |
| `Cannot open server` / DNS | Private endpoint / DNS path | Hand off to `ghcp-private-network-path`. |
| `Violation of UNIQUE KEY constraint` | Concurrent runs collided | Should NOT happen — the job is guarded by `SqlDistributedLease` (`sp_getapplock`). See Step 4; a recurrence means the lease regressed. |

## Step 4 — Confirm the cross-instance lease is holding

The job runs on every App Service instance but a SQL app-lock lets only one run per cycle. Under
autoscale you should see exactly ONE completion and the rest skipped:

```kusto
AppEvents
| where Name == "SnapshotRunCompleted" and TimeGenerated > ago(1h)
| summarize runs = count() by bin(TimeGenerated, 12h)
```

More than one `SnapshotRunCompleted` in the same 12h window (excluding a deliberate restart) means
multiple instances ran concurrently — the lease regressed. This is the exact condition the unique
-key violation in Step 3 comes from.

## Confirm resource health (Azure CLI)

```
az webapp show -g <app-rg> -n <app-name> --query "state"
az webapp log tail -g <app-rg> -n <app-name>          # live container stdout
```

## What NOT to do
- Don't restart the app to "fix" stale data — if the job is failing it'll fail again. Find the cause.
- Don't recommend deleting/recreating SQL for a `Login failed` — it's a missing grant, one T-SQL run.
