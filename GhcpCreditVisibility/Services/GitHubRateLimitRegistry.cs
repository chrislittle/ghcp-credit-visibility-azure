using System.Collections.Concurrent;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Per-enterprise <see cref="GitHubRateLimitState"/>, keyed by enterprise slug. GitHub rate
    /// limits are PER PAT, so each enterprise gets its own last-seen state — enterprise A being
    /// throttled must never read as pressure on enterprise B. Singleton: the (per-run) HTTP clients
    /// write into it and the diagnostics collector reads all of it.
    /// </summary>
    public sealed class GitHubRateLimitRegistry
    {
        private readonly ConcurrentDictionary<string, GitHubRateLimitState> _states =
            new(StringComparer.OrdinalIgnoreCase);

        public GitHubRateLimitState For(string enterpriseSlug) =>
            _states.GetOrAdd(enterpriseSlug ?? "", _ => new GitHubRateLimitState());

        /// <summary>Snapshot of every slug observed this process lifetime (mock enterprises never appear).</summary>
        public IReadOnlyDictionary<string, GitHubRateLimitState> All =>
            new Dictionary<string, GitHubRateLimitState>(_states, StringComparer.OrdinalIgnoreCase);
    }
}
