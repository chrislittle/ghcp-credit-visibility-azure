using System.Collections.Concurrent;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Resolves the GitHub PAT for one enterprise. Resolution order:
    ///  1. <c>GitHub:Tokens:&lt;slug&gt;</c> config key — lets a deployment wire a per-enterprise
    ///     Key Vault REFERENCE as an app setting if preferred.
    ///  2. <c>GitHub:Token</c> when the enterprise still uses the default secret name — this is the
    ///     pre-multi-enterprise path (App Service resolves the Key Vault reference), so existing
    ///     single-enterprise deployments keep working with zero settings changes.
    ///  3. The Key Vault SDK, reading the enterprise's <see cref="Enterprise.PatSecretName"/> from the
    ///     vault at <c>KeyVault:Uri</c> via the app's managed identity. The identity already holds
    ///     secret-read at VAULT scope (that's what resolves the app-setting reference), so a NEW
    ///     <c>github-pat-&lt;slug&gt;</c> secret is readable the moment it exists — no infra change.
    /// Mock enterprises never call this.
    /// </summary>
    public interface IGitHubPatResolver
    {
        /// <summary>Returns (token, resolved). resolved=false means no usable PAT was found — the
        /// same condition the per-enterprise <c>ghcp.github.token_resolved</c> metric reports.</summary>
        Task<(string? Token, bool Resolved)> TryResolveAsync(Enterprise enterprise, CancellationToken ct = default);
    }

    public sealed class GitHubPatResolver : IGitHubPatResolver
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private readonly IConfiguration _config;
        private readonly ILogger<GitHubPatResolver> _logger;
        private readonly ConcurrentDictionary<string, (string Token, DateTime CachedUtc)> _cache = new();

        public GitHubPatResolver(IConfiguration config, ILogger<GitHubPatResolver> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task<(string? Token, bool Resolved)> TryResolveAsync(Enterprise enterprise, CancellationToken ct = default)
        {
            // 1. Per-enterprise config key (supports per-enterprise Key Vault references as app settings).
            var perSlug = _config[$"GitHub:Tokens:{enterprise.Slug}"];
            if (IsUsable(perSlug)) return (perSlug, true);

            // 2. Legacy single-enterprise path: the platform-resolved GitHub:Token app setting.
            if (string.Equals(enterprise.PatSecretName, Enterprise.DefaultPatSecretName, StringComparison.OrdinalIgnoreCase))
            {
                var legacy = _config["GitHub:Token"];
                if (IsUsable(legacy)) return (legacy, true);
            }

            // 3. Key Vault SDK by secret name (managed identity; vault-scope read already granted).
            var vaultUri = _config["KeyVault:Uri"];
            if (!string.IsNullOrWhiteSpace(vaultUri))
            {
                if (_cache.TryGetValue(enterprise.PatSecretName, out var hit) && DateTime.UtcNow - hit.CachedUtc < CacheTtl)
                    return (hit.Token, true);
                try
                {
                    var client = new SecretClient(new Uri(vaultUri), new DefaultAzureCredential());
                    var secret = await client.GetSecretAsync(enterprise.PatSecretName, cancellationToken: ct);
                    var value = secret.Value.Value;
                    if (IsUsable(value))
                    {
                        _cache[enterprise.PatSecretName] = (value!, DateTime.UtcNow);
                        return (value, true);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Key Vault secret '{Secret}' for enterprise '{Slug}' could not be read.",
                        enterprise.PatSecretName, enterprise.Slug);
                }
            }

            return (null, false);
        }

        /// <summary>A value still starting with "@Microsoft.KeyVault" is App Service's way of saying
        /// the reference did NOT resolve — the exact failure that presents downstream as a 401.</summary>
        private static bool IsUsable(string? token) =>
            !string.IsNullOrWhiteSpace(token)
            && !token.StartsWith("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase);
    }
}
