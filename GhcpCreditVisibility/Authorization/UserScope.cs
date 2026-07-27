using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Authorization
{
    /// <summary>An enterprise-qualified cost center — the unit of access. Two enterprises WILL both
    /// have a cost center named "Engineering", and (in principle) even ids could collide, so scope
    /// is always the PAIR, never the bare cost-center id.</summary>
    public readonly record struct EnterpriseCostCenter(long EnterpriseId, string CostCenterId);

    /// <summary>
    /// The set of data a signed-in user is allowed to see. The dashboard/query layer
    /// filters snapshots to this scope so a manager sees only their people, an admin
    /// sees everything, etc. <see cref="EnterpriseFilter"/> narrows any scope (including
    /// SeesAll) to one enterprise — that's the dashboard's enterprise dropdown; pages
    /// validate the chosen enterprise is within the user's visibility before applying it.
    /// </summary>
    public sealed record UserScope(
        bool SeesAll,
        IReadOnlyCollection<EnterpriseCostCenter> CostCenters,
        IReadOnlyCollection<string> UserLogins)
    {
        /// <summary>Optional narrowing to a single enterprise (the UI's enterprise filter).</summary>
        public long? EnterpriseFilter { get; init; }

        public static UserScope All() => new(true, Array.Empty<EnterpriseCostCenter>(), Array.Empty<string>());
        public static UserScope None() => new(false, Array.Empty<EnterpriseCostCenter>(), Array.Empty<string>());

        /// <summary>The distinct enterprises this scope grants any visibility into (empty for SeesAll — meaning "all").</summary>
        public IReadOnlyCollection<long> EnterpriseIds =>
            CostCenters.Select(c => c.EnterpriseId).Distinct().ToList();

        /// <summary>Bare cost-center ids (for display labels only — never for filtering).</summary>
        public IReadOnlyCollection<string> CostCenterIds =>
            CostCenters.Select(c => c.CostCenterId).Distinct().ToList();
    }

    /// <summary>
    /// Turns the authenticated principal (from Entra via Easy Auth) into the set of
    /// enterprise-qualified GitHub cost centers they may view.
    /// </summary>
    public interface IUserScopeResolver
    {
        Task<UserScope> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default);
    }

    /// <summary>
    /// DB-backed principal→(enterprise, cost-center) mapping. Group membership stays in Entra; the
    /// mapping to GitHub cost centers is managed in-app via the admin console. Scope is
    /// resolved per request, so mapping changes take effect on the next page load.
    /// Admins (Entra "Admin" role or a DB-designated admin principal) see all.
    /// </summary>
    public sealed class DbGroupScopeResolver : IUserScopeResolver
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly IAppAdminChecker _admin;

        public DbGroupScopeResolver(IDbContextFactory<BillingDbContext> dbFactory, IAppAdminChecker admin)
        {
            _dbFactory = dbFactory;
            _admin = admin;
        }

        public async Task<UserScope> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            // Admins (bootstrap Entra role or DB admin principal) see everything.
            if (await _admin.IsAdminAsync(user, ct)) return UserScope.All();

            var groups = GroupClaims.GetGroupObjectIds(user);
            var oid = GroupClaims.GetUserObjectId(user);
            if (groups.Count == 0 && string.IsNullOrEmpty(oid)) return UserScope.None();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var pairs = await db.PrincipalCostCenterMappings
                .Where(m =>
                    (m.PrincipalType == PrincipalTypes.Group && groups.Contains(m.PrincipalObjectId)) ||
                    (m.PrincipalType == PrincipalTypes.User && oid != null && m.PrincipalObjectId == oid))
                .Select(m => new { m.EnterpriseId, m.CostCenterId })
                .Distinct()
                .ToListAsync(ct);

            return pairs.Count == 0
                ? UserScope.None()
                : new UserScope(false,
                    pairs.Select(p => new EnterpriseCostCenter(p.EnterpriseId, p.CostCenterId)).ToList(),
                    Array.Empty<string>());
        }
    }
}
