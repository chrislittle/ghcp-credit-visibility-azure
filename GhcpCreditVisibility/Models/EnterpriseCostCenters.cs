using System.Text.Json.Serialization;

namespace GhcpCreditVisibility.Models
{
    /// <summary>
    /// Response for the "List cost centers" endpoint
    /// (GET /enterprises/{enterprise}/settings/billing/cost-centers).
    /// </summary>
    public class EnterpriseCostCenters
    {
        [JsonPropertyName("costCenters")]
        public List<CostCenter> CostCenters { get; set; } = new();
    }

    /// <summary>
    /// A resource (user, organization, or repository) assigned to a cost center.
    /// </summary>
    public class CostCenterResource
    {
        /// <summary>
        /// The type of the resource, e.g. "User", "Org", or "Repo".
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>
        /// The name of the resource. For users this is the GitHub login.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
