using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Read-only view over budgets that are GOVERNED IN GITHUB (cost-center / enterprise budgets) and
    /// snapshotted to the DB by the snapshot job. This app never creates or edits budgets, and does not
    /// send alerts — GitHub owns budget configuration and alert emails. <see cref="GetStatusesAsync"/>
    /// compares each budget to the current month's actual net spend within the viewer's scope and
    /// returns a presentational status (on track / near limit / over) for the dashboard. Every budget
    /// is enterprise-qualified: each enterprise has its own org-wide budget row, and cost-center
    /// budgets only match cost centers of the SAME enterprise.
    /// </summary>
    public sealed class BudgetService
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly UsageQueryService _query;

        // Presentational-only thresholds for the dashboard meter (GitHub owns real alerting).
        private const int WarnPct = 75;
        private const int CriticalPct = 90;

        public BudgetService(IDbContextFactory<BillingDbContext> dbFactory, UsageQueryService query)
        {
            _dbFactory = dbFactory;
            _query = query;
        }

        public sealed record BudgetStatus(
            string Scope, string CostCenterId, string? CostCenterName, decimal Amount, decimal Actual,
            long EnterpriseId = 0, string? EnterpriseName = null,
            string? EntityName = null, bool PreventFurtherUsage = false)
        {
            public double Pct => Amount > 0 ? (double)(Actual / Amount) * 100.0 : 0;
            /// <summary>ok | warn | critical | over (presentational only)</summary>
            public string Level =>
                Amount <= 0 ? "ok"
                : Pct >= 100 ? "over"
                : Pct >= CriticalPct ? "critical"
                : Pct >= WarnPct ? "warn"
                : "ok";
            public decimal Remaining => Amount - Actual;
            public bool IsOrg => Scope == BudgetScopes.Org;
            public bool IsOrganization => Scope == BudgetScopes.Organization;

            /// <summary>What this budget targets: cost-centre name, organization name, or the
            /// enterprise itself.</summary>
            public string Label => Scope switch
            {
                BudgetScopes.Org => EnterpriseName ?? "Enterprise-wide",
                BudgetScopes.Organization => EntityName ?? "—",
                _ => CostCenterName ?? CostCenterId,
            };
        }

        /// <summary>Budgets applicable to the viewer, with the current month's actual spend + status.
        /// Ordered by enterprise, org budget first within each — the dashboard groups on that.</summary>
        public async Task<IReadOnlyList<BudgetStatus>> GetStatusesAsync(UserScope scope, int year, int month, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var budgetsQuery = db.BudgetSnapshots.AsQueryable();
            if (scope.EnterpriseFilter is long entFilter)
                budgetsQuery = budgetsQuery.Where(b => b.EnterpriseId == entFilter);

            // ALLOWLIST, applied in SQL. The snapshot job stores every scope GitHub returns; only
            // those whose actual spend we can genuinely compute are displayed, because a budget
            // rendered without a real utilisation figure is worse than one not shown at all.
            //
            // ORGANIZATION was excluded until OrgUsageSnapshots existed — there was no per-org
            // actual to compare against. It now joins on organization NAME, verified against the
            // live API as an exact match between a budget's `budget_entity_name` and usage's
            // `organizationName` (so no cost-centre-style name-to-id resolution is needed here).
            //
            // Still excluded: USER (computable, but the access policy for personal spending limits
            // is undecided) and MULTI_USER_CUSTOMER (semantics unestablished). Widening this set is
            // a deliberate act, not a side effect of storing a scope.
            budgetsQuery = budgetsQuery.Where(b => BudgetScopes.Displayable.Contains(b.Scope));

            // Scopes that cannot be narrowed by the viewer's scope are admin-only — organization
            // actuals come from a table with no cost-centre column, so there is nothing to filter
            // on and a manager would otherwise see organizations they have no grant for.
            if (!scope.SeesAll)
                budgetsQuery = budgetsQuery.Where(b => !BudgetScopes.AdminOnly.Contains(b.Scope));

            var budgets = await budgetsQuery.ToListAsync(ct);
            if (budgets.Count == 0) return Array.Empty<BudgetStatus>();

            var entNames = await db.Enterprises.ToDictionaryAsync(
                e => e.Id, e => string.IsNullOrWhiteSpace(e.DisplayName) ? e.Slug : e.DisplayName!, ct);

            var ccTotals = await _query.GetCostCenterTotalsAsync(year, month, scope, ct);
            // Actuals keyed by the (enterprise, cost-center) PAIR — a same-id cost center in another
            // enterprise must never satisfy this budget's actuals.
            var byCc = ccTotals.Where(c => c.CostCenterId != null)
                .ToDictionary(c => (c.EnterpriseId, c.CostCenterId!.ToLowerInvariant()), c => c.NetAmount);
            var orgTotals = ccTotals.GroupBy(c => c.EnterpriseId)
                .ToDictionary(g => g.Key, g => g.Sum(c => c.NetAmount));

            var grantedPairs = scope.CostCenters
                .Select(p => (p.EnterpriseId, p.CostCenterId.ToLowerInvariant()))
                .ToHashSet();

            // Organization actuals come from a DIFFERENT table — the per-user usage report carries no
            // organization at all. Only loaded when an organization budget is actually present, so
            // the common case pays nothing for this. Keyed by (enterprise, lower-cased org name):
            // the join is an exact match on the live API, but two enterprises can each have an
            // organization of the same name and their spend must never be pooled.
            var byOrg = new Dictionary<(long, string), decimal>();
            if (budgets.Any(b => b.Scope == BudgetScopes.Organization))
            {
                var orgQuery = db.OrgUsageSnapshots.Where(o => o.Year == year && o.Month == month);
                if (scope.EnterpriseFilter is long orgEntFilter)
                    orgQuery = orgQuery.Where(o => o.EnterpriseId == orgEntFilter);

                byOrg = (await orgQuery
                        .Where(o => o.OrganizationName != null)
                        .GroupBy(o => new { o.EnterpriseId, o.OrganizationName })
                        .Select(g => new { g.Key.EnterpriseId, g.Key.OrganizationName, Net = g.Sum(x => x.NetAmount) })
                        .ToListAsync(ct))
                    .ToDictionary(x => (x.EnterpriseId, x.OrganizationName!.ToLowerInvariant()), x => x.Net);
            }

            var result = new List<BudgetStatus>();
            foreach (var b in budgets
                .OrderBy(b => b.EnterpriseId)
                .ThenBy(b => b.Scope == BudgetScopes.Org ? 0 : 1)
                .ThenBy(b => b.CostCenterName ?? b.CostCenterId))
            {
                var entName = entNames.GetValueOrDefault(b.EnterpriseId);
                if (b.Scope == BudgetScopes.Org)
                {
                    if (!scope.SeesAll) continue; // managers don't see enterprise-wide budgets
                    var actualOrg = orgTotals.GetValueOrDefault(b.EnterpriseId, 0m);
                    result.Add(new BudgetStatus(b.Scope, b.CostCenterId, b.CostCenterName, b.Amount, actualOrg,
                        b.EnterpriseId, entName, b.EntityName, b.PreventFurtherUsage));
                }
                else if (b.Scope == BudgetScopes.Organization)
                {
                    // Already filtered to admins in SQL above; belt-and-braces so a future caller
                    // reaching this loop by another route cannot leak organization spend.
                    if (!scope.SeesAll) continue;
                    var orgKey = (b.EnterpriseId, (b.EntityName ?? "").ToLowerInvariant());
                    var actualByOrg = byOrg.TryGetValue(orgKey, out var ov) ? ov : 0m;
                    result.Add(new BudgetStatus(b.Scope, b.CostCenterId, b.CostCenterName, b.Amount, actualByOrg,
                        b.EnterpriseId, entName, b.EntityName, b.PreventFurtherUsage));
                }
                else
                {
                    if (!(scope.SeesAll || grantedPairs.Contains((b.EnterpriseId, b.CostCenterId.ToLowerInvariant())))) continue;
                    var actual = byCc.TryGetValue((b.EnterpriseId, b.CostCenterId.ToLowerInvariant()), out var v) ? v : 0m;
                    result.Add(new BudgetStatus(b.Scope, b.CostCenterId, b.CostCenterName, b.Amount, actual,
                        b.EnterpriseId, entName, b.EntityName, b.PreventFurtherUsage));
                }
            }
            return result;
        }
    }
}
