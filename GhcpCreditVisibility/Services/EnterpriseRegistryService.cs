using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// The enterprise registry: single source of truth for which GitHub enterprises this deployment
    /// snapshots. Adding an enterprise is a DAY-2 RUNTIME operation — a registry row (admin console)
    /// plus a Key Vault secret — never a redeploy: the snapshot job re-reads the registry each cycle,
    /// alert rules split by the enterprise telemetry dimension, and the app's managed identity reads
    /// Key Vault at vault scope, so nothing about enterprise N+1 touches Terraform.
    /// </summary>
    public sealed class EnterpriseRegistryService
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<EnterpriseRegistryService> _logger;

        public EnterpriseRegistryService(
            IDbContextFactory<BillingDbContext> dbFactory,
            IConfiguration config,
            ILogger<EnterpriseRegistryService> logger)
        {
            _dbFactory = dbFactory;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Ensures the registry has its bootstrap row and that row reflects current config.
        /// Two cases:
        ///  - SQL path: the migration seeded row #1 with a placeholder slug (a migration cannot read
        ///    config) — replace the placeholder with the GitHub:Enterprise value on first run.
        ///  - Local dev (in-memory, EnsureCreated — no migrations): the table starts empty — insert
        ///    the bootstrap row here.
        /// Idempotent and cheap; called before each snapshot cycle and by the admin console.
        /// </summary>
        public async Task EnsureBootstrapAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var configSlug = _config["GitHub:Enterprise"];
            var useMock = _config.GetValue("GitHub:UseMock", true);

            var any = await db.Enterprises.AnyAsync(ct);
            if (!any)
            {
                db.Enterprises.Add(new Enterprise
                {
                    Slug = string.IsNullOrWhiteSpace(configSlug) ? Enterprise.BootstrapPlaceholderSlug : configSlug.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(configSlug) ? "Default enterprise" : configSlug.Trim(),
                    PatSecretName = Enterprise.DefaultPatSecretName,
                    UseMockData = useMock,
                    Enabled = true,
                    ModifiedBy = "bootstrap"
                });
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Enterprise registry bootstrapped with slug '{Slug}'.", configSlug ?? "(placeholder)");
                return;
            }

            // Replace the migration's placeholder slug with the configured one (once).
            var placeholder = await db.Enterprises
                .FirstOrDefaultAsync(e => e.Slug == Enterprise.BootstrapPlaceholderSlug, ct);
            if (placeholder is not null && !string.IsNullOrWhiteSpace(configSlug))
            {
                var slug = configSlug.Trim();
                if (!await db.Enterprises.AnyAsync(e => e.Slug == slug && e.Id != placeholder.Id, ct))
                {
                    placeholder.Slug = slug;
                    placeholder.DisplayName ??= slug;
                    placeholder.UseMockData = useMock;
                    placeholder.ModifiedBy = "bootstrap";
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Enterprise registry bootstrap row updated to slug '{Slug}'.", slug);
                }
            }
        }

        public async Task<IReadOnlyList<Enterprise>> GetAllAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.Enterprises.OrderBy(e => e.Id).ToListAsync(ct);
        }

        public async Task<IReadOnlyList<Enterprise>> GetEnabledAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.Enterprises.Where(e => e.Enabled).OrderBy(e => e.Id).ToListAsync(ct);
        }

        /// <summary>Id → display name (falls back to slug) for every registered enterprise — the
        /// lookup read paths use to label rows/badges.</summary>
        public async Task<IReadOnlyDictionary<long, string>> GetDisplayNamesAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.Enterprises.ToDictionaryAsync(
                e => e.Id,
                e => string.IsNullOrWhiteSpace(e.DisplayName) ? e.Slug : e.DisplayName!,
                ct);
        }

        /// <summary>
        /// Adds an enterprise. New enterprises start DISABLED by design: seed the PAT, watch the
        /// first snapshot succeed, inspect the data, then enable — nobody sees an enterprise's data
        /// until mappings exist, so a bad first snapshot stays invisible and reversible.
        /// </summary>
        public async Task<Enterprise> AddAsync(string slug, string? displayName, string? patSecretName, bool useMockData, string? modifiedBy, CancellationToken ct = default)
        {
            slug = (slug ?? "").Trim();
            if (slug.Length == 0) throw new ArgumentException("Enterprise slug is required.");
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            if (await db.Enterprises.AnyAsync(e => e.Slug == slug, ct))
                throw new ArgumentException($"An enterprise with slug '{slug}' is already registered. A duplicate registration would double every number for that enterprise.");

            var row = new Enterprise
            {
                Slug = slug,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? slug : displayName!.Trim(),
                PatSecretName = string.IsNullOrWhiteSpace(patSecretName) ? $"github-pat-{slug}" : patSecretName!.Trim(),
                UseMockData = useMockData,
                Enabled = false,
                ModifiedBy = modifiedBy
            };
            db.Enterprises.Add(row);
            await db.SaveChangesAsync(ct);
            return row;
        }

        public async Task UpdateAsync(long id, string slug, string? displayName, string? patSecretName, bool useMockData, string? modifiedBy, CancellationToken ct = default)
        {
            slug = (slug ?? "").Trim();
            if (slug.Length == 0) throw new ArgumentException("Enterprise slug is required.");
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.Enterprises.FindAsync(new object[] { id }, ct)
                ?? throw new ArgumentException("Enterprise not found.");
            if (await db.Enterprises.AnyAsync(e => e.Slug == slug && e.Id != id, ct))
                throw new ArgumentException($"An enterprise with slug '{slug}' is already registered.");

            row.Slug = slug;
            row.DisplayName = string.IsNullOrWhiteSpace(displayName) ? slug : displayName!.Trim();
            row.PatSecretName = string.IsNullOrWhiteSpace(patSecretName) ? row.PatSecretName : patSecretName!.Trim();
            row.UseMockData = useMockData;
            row.ModifiedBy = modifiedBy;
            await db.SaveChangesAsync(ct);
        }

        public async Task SetEnabledAsync(long id, bool enabled, string? modifiedBy, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.Enterprises.FindAsync(new object[] { id }, ct)
                ?? throw new ArgumentException("Enterprise not found.");
            row.Enabled = enabled;
            row.ModifiedBy = modifiedBy;
            await db.SaveChangesAsync(ct);
        }

        public sealed record PurgeResult(int UsageRows, int BudgetRows, int DirectoryRows, int MappingRows, int RunRows);

        /// <summary>
        /// Removes an enterprise AND all of its data — usage history, budgets, cost-center directory,
        /// principal mappings, and run history. Built for real decommissions/consolidations; also what
        /// makes a test/demo enterprise fully reversible. Treat as irreversible for real data: GitHub
        /// does retain a rolling 24-month window (its usage endpoints accept year/month/day), so this
        /// is recoverable IN PRINCIPLE — but only by a backfill job this app does not have, and only
        /// for the last two years. Anything older is gone the moment this runs.
        /// </summary>
        public async Task<PurgeResult> DeleteWithDataAsync(long id, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.Enterprises.FindAsync(new object[] { id }, ct)
                ?? throw new ArgumentException("Enterprise not found.");

            int usage, budgets, directory, mappings, runs;
            if (db.Database.IsRelational())
            {
                usage = await db.UsageSnapshots.Where(x => x.EnterpriseId == id).ExecuteDeleteAsync(ct);
                budgets = await db.BudgetSnapshots.Where(x => x.EnterpriseId == id).ExecuteDeleteAsync(ct);
                directory = await db.CostCenterDirectory.Where(x => x.EnterpriseId == id).ExecuteDeleteAsync(ct);
                mappings = await db.PrincipalCostCenterMappings.Where(x => x.EnterpriseId == id).ExecuteDeleteAsync(ct);
                runs = await db.SnapshotRuns.Where(x => x.EnterpriseId == id).ExecuteDeleteAsync(ct);
            }
            else
            {
                // Local dev (in-memory provider): no ExecuteDelete support; volumes are tiny.
                var u = await db.UsageSnapshots.Where(x => x.EnterpriseId == id).ToListAsync(ct);
                var b = await db.BudgetSnapshots.Where(x => x.EnterpriseId == id).ToListAsync(ct);
                var d = await db.CostCenterDirectory.Where(x => x.EnterpriseId == id).ToListAsync(ct);
                var m = await db.PrincipalCostCenterMappings.Where(x => x.EnterpriseId == id).ToListAsync(ct);
                var r = await db.SnapshotRuns.Where(x => x.EnterpriseId == id).ToListAsync(ct);
                db.UsageSnapshots.RemoveRange(u); db.BudgetSnapshots.RemoveRange(b);
                db.CostCenterDirectory.RemoveRange(d); db.PrincipalCostCenterMappings.RemoveRange(m);
                db.SnapshotRuns.RemoveRange(r);
                await db.SaveChangesAsync(ct);
                (usage, budgets, directory, mappings, runs) = (u.Count, b.Count, d.Count, m.Count, r.Count);
            }

            db.Enterprises.Remove(row);
            await db.SaveChangesAsync(ct);
            _logger.LogWarning(
                "Enterprise '{Slug}' (id {Id}) removed with cascade purge: {Usage} usage, {Budgets} budget, {Directory} directory, {Mappings} mapping, {Runs} run rows.",
                row.Slug, id, usage, budgets, directory, mappings, runs);
            return new PurgeResult(usage, budgets, directory, mappings, runs);
        }

        public async Task MarkSnapshotCompletedAsync(long id, DateTime utc, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.Enterprises.FindAsync(new object[] { id }, ct);
            if (row is not null) { row.LastSnapshotUtc = utc; await db.SaveChangesAsync(ct); }
        }
    }
}
