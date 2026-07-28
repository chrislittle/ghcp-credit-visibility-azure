using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Operational health of ONE registry enterprise — the per-enterprise slice of
    /// <see cref="DiagnosticsSnapshot"/>. Freshness, last-run status, data counts, PAT resolution and
    /// rate limit are all judged PER ENTERPRISE: a global freshness number would hide "enterprise B
    /// has been stale for 3 days" behind enterprise A's healthy snapshot.
    /// </summary>
    public sealed record EnterpriseDiagnostics
    {
        public long EnterpriseId { get; init; }
        public string Slug { get; init; } = "";
        public string? DisplayName { get; init; }
        public bool Enabled { get; init; }
        public bool UseMock { get; init; }

        public double? SnapshotAgeHours { get; init; }
        public string? LastSnapshotStatus { get; init; }
        public DateTime? LastSnapshotCompletedUtc { get; init; }
        public int? LastSnapshotRowsWritten { get; init; }

        public int CostCenters { get; init; }
        public int Budgets { get; init; }
        public int MonthsWithData { get; init; }

        /// <summary>Null for mock enterprises (no PAT needed).</summary>
        public bool? TokenResolved { get; init; }
        public int? RateLimitRemaining { get; init; }
    }

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
    /// Azure Monitor alert). The global fields summarize across enterprises (kept for continuity);
    /// <see cref="Enterprises"/> carries the per-enterprise truth the alert rules key off.
    /// </summary>
    public sealed record DiagnosticsSnapshot
    {
        /// <summary>Hours since the most recent snapshot run started (or completed, if it finished),
        /// across ALL enterprises. Null if none has ever run. Per-enterprise ages live in <see cref="Enterprises"/>.</summary>
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
        /// <summary>Null in mock mode. Otherwise false when the Key Vault reference for the (default)
        /// PAT did not resolve. Per-enterprise PAT status lives in <see cref="Enterprises"/>.</summary>
        public bool? GitHubTokenResolved { get; init; }
        public int? GitHubRateLimitRemaining { get; init; }
        public DateTime? GitHubRateLimitSeenUtc { get; init; }

        /// <summary>Per-enterprise health — one entry per registry row (enabled or not).</summary>
        public IReadOnlyList<EnterpriseDiagnostics> Enterprises { get; init; } = Array.Empty<EnterpriseDiagnostics>();

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
        private readonly GitHubRateLimitRegistry _rateLimits;
        private readonly IGitHubPatResolver _patResolver;

        public SreDiagnosticsCollector(
            IDbContextFactory<BillingDbContext> dbFactory,
            IConfiguration config,
            GitHubRateLimitRegistry rateLimits,
            IGitHubPatResolver patResolver)
        {
            _dbFactory = dbFactory;
            _config = config;
            _rateLimits = rateLimits;
            _patResolver = patResolver;
        }

        public async Task<DiagnosticsSnapshot> CollectAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Most recent by TIME, not by insert order — with multiple enterprises in one cycle,
            // the last-inserted row isn't necessarily the freshest activity.
            var lastRun = await db.SnapshotRuns
                .OrderByDescending(r => r.CompletedUtc ?? r.StartedUtc)
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

            // ── Per-enterprise health ──
            // The migration-seeded placeholder row (slug "__bootstrap__") is pre-bootstrap state,
            // not an operational enterprise — it exists only until the first registry bootstrap
            // renames it from config. Reporting it here once caused a false Sev1 token_unresolved
            // alert: a diagnostics tick raced the rename and emitted token_resolved=0 for a row
            // that stopped existing seconds later.
            var enterprises = await db.Enterprises
                .Where(e => e.Slug != Enterprise.BootstrapPlaceholderSlug)
                .OrderBy(e => e.Id)
                .ToListAsync(ct);
            var perEnterprise = new List<EnterpriseDiagnostics>(enterprises.Count);
            if (enterprises.Count > 0)
            {
                // Latest run per enterprise: two-step (max id, then fetch) so it translates everywhere.
                var lastRunIds = await db.SnapshotRuns
                    .GroupBy(r => r.EnterpriseId)
                    .Select(g => g.Max(r => r.Id))
                    .ToListAsync(ct);
                var lastRuns = (await db.SnapshotRuns.Where(r => lastRunIds.Contains(r.Id)).ToListAsync(ct))
                    .ToDictionary(r => r.EnterpriseId);

                var ccCounts = (await db.CostCenterDirectory.GroupBy(x => x.EnterpriseId)
                    .Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
                var budgetCounts = (await db.BudgetSnapshots.GroupBy(x => x.EnterpriseId)
                    .Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
                var monthRows = await db.UsageSnapshots
                    .Select(x => new { x.EnterpriseId, x.Year, x.Month }).Distinct().ToListAsync(ct);
                var monthCounts = monthRows.GroupBy(x => x.EnterpriseId).ToDictionary(g => g.Key, g => g.Count());

                var rateStates = _rateLimits.All;
                foreach (var e in enterprises)
                {
                    lastRuns.TryGetValue(e.Id, out var run);
                    bool? entTokenResolved = null;
                    if (!e.UseMockData)
                    {
                        try { entTokenResolved = (await _patResolver.TryResolveAsync(e, ct)).Resolved; }
                        catch { entTokenResolved = false; }
                    }
                    perEnterprise.Add(new EnterpriseDiagnostics
                    {
                        EnterpriseId = e.Id,
                        Slug = e.Slug,
                        DisplayName = e.DisplayName,
                        Enabled = e.Enabled,
                        UseMock = e.UseMockData,
                        SnapshotAgeHours = run is null ? null : Math.Round((now - (run.CompletedUtc ?? run.StartedUtc)).TotalHours, 2),
                        LastSnapshotStatus = run?.Status,
                        LastSnapshotCompletedUtc = run?.CompletedUtc,
                        LastSnapshotRowsWritten = run?.RowsWritten,
                        CostCenters = ccCounts.GetValueOrDefault(e.Id),
                        Budgets = budgetCounts.GetValueOrDefault(e.Id),
                        MonthsWithData = monthCounts.GetValueOrDefault(e.Id),
                        TokenResolved = entTokenResolved,
                        RateLimitRemaining = rateStates.TryGetValue(e.Slug, out var rl) ? rl.Remaining : null,
                    });
                }
            }

            var anyRate = _rateLimits.All.Values
                .Where(s => s.Remaining is not null)
                .OrderBy(s => s.Remaining)
                .FirstOrDefault();

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
                GitHubRateLimitRemaining = anyRate?.Remaining,
                GitHubRateLimitSeenUtc = anyRate?.LastSeenUtc,
                Enterprises = perEnterprise,
                InstanceId = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"),
                CollectedUtc = now,
            };
        }
    }
}
