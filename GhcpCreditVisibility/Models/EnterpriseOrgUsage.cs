using System.Text.Json.Serialization;

namespace GhcpCreditVisibility.Models
{
    /// <summary>
    /// Response for the GENERAL billing usage report
    /// (GET /enterprises/{enterprise}/settings/billing/usage).
    ///
    /// Distinct from the AI-credit / premium-request reports, and confirmed against the live API:
    ///  * the response is JUST <c>usageItems</c> — no timePeriod/enterprise wrapper
    ///  * line items carry <c>organizationName</c>, <c>repositoryName</c> and a per-item
    ///    <c>date</c>, none of which the per-user reports return
    ///  * items have NO <c>model</c>, and a single <c>quantity</c> rather than gross/net quantities
    ///  * the endpoint does NOT support filtering by user
    ///
    /// One call returns the whole month, which is why this is the cheap source of organization,
    /// repository and daily attribution — and why it can never replace the per-user loop.
    /// </summary>
    public class EnterpriseOrgUsage
    {
        [JsonPropertyName("usageItems")]
        public List<OrgUsageItem> UsageItems { get; set; } = new();
    }

    public class OrgUsageItem
    {
        /// <summary>The day this usage occurred — per line item, so daily granularity is native
        /// here and needs no differencing.</summary>
        [JsonPropertyName("date")]
        public DateTime? Date { get; set; }

        [JsonPropertyName("product")]
        public string? Product { get; set; }

        [JsonPropertyName("sku")]
        public string? Sku { get; set; }

        /// <summary>Single quantity field; this report does not split gross from net.</summary>
        [JsonPropertyName("quantity")]
        public decimal Quantity { get; set; }

        [JsonPropertyName("unitType")]
        public string? UnitType { get; set; }

        [JsonPropertyName("pricePerUnit")]
        public decimal? PricePerUnit { get; set; }

        [JsonPropertyName("grossAmount")]
        public decimal GrossAmount { get; set; }

        [JsonPropertyName("discountAmount")]
        public decimal? DiscountAmount { get; set; }

        [JsonPropertyName("netAmount")]
        public decimal NetAmount { get; set; }

        /// <summary>Null for enterprise-level charges belonging to no organization — a live sample
        /// had 15 of 37 items unattributed, so callers must keep them rather than drop them.</summary>
        [JsonPropertyName("organizationName")]
        public string? OrganizationName { get; set; }

        [JsonPropertyName("repositoryName")]
        public string? RepositoryName { get; set; }
    }
}
