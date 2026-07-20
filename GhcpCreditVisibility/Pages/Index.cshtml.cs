using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Pages
{
    public class IndexModel : PageModel
    {
        private readonly UsageQueryService _query;
        private readonly IUserScopeResolver _scopeResolver;
        private readonly BudgetService _budgets;
        private readonly IConfiguration _config;

        public IndexModel(UsageQueryService query, IUserScopeResolver scopeResolver, BudgetService budgets, IConfiguration config)
        {
            _query = query;
            _scopeResolver = scopeResolver;
            _budgets = budgets;
            _config = config;
        }

        // Query-string driven controls (all optional; sensible defaults).
        [BindProperty(SupportsGet = true)] public string? Period { get; set; }      // "YYYY-MM" for the per-user table
        [BindProperty(SupportsGet = true)] public string? UserSearch { get; set; }  // filter the per-user table by name/login/cost center
        [BindProperty(SupportsGet = true)] public int UserPage { get; set; } = 1;   // 1-based page index for the per-user table

        public const int UserPageSize = 25;

        public int Year { get; private set; }
        public int Month { get; private set; }
        public bool SeesAll { get; private set; }
        public string ScopeLabel { get; private set; } = "";
        public IReadOnlyList<UsageQueryService.MonthOption> AvailableMonths { get; private set; } = Array.Empty<UsageQueryService.MonthOption>();

        /// <summary>The single page of per-user rows actually rendered in the table (already filtered by <see cref="UserSearch"/> and paged in the database).</summary>
        public IReadOnlyList<UsageQueryService.UserMonthTotal> DisplayUsers { get; private set; } = Array.Empty<UsageQueryService.UserMonthTotal>();
        /// <summary>Count of users matching <see cref="UserSearch"/> (all users if no search term) — drives the pagination controls and the "N match" label.</summary>
        public int MatchingUserCount { get; private set; }
        public int UserPageCount { get; private set; } = 1;
        public IReadOnlyList<UsageQueryService.CostCenterTotal> CostCenters { get; private set; } = Array.Empty<UsageQueryService.CostCenterTotal>();
        public IReadOnlyList<UsageQueryService.ModelTotal> Models { get; private set; } = Array.Empty<UsageQueryService.ModelTotal>();

        // Headline KPIs.
        public decimal TotalSpend { get; private set; }
        /// <summary>Real AI-credit consumption before the included-allowance discount is applied — only
        /// rendered when <see cref="ShowGrossUsage"/> is on (feature flag: Dashboard:ShowGrossUsage).
        /// Lets a demo/POC prove the pipeline is really pulling live usage even in months where every
        /// user is still fully within their included allowance (TotalSpend == 0).</summary>
        public decimal TotalGrossSpend { get; private set; }
        public bool ShowGrossUsage { get; private set; }
        public int UserCount { get; private set; }
        public int CostCenterCount { get; private set; }
        public decimal AvgPerUser { get; private set; }
        public decimal MaxUserNet { get; private set; }
        public UsageQueryService.UserMonthTotal? TopUser { get; private set; }
        public decimal PrevMonthTotal { get; private set; }
        public double? DeltaPct { get; private set; }
        public string PrevMonthLabel { get; private set; } = "";
        public IReadOnlyList<BudgetService.BudgetStatus> Budgets { get; private set; } = Array.Empty<BudgetService.BudgetStatus>();

        public string PeriodValue => $"{Year:D4}-{Month:D2}";
        public string MonthLabel => new DateTime(Year, Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        public static string FormatMonth(int year, int month) => new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        public static string ShortMonth(int year, int month) => new DateTime(year, month, 1).ToString("MMM", CultureInfo.InvariantCulture);

        public async Task OnGetAsync(CancellationToken ct)
        {
            ShowGrossUsage = _config.GetValue("Dashboard:ShowGrossUsage", false);

            var scope = await _scopeResolver.ResolveAsync(User, ct);
            SeesAll = scope.SeesAll;
            ScopeLabel = scope.SeesAll
                ? "All cost centers"
                : scope.CostCenterIds.Count > 0 ? $"Cost centers: {string.Join(", ", scope.CostCenterIds)}"
                : "No assigned scope";

            AvailableMonths = await _query.GetAvailableMonthsAsync(scope, ct);

            // Resolve the selected period: explicit ?Period=YYYY-MM, else latest available, else now.
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

            CostCenters = await _query.GetCostCenterTotalsAsync(Year, Month, scope, ct);
            Models = await _query.GetModelTotalsAsync(Year, Month, scope, ct);

            // Search + pagination happen entirely in the database (GROUP BY / WHERE / ORDER BY /
            // OFFSET-FETCH) — only the current page of rows is ever materialized here, regardless of
            // how many users or raw usage rows exist for the month. If the requested page lands past
            // the end (e.g. a stale bookmark after the result set shrank), refetch once with the
            // clamped page number rather than showing a blank page.
            UserPage = UserPage < 1 ? 1 : UserPage;
            var userPage = await _query.GetUserTotalsPagedAsync(Year, Month, scope, UserSearch, UserPage, UserPageSize, ct);
            UserPageCount = Math.Max(1, (int)Math.Ceiling(userPage.MatchingUserCount / (double)UserPageSize));
            if (UserPage > UserPageCount)
            {
                UserPage = UserPageCount;
                userPage = await _query.GetUserTotalsPagedAsync(Year, Month, scope, UserSearch, UserPage, UserPageSize, ct);
            }
            DisplayUsers = userPage.Items;
            MatchingUserCount = userPage.MatchingUserCount;

            // Headline KPIs derived from the scoped month (independent of search/paging).
            TotalSpend = userPage.TotalSpend;
            TotalGrossSpend = userPage.TotalGrossSpend;
            UserCount = userPage.TotalUserCount;
            CostCenterCount = CostCenters.Count;
            AvgPerUser = UserCount > 0 ? TotalSpend / UserCount : 0m;
            MaxUserNet = userPage.MaxUserNet;
            TopUser = userPage.TopUser;

            var prev = new DateTime(Year, Month, 1).AddMonths(-1);
            PrevMonthLabel = prev.ToString("MMM", CultureInfo.InvariantCulture);
            PrevMonthTotal = await _query.GetMonthTotalAsync(prev.Year, prev.Month, scope, ct);
            if (PrevMonthTotal > 0) DeltaPct = (double)((TotalSpend - PrevMonthTotal) / PrevMonthTotal) * 100.0;

            Budgets = await _budgets.GetStatusesAsync(scope, Year, Month, ct);
        }
    }
}

