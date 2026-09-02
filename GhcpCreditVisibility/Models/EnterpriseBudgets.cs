using System.Text.Json.Serialization;

namespace GhcpCreditVisibility.Models
{
    /// <summary>
    /// Response for the "List budgets" endpoint
    /// (GET /enterprises/{enterprise}/settings/billing/budgets).
    /// </summary>
    public class EnterpriseBudgets
    {
        [JsonPropertyName("budgets")]
        public List<Budget> Budgets { get; set; } = new();
    }

    /// <summary>
    /// A single billing budget configured in GitHub.
    /// </summary>
    public class Budget
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("budget_type")]
        public string? BudgetType { get; set; }

        /// <summary>The product/SKU the budget applies to, e.g. "ai_credits".</summary>
        [JsonPropertyName("budget_product_sku")]
        public string? BudgetProductSku { get; set; }

        /// <summary>The scope of the budget: "user", "enterprise", "organization", "cost_center", etc.</summary>
        [JsonPropertyName("budget_scope")]
        public string? BudgetScope { get; set; }

        /// <summary>The budget limit amount (USD). 0 typically means no limit is set.</summary>
        [JsonPropertyName("budget_amount")]
        public decimal BudgetAmount { get; set; }

        /// <summary>The name of the entity the budget targets (user login, org, cost center, enterprise).</summary>
        [JsonPropertyName("budget_entity_name")]
        public string? BudgetEntityName { get; set; }

        /// <summary>For user-scoped budgets, the GitHub login the budget applies to.</summary>
        [JsonPropertyName("user")]
        public string? User { get; set; }

        /// <summary>The amount consumed against the budget, when reported by GitHub.</summary>
        [JsonPropertyName("consumed_amount")]
        public decimal? ConsumedAmount { get; set; }

        /// <summary>
        /// When true, exceeding this budget BLOCKS further usage rather than merely alerting —
        /// it can stop a developer mid-task. Confirmed present on the live API. Null when GitHub
        /// omits it, which we persist as false (alert-only) rather than guessing a hard stop.
        /// </summary>
        [JsonPropertyName("prevent_further_usage")]
        public bool? PreventFurtherUsage { get; set; }

        /// <summary>
        /// The date this budget lapses (GitHub sends a plain <c>YYYY-MM-DD</c> date, never a
        /// timestamp — hence <see cref="DateOnly"/> and not <see cref="DateTime"/>, which would
        /// pin an evening US expiry to the wrong calendar day anywhere east of Greenwich).
        /// Null means the budget does not expire, which is the default and the only behavior that
        /// existed before 2026-09-01.
        ///
        /// ONLY POPULATED FOR <c>budget_scope: user</c> — GitHub rejects it on every other scope.
        /// On expiry GitHub DELETES the budget and the user falls back to the next applicable
        /// level, so the snapshot job's stale-row cleanup makes the row vanish on the following
        /// run. Capturing the date is the only way this app can ever warn that an override is
        /// about to lapse: once GitHub drops it there is nothing left to read.
        /// </summary>
        [JsonPropertyName("expires_at")]
        public DateOnly? ExpiresAt { get; set; }
    }

    /// <summary>
    /// A budget resolved for a specific user, along with the scope it was sourced from
    /// (for example "user" or "enterprise").
    /// </summary>
    public record ResolvedBudget(decimal Amount, string Scope);

    /// <summary>
    /// A budget resolved for a specific cost center, with the amount consumed against it
    /// when GitHub reports it (<c>Consumed</c> is <c>null</c> when not reported).
    /// </summary>
    public record CostCenterBudget(decimal Amount, decimal? Consumed);
}
