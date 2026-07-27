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

With the read-only SQL grant (`deploy.ps1 -Task grant-sre-sql`), go deeper:

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

## Check 2 — Gap in the trend series (UNRECOVERABLE if real; judge per enterprise)

**First distinguish a low total from a gap — they are NOT the same:**
- A **short history for a RECENTLY ONBOARDED ENTERPRISE is EXPECTED, not a concern.** A brand-new
  deployment legitimately has 1 month of data, and an enterprise added to the registry last month
  legitimately has 1 month of data even when its siblings have 12. Compare each enterprise's first
  data month against its registry row's `CreatedUtc` before flagging anything. Do NOT flag this as
  a data-integrity problem — say "expected for a newly onboarded enterprise" and move on.
- A **GAP** — a missing month *between* two present months FOR THE SAME ENTERPRISE (e.g. contoso
  has March and May but not April) — is the real, high-severity finding. That means the retention
  purge deleted too much, OR a month never got snapshotted, OR the enterprise was disabled for a
  while (check the registry's Enabled flag history with the operator). **GitHub's API only serves
  the current month**, so a gap is gone forever.

Snapshots are keyed `Day = 1` for monthly rows. Check for holes per enterprise:

```sql
-- Expect a contiguous run of (Year, Month) PER ENTERPRISE; any hole is a gap for that enterprise.
SELECT e.Slug, u.Year, u.Month
FROM (SELECT DISTINCT EnterpriseId, Year, Month FROM UsageSnapshots) u
JOIN Enterprises e ON e.Id = u.EnterpriseId
ORDER BY e.Slug, u.Year, u.Month;
```

Cross-check retention config: `Retention__Months` (app setting) with a floor of 3. If someone set it
to 1, the app clamps to 3 — but verify the setting and the actual span agree.

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

A user who appears in `consumed-licenses` (GitHub) but has zero usage rows is either genuinely idle or
was skipped — correlate against that enterprise's `SnapshotRunCompleted.rowsWritten` for the run.
Remember the same login CAN legitimately appear under two enterprises as two separate row sets —
that is correct billing data, not duplication.

## Check 4 — Budgets present (per enterprise)

Budgets are governed in GitHub and snapshotted read-only. **Expect exactly ONE Org-scope row PER
ENTERPRISE** (count = number of registered enterprises, not 1). A drop in an enterprise's
`BudgetSnapshots` count means budgets were deleted in GitHub or the scope-mapping broke:

```sql
SELECT e.Slug, b.Scope, COUNT(*) AS budgets
FROM BudgetSnapshots b JOIN Enterprises e ON e.Id = b.EnterpriseId
GROUP BY e.Slug, b.Scope;
```

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
