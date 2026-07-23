using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// A point-in-time snapshot of the app's operational health, computed from the database, config,
    /// and the last-seen GitHub rate-limit state. This is the shape both the periodic
    /// <see cref="SreDiagnosticsPublisher"/> (pushes it to Application Insights as metrics) and the
    /// <c>/health/diag</c> endpoint (returns it as JSON) work from.
    ///
    /// It exists because the failures that actually matter in this app — a snapshot job that has
    /// silently stopped, data that is present but wrong, a Key Vault reference that never resolved —
    /// are invisible to HTTP-level monitoring and live only in the private database. Surfacing them
    /// as telemetry is what makes them reachable by an out-of-network reliability agent (or a plain
    /// Azure Monitor alert).
    /// </summary>
    public sealed record DiagnosticsSnapshot
    {
        /// <summary>Hours since the most recent snapshot run started (or completed, if it finished). Null if none has ever run.</summary>
        public double? SnapshotAgeHours { get; init; }
        public string? LastSnapshotStatus { get; init; }
        public DateTime? LastSnapshotStartedUtc { get; init; }
        public DateTime? LastSnapshotCompletedUtc { get; init; }
        public int? LastSnapshotRowsWritten { get; init; }
        public int? LastSnapshotRowsPurged { get; init; }

        /// <summary>Migrations not yet applied. &gt; 0 means the schema is warming up or the DDL grant is missing.</summary>
        public int PendingMigrations { get; init; }

        // Data-integrity floor — a billing app can be fully "up" while serving wrong numbers.
        public int CostCenters { get; init; }
        public int Budgets { get; init; }
        public int MonthsWithData { get; init; }

        public bool UseMock { get; init; }
        /// <summary>Null in mock mode. Otherwise false when the Key Vault reference for the PAT did not resolve.</summary>
        public bool? GitHubTokenResolved { get; init; }
        public int? GitHubRateLimitRemaining { get; init; }
        public DateTime? GitHubRateLimitSeenUtc { get; init; }

        public string? InstanceId { get; init; }
        public DateTime CollectedUtc { get; init; }
    }

    /// <summary>
    /// Computes a <see cref="DiagnosticsSnapshot"/> from current state. Read-only; safe to call on a
    /// schedule and from a request handler.
    /// </summary>
    public sealed class SreDiagnosticsCollector
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly IConfiguration _config;
        private readonly GitHubRateLimitState _rateLimit;

        public SreDiagnosticsCollector(
            IDbContextFactory<BillingDbContext> dbFactory,
            IConfiguration config,
            GitHubRateLimitState rateLimit)
        {
            _dbFactory = dbFactory;
            _config = config;
            _rateLimit = rateLimit;
        }

        public async Task<DiagnosticsSnapshot> CollectAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var lastRun = await db.SnapshotRuns
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(ct);

            double? ageHours = lastRun is null
                ? null
                : Math.Round((now - (lastRun.CompletedUtc ?? lastRun.StartedUtc)).TotalHours, 2);

            // GetPendingMigrations is meaningless on the in-memory dev provider (no relational schema).
            var pending = db.Database.IsRelational()
                ? (await db.Database.GetPendingMigrationsAsync(ct)).Count()
                : 0;

            var costCenters = await db.CostCenterDirectory.CountAsync(ct);
            var budgets = await db.BudgetSnapshots.CountAsync(ct);
            var monthsWithData = await db.UsageSnapshots
                .Select(x => new { x.Year, x.Month })
                .Distinct()
                .CountAsync(ct);

            var useMock = _config.GetValue("GitHub:UseMock", true);

            // App Service resolves "@Microsoft.KeyVault(...)" references before the app sees them; when
            // resolution SUCCEEDS the config value is the raw secret, when it FAILS App Service leaves
            // the literal "@Microsoft.KeyVault(...)" string in place. So a value still starting with
            // that prefix means the reference never resolved — the exact failure that presents three
            // layers downstream as a GitHub 401. Only meaningful when we're actually using the token.
            bool? tokenResolved = null;
            if (!useMock)
            {
                var token = _config["GitHub:Token"] ?? "";
                tokenResolved = !string.IsNullOrEmpty(token)
                    && !token.StartsWith("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase);
            }

            return new DiagnosticsSnapshot
            {
                SnapshotAgeHours = ageHours,
                LastSnapshotStatus = lastRun?.Status,
                LastSnapshotStartedUtc = lastRun?.StartedUtc,
                LastSnapshotCompletedUtc = lastRun?.CompletedUtc,
                LastSnapshotRowsWritten = lastRun?.RowsWritten,
                LastSnapshotRowsPurged = lastRun?.RowsPurged,
                PendingMigrations = pending,
                CostCenters = costCenters,
                Budgets = budgets,
                MonthsWithData = monthsWithData,
                UseMock = useMock,
                GitHubTokenResolved = tokenResolved,
                GitHubRateLimitRemaining = _rateLimit.Remaining,
                GitHubRateLimitSeenUtc = _rateLimit.LastSeenUtc,
                InstanceId = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"),
                CollectedUtc = now,
            };
        }
    }
}
