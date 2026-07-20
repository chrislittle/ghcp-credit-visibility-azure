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
        Task<UserCreditUsage?> GetCurrentMonthUsageForUserAsync(string enterprise, string user, CancellationToken ct = default);
        Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string enterprise, CancellationToken ct = default);
        Task<IReadOnlyList<Budget>> GetBudgetsAsync(string enterprise, CancellationToken ct = default);
    }
}
