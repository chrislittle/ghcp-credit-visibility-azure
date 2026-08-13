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
    /// filters snapshots to this scope so a manager sees only their people, a global reader
    /// sees everything, etc. <see cref="EnterpriseFilter"/> narrows any scope (including
    /// SeesAll) to one enterprise — that's the dashboard's enterprise dropdown; pages
    /// validate the chosen enterprise is within the user's visibility before applying it.
    ///
    /// There are THREE tiers, and they UNION rather than override:
    ///   <see cref="SeesAll"/>              — everything, every enterprise (global Enterprise Reader)
    ///   <see cref="ReadAllEnterpriseIds"/> — everything WITHIN these enterprises
    ///   <see cref="CostCenters"/>          — only these (enterprise, cost center) pairs
    ///
    /// Being an application administrator grants NONE of these. That separation is the point:
    /// managing the console and seeing the whole company's spend are different rights.
    /// </summary>
    public sealed record UserScope(
        bool SeesAll,
        IReadOnlyCollection<EnterpriseCostCenter> CostCenters,
        IReadOnlyCollection<string> UserLogins)
    {
        /// <summary>Optional narrowing to a single enterprise (the UI's enterprise filter).</summary>
        public long? EnterpriseFilter { get; init; }

        /// <summary>
        /// Enterprises where the viewer sees EVERYTHING — every cost center, the organization
        /// dimension, enterprise-wide budgets. An init property rather than a positional parameter
        /// so existing two-tier construction (tests included) keeps compiling and defaults to empty.
        /// </summary>
        public IReadOnlyCollection<long> ReadAllEnterpriseIds { get; init; } = Array.Empty<long>();

        public static UserScope All() => new(true, Array.Empty<EnterpriseCostCenter>(), Array.Empty<string>());
        public static UserScope None() => new(false, Array.Empty<EnterpriseCostCenter>(), Array.Empty<string>());

        /// <summary>An Enterprise Reader over the given enterprises (empty/none = use <see cref="All"/>).</summary>
        public static UserScope Reader(params long[] enterpriseIds) =>
            new(false, Array.Empty<EnterpriseCostCenter>(), Array.Empty<string>())
            { ReadAllEnterpriseIds = enterpriseIds };

        /// <summary>The distinct enterprises this scope grants any visibility into (empty for SeesAll — meaning "all").
        /// Union of reader grants and the enterprises behind cost-center pairs.</summary>
        public IReadOnlyCollection<long> EnterpriseIds =>
            ReadAllEnterpriseIds.Concat(CostCenters.Select(c => c.EnterpriseId)).Distinct().ToList();

        /// <summary>Bare cost-center ids (for display labels only — never for filtering).</summary>
        public IReadOnlyCollection<string> CostCenterIds =>
            CostCenters.Select(c => c.CostCenterId).Distinct().ToList();

        /// <summary>True when the viewer has enterprise-grain read SOMEWHERE — the gate for features
        /// that cannot be narrowed below an enterprise (organization rollups, the allowance pool).
        /// Not sufficient on its own: the query must still be restricted to the granted enterprises.</summary>
        public bool HasEnterpriseRead => SeesAll || ReadAllEnterpriseIds.Count > 0;

        /// <summary>True when the viewer may see enterprise-grain data for THIS enterprise.</summary>
        public bool CanReadEnterprise(long enterpriseId) =>
            SeesAll || ReadAllEnterpriseIds.Contains(enterpriseId);

        /// <summary>
        /// The enterprises whose enterprise-grain data may be shown, honouring the UI's enterprise
        /// filter. Null means "no restriction" (a global reader with no filter applied); an EMPTY
        /// list means nothing may be shown, and callers must treat those two cases differently.
        /// </summary>
        public IReadOnlyCollection<long>? EnterpriseReadFilter()
        {
            if (SeesAll)
                return EnterpriseFilter is long f ? new[] { f } : null;
            var ids = EnterpriseFilter is long ef
                ? ReadAllEnterpriseIds.Where(id => id == ef).ToList()
                : ReadAllEnterpriseIds.ToList();
            return ids;
        }
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
    /// DB-backed scope resolution. Group membership stays in Entra; what a principal may SEE is
    /// managed in-app via the admin console, across two tables — Enterprise Reader grants and
    /// (enterprise, cost center) mappings. Scope is resolved per request, so a grant change takes
    /// effect on the next page load.
    ///
    /// NOTE what is deliberately absent: being an application administrator no longer implies
    /// seeing anything. Only the Entra "Admin" APP ROLE still grants see-all, purely as a bootstrap
    /// so a fresh deployment with an empty grants table is not locked out of its own data.
    /// DB-designated admin principals must be granted Enterprise Reader separately.
    /// </summary>
    public sealed class DbGroupScopeResolver : IUserScopeResolver
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;

        public DbGroupScopeResolver(IDbContextFactory<BillingDbContext> dbFactory) => _dbFactory = dbFactory;

        public async Task<UserScope> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            // Bootstrap only — see the class remark. This is the Entra app role, NOT the DB admin list.
            if (user?.Identity?.IsAuthenticated == true && user.IsInRole("Admin")) return UserScope.All();

            var groups = GroupClaims.GetGroupObjectIds(user!);
            var oid = GroupClaims.GetUserObjectId(user!);
            if (groups.Count == 0 && string.IsNullOrEmpty(oid)) return UserScope.None();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var grants = await db.PrincipalEnterpriseGrants
                .Where(g =>
                    (g.PrincipalType == PrincipalTypes.Group && groups.Contains(g.PrincipalObjectId)) ||
                    (g.PrincipalType == PrincipalTypes.User && oid != null && g.PrincipalObjectId == oid))
                .Select(g => g.EnterpriseId)
                .Distinct()
                .ToListAsync(ct);

            // A NULL grant means ALL enterprises, including ones registered later — so it collapses
            // to SeesAll rather than being expanded into today's enterprise ids, which would go stale
            // the moment another enterprise is onboarded.
            if (grants.Any(id => id is null)) return UserScope.All();

            var pairs = await db.PrincipalCostCenterMappings
                .Where(m =>
                    (m.PrincipalType == PrincipalTypes.Group && groups.Contains(m.PrincipalObjectId)) ||
                    (m.PrincipalType == PrincipalTypes.User && oid != null && m.PrincipalObjectId == oid))
                .Select(m => new { m.EnterpriseId, m.CostCenterId })
                .Distinct()
                .ToListAsync(ct);

            var readAll = grants.Where(id => id is long).Select(id => id!.Value).ToList();
            if (readAll.Count == 0 && pairs.Count == 0) return UserScope.None();

            // The two tiers UNION: a reader for one enterprise may also manage a cost center in
            // another, and neither grant may quietly swallow the other.
            return new UserScope(false,
                pairs.Select(p => new EnterpriseCostCenter(p.EnterpriseId, p.CostCenterId)).ToList(),
                Array.Empty<string>())
            { ReadAllEnterpriseIds = readAll };
        }
    }
}
