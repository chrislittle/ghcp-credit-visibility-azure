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

        [BindProperty(SupportsGet = true)] public string Dim { get; set; } = "costcenter";     // total | user | model | costcenter | enterprise
        [BindProperty(SupportsGet = true)] public string Gran { get; set; } = "month";           // day | week | month
        [BindProperty(SupportsGet = true)] public int Range { get; set; } = 12;                  // number of buckets back; 0 = all
        [BindProperty(SupportsGet = true)] public string? FilterUser { get; set; }
        [BindProperty(SupportsGet = true)] public string? FilterModel { get; set; }
        [BindProperty(SupportsGet = true)] public string? FilterCostCenter { get; set; }         // enterprise-qualified key "<enterpriseId>:<ccId>"
        [BindProperty(SupportsGet = true)] public long? Ent { get; set; }                        // enterprise filter (null = all in scope)
        [BindProperty(SupportsGet = true)] public string View { get; set; } = "chart";           // chart | table

        public bool SeesAll { get; private set; }
        public string ScopeLabel { get; private set; } = "";
        /// <summary>Full resolved cost-center list for the pill's tooltip when the label is summarized (null otherwise).</summary>
        public string? ScopeDetail { get; private set; }
        public IReadOnlyList<EnterpriseOption> VisibleEnterprises { get; private set; } = Array.Empty<EnterpriseOption>();
        public bool MultiEnterprise => VisibleEnterprises.Count > 1;
        public FilterOptions Options { get; private set; } = new(Array.Empty<UserOption>(), Array.Empty<string>(), Array.Empty<CostCenterFilterOption>(), Array.Empty<EnterpriseOption>());
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

        public string DimLabel => Dim switch { "user" => "user", "model" => "model", "costcenter" => "cost center", "enterprise" => "enterprise", "organization" => "organization", _ => "total" };

        /// <summary>
        /// Whether the ORGANIZATION breakdown may be offered. Admin-only: the underlying table
        /// carries no cost centre, so the access scope cannot narrow it below the enterprise —
        /// offering it to a cost-centre-scoped manager would expose every other team's spend.
        /// Enforced in <c>OnGetAsync</c> too, so a hand-edited URL cannot bypass the hidden option.
        /// </summary>
        public bool ShowOrganizationDim { get; private set; }

        /// <summary>Org rows come from a different source with no user/model/cost-centre columns, so
        /// none of the usual filters can compose with this dimension.</summary>
        public bool IsOrganization => Dim == "organization";

        /// <summary>When per-user collection began for this scope (backfilled only if an admin
        /// enabled it, and this date is that job's progress marker — see
        /// <see cref="UsageQueryService.GetCollectingSinceAsync"/>).</summary>
        public DateOnly? CollectingSince { get; private set; }

        /// <summary>
        /// True when the chosen range reaches back further than this deployment has been collecting.
        /// Without saying so, a "last 12 months" chart over three weeks of history reads as a
        /// catastrophic decline rather than a short history.
        ///
        /// Organization is exempt: that dimension IS backfilled, so its history genuinely extends
        /// back and the warning would be wrong.
        /// </summary>
        public bool RangeExceedsHistory =>
            !IsOrganization && CollectingSince is DateOnly since && Range > 0 &&
            (Gran switch
            {
                "day" => DateTime.UtcNow.Date.AddDays(-Range),
                "week" => DateTime.UtcNow.Date.AddDays(-7 * Range),
                _ => DateTime.UtcNow.Date.AddMonths(-Range),
            }) < new DateTime(since.Year, since.Month, since.Day);
        public string GranLabel => Gran switch { "day" => "day", "week" => "week", _ => "month" };
        public bool IsTotal => Dim == "total";

        // Contextual filters: only offer filters that can't collapse the breakdown to a trivial 100%.
        // (A user maps to exactly one cost center, so a user filter collapses a cost-center breakdown;
        // an enterprise filter collapses an enterprise breakdown the same way.)
        public bool ShowUserFilter => !IsOrganization && Dim is "model" or "total" or "enterprise";
        public bool ShowModelFilter => !IsOrganization && Dim is "costcenter" or "user" or "total" or "enterprise";
        public bool ShowCostCenterFilter => !IsOrganization && Dim is "user" or "model" or "total";
        public bool ShowEnterpriseFilter => Dim is not "enterprise" && MultiEnterprise;

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
            var scopeDesc = await _query.GetScopeDescriptionAsync(scope, ct);
            ScopeLabel = scopeDesc.Label;
            ScopeDetail = scopeDesc.Detail;

            // Enterprise filter: validated against the caller's visibility, then applied to the
            // scope itself so filter options AND series both narrow to the chosen enterprise.
            VisibleEnterprises = await _query.GetVisibleEnterprisesAsync(scope, ct);
            // An enterprise filter on the enterprise breakdown would collapse it to a trivial 100%.
            if (string.Equals(Dim, "enterprise", StringComparison.OrdinalIgnoreCase)) Ent = null;
            if (Ent is long entFilter && VisibleEnterprises.Any(e => e.Id == entFilter))
                scope = scope with { EnterpriseFilter = entFilter };
            else
                Ent = null;

            Options = await _query.GetFilterOptionsAsync(scope, ct);
            CollectingSince = await _query.GetCollectingSinceAsync(scope, ct);

            if (View != "table") View = "chart";

            // Organization needs ENTERPRISE-GRAIN read (see ShowOrganizationDim). Validating here —
            // not just hiding the dropdown option — is what actually enforces it: a hidden <option>
            // stops nobody from requesting ?Dim=organization by hand.
            //
            // This only decides whether the dimension is OFFERED. Which enterprises' organizations
            // appear is enforced inside UsageQueryService.BuildOrgSeriesAsync, because the scope here
            // may cover several enterprises and this flag cannot express "these but not those".
            ShowOrganizationDim = scope.HasEnterpriseRead;
            if (string.Equals(Dim, "organization", StringComparison.OrdinalIgnoreCase) && !ShowOrganizationDim)
                Dim = "costcenter";

            if (Dim is not ("total" or "user" or "model" or "costcenter" or "enterprise" or "organization")) Dim = "costcenter";
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
                "enterprise" => SeriesDimension.Enterprise,
                "organization" => SeriesDimension.Organization,
                _ => SeriesDimension.Total
            };
            var granularity = Gran switch { "day" => TimeGranularity.Day, "week" => TimeGranularity.Week, _ => TimeGranularity.Month };

            SeriesList = await _query.GetSeriesAsync(dimension, granularity, Range, FilterUser, FilterModel, FilterCostCenter, scope, 8, ct);
            Buckets = SeriesList.Count > 0 ? SeriesList[0].Points.Select(p => p.Label).ToList() : Array.Empty<string>();
            GrandTotal = SeriesList.Sum(s => s.Total);
        }
    }
}
