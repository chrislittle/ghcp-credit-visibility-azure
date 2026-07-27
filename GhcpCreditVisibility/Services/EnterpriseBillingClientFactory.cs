using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Routes each registry row to its billing client: the shared synthetic mock for
    /// <see cref="Enterprise.UseMockData"/> rows, or a per-enterprise real client otherwise.
    /// This composite routing is what enables HYBRID deployments — the one real enterprise
    /// coexisting with demo/fire-drill mock enterprises in the same tables and dashboards.
    /// </summary>
    public interface IEnterpriseBillingClientFactory
    {
        /// <summary>Builds the client for one enterprise. Throws <see cref="InvalidOperationException"/>
        /// for a real enterprise whose PAT cannot be resolved — the per-enterprise snapshot catch
        /// records that as a failed run for THAT enterprise only.</summary>
        Task<IGitHubBillingClient> GetClientAsync(Enterprise enterprise, CancellationToken ct = default);
    }

    public sealed class EnterpriseBillingClientFactory : IEnterpriseBillingClientFactory
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IGitHubPatResolver _patResolver;
        private readonly GitHubRateLimitRegistry _rateLimits;
        private readonly MockGitHubBillingClient _mock;
        private readonly ILoggerFactory _loggerFactory;

        public EnterpriseBillingClientFactory(
            IHttpClientFactory httpFactory,
            IGitHubPatResolver patResolver,
            GitHubRateLimitRegistry rateLimits,
            MockGitHubBillingClient mock,
            ILoggerFactory loggerFactory)
        {
            _httpFactory = httpFactory;
            _patResolver = patResolver;
            _rateLimits = rateLimits;
            _mock = mock;
            _loggerFactory = loggerFactory;
        }

        public async Task<IGitHubBillingClient> GetClientAsync(Enterprise enterprise, CancellationToken ct = default)
        {
            if (enterprise.UseMockData) return _mock;

            var (token, resolved) = await _patResolver.TryResolveAsync(enterprise, ct);
            if (!resolved)
            {
                throw new InvalidOperationException(
                    $"GitHub PAT for enterprise '{enterprise.Slug}' could not be resolved " +
                    $"(Key Vault secret '{enterprise.PatSecretName}'). Seed it with: " +
                    $"./deploy.ps1 -Task set-pat -Enterprise {enterprise.Slug}");
            }

            // One logical HttpClient name per enterprise: the resilience pipeline (retry, circuit
            // breaker, timeout — added via ConfigureHttpClientDefaults in Program.cs) is built per
            // name, so each enterprise gets its OWN circuit breaker. Enterprise A tripping its
            // breaker never blocks enterprise B's calls.
            var http = _httpFactory.CreateClient($"github:{enterprise.Slug}");
            http.BaseAddress = new Uri("https://api.github.com");

            return new RealGitHubBillingClient(
                http, token!,
                _loggerFactory.CreateLogger<RealGitHubBillingClient>(),
                _rateLimits.For(enterprise.Slug));
        }
    }
}
