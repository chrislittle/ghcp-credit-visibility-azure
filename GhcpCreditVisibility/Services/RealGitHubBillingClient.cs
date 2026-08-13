using System.Net.Http.Headers;
using System.Net.Http.Json;
using GhcpCreditVisibility.Models;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Real GitHub enterprise-billing client for ONE enterprise. Constructed per enterprise by
    /// <see cref="EnterpriseBillingClientFactory"/> with that enterprise's PAT and its own
    /// rate-limit state (rate limits are per PAT). Resilience (retry with exponential backoff +
    /// jitter, honoring Retry-After, circuit breaker and timeout) is applied to the injected
    /// <see cref="HttpClient"/> via <c>ConfigureHttpClientDefaults(...AddStandardResilienceHandler())</c>
    /// in Program.cs — the pipeline is built per client NAME, so each enterprise gets its own
    /// circuit breaker. The dashboard itself never calls this client directly; only the background
    /// snapshot job does, so per-user N+1 live traffic is gone.
    /// </summary>
    public sealed class RealGitHubBillingClient : IGitHubBillingClient
    {
        private const string GitHubApiVersion = "2026-03-10";

        private readonly HttpClient _http;   // BaseAddress = https://api.github.com, resilience handler attached
        private readonly string _token;
        private readonly ILogger<RealGitHubBillingClient> _logger;
        private readonly GitHubRateLimitState _rateLimit;

        public RealGitHubBillingClient(HttpClient http, string token, ILogger<RealGitHubBillingClient> logger, GitHubRateLimitState rateLimit)
        {
            _http = http;
            _token = token;
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

        public async Task<IReadOnlyList<CopilotSeat>> GetCopilotSeatsAsync(string enterprise, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(enterprise);
            const int perPage = 100;
            var page = 1;
            var seats = new List<CopilotSeat>();

            // Same pagination shape as GetEnterpriseUsersAsync above. Cost is one call per enterprise
            // per run for any enterprise under 100 seats, which is the common case.
            while (true)
            {
                var uri = $"/enterprises/{Uri.EscapeDataString(enterprise)}/copilot/billing/seats?per_page={perPage}&page={page}";
                var result = await SendAsync<CopilotSeatsResponse>(uri, ct);
                if (result?.Seats is not { Count: > 0 } pageSeats) break;
                seats.AddRange(pageSeats);
                if (pageSeats.Count < perPage) break;
                page++;
            }
            return seats;
        }

        public async Task<UserCreditUsage?> GetUsageForUserAsync(string enterprise, string user, int year, int month, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(enterprise);
            ArgumentException.ThrowIfNullOrWhiteSpace(user);
            // year/month are explicit: GitHub serves a rolling 24-month window, so this same call
            // fetches a past month for backfill exactly as it fetches the current one.
            var uri = $"/enterprises/{Uri.EscapeDataString(enterprise)}/settings/billing/ai_credit/usage" +
                      $"?year={year}&month={month}&user={Uri.EscapeDataString(user)}";
            return await SendAsync<UserCreditUsage>(uri, ct);
        }

        public async Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string enterprise, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(enterprise);
            var uri = $"/enterprises/{Uri.EscapeDataString(enterprise)}/settings/billing/cost-centers";
            var result = await SendAsync<EnterpriseCostCenters>(uri, ct);
            return result?.CostCenters ?? new List<CostCenter>();
        }

        public async Task<IReadOnlyList<OrgUsageItem>> GetOrgUsageAsync(string enterprise, int year, int month, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(enterprise);
            // Whole month in ONE request — this endpoint takes no user filter, so there is no
            // per-user fan-out here and it costs a single call against the rate-limit budget.
            var uri = $"/enterprises/{Uri.EscapeDataString(enterprise)}/settings/billing/usage" +
                      $"?year={year}&month={month}";
            var result = await SendAsync<EnterpriseOrgUsage>(uri, ct);
            return result?.UsageItems ?? new List<OrgUsageItem>();
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
