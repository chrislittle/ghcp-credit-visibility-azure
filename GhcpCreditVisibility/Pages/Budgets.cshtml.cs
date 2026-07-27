using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Pages
{
    /// <summary>
    /// The complete budgets view — every budget in the caller's scope, grouped by enterprise and
    /// filterable by status/enterprise/text. The dashboard deliberately shows only exceptions at
    /// scale (a SeesAll exec can face dozens of budgets across enterprises); this page is where
    /// "show me everything, including the watch-level ones" lives. Budgets remain GOVERNED IN
    /// GITHUB: read-only here, alert emails come from GitHub.
    /// </summary>
    public class BudgetsModel : PageModel
    {
        private readonly UsageQueryService _query;
        private readonly IUserScopeResolver _scopeResolver;
        private readonly BudgetService _budgets;

        public BudgetsModel(UsageQueryService query, IUserScopeResolver scopeResolver, BudgetService budgets)
        {
            _query = query;
            _scopeResolver = scopeResolver;
            _budgets = budgets;
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

            var rows = filtered.ToList();
            MatchCount = rows.Count;
            Groups = rows
                .GroupBy(b => new { b.EnterpriseId, b.EnterpriseName })
                .OrderBy(g => g.Key.EnterpriseId)
                .Select(g => (g.Key.EnterpriseId, g.Key.EnterpriseName,
                    (IReadOnlyList<BudgetService.BudgetStatus>)g
                        .OrderBy(b => SeverityRank(b.Level))
                        .ThenByDescending(b => b.Pct)
                        .ToList()))
                .ToList();
        }
    }
}
