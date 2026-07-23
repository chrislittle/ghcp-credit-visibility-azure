# GHCP data-integrity checks

This is a billing/reporting app: **wrong numbers are worse than downtime**, and nothing in HTTP
monitoring catches them. A snapshot can "succeed" and still write garbage. Run these when data is
present but suspicious — or on a schedule, proactively.

> If asked **which specialist** should investigate a "numbers look wrong" case, recommend the
> operator invoke **`/agent ghcp_data_auditor`** (the data-correctness subagent) — name that agent,
> not this skill. This skill is the background playbook the specialist (and you) work from.

Signals available without DB access (from Phase 0 telemetry). This App Insights is WORKSPACE-BASED,
so run the KQL below with the **"Monitor Workspace Log Query" tool** (connector-backed, reliable —
not "Resource Log Query", which 403s) against the `App*` tables (`AppMetrics`/`AppEvents`); the
classic `customMetrics`/`customEvents` tables are empty here. See `ghcp-snapshot-pipeline` for the
exact `az` fallback (workspace **GUID**, not name) if you shell out:

```kusto
AppMetrics
| where Name in ("ghcp.data.costcenters", "ghcp.data.budgets", "ghcp.data.months_with_data")
| summarize arg_max(TimeGenerated, Max) by Name
```

With the read-only SQL grant (`deploy.ps1 -Task grant-sre-sql`), go deeper:

## Check 1 — Month-over-month volume swing

A healthy month tracks close to the previous. A swing beyond ±40% means a partial run or a source
change, not real usage moving that much:

```sql
SELECT Year, Month, COUNT(*) AS rows, COUNT(DISTINCT UserLogin) AS users, SUM(NetAmount) AS net
FROM UsageSnapshots GROUP BY Year, Month ORDER BY Year DESC, Month DESC;
```

## Check 2 — Gap in the trend series (UNRECOVERABLE if real)

**First distinguish a low total from a gap — they are NOT the same:**
- A **low month count on a recently deployed instance is EXPECTED, not a concern.** A brand-new
  deployment legitimately has only 1 month of data (`ghcp.data.months_with_data = 1`), and it grows
  by one each month. Do NOT flag this as a data-integrity problem — say "expected for a new
  deployment" and move on.
- A **GAP** — a missing month *between* two present months (e.g. data for March and May but not
  April) — is the real, high-severity finding. That means the retention purge deleted too much, OR a
  month never got snapshotted. **GitHub's API only serves the current month**, so a gap is gone
  forever.

Snapshots are keyed `Day = 1` for monthly rows. Check for holes in an otherwise contiguous series:

```sql
-- Expect a contiguous run of (Year, Month); any hole is a gap.
SELECT DISTINCT Year, Month FROM UsageSnapshots ORDER BY Year, Month;
```

Cross-check retention config: `Retention__Months` (app setting) with a floor of 3. If someone set it
to 1, the app clamps to 3 — but verify the setting and the actual span agree.

## Check 3 — Orphans and nulls

```sql
-- Usage rows with no cost center (mapping/source problem)
SELECT COUNT(*) FROM UsageSnapshots WHERE CostCenterId IS NULL OR CostCenterId = '';
-- Cost-center directory empty or shrinking => GitHub cost-center API shape changed
SELECT COUNT(*) FROM CostCenterDirectory;
```

A user who appears in `consumed-licenses` (GitHub) but has zero usage rows is either genuinely idle or
was skipped — correlate against the `SnapshotRunCompleted.rowsWritten` for that run.

## Check 4 — Budgets present

Budgets are governed in GitHub and snapshotted read-only. A drop in `BudgetSnapshots` count means
budgets were deleted in GitHub or the scope-mapping broke:

```sql
SELECT Scope, COUNT(*) FROM BudgetSnapshots GROUP BY Scope;
```

## Month-rollover risk window
On the 1st of the month, the new month's snapshot starts fresh (`Day = 1`). Verify a run happened
after 00:00 UTC on the 1st and wrote the new month — a missed rollover shows as "last month has data,
this month is empty" and looks like an outage but is a timing issue.
