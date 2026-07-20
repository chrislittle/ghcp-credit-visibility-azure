using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Services;
using static GhcpCreditVisibility.Services.UsageQueryService;

namespace GhcpCreditVisibility.Pages
{
    /// <summary>
    /// Reporting workbench: spend broken down by Total / User / Model / Cost center, bucketed by
    /// Day / Week / Month, with composable pinned filters and a selectable look-back range.
    /// Everything is scope-aware — a cost-center manager only ever sees their own people.
    /// </summary>
    public class ReportsModel : PageModel
    {
        private readonly UsageQueryService _query;
        private readonly IUserScopeResolver _scopeResolver;

        public ReportsModel(UsageQueryService query, IUserScopeResolver scopeResolver)
        {
            _query = query;
            _scopeResolver = scopeResolver;
        }

        [BindProperty(SupportsGet = true)] public string Dim { get; set; } = "costcenter";     // total | user | model | costcenter
        [BindProperty(SupportsGet = true)] public string Gran { get; set; } = "month";           // day | week | month
        [BindProperty(SupportsGet = true)] public int Range { get; set; } = 12;                  // number of buckets back; 0 = all
        [BindProperty(SupportsGet = true)] public string? FilterUser { get; set; }
        [BindProperty(SupportsGet = true)] public string? FilterModel { get; set; }
        [BindProperty(SupportsGet = true)] public string? FilterCostCenter { get; set; }
        [BindProperty(SupportsGet = true)] public string View { get; set; } = "chart";           // chart | table

        public bool SeesAll { get; private set; }
        public string ScopeLabel { get; private set; } = "";
        public FilterOptions Options { get; private set; } = new(Array.Empty<UserOption>(), Array.Empty<string>(), Array.Empty<CostCenterTotal>());
        public IReadOnlyList<Series> SeriesList { get; private set; } = Array.Empty<Series>();
        public IReadOnlyList<string> Buckets { get; private set; } = Array.Empty<string>();
        public decimal GrandTotal { get; private set; }

        // Range options offered per granularity (value, label). value 0 = All.
        public static readonly IReadOnlyList<(int Value, string Label)> DayRanges = new[] { (14, "Last 14 days"), (30, "Last 30 days"), (60, "Last 60 days"), (90, "Last 90 days"), (0, "All time") };
        public static readonly IReadOnlyList<(int Value, string Label)> WeekRanges = new[] { (8, "Last 8 weeks"), (12, "Last 12 weeks"), (26, "Last 26 weeks"), (52, "Last 52 weeks"), (0, "All time") };
        public static readonly IReadOnlyList<(int Value, string Label)> MonthRanges = new[] { (3, "Last 3 months"), (6, "Last 6 months"), (12, "Last 12 months"), (0, "All time") };

        public IReadOnlyList<(int Value, string Label)> RangeOptions => Gran switch
        {
            "day" => DayRanges,
            "week" => WeekRanges,
            _ => MonthRanges
        };

        public string DimLabel => Dim switch { "user" => "user", "model" => "model", "costcenter" => "cost center", _ => "total" };
        public string GranLabel => Gran switch { "day" => "day", "week" => "week", _ => "month" };
        public bool IsTotal => Dim == "total";

        // Contextual filters: only offer filters that can't collapse the breakdown to a trivial 100%.
        // (A user maps to exactly one cost center, so a user filter collapses a cost-center breakdown.)
        public bool ShowUserFilter => Dim is "model" or "total";
        public bool ShowModelFilter => Dim is "costcenter" or "user" or "total";
        public bool ShowCostCenterFilter => Dim is "user" or "model" or "total";

        public string PeriodLabel
        {
            get
            {
                if (Range <= 0) return "all time";
                var unit = Gran == "day" ? "days" : Gran == "week" ? "weeks" : "months";
                return $"last {Range} {unit}";
            }
        }

        public async Task OnGetAsync(CancellationToken ct)
        {
            var scope = await _scopeResolver.ResolveAsync(User, ct);
            SeesAll = scope.SeesAll;
            ScopeLabel = scope.SeesAll ? "All cost centers"
                : scope.CostCenterIds.Count > 0 ? $"Cost centers: {string.Join(", ", scope.CostCenterIds)}"
                : "No assigned scope";

            Options = await _query.GetFilterOptionsAsync(scope, ct);

            if (View != "table") View = "chart";
            if (Dim is not ("total" or "user" or "model" or "costcenter")) Dim = "costcenter";
            if (Gran is not ("day" or "week" or "month")) Gran = "month";
            // Snap the range to a valid option for the chosen granularity (avoids e.g. "12 days" after switching from months).
            if (!RangeOptions.Any(o => o.Value == Range))
                Range = Gran switch { "day" => 30, "week" => 12, _ => 12 };

            // Sanitize filters that would collapse the breakdown (also protects bookmarked/stale URLs).
            if (!ShowUserFilter) FilterUser = null;
            if (!ShowModelFilter) FilterModel = null;
            if (!ShowCostCenterFilter) FilterCostCenter = null;

            var dimension = Dim switch
            {
                "user" => SeriesDimension.User,
                "model" => SeriesDimension.Model,
                "costcenter" => SeriesDimension.CostCenter,
                _ => SeriesDimension.Total
            };
            var granularity = Gran switch { "day" => TimeGranularity.Day, "week" => TimeGranularity.Week, _ => TimeGranularity.Month };

            SeriesList = await _query.GetSeriesAsync(dimension, granularity, Range, FilterUser, FilterModel, FilterCostCenter, scope, 8, ct);
            Buckets = SeriesList.Count > 0 ? SeriesList[0].Points.Select(p => p.Label).ToList() : Array.Empty<string>();
            GrandTotal = SeriesList.Sum(s => s.Total);
        }
    }
}
