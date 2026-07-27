using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// CRUD over the admin-managed authorization tables (principal→cost-center mappings and
    /// admin-principal designations) plus simple app settings (e.g. organization display name).
    /// A "principal" is an Entra security GROUP or an individual USER (object ID).
    /// </summary>
    public sealed class AdminMappingService
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        public AdminMappingService(IDbContextFactory<BillingDbContext> dbFactory) => _dbFactory = dbFactory;

        /// <summary>An enterprise-qualified cost center the admin can map. Label carries the
        /// enterprise name whenever more than one enterprise is registered — two enterprises WILL
        /// both have an "Engineering", so a bare name is ambiguous.</summary>
        public sealed record CostCenterOption(long EnterpriseId, string Id, string? Name, string? EnterpriseName)
        {
            /// <summary>Stable form value: "&lt;enterpriseId&gt;:&lt;ccId&gt;".</summary>
            public string Key => $"{EnterpriseId}:{Id}";
        }

        public const string OrgDisplayNameKey = "OrgDisplayName";

        private static string NormalizeType(string? t)
            => string.Equals(t, PrincipalTypes.User, StringComparison.OrdinalIgnoreCase) ? PrincipalTypes.User : PrincipalTypes.Group;

        // ── App settings ──
        public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return (await db.AppSettings.FindAsync(new object[] { key }, ct))?.Value;
        }

        public async Task SetSettingAsync(string key, string? value, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.AppSettings.FindAsync(new object[] { key }, ct);
            if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
            else row.Value = value;
            await db.SaveChangesAsync(ct);
        }

        // ── Cost centers discovered from the snapshot (real GitHub cost centers), enterprise-qualified ──
        public async Task<IReadOnlyList<CostCenterOption>> GetKnownCostCentersAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            // GroupBy + projecting straight into a record constructor + OrderBy on the
            // projected member doesn't always translate to SQL (same class of issue fixed
            // earlier in SnapshotService.cs). Materialize the grouped keys first, then
            // project/order client-side.
            //
            // Group by (EnterpriseId, CostCenterId) ONLY (never the name) — GitHub cost centers are
            // keyed by a stable id but the display name can be renamed at any time. Snapshot
            // rows freeze whatever name was live when written, so grouping by the pair would
            // otherwise show a renamed cost center as two separate dropdown entries.
            var rows = await db.UsageSnapshots
                .Where(x => x.CostCenterId != null)
                .Select(x => new { x.EnterpriseId, x.CostCenterId, x.CostCenterName, x.SnapshotUtc })
                .ToListAsync(ct);
            // CostCenterDirectory is the single source of truth for the CURRENT name (refreshed every
            // snapshot run); prefer it over the frozen per-row name so a rename shows up immediately
            // even for ids whose most recent usage row still has the old name cached. It also
            // surfaces cost centers that exist in GitHub but have no usage rows yet.
            var directory = await db.CostCenterDirectory.ToListAsync(ct);
            var currentNames = directory.ToDictionary(x => (x.EnterpriseId, x.CostCenterId), x => x.CurrentName);
            var entNames = await db.Enterprises.ToDictionaryAsync(
                e => e.Id, e => string.IsNullOrWhiteSpace(e.DisplayName) ? e.Slug : e.DisplayName!, ct);

            var fromUsage = rows
                .GroupBy(x => new { x.EnterpriseId, x.CostCenterId })
                .Select(g =>
                {
                    var id = g.Key.CostCenterId!;
                    var name = currentNames.TryGetValue((g.Key.EnterpriseId, id), out var current) && current is not null
                        ? current
                        : g.OrderByDescending(x => x.SnapshotUtc).First().CostCenterName;
                    return new CostCenterOption(g.Key.EnterpriseId, id, name, entNames.GetValueOrDefault(g.Key.EnterpriseId));
                });
            var fromDirectory = directory
                .Select(d => new CostCenterOption(d.EnterpriseId, d.CostCenterId, d.CurrentName, entNames.GetValueOrDefault(d.EnterpriseId)));

            return fromUsage.Concat(fromDirectory)
                .GroupBy(o => (o.EnterpriseId, o.Id))
                .Select(g => g.First())
                .OrderBy(o => o.EnterpriseName ?? "")
                .ThenBy(o => o.Name ?? o.Id)
                .ToList();
        }

        // ── Mappings (principal → cost center) ──
        public async Task<IReadOnlyList<PrincipalCostCenterMapping>> GetMappingsAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.PrincipalCostCenterMappings
                .OrderBy(m => m.PrincipalDisplayName ?? m.PrincipalObjectId).ThenBy(m => m.CostCenterName ?? m.CostCenterId)
                .ToListAsync(ct);
        }

        public async Task UpsertMappingAsync(string principalType, string principalObjectId, string? principalName, long enterpriseId, string costCenterId, string? costCenterName, string? modifiedBy, CancellationToken ct = default)
        {
            var type = NormalizeType(principalType);
            principalObjectId = principalObjectId?.Trim() ?? "";
            costCenterId = costCenterId?.Trim() ?? "";
            if (principalObjectId.Length == 0 || costCenterId.Length == 0)
                throw new ArgumentException("Principal object ID and cost center ID are required.");

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var existing = await db.PrincipalCostCenterMappings
                .FirstOrDefaultAsync(m => m.PrincipalType == type && m.PrincipalObjectId == principalObjectId && m.EnterpriseId == enterpriseId && m.CostCenterId == costCenterId, ct);
            if (existing is null)
            {
                db.PrincipalCostCenterMappings.Add(new PrincipalCostCenterMapping
                {
                    PrincipalType = type,
                    PrincipalObjectId = principalObjectId,
                    PrincipalDisplayName = string.IsNullOrWhiteSpace(principalName) ? null : principalName.Trim(),
                    EnterpriseId = enterpriseId,
                    CostCenterId = costCenterId,
                    CostCenterName = string.IsNullOrWhiteSpace(costCenterName) ? null : costCenterName.Trim(),
                    ModifiedBy = modifiedBy
                });
            }
            else
            {
                existing.PrincipalDisplayName = string.IsNullOrWhiteSpace(principalName) ? existing.PrincipalDisplayName : principalName.Trim();
                existing.CostCenterName = string.IsNullOrWhiteSpace(costCenterName) ? existing.CostCenterName : costCenterName.Trim();
                existing.UpdatedUtc = DateTime.UtcNow;
                existing.ModifiedBy = modifiedBy;
            }
            await db.SaveChangesAsync(ct);
        }

        public async Task DeleteMappingAsync(long id, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.PrincipalCostCenterMappings.FindAsync(new object[] { id }, ct);
            if (row is not null) { db.PrincipalCostCenterMappings.Remove(row); await db.SaveChangesAsync(ct); }
        }

        // ── Admin principals (group OR user) ──
        public async Task<IReadOnlyList<AdminPrincipal>> GetAdminPrincipalsAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.AdminPrincipals.OrderBy(a => a.PrincipalDisplayName ?? a.PrincipalObjectId).ToListAsync(ct);
        }

        public async Task AddAdminPrincipalAsync(string principalType, string principalObjectId, string? principalName, string? modifiedBy, CancellationToken ct = default)
        {
            var type = NormalizeType(principalType);
            principalObjectId = principalObjectId?.Trim() ?? "";
            if (principalObjectId.Length == 0) throw new ArgumentException("Principal object ID is required.");
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            if (await db.AdminPrincipals.AnyAsync(a => a.PrincipalType == type && a.PrincipalObjectId == principalObjectId, ct)) return;
            db.AdminPrincipals.Add(new AdminPrincipal
            {
                PrincipalType = type,
                PrincipalObjectId = principalObjectId,
                PrincipalDisplayName = string.IsNullOrWhiteSpace(principalName) ? null : principalName.Trim(),
                ModifiedBy = modifiedBy
            });
            await db.SaveChangesAsync(ct);
        }

        public async Task DeleteAdminPrincipalAsync(long id, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.AdminPrincipals.FindAsync(new object[] { id }, ct);
            if (row is not null) { db.AdminPrincipals.Remove(row); await db.SaveChangesAsync(ct); }
        }
    }
}
