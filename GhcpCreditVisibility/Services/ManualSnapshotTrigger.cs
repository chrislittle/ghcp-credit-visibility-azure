using GhcpCreditVisibility.Data;
using Microsoft.EntityFrameworkCore;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Starts a snapshot for ONE enterprise on demand, from the admin console.
    ///
    /// Why this exists: the scheduled job is an in-process timer — startup, then every 12 hours —
    /// with no manual trigger, so the only way to make it run was to restart the container.
    /// Bouncing production to make a background job run is a bad answer, and onboarding an
    /// enterprise (add, seed PAT, enable, verify) previously meant either that or waiting up to
    /// half a day to find out whether the PAT even worked.
    ///
    /// Two things this must not do:
    ///  * BLOCK THE REQUEST. A snapshot is seconds for a small enterprise and minutes for a large
    ///    one, so the work runs on a background task and the page redirects immediately.
    ///  * COLLIDE with the scheduled run or another instance. It takes the same
    ///    <see cref="SqlDistributedLease"/> the timer does; if the lease is held, this run is
    ///    skipped rather than queued — the holder is already doing exactly this work, and running
    ///    twice would duplicate rate-limited GitHub traffic and race the unique indexes.
    /// </summary>
    public sealed class ManualSnapshotTrigger
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<ManualSnapshotTrigger> _logger;

        /// <summary>Ids currently running ON THIS INSTANCE. The SQL lease is what actually guarantees
        /// exclusivity across instances; this only exists so a double-click gets told "already
        /// running" instead of silently doing nothing.</summary>
        private readonly HashSet<long> _inFlight = new();
        private readonly object _gate = new();

        public ManualSnapshotTrigger(
            IServiceScopeFactory scopes,
            IDbContextFactory<BillingDbContext> dbFactory,
            IHostApplicationLifetime lifetime,
            ILogger<ManualSnapshotTrigger> logger)
        {
            _scopes = scopes;
            _dbFactory = dbFactory;
            _lifetime = lifetime;
            _logger = logger;
        }

        public enum StartResult { Started, AlreadyRunning }

        /// <summary>
        /// Fire-and-forget. Returns as soon as the work is scheduled — the caller redirects and the
        /// operator watches the enterprise row's "last snapshot" column update.
        /// </summary>
        public StartResult TryStart(long enterpriseId, string? actor)
        {
            lock (_gate)
            {
                if (!_inFlight.Add(enterpriseId)) return StartResult.AlreadyRunning;
            }

            // NOT the request's CancellationToken — that is cancelled the moment the response
            // completes, which would kill the snapshot a few milliseconds after starting it.
            // ApplicationStopping lets a real shutdown cancel it cleanly instead.
            var ct = _lifetime.ApplicationStopping;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Same lease as the scheduled job. Held => that run is already doing this work.
                    await using var lease = await SqlDistributedLease.TryAcquireAsync(
                        _dbFactory, SqlDistributedLease.SnapshotResource, _logger, ct);

                    if (lease is null)
                    {
                        _logger.LogInformation(
                            "Manual snapshot for enterprise {Id} skipped: the '{Resource}' lease is held " +
                            "(a scheduled or concurrent run is already in progress).",
                            enterpriseId, SqlDistributedLease.SnapshotResource);
                        return;
                    }

                    using var scope = _scopes.CreateScope();
                    var snapshot = scope.ServiceProvider.GetRequiredService<SnapshotService>();
                    _logger.LogInformation("Manual snapshot for enterprise {Id} started by {Actor}.",
                        enterpriseId, string.IsNullOrWhiteSpace(actor) ? "unknown" : actor);
                    await snapshot.RunOneAsync(enterpriseId, ct);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Manual snapshot for enterprise {Id} cancelled (app shutting down).", enterpriseId);
                }
                catch (Exception ex)
                {
                    // Nothing is awaiting this task, so an unlogged throw would vanish silently.
                    // The per-enterprise SnapshotRun row also records the failure for the UI.
                    _logger.LogError(ex, "Manual snapshot for enterprise {Id} failed.", enterpriseId);
                }
                finally
                {
                    lock (_gate) { _inFlight.Remove(enterpriseId); }
                }
            }, ct);

            return StartResult.Started;
        }
    }
}
