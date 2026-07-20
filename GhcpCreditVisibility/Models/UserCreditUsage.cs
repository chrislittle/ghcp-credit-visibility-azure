using System.Text.Json.Serialization;

namespace GhcpCreditVisibility.Models
{
    public class UserCreditUsage
    {
        /// <summary>
        /// The time period for the usage report.
        /// </summary>
        [JsonPropertyName("timePeriod")]
        public required TimePeriod TimePeriod { get; set; }

        /// <summary>
        /// The name of the enterprise for the usage report.
        /// </summary>
        [JsonPropertyName("enterprise")]
        public required string Enterprise { get; set; }

        /// <summary>
        /// The name of the user for the usage report.
        /// </summary>
        [JsonPropertyName("user")]
        public string? User { get; set; }

        /// <summary>
        /// The name of the organization for the usage report.
        /// </summary>
        [JsonPropertyName("organization")]
        public string? Organization { get; set; }

        /// <summary>
        /// The product for the usage report.
        /// </summary>
        [JsonPropertyName("product")]
        public string? Product { get; set; }

        /// <summary>
        /// The model for the usage report.
        /// </summary>
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        /// <summary>
        /// The cost center for the usage report.
        /// </summary>
        [JsonPropertyName("costCenter")]
        public CostCenter? CostCenter { get; set; }

        /// <summary>
        /// The collection of usage line items for the report.
        /// </summary>
        [JsonPropertyName("usageItems")]
        public required List<UsageItem> UsageItems { get; set; }
    }

    public class TimePeriod
    {
        /// <summary>
        /// The year for the usage report.
        /// </summary>
        [JsonPropertyName("year")]
        public required int Year { get; set; }

        /// <summary>
        /// The month for the usage report.
        /// </summary>
        [JsonPropertyName("month")]
        public int? Month { get; set; }

        /// <summary>
        /// The day for the usage report.
        /// </summary>
        [JsonPropertyName("day")]
        public int? Day { get; set; }
    }

    public class CostCenter
    {
        /// <summary>
        /// The unique identifier of the cost center.
        /// </summary>
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        /// <summary>
        /// The name of the cost center.
        /// </summary>
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        /// <summary>
        /// The resources (users, organizations, repositories) assigned to the cost center.
        /// Only populated by the "List cost centers" endpoint.
        /// </summary>
        [JsonPropertyName("resources")]
        public List<CostCenterResource> Resources { get; set; } = new();
    }

    public class UsageItem
    {
        /// <summary>
        /// Product name.
        /// </summary>
        [JsonPropertyName("product")]
        public required string Product { get; set; }

        /// <summary>
        /// SKU name.
        /// </summary>
        [JsonPropertyName("sku")]
        public required string Sku { get; set; }

        /// <summary>
        /// Model name.
        /// </summary>
        [JsonPropertyName("model")]
        public required string Model { get; set; }

        /// <summary>
        /// Unit type of the usage line item.
        /// </summary>
        [JsonPropertyName("unitType")]
        public required string UnitType { get; set; }

        /// <summary>
        /// Price per unit of the usage line item.
        /// </summary>
        [JsonPropertyName("pricePerUnit")]
        public required decimal PricePerUnit { get; set; }

        /// <summary>
        /// Gross quantity of the usage line item.
        /// </summary>
        [JsonPropertyName("grossQuantity")]
        public required decimal GrossQuantity { get; set; }

        /// <summary>
        /// Gross amount of the usage line item.
        /// </summary>
        [JsonPropertyName("grossAmount")]
        public required decimal GrossAmount { get; set; }

        /// <summary>
        /// Discount quantity of the usage line item.
        /// </summary>
        [JsonPropertyName("discountQuantity")]
        public required decimal DiscountQuantity { get; set; }

        /// <summary>
        /// Discount amount of the usage line item.
        /// </summary>
        [JsonPropertyName("discountAmount")]
        public required decimal DiscountAmount { get; set; }

        /// <summary>
        /// Net quantity of the usage line item.
        /// </summary>
        [JsonPropertyName("netQuantity")]
        public required decimal NetQuantity { get; set; }

        /// <summary>
        /// Net amount of the usage line item.
        /// </summary>
        [JsonPropertyName("netAmount")]
        public required decimal NetAmount { get; set; }
    }
}
