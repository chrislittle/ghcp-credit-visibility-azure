namespace GhcpCreditVisibility.Configuration
{
    /// <summary>
    /// Options for usage budget alerting, bound from the "Alerting" configuration section.
    /// </summary>
    public class UsageBudgetOptions
    {
        /// <summary>
        /// The percentage of the budget at which a user is flagged as "approaching budget".
        /// Defaults to 80%.
        /// </summary>
        public int WarningThresholdPercent { get; set; } = 80;
    }
}
