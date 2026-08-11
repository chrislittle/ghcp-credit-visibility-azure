using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Pulls current-month usage for every licensed user of every ENABLED registry enterprise from
    /// that enterprise's billing client (real or mock, routed per row) and upserts it into the
    /// database, then purges snapshots older than the retention window. Enterprises run
    /// SEQUENTIALLY with per-enterprise error isolation: one enterprise's expired PAT or API outage
    /// records a failed <see cref="SnapshotRun"/> for THAT enterprise and the loop continues — it
    /// never aborts the others. This is the ONLY caller of the GitHub API — the UI reads exclusively
    /// from the database. The registry is re-read every cycle, so a newly enabled enterprise is
    /// picked up on the next run with no restart.
    /// </summary>
    public sealed class SnapshotService
    {
        private readonly IEnterpriseBillingClientFactory _clientFactory;
        private readonly EnterpriseRegistryService _registry;
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<SnapshotService> _logger;
        private readonly TelemetryClient? _telemetry;

        public SnapshotService(
            IEnterpriseBillingClientFactory clientFactory,
            EnterpriseRegistryService registry,
            IDbContextFactory<BillingDbContext> dbFactory,
            IConfiguration config,
            ILogger<SnapshotService> logger,
            TelemetryClient? telemetry = null)
        {
            _clientFactory = clientFactory;
            _registry = registry;
            _dbFactory = dbFactory;
            _config = config;
            _logger = logger;
            _telemetry = telemetry;
        }

        /// <summary>App Service instance running this snapshot — lets the SnapshotRunCompleted event distinguish concurrent runs.</summary>
        private static string InstanceId =>
            Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "local";

        /// <summary>
        /// Floor on months of history kept, regardless of configuration. Reports and trends need at
        /// least a quarter to mean anything, and purged rows are UNRECOVERABLE — GitHub's billing
        /// API only serves the current month, so history exists nowhere else once deleted.
        /// </summary>
        public const int MinRetentionMonths = 3;

        /// <summary>
        /// Returns the first (Year, Month) that is KEPT; rows strictly older than it are purged.
        /// Expressed as integers rather than a <see cref="DateTime"/> because the purge predicate
        /// compares the Year/Month columns directly — building a DateTime from column values inside
        /// the query is not translatable by the SQL Server provider.
        /// </summary>
        public static (int Year, int Month) ComputeRetentionCutoff(DateTime nowUtc, int retentionMonths)
        {
            var months = Math.Max(MinRetentionMonths, retentionMonths);
            var cutoff = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-months);
            return (cutoff.Year, cutoff.Month);
        }

        /// <summary>
        /// One full cycle: bootstrap the registry (idempotent), then snapshot each enabled
        /// enterprise in turn. Per-enterprise failures are recorded and swallowed (isolation);
        /// only infrastructure failures (registry/DB unreachable) propagate to the caller's
        /// retry/backoff.
        /// </summary>
        public async Task RunAsync(CancellationToken ct = default)
        {
            await _registry.EnsureBootstrapAsync(ct);
            var enterprises = await _registry.GetEnabledAsync(ct);
            if (enterprises.Count == 0)
            {
                _logger.LogWarning("Snapshot cycle skipped: no enabled enterprises in the registry.");
                return;
            }

            foreach (var enterprise in enterprises)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RunForEnterpriseAsync(enterprise, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Isolation by design: this enterprise's run row + SnapshotFailed event already
                    // record the failure (see RunForEnterpriseAsync); the remaining enterprises
                    // still get their snapshot this cycle.
                    _logger.LogError(ex, "Snapshot for enterprise '{Slug}' failed; continuing with the next enterprise.", enterprise.Slug);
                }
            }
        }

        private async Task RunForEnterpriseAsync(Enterprise enterprise, CancellationToken ct)
        {
            var retentionMonths = _config.GetValue("Retention:Months", 6);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var run = new SnapshotRun { EnterpriseId = enterprise.Id, StartedUtc = DateTime.UtcNow };
            db.SnapshotRuns.Add(run);
            await db.SaveChangesAsync(ct);

            try
            {
                var now = DateTime.UtcNow;
                var client = await _clientFactory.GetClientAsync(enterprise, ct);
                var users = await client.GetEnterpriseUsersAsync(enterprise.Slug, ct);
                var costCenters = await client.GetCostCentersAsync(enterprise.Slug, ct);
                var userToCc = BuildUserCostCenterMap(costCenters);

                var written = 0;
                foreach (var u in users)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(u.GitHubComLogin)) continue;

                    var usage = await client.GetCurrentMonthUsageForUserAsync(enterprise.Slug, u.GitHubComLogin, ct);
                    if (usage?.UsageItems is null) continue;

                    var (ccId, ccName) = usage.CostCenter is not null
                        ? (usage.CostCenter.Id, usage.CostCenter.Name)
                        : userToCc.GetValueOrDefault(u.GitHubComLogin);

                    foreach (var item in usage.UsageItems)
                    {
                        var existing = await db.UsageSnapshots.FirstOrDefaultAsync(x =>
                            x.EnterpriseId == enterprise.Id &&
                            x.Year == now.Year && x.Month == now.Month && x.Day == 1 &&
                            x.UserLogin == u.GitHubComLogin && x.Model == item.Model && x.Sku == item.Sku, ct);

                        if (existing is null)
                        {
                            db.UsageSnapshots.Add(new UsageSnapshot
                            {
                                EnterpriseId = enterprise.Id,
                                SnapshotUtc = now, Year = now.Year, Month = now.Month, Day = 1,
                                UserLogin = u.GitHubComLogin, UserName = u.GitHubComName,
                                CostCenterId = ccId, CostCenterName = ccName,
                                Product = item.Product, Sku = item.Sku, Model = item.Model,
                                NetQuantity = item.NetQuantity, NetAmount = item.NetAmount, GrossAmount = item.GrossAmount,
                                DiscountQuantity = item.DiscountQuantity, DiscountAmount = item.DiscountAmount,
                                PricePerUnit = item.PricePerUnit, GrossQuantity = item.GrossQuantity
                            });
                        }
                        else
                        {
                            existing.SnapshotUtc = now;
                            existing.CostCenterId = ccId; existing.CostCenterName = ccName;
                            existing.NetQuantity = item.NetQuantity; existing.NetAmount = item.NetAmount; existing.GrossAmount = item.GrossAmount;
                            // Must be updated alongside the amounts above: within a month this row is
                            // rewritten in place on every run, so a field refreshed on INSERT but not
                            // on UPDATE would silently freeze at its first-of-month value.
                            existing.DiscountQuantity = item.DiscountQuantity; existing.DiscountAmount = item.DiscountAmount;
                            existing.PricePerUnit = item.PricePerUnit; existing.GrossQuantity = item.GrossQuantity;
                        }
                        written++;
                    }
                    await db.SaveChangesAsync(ct);
                }

                // ── Cost-center directory (current names, keyed by (enterprise, GitHub's stable id)) ──
                // Refreshed every run so a rename in GitHub propagates to reports/trends/the admin
                // mapping dropdown without rewriting the frozen historical name on past snapshot rows.
                var existingDirectory = await db.CostCenterDirectory
                    .Where(x => x.EnterpriseId == enterprise.Id)
                    .ToDictionaryAsync(x => x.CostCenterId, ct);
                foreach (var cc in costCenters)
                {
                    if (existingDirectory.TryGetValue(cc.Id, out var dirEntry))
                    {
                        dirEntry.CurrentName = cc.Name;
                        dirEntry.LastSeenUtc = now;
                    }
                    else
                    {
                        var newEntry = new CostCenterDirectoryEntry { EnterpriseId = enterprise.Id, CostCenterId = cc.Id, CurrentName = cc.Name, LastSeenUtc = now };
                        db.CostCenterDirectory.Add(newEntry);
                        existingDirectory[cc.Id] = newEntry;
                    }
                }
                await db.SaveChangesAsync(ct);

                // ── Budgets (GOVERNED IN GITHUB; snapshotted here for read-only display) ──
                // Load THIS enterprise's existing rows once, then track adds made during the run too —
                // querying the DB per iteration misses not-yet-saved Adds, so duplicate scopes/cost-
                // centers in the same batch would otherwise insert twice and violate the unique
                // index on (EnterpriseId, Scope, CostCenterId).
                // Keyed by GitHub's own budget id (see BudgetScopeMapper). The previous key was
                // (Scope, CostCenterId), which collapsed every non-cost-center scope onto one row.
                var existingBudgets = await db.BudgetSnapshots
                    .Where(x => x.EnterpriseId == enterprise.Id)
                    .ToDictionaryAsync(x => x.GitHubBudgetId, StringComparer.Ordinal, ct);
                var seenBudgetKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var gb in await client.GetBudgetsAsync(enterprise.Slug, ct))
                {
                    var key = BudgetScopeMapper.KeyFor(gb);
                    // GitHub returning two budgets with the same id in one response would violate the
                    // unique index and fail the whole run; skip the duplicate and carry on instead.
                    if (!seenBudgetKeys.Add(key))
                    {
                        _logger.LogWarning("Duplicate budget id '{Key}' from GitHub for '{Slug}'; ignoring the repeat.", key, enterprise.Slug);
                        continue;
                    }
                    if (!existingBudgets.TryGetValue(key, out var row))
                    {
                        row = new BudgetSnapshot { EnterpriseId = enterprise.Id };
                        db.BudgetSnapshots.Add(row);
                        existingBudgets[key] = row;
                    }
                    BudgetScopeMapper.Apply(row, gb, costCenters, now);
                }
                // Budgets that GitHub no longer reports for this enterprise are removed — this covers
                // budgets deleted in GitHub AND self-heals rows persisted under a stale key. That
                // self-healing is what migrates the pre-fix rows: on the first run after deploy every
                // row keyed the old way is absent from seenBudgetKeys, so it is deleted and reinserted
                // under its GitHub budget id. No backfill script is required, and the table converges
                // within a single cycle.
                var staleBudgets = existingBudgets
                    .Where(kv => !seenBudgetKeys.Contains(kv.Key))
                    .Select(kv => kv.Value)
                    .ToList();
                if (staleBudgets.Count > 0) db.BudgetSnapshots.RemoveRange(staleBudgets);
                await db.SaveChangesAsync(ct);

                // Retention purge (>= 3 months kept), scoped to THIS enterprise so the purge count on
                // the run row stays meaningful. Comparing Year/Month as integers (rather than
                // constructing a DateTime from column values inside the query) is what SQL Server's
                // EF Core provider can actually translate for ExecuteDelete.
                var (cutoffYear, cutoffMonth) = ComputeRetentionCutoff(now, retentionMonths);
                var stale = db.UsageSnapshots
                    .Where(x => x.EnterpriseId == enterprise.Id)
                    .Where(x => x.Year < cutoffYear || (x.Year == cutoffYear && x.Month < cutoffMonth));

                int purged;
                if (db.Database.IsRelational())
                {
                    // Azure SQL: set-based delete in a single statement, no entities materialized.
                    purged = await stale.ExecuteDeleteAsync(ct);
                }
                else
                {
                    // Local dev (in-memory provider) has no ExecuteDelete support — without this
                    // fallback the whole snapshot run throws here, on its very last step, and the
                    // job never completes locally. Volumes are tiny in dev, so load + RemoveRange
                    // is fine.
                    var staleRows = await stale.ToListAsync(ct);
                    db.UsageSnapshots.RemoveRange(staleRows);
                    await db.SaveChangesAsync(ct);
                    purged = staleRows.Count;
                }

                run.RowsWritten = written;
                run.RowsPurged = purged;
                run.Status = "succeeded";
                run.CompletedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await _registry.MarkSnapshotCompletedAsync(enterprise.Id, run.CompletedUtc.Value, ct);
                _logger.LogInformation("Snapshot complete for '{Slug}': {Written} rows written, {Purged} purged.", enterprise.Slug, written, purged);

                // Point-in-time signal for the SRE agent / alert rules: WHICH ENTERPRISE, which
                // instance ran, how long it took, and what it wrote. One completion per enterprise
                // per cycle is the healthy pattern (the cross-instance lease still guarantees a
                // single instance runs the whole cycle).
                _telemetry?.TrackEvent("SnapshotRunCompleted",
                    new Dictionary<string, string> { ["status"] = "succeeded", ["instanceId"] = InstanceId, ["enterprise"] = enterprise.Slug },
                    new Dictionary<string, double>
                    {
                        ["rowsWritten"] = written,
                        ["rowsPurged"] = purged,
                        ["durationMs"] = (run.CompletedUtc.Value - run.StartedUtc).TotalMilliseconds,
                    });
            }
            catch (Exception ex)
            {
                run.Status = "failed";
                run.Error = ex.Message;
                run.CompletedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                _logger.LogError(ex, "Snapshot run failed for enterprise '{Slug}'.", enterprise.Slug);

                _telemetry?.TrackEvent("SnapshotFailed",
                    new Dictionary<string, string> { ["error"] = ex.Message, ["instanceId"] = InstanceId, ["enterprise"] = enterprise.Slug },
                    new Dictionary<string, double>
                    {
                        ["durationMs"] = (DateTime.UtcNow - run.StartedUtc).TotalMilliseconds,
                    });
                throw;
            }
        }

        private static Dictionary<string, (string?, string?)> BuildUserCostCenterMap(IReadOnlyList<Models.CostCenter> costCenters)
        {
            var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
            foreach (var cc in costCenters)
                foreach (var r in cc.Resources.Where(r => string.Equals(r.Type, "User", StringComparison.OrdinalIgnoreCase)))
                    if (!string.IsNullOrWhiteSpace(r.Name)) map[r.Name] = (cc.Id, cc.Name);
            return map;
        }
    }
}
