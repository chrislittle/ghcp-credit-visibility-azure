using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Authorization
{
    /// <summary>
    /// Helpers for reading the caller's Entra security-group object IDs from claims.
    /// Behind Easy Auth the app registration is configured to emit the "groups" claim
    /// (group_membership_claims = SecurityGroup), so group IDs arrive in the token and are
    /// hydrated onto HttpContext.User by EasyAuthClaimsMiddleware — no Graph call needed.
    /// NOTE: if a user is in >200 groups Entra omits the claim (emits a "hasgroups"/overage
    /// marker); handling that overflow via Microsoft Graph is a future enhancement.
    /// </summary>
    public static class GroupClaims
    {
        public static IReadOnlyCollection<string> GetGroupObjectIds(ClaimsPrincipal user)
        {
            if (user?.Identity?.IsAuthenticated != true) return Array.Empty<string>();
            return user.FindAll("groups").Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>The signed-in user's Entra object ID (the "oid" claim), used for user-type mappings.</summary>
        public static string? GetUserObjectId(ClaimsPrincipal user)
        {
            if (user?.Identity?.IsAuthenticated != true) return null;
            // Entra emits the object ID under either the short "oid" or the SOAP-style URI claim type.
            return user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                ?? user.FindFirst("oid")?.Value;
        }
    }

    /// <summary>
    /// Decides whether the signed-in user is an application administrator (can see all data AND
    /// manage the group→cost-center mappings). Admin is granted by EITHER:
    ///   (a) the Entra "Admin" app role — a bootstrap so the first admin can always sign in, or
    ///   (b) membership of an Entra group designated as an admin group in the app (DB, self-service).
    /// </summary>
    public interface IAppAdminChecker
    {
        Task<bool> IsAdminAsync(ClaimsPrincipal user, CancellationToken ct = default);
    }

    public sealed class AppAdminChecker : IAppAdminChecker
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        public AppAdminChecker(IDbContextFactory<BillingDbContext> dbFactory) => _dbFactory = dbFactory;

        public async Task<bool> IsAdminAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            if (user?.Identity?.IsAuthenticated != true) return false;

            // (a) Bootstrap: Entra "Admin" app role.
            if (user.IsInRole("Admin")) return true;

            // (b) Self-service: the user themselves, or one of their groups, is a designated admin principal.
            var groups = GroupClaims.GetGroupObjectIds(user);
            var oid = GroupClaims.GetUserObjectId(user);
            if (groups.Count == 0 && string.IsNullOrEmpty(oid)) return false;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.AdminPrincipals.AnyAsync(a =>
                (a.PrincipalType == PrincipalTypes.Group && groups.Contains(a.PrincipalObjectId)) ||
                (a.PrincipalType == PrincipalTypes.User && oid != null && a.PrincipalObjectId == oid), ct);
        }
    }
}
