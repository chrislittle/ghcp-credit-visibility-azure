using GhcpCreditVisibility.Models;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Abstraction over the GitHub enterprise billing APIs so the rest of the app
    /// (snapshot job, query service) is agnostic to whether data comes from the
    /// real GitHub API (<see cref="RealGitHubBillingClient"/>) or synthetic sample
    /// data (<see cref="MockGitHubBillingClient"/>).
    /// </summary>
    public interface IGitHubBillingClient
    {
        Task<IReadOnlyList<EnterpriseLicenseUser>> GetEnterpriseUsersAsync(string enterprise, CancellationToken ct = default);
        /// <summary>
        /// One user's AI-credit usage for a specific month. Takes an explicit period rather than
        /// assuming "now" so the same call serves both live collection and historical backfill —
        /// backfilled rows are then written by the identical code path and cannot drift in shape
        /// from rows collected live.
        /// </summary>
        Task<UserCreditUsage?> GetUsageForUserAsync(string enterprise, string user, int year, int month, CancellationToken ct = default);
        Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string enterprise, CancellationToken ct = default);
        Task<IReadOnlyList<Budget>> GetBudgetsAsync(string enterprise, CancellationToken ct = default);

        /// <summary>
        /// Organization/repository/date-attributed usage for one month, from the general billing
        /// usage report. ONE call covers the whole month — unlike the per-user AI-credit report,
        /// which needs a call per user. Carries no user attribution, so it complements that loop
        /// rather than replacing it.
        /// </summary>
        Task<IReadOnlyList<OrgUsageItem>> GetOrgUsageAsync(string enterprise, int year, int month, CancellationToken ct = default);

        /// <summary>
        /// Assigned Copilot seats, each carrying its plan. NOT the same population as
        /// <see cref="GetEnterpriseUsersAsync"/>, which returns GHEC licence holders — a person can
        /// hold an enterprise licence with no Copilot seat, and a live enterprise showed 8 licences
        /// against 3 seats. Anything sizing the included-credit allowance must use THIS count.
        /// </summary>
        Task<IReadOnlyList<CopilotSeat>> GetCopilotSeatsAsync(string enterprise, CancellationToken ct = default);
    }
}
