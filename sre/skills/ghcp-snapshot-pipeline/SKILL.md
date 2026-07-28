# GHCP snapshot pipeline troubleshooting

The dashboard **never calls GitHub live** — a background job (`SnapshotHostedService`, every 12h)
writes usage into Azure SQL, and the UI only reads the DB. So "the numbers are wrong/old" is almost
always a snapshot-pipeline problem, not a UI problem.

**MULTI-ENTERPRISE: "which enterprise?" is triage question #1.** The app snapshots one or more
GitHub enterprises, listed in the `Enterprises` registry table in the app's database (NOT an app
setting). Each cycle runs the enterprises SEQUENTIALLY with per-enterprise isolation: every enabled
enterprise gets its own `SnapshotRun` row, its own `SnapshotRunCompleted`/`SnapshotFailed` event
(with an `enterprise` property), its own PAT (Key Vault secret named in the registry row), and its
own rate-limit budget. One enterprise failing NEVER aborts the others — so "enterprise B is stale
while A is fine" is a normal, expected failure shape, and a global freshness number can hide it.
Work per enterprise, in this order.

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
and `Measurements` (not `customDimensions`/`customMeasurements`); time → `TimeGenerated`. The
**enterprise dimension** on metrics lands in `Properties["enterprise"]` (the slug).

## Telemetry you have

| Signal (AppMetrics.Name / AppEvents.Name) | Meaning |
|---|---|
| `ghcp.snapshot.age_hours` (dim: enterprise) | Hours since THAT enterprise's last run. Job runs every 12h; **>26h = broken for that enterprise, not slow.** |
| `ghcp.snapshot.rows_written` (dim: enterprise) | Rows that enterprise's last run wrote. **0 on a success = silent failure (bad slug / PAT scope).** |
| `ghcp.github.token_resolved` (dim: enterprise) | 0 = that enterprise's PAT (Key Vault secret from its registry row) did not resolve (check this BEFORE blaming GitHub). Mock enterprises never emit it. |
| `ghcp.github.rate_limit_remaining` (dim: enterprise) | That enterprise's PAT budget left. Limits are PER PAT — one enterprise being throttled says nothing about the others. |
| `ghcp.db.pending_migrations` (no dimension) | Infra-level: schema not fully applied. |
| `SnapshotRunCompleted` (event) | `Measurements`: rowsWritten, rowsPurged, durationMs; `Properties`: instanceId, status, **enterprise**. |
| `SnapshotFailed` (event) | `Properties.error` has the exception message, `Properties.enterprise` names the enterprise — **branch on error (below).** |

The same data is available per enterprise (with slugs, PAT status, enabled flags) at
`GET /health/diag` → `enterprises[]`, and in the admin console's enterprise registry table.

## Step 1 — WHICH enterprises are stale?

```kusto
AppMetrics
| where Name == "ghcp.snapshot.age_hours"
| extend enterprise = tostring(Properties["enterprise"])
| summarize arg_max(TimeGenerated, Max) by enterprise
```

Any enterprise with `Max > 26` → the job has stopped FOR THAT ENTERPRISE. Continue with that
enterprise (there may be several — triage each independently). All `<= 26` → data is current; the
complaint is probably about *correctness*, not freshness — hand off to `ghcp-data-integrity`.
An enterprise **missing from the result entirely** is either disabled in the registry (expected,
not alertable) or newly added and never snapshotted — check the registry in the admin console.

## Step 2 — Did it fail, or succeed with zero rows?

```kusto
AppEvents
| where Name in ("SnapshotRunCompleted", "SnapshotFailed")
| order by TimeGenerated desc
| take 40
| project TimeGenerated, Name, enterprise = tostring(Properties.enterprise),
          error = tostring(Properties.error),
          rows = toreal(Measurements.rowsWritten), instance = tostring(Properties.instanceId)
```

- **Succeeded, rows == 0** → GitHub returned an empty user list for that enterprise. Cause is the
  **enterprise slug in its REGISTRY ROW or that PAT's scope**, not the DB. Check the slug in the
  admin console's enterprise registry (NOT `GitHub__Enterprise` — that app setting only seeds the
  first registry row on upgrade) and the PAT's `read:enterprise` / `manage_billing:enterprise`
  scopes. Do NOT touch SQL.
- **SnapshotFailed** → read `error` (and `enterprise`) and branch:

## Step 3 — Branch on the error string

| Error contains | Real cause | Action |
|---|---|---|
| `PAT for enterprise ... could not be resolved` | That enterprise's Key Vault secret is missing/unreadable | Seed it: `./deploy.ps1 -Task set-pat -Enterprise <slug>`. The secret NAME is in the registry row (`github-pat-<slug>` by convention; row 1 keeps legacy `github-pat`). |
| `401 (Unauthorized)` | That enterprise's PAT expired **or** its Key Vault secret unresolved | Check `ghcp.github.token_resolved` for THAT enterprise FIRST — if 0, it's Key Vault/DNS, not GitHub → hand off to `ghcp-private-network-path`. If 1, the PAT itself is bad/expired. |
| `403` / `429` | Rate limit — for THAT enterprise's PAT only | The client calls the usage API **once per user, sequentially** — at N users that's N calls/run, per enterprise. Report that enterprise's `ghcp.github.rate_limit_remaining` trend; expected pressure at enterprise scale, not a bug. Other enterprises are unaffected (separate PATs, separate circuit breakers). |
| `Simulated outage` | The mock fire-drill enterprise (`demo-broken`) | WORKING AS INTENDED — this enterprise exists to rehearse exactly this alert path. Do not "fix" it; note the drill succeeded. |
| `Login failed for user` | The one-time SQL grant never ran (system_assigned mode) | Output: `./deploy.ps1 -Task grant-sql`. |
| `Cannot open server` / DNS | Private endpoint / DNS path | Hand off to `ghcp-private-network-path`. |
| `Violation of UNIQUE KEY constraint` | Concurrent runs collided | Should NOT happen — the whole multi-enterprise cycle is guarded by `SqlDistributedLease` (`sp_getapplock`). See Step 4; a recurrence means the lease regressed. |

## Step 4 — Confirm the cross-instance lease is holding (MULTI-ENTERPRISE INVARIANT)

The job runs on every App Service instance but a SQL app-lock lets only one instance run the whole
cycle. **The healthy pattern is exactly ONE `SnapshotRunCompleted` PER ENABLED ENTERPRISE per 12h
window** — with 3 enabled enterprises, 3 completions per window (one per enterprise) is CORRECT and
is NOT a lease regression. The regression signal is more than one completion for the SAME
enterprise in the same window:

```kusto
AppEvents
| where Name == "SnapshotRunCompleted" and TimeGenerated > ago(13h)
| extend enterprise = tostring(Properties.enterprise)
| summarize runs = count() by enterprise, bin(TimeGenerated, 12h)
| where runs > 1
```

Any row returned (excluding a deliberate restart) means multiple instances ran concurrently — the
lease regressed. This is the exact condition the unique-key violation in Step 3 comes from.

## Confirm resource health (Azure CLI)

```
az webapp show -g <app-rg> -n <app-name> --query "state"
az webapp log tail -g <app-rg> -n <app-name>          # live container stdout
```

## What NOT to do
- Don't restart the app to "fix" stale data — if the job is failing it'll fail again. Find the cause.
- Don't recommend deleting/recreating SQL for a `Login failed` — it's a missing grant, one T-SQL run.
- Don't treat N completions per window (N = enabled enterprises) as a concurrency bug — that's the
  healthy multi-enterprise pattern. Only same-enterprise duplicates indicate lease regression.
- Don't triage the `demo-broken` mock enterprise as an outage — it fails by design (fire drill).
