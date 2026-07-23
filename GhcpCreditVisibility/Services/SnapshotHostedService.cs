using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Runs the snapshot at startup (after ensuring the schema exists) and then on a
    /// fixed interval. For production, prefer an external scheduler (Container Apps Job
    /// or a Function timer) so the web tier stays stateless; this in-process timer keeps
    /// the POC self-contained.
    ///
    /// This service is started on EVERY App Service instance (autoscale runs 1-3), so each
    /// run is guarded by a cross-instance lease — see <see cref="SqlDistributedLease"/>.
    /// Without it, a redeploy or scale-out while the plan has more than one instance starts
    /// several snapshots within seconds of each other; they then collide on the unique index
    /// over (Year, Month, Day, UserLogin, Model, Sku) and on the BudgetSnapshots /
    /// CostCenterDirectory keys, failing the run and leaving the month partially written.
    /// </summary>
    public sealed class SnapshotHostedService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

        private readonly IServiceProvider _sp;
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly ILogger<SnapshotHostedService> _logger;

        public SnapshotHostedService(
            IServiceProvider sp,
            IDbContextFactory<BillingDbContext> dbFactory,
            ILogger<SnapshotHostedService> logger)
        {
            _sp = sp;
            _dbFactory = dbFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Schema is owned elsewhere — DatabaseMigratorHostedService applies EF migrations on the
            // SQL path; the in-memory dev DB is created/seeded at startup. Do NOT call EnsureCreated
            // here: it would conflict with migrations (creates tables without the migration-history
            // table, breaking Migrate()) and race the migrator.

            // Initial snapshot: retry with short backoff so we don't wait a full interval if the DB,
            // schema, or the identity's DDL grant (system_assigned) isn't ready yet. Losing the lease
            // race is NOT a failure — another instance is doing the work, so we stop retrying.
            var backoff = new[] { 5, 10, 20, 30, 60 };
            var attempt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunGuardedAsync(stoppingToken);
                    break; // first snapshot ran here, or is running on another instance
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    var delaySec = backoff[Math.Min(attempt, backoff.Length - 1)];
                    attempt++;
                    _logger.LogWarning(ex, "Initial snapshot attempt {Attempt} failed (DB/schema/grant may not be ready). Retrying in {Delay}s.", attempt, delaySec);
                    try { await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken); }
                    catch (OperationCanceledException) { return; }
                }
            }

            // Steady-state cadence.
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }

                try
                {
                    await RunGuardedAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled snapshot failed; will retry next interval.");
                }
            }
        }

        /// <summary>
        /// Runs one snapshot, but only if this instance can take the snapshot lease. If another
        /// instance holds it the run is skipped — the holder is already doing exactly this work,
        /// and queueing behind it would just duplicate the (rate-limited) GitHub API traffic.
        /// A failure to REACH the database still throws, so the caller's retry/backoff applies.
        /// </summary>
        private async Task RunGuardedAsync(CancellationToken ct)
        {
            await using var lease = await SqlDistributedLease.TryAcquireAsync(
                _dbFactory, SqlDistributedLease.SnapshotResource, _logger, ct);

            if (lease is null)
            {
                _logger.LogInformation(
                    "Snapshot skipped on this instance: another instance holds the '{Resource}' lease.",
                    SqlDistributedLease.SnapshotResource);
                return;
            }

            using var scope = _sp.CreateScope();
            var snapshot = scope.ServiceProvider.GetRequiredService<SnapshotService>();
            await snapshot.RunAsync(ct);
        }
    }
}
