namespace GhcpCreditVisibility.Models
{
    /// <summary>
    /// View model passed to the _UsageItems partial: a user's usage together with the
    /// budget to compare it against and where that budget came from.
    /// </summary>
    public class UserUsageView
    {
        /// <summary>The user's current-month usage report, or <c>null</c> if none was returned.</summary>
        public UserCreditUsage? Usage { get; init; }

        /// <summary>The budget amount to compare usage against. 0 means the user is uncapped.</summary>
        public decimal Budget { get; init; }

        /// <summary>The percentage of the budget at which a user is flagged as "approaching budget".</summary>
        public int WarningThresholdPercent { get; init; } = 80;

        /// <summary>
        /// Where the budget came from: "user" or "enterprise" (from GitHub).
        /// <c>null</c> when no budget applies and the user is uncapped.
        /// </summary>
        public string? BudgetSource { get; init; }
    }
}
