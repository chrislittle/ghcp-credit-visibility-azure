using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Cross-instance mutual exclusion backed by SQL Server session-scoped application locks
    /// (<c>sp_getapplock</c>). Used so that work which must run exactly once per deployment —
    /// the snapshot job — runs on ONE App Service instance at a time, even though the hosting
    /// <see cref="BackgroundService"/> is started on every instance.
    ///
    /// Why this and not "just make the writes idempotent": a second concurrent run also doubles
    /// the per-user GitHub API traffic (see <see cref="RealGitHubBillingClient"/>), which is
    /// rate-limited. Serialising the whole run fixes both the unique-index collisions and the
    /// wasted API budget.
    ///
    /// The lock is held by the SQL SESSION, so it is released automatically if the instance
    /// crashes, is recycled, or loses its connection — there is no lease to expire and no
    /// stuck-lock recovery path to get wrong. <see cref="DisposeAsync"/> releases it explicitly
    /// on the happy path.
    ///
    /// Permissions: <c>sp_getapplock</c>/<c>sp_releaseapplock</c> are executable by <c>public</c>,
    /// so both identity_mode options already have what they need — no extra SQL grant.
    ///
    /// Local development uses the in-memory provider, where there are no other instances; the
    /// lease is a no-op there and always succeeds.
    /// </summary>
    public sealed class SqlDistributedLease : IAsyncDisposable
    {
        /// <summary>Lease name for the snapshot job (sp_getapplock @Resource, max 255 chars).</summary>
        public const string SnapshotResource = "GhcpCreditVisibility:Snapshot";

        /// <summary>Lease name for EF Core schema migrations.</summary>
        public const string MigrationResource = "GhcpCreditVisibility:Migrate";

        // Null for the in-memory (local dev) no-op lease. Otherwise this context owns the open
        // connection whose SESSION holds the lock, and must stay alive for the lease's lifetime.
        private readonly BillingDbContext? _db;
        private readonly string _resource;
        private readonly ILogger _logger;

        private SqlDistributedLease(BillingDbContext? db, string resource, ILogger logger)
        {
            _db = db;
            _resource = resource;
            _logger = logger;
        }

        /// <summary>
        /// Tries to take the named lease without waiting. Returns null if another instance holds
        /// it — that is the normal, expected outcome on every instance but one, not an error.
        /// Throws if the database can't be reached, so callers keep their existing retry/backoff
        /// behaviour during the DNS / grant / migration warm-up window.
        /// </summary>
        public static async Task<SqlDistributedLease?> TryAcquireAsync(
            IDbContextFactory<BillingDbContext> dbFactory,
            string resource,
            ILogger logger,
            CancellationToken ct = default)
        {
            var db = await dbFactory.CreateDbContextAsync(ct);
            try
            {
                // In-memory dev database: single process, nothing to serialise against.
                if (!db.Database.IsRelational())
                {
                    await db.DisposeAsync();
                    return new SqlDistributedLease(null, resource, logger);
                }

                // Explicitly opening the connection keeps it (and therefore the SQL session that
                // owns the lock) alive until this context is disposed.
                await db.Database.OpenConnectionAsync(ct);

                var conn = db.Database.GetDbConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "sp_getapplock";
                cmd.CommandType = CommandType.StoredProcedure;

                // Added FIRST by convention: some ADO.NET providers bind the RETURN value
                // positionally rather than by ParameterDirection.
                var returnValue = cmd.CreateParameter();
                returnValue.ParameterName = "@Result";
                returnValue.DbType = DbType.Int32;
                returnValue.Direction = ParameterDirection.ReturnValue;
                cmd.Parameters.Add(returnValue);

                AddParameter(cmd, "@Resource", DbType.String, resource);
                AddParameter(cmd, "@LockMode", DbType.String, "Exclusive");
                AddParameter(cmd, "@LockOwner", DbType.String, "Session");
                AddParameter(cmd, "@LockTimeout", DbType.Int32, 0); // fail fast; never queue behind the holder

                await cmd.ExecuteNonQueryAsync(ct);

                // 0 = granted immediately, 1 = granted after waiting. Negative values mean not
                // granted (-1 timeout — i.e. another instance holds it, -2 cancelled,
                // -3 deadlock victim, -999 parameter/other error).
                var result = returnValue.Value is int r ? r : -999;
                if (result >= 0)
                {
                    return new SqlDistributedLease(db, resource, logger);
                }

                await db.DisposeAsync();
                return null;
            }
            catch
            {
                await db.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_db is null) return; // no-op lease (local dev)

            try
            {
                var conn = _db.Database.GetDbConnection();
                if (conn.State == ConnectionState.Open)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "sp_releaseapplock";
                    cmd.CommandType = CommandType.StoredProcedure;
                    AddParameter(cmd, "@Resource", DbType.String, _resource);
                    AddParameter(cmd, "@LockOwner", DbType.String, "Session");
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                // Never fatal: closing the connection ends the session, which releases the lock.
                _logger.LogDebug(ex,
                    "Releasing lease '{Resource}' failed; it will be released when the connection closes.",
                    _resource);
            }
            finally
            {
                await _db.DisposeAsync();
            }
        }

        private static void AddParameter(DbCommand cmd, string name, DbType type, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.DbType = type;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
    }
}
