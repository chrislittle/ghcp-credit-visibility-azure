using System.Text.Json.Serialization;

namespace GhcpCreditVisibility.Models
{
    /// <summary>
    /// Response for the "List enterprise consumed licenses" endpoint
    /// (GET /enterprises/{enterprise}/consumed-licenses).
    /// </summary>
    public class EnterpriseConsumedLicenses
    {
        [JsonPropertyName("total_seats_consumed")]
        public int TotalSeatsConsumed { get; set; }

        [JsonPropertyName("total_seats_purchased")]
        public int TotalSeatsPurchased { get; set; }

        [JsonPropertyName("users")]
        public List<EnterpriseLicenseUser> Users { get; set; } = new();
    }

    /// <summary>
    /// A single licensed member of the enterprise.
    /// </summary>
    public class EnterpriseLicenseUser
    {
        /// <summary>
        /// The GitHub.com login (handle). For EMU enterprises this is the managed
        /// user name, which is the value expected by the billing usage "user" parameter.
        /// </summary>
        [JsonPropertyName("github_com_login")]
        public string? GitHubComLogin { get; set; }

        /// <summary>
        /// The user's display name on GitHub.com.
        /// </summary>
        [JsonPropertyName("github_com_name")]
        public string? GitHubComName { get; set; }
    }
}
