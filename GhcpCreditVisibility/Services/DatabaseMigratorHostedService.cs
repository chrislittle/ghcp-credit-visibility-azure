using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Applies EF Core migrations in the background with retry/backoff instead of blocking startup.
    /// This keeps the web app serving even if the database isn't ready or the app's managed identity
    /// hasn't yet been granted DDL rights (the one-time <c>post_deploy_sql_grant</c> in system_assigned
    /// mode). Once the grant lands, the next retry succeeds and the schema is built automatically —
    /// no crash-loop and no manual restart. In user_assigned_selfadmin mode the identity is the SQL
    /// admin, so the very first attempt succeeds.
    /// </summary>
    public sealed class DatabaseMigratorHostedService : IHostedService
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly ILogger<DatabaseMigratorHostedService> _logger;
        private readonly CancellationTokenSource _cts = new();
        private Task? _worker;

        public DatabaseMigratorHostedService(
            IDbContextFactory<BillingDbContext> dbFactory,
            ILogger<DatabaseMigratorHostedService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Fire-and-forget so app startup is never blocked by DB availability/permissions.
            _worker = Task.Run(() => MigrateWithRetryAsync(_cts.Token));
            return Task.CompletedTask;
        }

        private async Task MigrateWithRetryAsync(CancellationToken ct)
        {
            // Backoff schedule (seconds), capped — retries indefinitely at the cap until it succeeds.
            var backoff = new[] { 5, 10, 20, 30, 60 };
            var attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(ct);
                    var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                    if (pending.Count == 0)
                    {
                        _logger.LogInformation("Database schema is up to date (no pending migrations).");
                        return;
                    }

                    _logger.LogInformation("Applying {Count} pending migration(s): {Names}", pending.Count, string.Join(", ", pending));
                    await db.Database.MigrateAsync(ct);
                    _logger.LogInformation("Database migrations applied successfully on attempt {Attempt}.", attempt);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // app shutting down
                }
                catch (Exception ex)
                {
                    var delaySec = backoff[Math.Min(attempt - 1, backoff.Length - 1)];
                    _logger.LogWarning(ex,
                        "Database migration attempt {Attempt} failed: {Message}. Retrying in {Delay}s. " +
                        "If this is system_assigned mode, ensure the one-time 'post_deploy_sql_grant' T-SQL " +
                        "(grants the app identity db_ddladmin + read/write) has been run against the database.",
                        attempt, ex.Message, delaySec);
                    try { await Task.Delay(TimeSpan.FromSeconds(delaySec), ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            if (_worker is not null)
            {
                // Give the in-flight attempt a moment to observe cancellation.
                await Task.WhenAny(_worker, Task.Delay(Timeout.Infinite, cancellationToken));
            }
        }
    }
}
