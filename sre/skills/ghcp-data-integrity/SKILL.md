# GHCP data-integrity checks

This is a billing/reporting app: **wrong numbers are worse than downtime**, and nothing in HTTP
monitoring catches them. A snapshot can "succeed" and still write garbage. Run these when data is
present but suspicious — or on a schedule, proactively.

> If asked **which specialist** should investigate a "numbers look wrong" case, recommend the
> operator invoke **`/agent ghcp_data_auditor`** (the data-correctness subagent) — name that agent,
> not this skill. This skill is the background playbook the specialist (and you) work from.

**MULTI-ENTERPRISE: every check below is PER ENTERPRISE.** The app snapshots one or more GitHub
enterprises (the `Enterprises` registry table; each data row carries `EnterpriseId`). Global
aggregates can hide one enterprise cratering inside a stable total — a ±40% drop in one enterprise
is invisible when the other two are flat. Always `GROUP BY EnterpriseId` (join `Enterprises` for
the slug), and judge each enterprise against ITS OWN history.

Signals available without DB access (from the app's diagnostics telemetry). This App Insights is WORKSPACE-BASED,
so run the KQL below with the **"Monitor Workspace Log Query" tool** (connector-backed, reliable —
not "Resource Log Query", which 403s) against the `App*` tables (`AppMetrics`/`AppEvents`); the
classic `customMetrics`/`customEvents` tables are empty here. See `ghcp-snapshot-pipeline` for the
exact `az` fallback (workspace **GUID**, not name) if you shell out:

```kusto
AppMetrics
| where Name in ("ghcp.data.costcenters", "ghcp.data.budgets", "ghcp.data.months_with_data")
| extend enterprise = tostring(Properties["enterprise"])
| summarize arg_max(TimeGenerated, Max) by Name, enterprise
```

The same numbers are in `GET /health/diag` → `enterprises[]`, per enterprise, with slugs.

With the read-only SQL grant (`deploy.ps1 -Task grant-sre-sql`), go deeper — **but only if a SQL
execution tool is available in your session**. Your sandbox has no `sqlcmd`/ODBC and pip installs
are blocked (see `ghcp-sql-deep-dive`, "Executing SQL from this agent"); without a SQL tool,
answer from the telemetry above and `/health/diag`, and output the queries below clearly labeled
for the OPERATOR to run:

## Check 1 — Month-over-month volume swing (per enterprise)

A healthy month tracks close to the previous FOR THE SAME ENTERPRISE. A swing beyond ±40% in one
enterprise means a partial run or a source change there, not real usage moving that much — and it
will NOT show in the global total if the other enterprises are stable:

```sql
SELECT e.Slug, u.Year, u.Month, COUNT(*) AS rows,
       COUNT(DISTINCT u.UserLogin) AS users, SUM(u.NetAmount) AS net
FROM UsageSnapshots u JOIN Enterprises e ON e.Id = u.EnterpriseId
GROUP BY e.Slug, u.Year, u.Month
ORDER BY e.Slug, u.Year DESC, u.Month DESC;
```

## Check 2 — Gap in the trend series (RECOVERABLE within 24 months; judge per enterprise)

**First distinguish a low total from a gap — they are NOT the same:**
- A **short history for a RECENTLY ONBOARDED ENTERPRISE is EXPECTED, not a concern.** A brand-new
  deployment legitimately has 1 month of data, and an enterprise added to the registry last month
  legitimately has 1 month of data even when its siblings have 12. Compare each enterprise's first
  data month against its registry row's `CreatedUtc` before flagging anything. Do NOT flag this as
  a data-integrity problem — say "expected for a newly onboarded enterprise" and move on.
- A **GAP** — a missing month *between* two present months FOR THE SAME ENTERPRISE (e.g. contoso
  has March and May but not April) — is the real finding. That means the retention purge deleted too
  much, OR a month never got snapshotted, OR the enterprise was disabled for a while (check the
  registry's Enabled flag history with the operator).

> **Severity corrected 2026-08-11 (verified against the live API).** Earlier versions of this
> runbook said a gap was "gone forever" because GitHub served only the current month. **That is
> wrong.** The billing usage endpoints accept optional `year`/`month`/`day` and serve a rolling
> **two-year** window; beyond it they fail with *"Time period cannot be more than 2 years in the
> past."* So a gap inside 24 months is **RECOVERABLE IN PRINCIPLE**.
>
> Two caveats keep it serious. **The app has no backfill job** — it only ever requests the current
> month — so recovery is a code change, not an operator action, and there is nothing to run today.
> And a gap older than 24 months **is** permanently gone. Report a gap as: which enterprise, which
> (Year, Month), whether it falls inside the 24-month window, and whether retention config or a
> disabled period explains it. Do not describe it as unrecoverable without checking its age.

`UsageSnapshots` rows are keyed `Day = 1` — one row per user/model/sku per MONTH, rewritten in place
on every run. (Intra-month detail lives in `DailyUsageSnapshots`; see Check 5. Do not look for
per-day rows here.) Check for holes per enterprise:

```sql
-- Expect a contiguous run of (Year, Month) PER ENTERPRISE; any hole is a gap for that enterprise.
SELECT e.Slug, u.Year, u.Month
FROM (SELECT DISTINCT EnterpriseId, Year, Month FROM UsageSnapshots) u
JOIN Enterprises e ON e.Id = u.EnterpriseId
ORDER BY e.Slug, u.Year, u.Month;
```

Cross-check retention config: `Retention__Months` (app setting) with a floor of 3. If someone set it
to 1, the app clamps to 3 — but verify the setting and the actual span agree.

`DailyUsageSnapshots` purges on its own window, `Retention__DailyMonths`, which **falls back to
`Retention__Months` when unset** and clamps to the same floor of 3. Two things to know: it is NOT
wired into Terraform (`infra/appservice.tf` sets only `Retention__Months`), so if it is present at
all somebody added it by hand; and daily history sits under the same 24-month GitHub window as
monthly, so a shortened window discards detail that only a (not-yet-written) backfill could restore,
and nothing at all once it ages past two years.

## Check 3 — Orphans and nulls (per enterprise)

```sql
-- Usage rows with no cost center (mapping/source problem) — broken per enterprise
SELECT e.Slug, COUNT(*) AS orphaned
FROM UsageSnapshots u JOIN Enterprises e ON e.Id = u.EnterpriseId
WHERE u.CostCenterId IS NULL OR u.CostCenterId = ''
GROUP BY e.Slug;
-- Cost-center directory empty or shrinking FOR AN ENTERPRISE => GitHub cost-center API shape
-- changed, or that enterprise's cost centers were deleted in GitHub
SELECT e.Slug, COUNT(*) AS directory_entries
FROM CostCenterDirectory d RIGHT JOIN Enterprises e ON e.Id = d.EnterpriseId
GROUP BY e.Slug;
```

**NULL is meaningful on the billing-detail columns — do not "fix" it.** `UsageSnapshots` carries
`DiscountAmount`, `DiscountQuantity`, `PricePerUnit` and `GrossQuantity` as NULLABLE on purpose:

- `NULL` = **not captured**. Rows written before these columns existed, and any month already frozen
  at that point. GitHub's 24-month window means these COULD be backfilled by a job the app does not
  yet have — and NULL is exactly the marker such a job would select on, which is why defaulting them
  to 0 would be actively destructive.
- `0` = GitHub genuinely reported zero.

Answering "what was July's discount?" with $0 when the truth is "never recorded" is exactly the kind
of confidently-wrong number this app exists to avoid. Expect NULLs on older rows and populated
values on newer ones; the changeover point is when the column was added, not a data fault.

A user who appears in `consumed-licenses` (GitHub) but has zero usage rows is either genuinely idle or
was skipped — correlate against that enterprise's `SnapshotRunCompleted.rowsWritten` for the run.
Remember the same login CAN legitimately appear under two enterprises as two separate row sets —
that is correct billing data, not duplication.

## Check 4 — Budgets present and correctly scoped (per enterprise)

Budgets are governed in GitHub and snapshotted read-only. Rows are keyed by **GitHub's own budget
id** (`GitHubBudgetId`), unique per enterprise.

> **This check changed.** It previously said "expect exactly ONE Org-scope row per enterprise".
> That was built on a bug: every non-`cost_center` scope was stored as `Org`, so four different
> budget kinds collapsed onto one row and the survivor was displayed as the enterprise-wide budget.
> GitHub returns at least five scopes (`cost_center`, `enterprise`, `organization`,
> `multi_user_customer`, `user`) and now each is stored distinctly. **Multiple non-CostCenter rows
> per enterprise are now EXPECTED — do not flag that as duplication.**

```sql
SELECT e.Slug, b.Scope, COUNT(*) AS budgets
FROM BudgetSnapshots b JOIN Enterprises e ON e.Id = b.EnterpriseId
GROUP BY e.Slug, b.Scope
ORDER BY e.Slug, b.Scope;
```

How to read it:

| Scope | Expectation | Displayed in the app? |
|---|---|---|
| `Org` | Enterprise-wide budget. **At most ONE per enterprise** — more than one is a real defect | yes |
| `CostCenter` | One per cost center that has a budget in GitHub | yes |
| `User` | Personal spending limits. Any number, including hundreds | **no** — stored only |
| `Organization` | One per org with a budget | **no** — actuals aren't computable until usage carries an Organization dimension |
| `MultiUserCustomer` | Rare | **no** |
| `Unknown` | **A real signal — investigate** | **no** |

**`Unknown` means GitHub introduced a budget scope this app does not map.** It is stored rather than
guessed so it can never masquerade as the enterprise budget, but it should be reported: the mapping
in `Services/BudgetScopeMapper.cs` needs a new case. Find them with:

```sql
SELECT e.Slug, b.GitHubBudgetId, b.EntityName, b.Amount
FROM BudgetSnapshots b JOIN Enterprises e ON e.Id = b.EnterpriseId
WHERE b.Scope = 'Unknown';
```

Two more things worth surfacing to an operator, neither of which the UI shows today:

```sql
-- HARD STOPS: these BLOCK usage when hit, not merely alert. A developer hitting one is
-- stopped mid-task, which presents to a helpdesk as "Copilot broke", not as a billing issue.
SELECT e.Slug, b.Scope, b.EntityName, b.UserLogin, b.Amount, b.ConsumedAmount
FROM BudgetSnapshots b JOIN Enterprises e ON e.Id = b.EnterpriseId
WHERE b.PreventFurtherUsage = 1
ORDER BY CASE WHEN b.Amount > 0 THEN b.ConsumedAmount / b.Amount ELSE 0 END DESC;

-- More than one enterprise-wide budget for an enterprise IS a defect (see the table above).
SELECT e.Slug, COUNT(*) AS org_budgets
FROM BudgetSnapshots b JOIN Enterprises e ON e.Id = b.EnterpriseId
WHERE b.Scope = 'Org'
GROUP BY e.Slug HAVING COUNT(*) > 1;
```

A budget count dropping to zero for an enterprise still means budgets were deleted in GitHub, that
enterprise's snapshot is failing, or the PAT lost billing permission.

## Check 5 — Intra-month history (`DailyUsageSnapshots`)

A second usage table holds intra-month detail so "which day did spend jump?" is answerable.
`UsageSnapshots` remains authoritative for every monthly figure; this table exists alongside it.

> ### ⚠️ NEVER `SUM(NetAmount)` ON THIS TABLE
> Its rows are **CUMULATIVE month-to-date**, not per-day. A row for the 6th holds everything spent
> from the 1st through the 6th. Summing them inflates the total by roughly the number of days
> observed — a ~30x overstatement on a full month. Per-day spend is derived by DIFFERENCING
> consecutive days, which the app does at read time (`UsageQueryService.ToPerDayRows`).
> If you need a monthly total, query `UsageSnapshots`.

```sql
-- Coverage: how many days were observed per enterprise per month. Gaps mean missed runs.
SELECT e.Slug, d.Year, d.Month, COUNT(DISTINCT d.Day) AS days_observed
FROM DailyUsageSnapshots d JOIN Enterprises e ON e.Id = d.EnterpriseId
GROUP BY e.Slug, d.Year, d.Month
ORDER BY e.Slug, d.Year DESC, d.Month DESC;
```

**Reconciliation — the highest-value check here.** The final cumulative reading of a month must
match that month's total in `UsageSnapshots`. Divergence means one of the two writes is failing:

```sql
WITH last_day AS (
  SELECT EnterpriseId, Year, Month, UserLogin, Model, Sku, NetAmount,
         ROW_NUMBER() OVER (PARTITION BY EnterpriseId, Year, Month, UserLogin, Model, Sku
                            ORDER BY Day DESC) AS rn
  FROM DailyUsageSnapshots
)
SELECT e.Slug, u.Year, u.Month,
       SUM(u.NetAmount) AS monthly_total,
       SUM(l.NetAmount) AS daily_final_total,
       SUM(u.NetAmount) - SUM(l.NetAmount) AS drift
FROM UsageSnapshots u
JOIN Enterprises e ON e.Id = u.EnterpriseId
JOIN last_day l ON l.rn = 1
  AND l.EnterpriseId = u.EnterpriseId AND l.Year = u.Year AND l.Month = u.Month
  AND l.UserLogin = u.UserLogin AND l.Model = u.Model AND l.Sku = u.Sku
GROUP BY e.Slug, u.Year, u.Month
HAVING ABS(SUM(u.NetAmount) - SUM(l.NetAmount)) > 0.01
ORDER BY e.Slug, u.Year DESC, u.Month DESC;
```

Non-zero drift for the CURRENT month is benign if the two writes straddled a snapshot run. Drift on
a CLOSED month is a real finding.

**Expected, do NOT flag:**
- A cumulative value DROPPING day over day — GitHub restated a figure downward. The app preserves
  the resulting negative day rather than hiding it, deliberately.
- Months predating this table having no rows. They render from their monthly total instead.
- Roughly 30x the row count of `UsageSnapshots` — that is the table working as designed. See
  `ghcp-sql-deep-dive` for the storage implications.

## Month-rollover risk window
On the 1st of the month, the new month's snapshot starts fresh (`Day = 1`) — for EVERY enterprise.
Verify a run happened after 00:00 UTC on the 1st and wrote the new month for EACH enabled
enterprise — a missed rollover shows as "last month has data, this month is empty" for that
enterprise and looks like an outage but is a timing issue.

## Known-benign patterns (do not flag)
- The mock fire-drill enterprise (`demo-broken`) has failed runs and no data — by design.
- Mock demo enterprises (`contoso`, `fabrikam`) coexisting with a real enterprise is a supported
  HYBRID deployment, not test-data contamination. Check the registry's UseMockData flag.
- Two enterprises both having a cost center named "Engineering" is expected — cost centers are
  enterprise-qualified by (EnterpriseId, CostCenterId), never merged by name.
- Many `User`-scope budget rows for one enterprise — personal spending limits, stored but not
  displayed. Not duplication.
- `Organization` / `MultiUserCustomer` budgets showing no utilization in the app — those scopes are
  stored but intentionally not rendered, because their actual spend is not computable yet.
- `DailyUsageSnapshots` holding ~30x the rows of `UsageSnapshots`, and a cumulative value that drops
  day over day (a GitHub restatement).
- NULL `DiscountAmount` / `PricePerUnit` on older `UsageSnapshots` rows — "not captured", not a fault.
