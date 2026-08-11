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

        public sealed record BudgetStatus(string Scope, string CostCenterId, string? CostCenterName, decimal Amount, decimal Actual, long EnterpriseId = 0, string? EnterpriseName = null)
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
        }

        /// <summary>Budgets applicable to the viewer, with the current month's actual spend + status.
        /// Ordered by enterprise, org budget first within each — the dashboard groups on that.</summary>
        public async Task<IReadOnlyList<BudgetStatus>> GetStatusesAsync(UserScope scope, int year, int month, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var budgetsQuery = db.BudgetSnapshots.AsQueryable();
            if (scope.EnterpriseFilter is long entFilter)
                budgetsQuery = budgetsQuery.Where(b => b.EnterpriseId == entFilter);

            // ALLOWLIST, applied in SQL. The snapshot job stores every scope GitHub returns —
            // including user-scoped (personal spending limits), organization and
            // multi_user_customer. Only scopes whose actual spend we can genuinely compute are
            // displayed: cost-center and enterprise-wide. In particular, user budgets are personal
            // data whose access policy is undecided, and organization budgets cannot be reconciled
            // against actuals until UsageSnapshot carries an Organization dimension — rendering
            // either would mean showing a meter we cannot compute, or data we have not decided who
            // may see. Widening this set is a deliberate act, not a side effect of storing a scope.
            budgetsQuery = budgetsQuery.Where(b => b.Scope == BudgetScopes.Org || b.Scope == BudgetScopes.CostCenter);

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
                    result.Add(new BudgetStatus(b.Scope, b.CostCenterId, b.CostCenterName, b.Amount, actualOrg, b.EnterpriseId, entName));
                }
                else
                {
                    if (!(scope.SeesAll || grantedPairs.Contains((b.EnterpriseId, b.CostCenterId.ToLowerInvariant())))) continue;
                    var actual = byCc.TryGetValue((b.EnterpriseId, b.CostCenterId.ToLowerInvariant()), out var v) ? v : 0m;
                    result.Add(new BudgetStatus(b.Scope, b.CostCenterId, b.CostCenterName, b.Amount, actual, b.EnterpriseId, entName));
                }
            }
            return result;
        }
    }
}
