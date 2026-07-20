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
    /// returns a presentational status (on track / near limit / over) for the dashboard.
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

        public sealed record BudgetStatus(string Scope, string CostCenterId, string? CostCenterName, decimal Amount, decimal Actual)
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

        /// <summary>Budgets applicable to the viewer, with the current month's actual spend + status.</summary>
        public async Task<IReadOnlyList<BudgetStatus>> GetStatusesAsync(UserScope scope, int year, int month, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var budgets = await db.BudgetSnapshots.ToListAsync(ct);
            if (budgets.Count == 0) return Array.Empty<BudgetStatus>();

            var ccTotals = await _query.GetCostCenterTotalsAsync(year, month, scope, ct);
            var byCc = ccTotals.Where(c => c.CostCenterId != null)
                .ToDictionary(c => c.CostCenterId!, c => c.NetAmount, StringComparer.OrdinalIgnoreCase);
            var orgTotal = ccTotals.Sum(c => c.NetAmount);

            var result = new List<BudgetStatus>();
            foreach (var b in budgets.OrderBy(b => b.Scope == BudgetScopes.Org ? 0 : 1).ThenBy(b => b.CostCenterName ?? b.CostCenterId))
            {
                if (b.Scope == BudgetScopes.Org)
                {
                    if (!scope.SeesAll) continue; // managers don't see the org-wide budget
                    result.Add(new BudgetStatus(b.Scope, b.CostCenterId, b.CostCenterName, b.Amount, orgTotal));
                }
                else
                {
                    if (!(scope.SeesAll || scope.CostCenterIds.Contains(b.CostCenterId))) continue;
                    var actual = byCc.TryGetValue(b.CostCenterId, out var v) ? v : 0m;
                    result.Add(new BudgetStatus(b.Scope, b.CostCenterId, b.CostCenterName, b.Amount, actual));
                }
            }
            return result;
        }
    }
}
