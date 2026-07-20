using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Authorization
{
    /// <summary>
    /// The set of data a signed-in user is allowed to see. The dashboard/query layer
    /// filters snapshots to this scope so a manager sees only their people, an admin
    /// sees everything, etc.
    /// </summary>
    public sealed record UserScope(
        bool SeesAll,
        IReadOnlyCollection<string> CostCenterIds,
        IReadOnlyCollection<string> UserLogins)
    {
        public static UserScope All() => new(true, Array.Empty<string>(), Array.Empty<string>());
        public static UserScope None() => new(false, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// Turns the authenticated principal (from Entra via Easy Auth) into the set of
    /// GitHub cost centers they may view.
    /// </summary>
    public interface IUserScopeResolver
    {
        Task<UserScope> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default);
    }

    /// <summary>
    /// DB-backed principal→cost-center mapping. Group membership stays in Entra; the
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
            var costCenters = await db.PrincipalCostCenterMappings
                .Where(m =>
                    (m.PrincipalType == PrincipalTypes.Group && groups.Contains(m.PrincipalObjectId)) ||
                    (m.PrincipalType == PrincipalTypes.User && oid != null && m.PrincipalObjectId == oid))
                .Select(m => m.CostCenterId)
                .Distinct()
                .ToListAsync(ct);

            return costCenters.Count == 0
                ? UserScope.None()
                : new UserScope(false, costCenters, Array.Empty<string>());
        }
    }
}
