using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Pulls current-month usage for every licensed user from the billing client
    /// (real or mock) and upserts it into the database, then purges snapshots older
    /// than the retention window. Runs sequentially and is the ONLY caller of the
    /// GitHub API — the UI reads exclusively from the database.
    /// </summary>
    public sealed class SnapshotService
    {
        private readonly IGitHubBillingClient _client;
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<SnapshotService> _logger;

        public SnapshotService(
            IGitHubBillingClient client,
            IDbContextFactory<BillingDbContext> dbFactory,
            IConfiguration config,
            ILogger<SnapshotService> logger)
        {
            _client = client;
            _dbFactory = dbFactory;
            _config = config;
            _logger = logger;
        }

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

        public async Task RunAsync(CancellationToken ct = default)
        {
            var enterprise = _config["GitHub:Enterprise"] ?? "";
            var retentionMonths = _config.GetValue("Retention:Months", 6);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var run = new SnapshotRun { StartedUtc = DateTime.UtcNow };
            db.SnapshotRuns.Add(run);
            await db.SaveChangesAsync(ct);

            try
            {
                var now = DateTime.UtcNow;
                var users = await _client.GetEnterpriseUsersAsync(enterprise, ct);
                var costCenters = await _client.GetCostCentersAsync(enterprise, ct);
                var userToCc = BuildUserCostCenterMap(costCenters);

                var written = 0;
                foreach (var u in users)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(u.GitHubComLogin)) continue;

                    var usage = await _client.GetCurrentMonthUsageForUserAsync(enterprise, u.GitHubComLogin, ct);
                    if (usage?.UsageItems is null) continue;

                    var (ccId, ccName) = usage.CostCenter is not null
                        ? (usage.CostCenter.Id, usage.CostCenter.Name)
                        : userToCc.GetValueOrDefault(u.GitHubComLogin);

                    foreach (var item in usage.UsageItems)
                    {
                        var existing = await db.UsageSnapshots.FirstOrDefaultAsync(x =>
                            x.Year == now.Year && x.Month == now.Month && x.Day == 1 &&
                            x.UserLogin == u.GitHubComLogin && x.Model == item.Model && x.Sku == item.Sku, ct);

                        if (existing is null)
                        {
                            db.UsageSnapshots.Add(new UsageSnapshot
                            {
                                SnapshotUtc = now, Year = now.Year, Month = now.Month, Day = 1,
                                UserLogin = u.GitHubComLogin, UserName = u.GitHubComName,
                                CostCenterId = ccId, CostCenterName = ccName,
                                Product = item.Product, Sku = item.Sku, Model = item.Model,
                                NetQuantity = item.NetQuantity, NetAmount = item.NetAmount, GrossAmount = item.GrossAmount
                            });
                        }
                        else
                        {
                            existing.SnapshotUtc = now;
                            existing.CostCenterId = ccId; existing.CostCenterName = ccName;
                            existing.NetQuantity = item.NetQuantity; existing.NetAmount = item.NetAmount; existing.GrossAmount = item.GrossAmount;
                        }
                        written++;
                    }
                    await db.SaveChangesAsync(ct);
                }

                // ── Cost-center directory (current names, keyed by GitHub's stable id) ──
                // Refreshed every run so a rename in GitHub propagates to reports/trends/the admin
                // mapping dropdown without rewriting the frozen historical name on past snapshot rows.
                var existingDirectory = await db.CostCenterDirectory.ToDictionaryAsync(x => x.CostCenterId, ct);
                foreach (var cc in costCenters)
                {
                    if (existingDirectory.TryGetValue(cc.Id, out var dirEntry))
                    {
                        dirEntry.CurrentName = cc.Name;
                        dirEntry.LastSeenUtc = now;
                    }
                    else
                    {
                        var newEntry = new CostCenterDirectoryEntry { CostCenterId = cc.Id, CurrentName = cc.Name, LastSeenUtc = now };
                        db.CostCenterDirectory.Add(newEntry);
                        existingDirectory[cc.Id] = newEntry;
                    }
                }
                await db.SaveChangesAsync(ct);

                // ── Budgets (GOVERNED IN GITHUB; snapshotted here for read-only display) ──
                var ccNameById = costCenters.ToDictionary(c => c.Id, c => c.Name, StringComparer.OrdinalIgnoreCase);
                // Load existing rows once, then track adds made during THIS run too — querying the DB per
                // iteration misses not-yet-saved Adds, so duplicate scopes/cost-centers in the same batch
                // (e.g. more than one "org" budget) would otherwise insert twice and violate the unique
                // index on (Scope, CostCenterId).
                var existingBudgets = await db.BudgetSnapshots.ToDictionaryAsync(x => (x.Scope, x.CostCenterId), ct);
                foreach (var gb in await _client.GetBudgetsAsync(enterprise, ct))
                {
                    var isCc = string.Equals(gb.BudgetScope, "cost_center", StringComparison.OrdinalIgnoreCase);
                    var scopeVal = isCc ? BudgetScopes.CostCenter : BudgetScopes.Org;
                    var ccId = isCc ? (gb.BudgetEntityName ?? "") : "";
                    var ccName = isCc ? ccNameById.GetValueOrDefault(ccId) : null;
                    var key = (scopeVal, ccId);
                    if (existingBudgets.TryGetValue(key, out var existingB))
                    {
                        existingB.Amount = gb.BudgetAmount; existingB.ConsumedAmount = gb.ConsumedAmount ?? 0m; existingB.CostCenterName = ccName ?? existingB.CostCenterName; existingB.SnapshotUtc = now;
                    }
                    else
                    {
                        var newB = new BudgetSnapshot { Scope = scopeVal, CostCenterId = ccId, CostCenterName = ccName, Amount = gb.BudgetAmount, ConsumedAmount = gb.ConsumedAmount ?? 0m, SnapshotUtc = now };
                        db.BudgetSnapshots.Add(newB);
                        existingBudgets[key] = newB;
                    }
                }
                await db.SaveChangesAsync(ct);

                // Retention purge (>= 3 months kept). Comparing Year/Month as integers (rather than
                // constructing a DateTime from column values inside the query) is what SQL Server's
                // EF Core provider can actually translate for ExecuteDelete — same class of bug as the
                // BudgetSnapshots ExecuteDelete fix.
                var (cutoffYear, cutoffMonth) = ComputeRetentionCutoff(now, retentionMonths);
                var stale = db.UsageSnapshots
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
                _logger.LogInformation("Snapshot complete: {Written} rows written, {Purged} purged.", written, purged);
            }
            catch (Exception ex)
            {
                run.Status = "failed";
                run.Error = ex.Message;
                run.CompletedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                _logger.LogError(ex, "Snapshot run failed.");
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
