using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Readiness check: is the database reachable AND is the schema applied? This is what turns
    /// "green" once private DNS has resolved (private/CAF deployments), the SQL grant has landed
    /// (system_assigned), and the background migrator has applied migrations. Ops can poll
    /// <c>/health/ready</c> to watch the warm-up instead of guessing.
    ///
    ///   Healthy  → DB reachable, no pending migrations (fully ready)
    ///   Degraded → DB reachable but migrations still pending (schema warming up)  → /health/ready returns 503
    ///   Unhealthy→ can't reach the DB yet (DNS / private endpoint / firewall / grant not ready) → 503
    ///
    /// The in-memory dev database is always reported Healthy (no relational schema/migrations).
    /// </summary>
    public sealed class DatabaseHealthCheck : IHealthCheck
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        public DatabaseHealthCheck(IDbContextFactory<BillingDbContext> dbFactory) => _dbFactory = dbFactory;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

                if (!db.Database.IsRelational())
                    return HealthCheckResult.Healthy("In-memory development database (no migrations).");

                if (!await db.Database.CanConnectAsync(cancellationToken))
                    return HealthCheckResult.Unhealthy("Cannot reach the database yet (DNS/private endpoint, firewall, or identity grant may still be provisioning).");

                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count > 0)
                    return HealthCheckResult.Degraded(
                        $"Database reachable; {pending.Count} migration(s) pending (schema warming up).",
                        data: new Dictionary<string, object> { ["pendingMigrations"] = pending });

                return HealthCheckResult.Healthy("Database reachable and schema up to date.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Database not ready.", ex);
            }
        }
    }
}
