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
