using System.Net.Http.Headers;
using System.Net.Http.Json;
using GhcpCreditVisibility.Models;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Real GitHub enterprise-billing client.
    /// Resilience (retry with exponential backoff + jitter, honoring Retry-After,
    /// circuit breaker and timeout) is applied to the injected <see cref="HttpClient"/>
    /// via <c>AddStandardResilienceHandler()</c> in Program.cs. The dashboard itself
    /// never calls this client directly; only the background snapshot job does, so
    /// per-user N+1 live traffic is gone.
    /// </summary>
    public sealed class RealGitHubBillingClient : IGitHubBillingClient
    {
        private const string GitHubApiVersion = "2026-03-10";

        private readonly HttpClient _http;   // BaseAddress = https://api.github.com, resilience handler attached
        private readonly string _token;
        private readonly ILogger<RealGitHubBillingClient> _logger;
        private readonly GitHubRateLimitState _rateLimit;

        // Typed-client ctor: token is read from configuration (GitHub:Token), which in
        // Azure is a Key Vault reference resolved via the app's managed identity.
        public RealGitHubBillingClient(HttpClient http, IConfiguration config, ILogger<RealGitHubBillingClient> logger, GitHubRateLimitState rateLimit)
        {
            _http = http;
            _token = config["GitHub:Token"] ?? string.Empty;
            _logger = logger;
            _rateLimit = rateLimit;
        }

        public async Task<IReadOnlyList<EnterpriseLicenseUser>> GetEnterpriseUsersAsync(string enterprise, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(enterprise);
            const int perPage = 100;
            var page = 1;
            var users = new List<EnterpriseLicenseUser>();

            while (true)
            {
                var uri = $"/enterprises/{Uri.EscapeDataString(enterprise)}/consumed-licenses?per_page={perPage}&page={page}";
                var result = await SendAsync<EnterpriseConsumedLicenses>(uri, ct);
                if (result?.Users is not { Count: > 0 } pageUsers) break;
                users.AddRange(pageUsers);
                if (pageUsers.Count < perPage) break;
                page++;
            }
            return users;
        }

        public async Task<UserCreditUsage?> GetCurrentMonthUsageForUserAsync(string enterprise, string user, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(enterprise);
            ArgumentException.ThrowIfNullOrWhiteSpace(user);
            var now = DateTime.UtcNow;
            var uri = $"/enterprises/{Uri.EscapeDataString(enterprise)}/settings/billing/ai_credit/usage" +
                      $"?year={now.Year}&month={now.Month}&user={Uri.EscapeDataString(user)}";
            return await SendAsync<UserCreditUsage>(uri, ct);
        }

        public async Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string enterprise, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(enterprise);
            var uri = $"/enterprises/{Uri.EscapeDataString(enterprise)}/settings/billing/cost-centers";
            var result = await SendAsync<EnterpriseCostCenters>(uri, ct);
            return result?.CostCenters ?? new List<CostCenter>();
        }

        public async Task<IReadOnlyList<Budget>> GetBudgetsAsync(string enterprise, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(enterprise);
            var uri = $"/enterprises/{Uri.EscapeDataString(enterprise)}/settings/billing/budgets";
            var result = await SendAsync<EnterpriseBudgets>(uri, ct);
            return result?.Budgets ?? new List<Budget>();
        }

        private async Task<T?> SendAsync<T>(string relativeUri, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GhcpCreditVisibility", "1.0"));

            using var response = await _http.SendAsync(request, ct);

            // Record rate-limit headers (present on both success and 4xx) so the diagnostics
            // publisher can surface the remaining budget as an alertable metric — the sequential
            // per-user calls here are what put pressure on it at enterprise scale.
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
                && int.TryParse(remainingValues.FirstOrDefault(), out var remaining))
            {
                int? limit = null;
                if (response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues)
                    && int.TryParse(limitValues.FirstOrDefault(), out var l))
                    limit = l;
                _rateLimit.Record(remaining, limit);
            }

            // Log (but do not crash the snapshot run on) rate-limit responses; the
            // resilience handler already retried transient 429/5xx before we get here.
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("GitHub rate limit hit on {Uri}. Retry-After={RetryAfter}.",
                    relativeUri, response.Headers.RetryAfter?.ToString() ?? "n/a");
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
    }
}
