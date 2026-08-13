using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Pages
{
    /// <summary>
    /// Ceilings, in the order they bite: the included ALLOWANCE POOL first, then BUDGETS.
    ///
    /// The two answer one question — "are we going to overspend, and when" — from opposite ends,
    /// and the ordering is the point: budget spend cannot begin until the pool is empty, so a budget
    /// reading comfortably green while the pool is days from exhaustion is not a contradiction, it
    /// is cause and effect. Splitting them across two pages would leave the reader to join that up.
    ///
    /// The pool is the app's only LEADING indicator; everything else reports spend already incurred.
    /// The dashboard shows a summary of it and links here for the burn-down and contributors.
    ///
    /// Budgets themselves remain GOVERNED IN GITHUB: read-only here, alert emails come from GitHub.
    /// The dashboard shows only exceptions at scale; this page is where "show me everything,
    /// including the watch-level ones" lives.
    /// </summary>
    public class BudgetsModel : PageModel
    {
        private readonly UsageQueryService _query;
        private readonly IUserScopeResolver _scopeResolver;
        private readonly BudgetService _budgets;
        private readonly IConfiguration _config;

        public BudgetsModel(UsageQueryService query, IUserScopeResolver scopeResolver, BudgetService budgets, IConfiguration config)
        {
            _query = query;
            _scopeResolver = scopeResolver;
            _budgets = budgets;
            _config = config;
        }

        [BindProperty(SupportsGet = true)] public string? Period { get; set; }   // "YYYY-MM"
        [BindProperty(SupportsGet = true)] public long? Ent { get; set; }        // enterprise filter
        [BindProperty(SupportsGet = true)] public string Status { get; set; } = "all"; // all | over | critical | warn | ok
        [BindProperty(SupportsGet = true)] public string? Q { get; set; }        // name search

        public int Year { get; private set; }
        public int Month { get; private set; }
        public bool SeesAll { get; private set; }
        public IReadOnlyList<UsageQueryService.MonthOption> AvailableMonths { get; private set; } = Array.Empty<UsageQueryService.MonthOption>();
        public IReadOnlyList<UsageQueryService.EnterpriseOption> VisibleEnterprises { get; private set; } = Array.Empty<UsageQueryService.EnterpriseOption>();
        public bool MultiEnterprise => VisibleEnterprises.Count > 1;

        public IReadOnlyList<BudgetService.BudgetStatus> AllBudgets { get; private set; } = Array.Empty<BudgetService.BudgetStatus>();
        /// <summary>Filtered rows, grouped by enterprise (group order = enterprise id; rows worst first).</summary>
        public IReadOnlyList<(long EnterpriseId, string? EnterpriseName, IReadOnlyList<BudgetService.BudgetStatus> Rows)> Groups { get; private set; }
            = Array.Empty<(long, string?, IReadOnlyList<BudgetService.BudgetStatus>)>();
        public int MatchCount { get; private set; }

        public int OverCount { get; private set; }
        public int NearLimitCount { get; private set; }
        public int WatchCount { get; private set; }
        public int OnTrackCount { get; private set; }

        // ── Allowance pool ──────────────────────────────────────────────────────────────────────

        /// <summary>Included credits per seat by plan — see <see cref="IndexModel.DefaultCreditsPerSeatByPlan"/>.</summary>
        private IReadOnlyDictionary<string, decimal> CreditsPerSeatByPlan()
        {
            var section = _config.GetSection("Allowance:CreditsPerSeat");
            if (!section.Exists()) return IndexModel.DefaultCreditsPerSeatByPlan;
            var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in section.GetChildren())
                if (decimal.TryParse(child.Value, out var v) && v > 0) map[child.Key] = v;
            return map;
        }

        /// <summary>One pool per visible enterprise, with its burn-down curve and contributors.
        /// <see cref="Contributors"/> is empty in overview mode, where it is not rendered.</summary>
        public sealed record PoolDetail(
            UsageQueryService.AllowancePool Pool,
            IReadOnlyList<UsageQueryService.BurnDownPoint> BurnDown,
            IReadOnlyList<UsageQueryService.PoolContributor> Contributors);

        public IReadOnlyList<PoolDetail> Pools { get; private set; } = Array.Empty<PoolDetail>();

        /// <summary>
        /// TRUE when no single enterprise is selected and more than one is visible — the page then
        /// shows one summary ROW per enterprise instead of a full chart each.
        ///
        /// Ten enterprises rendered at full detail is not a long page, it is a page with no answer to
        /// "which one should I look at?", which is the only question a reader with ten of them has.
        /// The dashboard already works this way for budgets ("the two red ones, not a wall of dozens
        /// of green meters"); this brings the allowance surface into line.
        ///
        /// A single-enterprise deployment NEVER sees the overview: a one-row summary you must click
        /// through would be pure friction.
        /// </summary>
        public bool IsOverview { get; private set; }

        /// <summary>Per-enterprise budget counts for the overview strip. Derived from budgets already
        /// loaded, so the overview costs no extra query.</summary>
        public sealed record EnterpriseBudgetSummary(
            long EnterpriseId, string? EnterpriseName, int Over, int Critical, int Warn, int Ok)
        {
            public int Total => Over + Critical + Warn + Ok;
            public int NeedsAttention => Over + Critical;
        }

        public IReadOnlyList<EnterpriseBudgetSummary> BudgetSummaries { get; private set; }
            = Array.Empty<EnterpriseBudgetSummary>();

        /// <summary>
        /// The selected month is not the current one, so there is no pool to show.
        ///
        /// Rendered as an explanation rather than an absent section: a block that silently vanishes
        /// when you change the month picker reads as a bug. Seat counts carry no history before this
        /// feature, and a burn-down of a closed month has nothing left to project.
        /// </summary>
        public bool PoolIsPastMonth { get; private set; }

        /// <summary>Billable overage for a past month — what the pool question reduces to once the
        /// month is closed. Any net spend at all means the allowance was exhausted.</summary>
        public decimal PastMonthBillable { get; private set; }

        /// <summary>The caller lacks enterprise-grain read, so the pool section is absent entirely
        /// (not empty) — same gate as the organization rollup.</summary>
        public bool CanSeePool { get; private set; }

        public string PeriodValue => $"{Year:D4}-{Month:D2}";
        public string MonthLabel => new DateTime(Year, Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        public static string FormatMonth(int year, int month) => new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        private static int SeverityRank(string level) => level switch { "over" => 0, "critical" => 1, "warn" => 2, _ => 3 };

        public async Task OnGetAsync(CancellationToken ct)
        {
            var scope = await _scopeResolver.ResolveAsync(User, ct);
            SeesAll = scope.SeesAll;

            VisibleEnterprises = await _query.GetVisibleEnterprisesAsync(scope, ct);
            if (Ent is long entFilter && VisibleEnterprises.Any(e => e.Id == entFilter))
                scope = scope with { EnterpriseFilter = entFilter };
            else
                Ent = null;

            AvailableMonths = await _query.GetAvailableMonthsAsync(scope, ct);
            var now = DateTime.UtcNow;
            (Year, Month) = (now.Year, now.Month);
            if (!string.IsNullOrWhiteSpace(Period) &&
                int.TryParse(Period.Split('-').ElementAtOrDefault(0), out var y) &&
                int.TryParse(Period.Split('-').ElementAtOrDefault(1), out var m) && m is >= 1 and <= 12)
            {
                (Year, Month) = (y, m);
            }
            else if (AvailableMonths.Count > 0)
            {
                (Year, Month) = (AvailableMonths[0].Year, AvailableMonths[0].Month);
            }

            // Overview whenever the reader is looking at several enterprises at once. Ent is already
            // validated against VisibleEnterprises above, so this cannot be forced by a hand-edited URL.
            IsOverview = Ent is null && VisibleEnterprises.Count > 1;

            // ── Allowance pool, above the budgets it precedes ───────────────────────────────────
            CanSeePool = scope.HasEnterpriseRead;
            if (CanSeePool)
            {
                var pools = await _query.GetAllowancePoolsAsync(Year, Month, scope, CreditsPerSeatByPlan(), now, ct);
                PoolIsPastMonth = pools.Count == 0 && (Year != now.Year || Month != now.Month);
                if (PoolIsPastMonth)
                {
                    // A closed month's pool question reduces to one number: did anything become
                    // billable? Net spend above zero means the allowance ran out.
                    PastMonthBillable = await _query.GetMonthTotalAsync(Year, Month, scope, ct);
                }

                // ONE query for every enterprise's curve — the overview draws a sparkline per row,
                // and a reader granted ten enterprises must not cost ten round trips for one screen.
                var curves = await _query.GetAllowanceBurnDownsAsync(
                    pools.Select(p => p.EnterpriseId), Year, Month, scope, ct);

                var details = new List<PoolDetail>();
                foreach (var p in pools)
                {
                    // Contributors are a detail-mode concern: a ranked table per enterprise is
                    // exactly the wall of content the overview exists to avoid.
                    var contributors = IsOverview
                        ? Array.Empty<UsageQueryService.PoolContributor>()
                        : await _query.GetPoolContributorsAsync(p.EnterpriseId, Year, Month, scope, ct: ct);

                    details.Add(new PoolDetail(
                        p,
                        curves.TryGetValue(p.EnterpriseId, out var pts) ? pts : Array.Empty<UsageQueryService.BurnDownPoint>(),
                        contributors));
                }

                // Worst first. The reader's question is "which one needs me", so an alphabetical
                // list would bury the answer among the healthy ones.
                Pools = details
                    .OrderBy(d => d.Pool.Level switch { "over" => 0, "critical" => 1, _ => 2 })
                    .ThenByDescending(d => d.Pool.PctUsed)
                    .ThenBy(d => d.Pool.EnterpriseName)
                    .ToList();
            }

            AllBudgets = await _budgets.GetStatusesAsync(scope, Year, Month, ct);
            OverCount = AllBudgets.Count(b => b.Level == "over");
            NearLimitCount = AllBudgets.Count(b => b.Level == "critical");
            WatchCount = AllBudgets.Count(b => b.Level == "warn");
            OnTrackCount = AllBudgets.Count(b => b.Level == "ok");

            if (Status is not ("all" or "over" or "critical" or "warn" or "ok")) Status = "all";
            IEnumerable<BudgetService.BudgetStatus> filtered = AllBudgets;
            if (Status != "all") filtered = filtered.Where(b => b.Level == Status);
            var term = Q?.Trim();
            if (!string.IsNullOrEmpty(term))
            {
                filtered = filtered.Where(b =>
                    (b.CostCenterName ?? b.CostCenterId).Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (b.EnterpriseName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            // Per-enterprise counts for the overview strip — derived from budgets already loaded, so
            // the overview costs nothing extra. Worst first, same reasoning as the pools above.
            BudgetSummaries = AllBudgets
                .GroupBy(b => new { b.EnterpriseId, b.EnterpriseName })
                .Select(g => new EnterpriseBudgetSummary(
                    g.Key.EnterpriseId, g.Key.EnterpriseName,
                    g.Count(b => b.Level == "over"),
                    g.Count(b => b.Level == "critical"),
                    g.Count(b => b.Level == "warn"),
                    g.Count(b => b.Level == "ok")))
                .OrderByDescending(s => s.Over)
                .ThenByDescending(s => s.Critical)
                .ThenBy(s => s.EnterpriseName)
                .ToList();

            var rows = filtered.ToList();
            MatchCount = rows.Count;
            Groups = rows
                .GroupBy(b => new { b.EnterpriseId, b.EnterpriseName })
                .OrderBy(g => g.Key.EnterpriseId)
                .Select(g => (g.Key.EnterpriseId, g.Key.EnterpriseName,
                    (IReadOnlyList<BudgetService.BudgetStatus>)g
                        // The enterprise-wide budget is the roll-up ceiling, not a peer of the
                        // cost-center budgets — pin it first instead of interleaving by severity.
                        .OrderBy(b => b.IsOrg ? 0 : 1)
                        .ThenBy(b => SeverityRank(b.Level))
                        .ThenByDescending(b => b.Pct)
                        .ToList()))
                .ToList();
        }
    }
}
