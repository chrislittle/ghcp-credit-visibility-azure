using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Runs the snapshot at startup (after ensuring the schema exists) and then on a
    /// fixed interval. For production, prefer an external scheduler (Container Apps Job
    /// or a Function timer) so the web tier stays stateless; this in-process timer keeps
    /// the POC self-contained.
    /// </summary>
    public sealed class SnapshotHostedService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

        private readonly IServiceProvider _sp;
        private readonly ILogger<SnapshotHostedService> _logger;

        public SnapshotHostedService(IServiceProvider sp, ILogger<SnapshotHostedService> logger)
        {
            _sp = sp;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Schema is owned elsewhere — DatabaseMigratorHostedService applies EF migrations on the
            // SQL path; the in-memory dev DB is created/seeded at startup. Do NOT call EnsureCreated
            // here: it would conflict with migrations (creates tables without the migration-history
            // table, breaking Migrate()) and race the migrator.

            // Initial snapshot: retry with short backoff so we don't wait a full interval if the DB,
            // schema, or the identity's DDL grant (system_assigned) isn't ready yet.
            var backoff = new[] { 5, 10, 20, 30, 60 };
            var attempt = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _sp.CreateScope();
                    var snapshot = scope.ServiceProvider.GetRequiredService<SnapshotService>();
                    await snapshot.RunAsync(stoppingToken);
                    break; // first snapshot succeeded
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
                    using var scope = _sp.CreateScope();
                    var snapshot = scope.ServiceProvider.GetRequiredService<SnapshotService>();
                    await snapshot.RunAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled snapshot failed; will retry next interval.");
                }
            }
        }
    }
}
