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
| `ghcp.snapshot.rows_written` (dim: enterprise) | Rows that enterprise's last run wrote. **0 is NOT a failure signal on its own** — it has three causes, and two are healthy: an empty user list (the incident), users whose usage call returned no items, and users who genuinely consumed nothing this month. This metric cannot separate them, so do not alert on it; read `ghcp.github.licensed_users` for the incident case. Useful as a trend against the same enterprise's own history. |
| `ghcp.github.licensed_users` (dim: enterprise) | Users the consumed-licenses endpoint returned on that enterprise's last run. **0 = the user list came back EMPTY** — wrong slug in the registry row, PAT lost enterprise scope, or licences removed. This is the alertable signal (`empty_user_list`), because unlike rows written it means only that one thing. Not published until an enterprise's first users call completes, so "no series yet" means never-yet-run, not zero. |
| `ghcp.github.token_resolved` (dim: enterprise) | 0 = that enterprise's PAT (Key Vault secret from its registry row) did not resolve (check this BEFORE blaming GitHub). Mock enterprises never emit it. |
| `ghcp.github.rate_limit_remaining` (dim: enterprise) | That enterprise's PAT budget left. Limits are PER PAT — one enterprise being throttled says nothing about the others. |
| `ghcp.data.org_usage_rows` (dim: enterprise) | Rows of organization/repository attribution held for that enterprise. |
| `ghcp.data.org_months_with_data` (dim: enterprise) | Distinct months of organization history — grows as the backfill walks back, then stops. |
| `ghcp.backfill.complete` (dim: enterprise) | 1 = the org backfill watermark reached the retention floor and issues no further calls. **Do NOT alert on 0 by itself** — a newly onboarded enterprise is legitimately 0 for its first few cycles (3 months are fetched per cycle, so a 12-month window needs ~4). Alert only when it stays 0 for far longer than that catch-up should take, which means it stalled. |
| `ghcp.db.pending_migrations` (no dimension) | Infra-level: schema not fully applied. |
| `SnapshotRunCompleted` (event) | `Measurements`: rowsWritten, rowsPurged, durationMs; `Properties`: instanceId, status, **enterprise**. See the counting note below — neither measurement is a plain row count. |
| `SnapshotFailed` (event) | `Properties.error` has the exception message, `Properties.enterprise` names the enterprise — **branch on error (below).** |
| `OrgUsageUnavailable` (event) | Organization usage could not be collected for that enterprise. The run still **succeeded** and per-user data is unaffected — organization attribution is deliberately non-fatal — but the Reports Organization breakdown and organization budgets go stale until it recovers. `Properties.error` has the cause; common ones are the enterprise not being on the general billing usage endpoint, or the PAT losing billing scope. This event exists because the app's own log warning goes to container stdout, not App Insights, so without it the failure would be completely invisible. |

**How rowsWritten and rowsPurged actually count.** Each run writes to TWO usage tables — the monthly
`UsageSnapshots` row and the cumulative `DailyUsageSnapshots` row for today:

- `rowsWritten` counts **usage line items processed**, not database rows. Each item now produces two
  rows, so the database grows about twice as fast as this number suggests. **0 on a success is
  ambiguous and is not alerted on** — see the metric note above; pair it with
  `ghcp.github.licensed_users` to tell an empty user list from an idle enterprise. Comparing it
  against the same enterprise's own history is still the right read for trends.
- A run also fetches **organization usage** (one call per enterprise per month) and backfills a few
  PAST months per cycle. Neither is counted in `rowsWritten`. Both are wrapped in a catch: an
  enterprise not yet on that endpoint, or a PAT lacking scope, logs a warning and the run still
  reports **succeeded** — per-user data, the app's primary output, is already written by then. Look
  for `Organization usage unavailable for '<slug>'` and `Backfilled organization usage for ...`.
- If `Enterprises.UserBackfillEnabled = 1`, the run ALSO fills past months of **per-user** usage —
  one call per user per month, which is why it is opt-in. `rowsWritten` for such a run is legitimately
  many times its normal value; that is the backfill, not a duplication bug. It fills whole months
  only, advancing `UserBackfillOldestYear`/`Month` after each, pauses when the rate limit nears its
  reserve (resuming next cycle), and clears its own flag when the floor is reached. Look for
  `Backfilled per-user usage for '<slug>' <year>-<month>`, `... pausing before ...`, and
  `Per-user backfill complete for '<slug>'`. A run rate-limited by backfill is a config choice, not
  an incident — but if the regular per-user collection starts failing on rate limits, that is one.
- `rowsPurged` is the **combined** total of monthly, daily and organization rows purged. Daily rows are ~30x more
  numerous, so once daily history starts ageing past its retention window this number jumps sharply
  and stays high. **That is expected, not a runaway purge.** The two windows are separate:
  `Retention__Months` and `Retention__DailyMonths` (which defaults to the monthly value, floor 3).

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

- **Succeeded, rows == 0** → **do not conclude anything yet.** Read that enterprise's
  `ghcp.github.licensed_users` before acting:
  - **licensed_users == 0** → GitHub returned an empty user list. Cause is the **enterprise slug in
    its REGISTRY ROW or that PAT's scope**, not the DB. Check the slug in the admin console's
    enterprise registry (NOT `GitHub__Enterprise` — that app setting only seeds the first registry
    row on upgrade) and the PAT's `read:enterprise` / `manage_billing:enterprise` scopes. Do NOT
    touch SQL. This is what the `empty_user_list` alert fires on.
  - **licensed_users > 0** → the users are there and simply consumed nothing this month. **Healthy
    — no action.** Common on lab and low-traffic enterprises. Note that stored history does NOT
    contradict this: the opt-in per-user backfill fills PAST months, so an idle enterprise can hold
    months of history while legitimately writing 0 rows today.
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
