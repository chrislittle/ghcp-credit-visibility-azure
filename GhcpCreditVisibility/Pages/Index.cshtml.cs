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
        private readonly IAppAdminChecker _admin;

        public IndexModel(UsageQueryService query, IUserScopeResolver scopeResolver, BudgetService budgets, IConfiguration config, IAppAdminChecker admin)
        {
            _query = query;
            _scopeResolver = scopeResolver;
            _budgets = budgets;
            _config = config;
            _admin = admin;
        }

        // Query-string driven controls (all optional; sensible defaults).
        [BindProperty(SupportsGet = true)] public string? Period { get; set; }      // "YYYY-MM" for the per-user table
        [BindProperty(SupportsGet = true)] public string? UserSearch { get; set; }  // filter the per-user table by name/login/cost center
        [BindProperty(SupportsGet = true)] public int UserPage { get; set; } = 1;   // 1-based page index for the per-user table
        [BindProperty(SupportsGet = true)] public long? Ent { get; set; }           // enterprise filter (null = all in scope)

        public const int UserPageSize = 25;
        /// <summary>A month-over-month move of at least this % renders as a highlighted pill in the
        /// per-user table — the "who suddenly changed" signal.</summary>
        public const double BigMoveThresholdPct = 50;

        // ── Budgets at scale ──
        // A SeesAll exec across several enterprises can face dozens of budgets; a wall of on-track
        // meters buries the two red ones. The dashboard therefore surfaces EXCEPTIONS: when more
        // than BudgetInlineLimit budgets are visible, only over/near-limit ones get meters (capped
        // at BudgetAttentionCap, worst first) and the rest collapse into summary counts; the full
        // list lives on the Budgets page. Small deployments (<= the limit) see every meter, exactly
        // as before — no regression for a single-cost-center manager.
        public const int BudgetInlineLimit = 6;
        public const int BudgetAttentionCap = 6;

        /// <summary>Splits budgets for the dashboard card. ExceptionsMode=false → Shown is ALL
        /// budgets (small scale). Otherwise Shown is only over/near-limit, worst first, capped;
        /// HiddenAttention counts capped-out attention budgets ("…and N more").</summary>
        public static (IReadOnlyList<BudgetService.BudgetStatus> Shown, int HiddenAttention, bool ExceptionsMode) PartitionBudgets(
            IReadOnlyList<BudgetService.BudgetStatus> all, int inlineLimit = BudgetInlineLimit, int cap = BudgetAttentionCap)
        {
            if (all.Count <= inlineLimit) return (all, 0, false);
            var attention = all
                .Where(b => b.Level is "over" or "critical")
                .OrderBy(b => b.Level == "over" ? 0 : 1)
                .ThenByDescending(b => b.Pct)
                .ToList();
            var shown = attention.Take(cap).ToList();
            return (shown, attention.Count - shown.Count, true);
        }

        public int Year { get; private set; }
        public int Month { get; private set; }
        public bool SeesAll { get; private set; }

        /// <summary>
        /// The caller administers this deployment but has been granted no Enterprise Reader access,
        /// so they legitimately see nothing. Distinguished from "no data yet" because the two look
        /// identical on screen and lead to opposite conclusions — one is a grant to make, the other
        /// is a job that has not run. Since Admin stopped implying see-all, this is the most likely
        /// reason for an otherwise-inexplicable empty dashboard.
        /// </summary>
        public bool IsAdminWithoutReadAccess { get; private set; }

        public string ScopeLabel { get; private set; } = "";
        /// <summary>Full resolved cost-center list for the pill's tooltip when the label is summarized (null otherwise).</summary>
        public string? ScopeDetail { get; private set; }
        /// <summary>Enterprises whose data the caller can see; the filter dropdown renders only when there's more than one.</summary>
        public IReadOnlyList<UsageQueryService.EnterpriseOption> VisibleEnterprises { get; private set; } = Array.Empty<UsageQueryService.EnterpriseOption>();
        public bool MultiEnterprise => VisibleEnterprises.Count > 1;
        public IReadOnlyList<UsageQueryService.MonthOption> AvailableMonths { get; private set; } = Array.Empty<UsageQueryService.MonthOption>();

        /// <summary>The single page of per-user rows actually rendered in the table (already filtered by <see cref="UserSearch"/> and paged in the database).</summary>
        public IReadOnlyList<UsageQueryService.UserMonthTotal> DisplayUsers { get; private set; } = Array.Empty<UsageQueryService.UserMonthTotal>();
        /// <summary>Count of users matching <see cref="UserSearch"/> (all users if no search term) — drives the pagination controls and the "N match" label.</summary>
        public int MatchingUserCount { get; private set; }
        /// <summary>False when the previous month has no data in scope — per-user deltas render as em-dashes instead of flagging everyone "new".</summary>
        public bool HasPrevUserDeltas { get; private set; }
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

        /// <summary>
        /// Consumed-vs-billable for the selected month. Deliberately NOT gated on
        /// <see cref="ShowGrossUsage"/>: that flag governs whether an EXTRA gross KPI is rendered,
        /// a presentation preference. This is a correctness concern — a "$0.00" total with no
        /// explanation reads as "nobody used Copilot" when the truth may be "everyone did, and the
        /// allowance covered it". The headline stays net either way.
        /// </summary>
        public UsageQueryService.AllowanceCoverage Coverage =>
            new(TotalSpend, TotalGrossSpend);

        /// <summary>When per-user collection began for this scope. Per-user history is not
        /// backfilled, so an empty month before this date is expected rather than broken.</summary>
        public DateOnly? CollectingSince { get; private set; }

        /// <summary>
        /// True when the selected month has no data AND that month predates collection — i.e. the
        /// emptiness is explained by "we weren't watching yet", not by a failure. An empty month
        /// AFTER collection started is a different thing (genuinely no usage) and says so instead.
        /// </summary>
        public bool SelectedMonthPrecedesCollection =>
            UserCount == 0 && CollectingSince is DateOnly since &&
            new DateOnly(Year, Month, 1) < new DateOnly(since.Year, since.Month, 1);
        public int UserCount { get; private set; }
        public int CostCenterCount { get; private set; }
        public decimal AvgPerUser { get; private set; }
        public decimal MaxUserNet { get; private set; }
        public UsageQueryService.UserMonthTotal? TopUser { get; private set; }
        public decimal PrevMonthTotal { get; private set; }
        public double? DeltaPct { get; private set; }
        public string PrevMonthLabel { get; private set; } = "";
        public IReadOnlyList<BudgetService.BudgetStatus> Budgets { get; private set; } = Array.Empty<BudgetService.BudgetStatus>();
        /// <summary>The budgets actually rendered as meters (see <see cref="PartitionBudgets"/>).</summary>
        public IReadOnlyList<BudgetService.BudgetStatus> DisplayBudgets { get; private set; } = Array.Empty<BudgetService.BudgetStatus>();
        public bool BudgetExceptionsMode { get; private set; }
        public int HiddenAttentionBudgetCount { get; private set; }
        public int BudgetOverCount { get; private set; }
        public int BudgetNearLimitCount { get; private set; }
        public int BudgetOnTrackCount { get; private set; }
        public int BudgetEnterpriseCount { get; private set; }

        public string PeriodValue => $"{Year:D4}-{Month:D2}";
        public string MonthLabel => new DateTime(Year, Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        public static string FormatMonth(int year, int month) => new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        public static string ShortMonth(int year, int month) => new DateTime(year, month, 1).ToString("MMM", CultureInfo.InvariantCulture);

        public async Task OnGetAsync(CancellationToken ct)
        {
            ShowGrossUsage = _config.GetValue("Dashboard:ShowGrossUsage", false);

            var scope = await _scopeResolver.ResolveAsync(User, ct);
            SeesAll = scope.SeesAll;
            // Only worth asking when the scope is genuinely empty — an admin who also holds a grant
            // sees data and needs no explanation, and this costs a DB round trip.
            IsAdminWithoutReadAccess =
                !scope.SeesAll && scope.ReadAllEnterpriseIds.Count == 0 && scope.CostCenters.Count == 0
                && await _admin.IsAdminAsync(User, ct);
            var scopeDesc = await _query.GetScopeDescriptionAsync(scope, ct);
            ScopeLabel = scopeDesc.Label;
            ScopeDetail = scopeDesc.Detail;

            // Enterprise filter: only applies when the chosen enterprise is within the caller's
            // visibility (a bookmarked ?Ent= for an enterprise they can't see silently resets to all).
            VisibleEnterprises = await _query.GetVisibleEnterprisesAsync(scope, ct);
            if (Ent is long entFilter && VisibleEnterprises.Any(e => e.Id == entFilter))
                scope = scope with { EnterpriseFilter = entFilter };
            else
                Ent = null;

            AvailableMonths = await _query.GetAvailableMonthsAsync(scope, ct);
            CollectingSince = await _query.GetCollectingSinceAsync(scope, ct);

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
            HasPrevUserDeltas = userPage.HasPrevMonthData;

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
            (DisplayBudgets, HiddenAttentionBudgetCount, BudgetExceptionsMode) = PartitionBudgets(Budgets);
            BudgetOverCount = Budgets.Count(b => b.Level == "over");
            BudgetNearLimitCount = Budgets.Count(b => b.Level == "critical");
            BudgetOnTrackCount = Budgets.Count - BudgetOverCount - BudgetNearLimitCount;
            BudgetEnterpriseCount = Budgets.Select(b => b.EnterpriseId).Distinct().Count();
        }
    }
}

