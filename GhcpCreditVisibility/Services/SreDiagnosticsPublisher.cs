using Microsoft.ApplicationInsights;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Publishes the app's operational health to Application Insights as custom metrics every few
    /// minutes, so failures that live only in the private database — a stalled snapshot job, a
    /// broken Key Vault reference, data that has gone wrong — become alertable and reachable by an
    /// out-of-network reliability agent.
    ///
    /// This is the foundation the Azure SRE Agent integration depends on: without these series there
    /// is nothing for an alert rule to fire on and nothing for the agent to query. It runs on every
    /// instance; the metrics are gauges, so concurrent publishers just report the same values.
    ///
    /// In local development the Application Insights connection string is absent, so TelemetryClient
    /// is a no-op and this loops harmlessly.
    /// </summary>
    public sealed class SreDiagnosticsPublisher : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        private readonly IServiceProvider _sp;
        private readonly ILogger<SreDiagnosticsPublisher> _logger;

        // TelemetryClient is resolved SOFTLY at runtime (not via the constructor) so that a missing
        // registration can never crash the app at startup. An observability component taking the
        // whole process down is a worse failure than losing the metrics. (Some hosting environments
        // — e.g. App Service with codeless Application Insights attach — don't register TelemetryClient
        // in DI even though AddApplicationInsightsTelemetry() was called.)
        public SreDiagnosticsPublisher(
            IServiceProvider sp,
            ILogger<SreDiagnosticsPublisher> logger)
        {
            _sp = sp;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var telemetry = _sp.GetService<TelemetryClient>();
            if (telemetry is null)
            {
                _logger.LogWarning(
                    "TelemetryClient is not registered; SRE diagnostics metrics will not be published. " +
                    "The app is unaffected. (Check that Application Insights is wired and codeless attach " +
                    "isn't suppressing the SDK's TelemetryClient.)");
                return;
            }

            // Brief initial delay so the first publish doesn't race DB/schema warm-up (and so its
            // pending-migrations reading reflects steady state, not the migrator still running).
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _sp.CreateScope();
                    var collector = scope.ServiceProvider.GetRequiredService<SreDiagnosticsCollector>();
                    Publish(telemetry, await collector.CollectAsync(stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Diagnostics must never take the app down; a DB blip just means we skip a tick.
                    _logger.LogWarning(ex, "SRE diagnostics publish failed; will retry next interval.");
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static void Publish(TelemetryClient telemetry, DiagnosticsSnapshot s)
        {
            if (s.SnapshotAgeHours is double age)
                telemetry.GetMetric("ghcp.snapshot.age_hours").TrackValue(age);

            telemetry.GetMetric("ghcp.snapshot.last_status").TrackValue(
                string.Equals(s.LastSnapshotStatus, "succeeded", StringComparison.OrdinalIgnoreCase) ? 1 : 0);

            if (s.LastSnapshotRowsWritten is int rows)
                telemetry.GetMetric("ghcp.snapshot.rows_written").TrackValue(rows);

            telemetry.GetMetric("ghcp.db.pending_migrations").TrackValue(s.PendingMigrations);
            telemetry.GetMetric("ghcp.data.costcenters").TrackValue(s.CostCenters);
            telemetry.GetMetric("ghcp.data.budgets").TrackValue(s.Budgets);
            telemetry.GetMetric("ghcp.data.months_with_data").TrackValue(s.MonthsWithData);

            if (s.GitHubTokenResolved is bool resolved)
                telemetry.GetMetric("ghcp.github.token_resolved").TrackValue(resolved ? 1 : 0);

            if (s.GitHubRateLimitRemaining is int remaining)
                telemetry.GetMetric("ghcp.github.rate_limit_remaining").TrackValue(remaining);
        }
    }
}
