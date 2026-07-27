using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Pages.Admin
{
    /// <summary>
    /// Admin console: manage the enterprise registry, the Entra principal -> (enterprise, cost-center)
    /// mappings, and admin-principal designations. Group MEMBERSHIP is owned by Entra; this page owns
    /// the MAPPING. Gated to application administrators (Entra "Admin" role or a DB-designated admin
    /// principal).
    ///
    /// Enterprise PAT VALUES are deliberately NOT entered here: a paste-the-PAT box would require
    /// granting this app Key Vault WRITE access and pass a live PAT through a web form, app memory,
    /// and potentially logs. The console manages registry metadata and shows live PAT status; secret
    /// seeding stays out-of-band (./deploy.ps1 -Task set-pat -Enterprise &lt;slug&gt;).
    /// </summary>
    public class MappingsModel : PageModel
    {
        private readonly AdminMappingService _svc;
        private readonly EnterpriseRegistryService _registry;
        private readonly IGitHubPatResolver _patResolver;
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly IAppAdminChecker _admin;
        private readonly IConfiguration _config;

        public MappingsModel(
            AdminMappingService svc,
            EnterpriseRegistryService registry,
            IGitHubPatResolver patResolver,
            IDbContextFactory<BillingDbContext> dbFactory,
            IAppAdminChecker admin,
            IConfiguration config)
        {
            _svc = svc;
            _registry = registry;
            _patResolver = patResolver;
            _dbFactory = dbFactory;
            _admin = admin;
            _config = config;
        }

        public bool IsAdmin { get; private set; }
        public IReadOnlyList<PrincipalCostCenterMapping> Mappings { get; private set; } = Array.Empty<PrincipalCostCenterMapping>();
        public IReadOnlyList<AdminPrincipal> AdminPrincipals { get; private set; } = Array.Empty<AdminPrincipal>();
        public IReadOnlyList<AdminMappingService.CostCenterOption> CostCenters { get; private set; } = Array.Empty<AdminMappingService.CostCenterOption>();
        public IReadOnlyCollection<string> MyGroups { get; private set; } = Array.Empty<string>();
        public string? MyUserObjectId { get; private set; }
        public string? OrgDisplayName { get; private set; }

        /// <summary>One registry enterprise + its operational status for the console table.</summary>
        public sealed record EnterpriseRow(
            Enterprise Enterprise,
            string? LastRunStatus,
            DateTime? LastRunUtc,
            int? LastRunRowsWritten,
            bool? PatResolved); // null = mock (no PAT needed)

        public IReadOnlyList<EnterpriseRow> Enterprises { get; private set; } = Array.Empty<EnterpriseRow>();
        public bool MultiEnterprise => Enterprises.Count > 1;
        /// <summary>Id → display label for enterprise badges on mapping rows.</summary>
        public IReadOnlyDictionary<long, string> EnterpriseNames { get; private set; } = new Dictionary<long, string>();

        // Read-only runtime configuration (surfaced to admins only).
        public string ScopingStrategy => "DbGroupMapping";
        public int RetentionMonths { get; private set; } = 12;
        public bool UseMock { get; private set; } = true;
        public bool UsingSqlServer { get; private set; }
        public string ScopingStrategyLabel => "DB-backed principal → (enterprise, cost-center) mapping (admin console)";

        [TempData] public string? Message { get; set; }
        [TempData] public string? Error { get; set; }

        private string? Actor => User?.Identity?.Name;

        private async Task LoadAsync(CancellationToken ct)
        {
            IsAdmin = await _admin.IsAdminAsync(User, ct);
            if (!IsAdmin) return;
            await _registry.EnsureBootstrapAsync(ct);
            Mappings = await _svc.GetMappingsAsync(ct);
            AdminPrincipals = await _svc.GetAdminPrincipalsAsync(ct);
            CostCenters = await _svc.GetKnownCostCentersAsync(ct);
            MyGroups = GroupClaims.GetGroupObjectIds(User);
            MyUserObjectId = GroupClaims.GetUserObjectId(User);
            OrgDisplayName = await _svc.GetSettingAsync(AdminMappingService.OrgDisplayNameKey, ct);
            EnterpriseNames = await _registry.GetDisplayNamesAsync(ct);

            // Enterprise table: registry rows + last run per enterprise + live PAT status.
            var enterprises = await _registry.GetAllAsync(ct);
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            // Two-step "latest run per enterprise" (max-id then fetch) — the grouped-First() shape
            // doesn't translate reliably across EF providers.
            var lastRunIds = await db.SnapshotRuns
                .GroupBy(r => r.EnterpriseId)
                .Select(g => g.Max(r => r.Id))
                .ToListAsync(ct);
            var lastRuns = (await db.SnapshotRuns.Where(r => lastRunIds.Contains(r.Id)).ToListAsync(ct))
                .ToDictionary(r => r.EnterpriseId);
            var rows = new List<EnterpriseRow>();
            foreach (var e in enterprises)
            {
                bool? patResolved = null;
                if (!e.UseMockData)
                {
                    try { patResolved = (await _patResolver.TryResolveAsync(e, ct)).Resolved; }
                    catch { patResolved = false; }
                }
                lastRuns.TryGetValue(e.Id, out var run);
                rows.Add(new EnterpriseRow(e, run?.Status, run?.CompletedUtc ?? run?.StartedUtc, run?.RowsWritten, patResolved));
            }
            Enterprises = rows;

            RetentionMonths = _config.GetValue("Retention:Months", 12);
            UseMock = _config.GetValue("GitHub:UseMock", true);
            var conn = _config.GetConnectionString("BillingDb") ?? _config["ConnectionStrings:BillingDb"];
            UsingSqlServer = !string.IsNullOrWhiteSpace(conn);
        }

        public async Task<IActionResult> OnGetAsync(CancellationToken ct)
        {
            await LoadAsync(ct);
            if (!IsAdmin) return Forbid();
            return Page();
        }

        public async Task<IActionResult> OnPostSaveSettingsAsync(string? orgDisplayName, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            await _svc.SetSettingAsync(AdminMappingService.OrgDisplayNameKey, orgDisplayName?.Trim(), ct);
            Message = "Organization display name saved.";
            return RedirectToPage();
        }

        // ── Enterprise registry ──────────────────────────────────────────────────

        public async Task<IActionResult> OnPostAddEnterpriseAsync(string slug, string? displayName, string? patSecretName, bool useMockData, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                var row = await _registry.AddAsync(slug, displayName, patSecretName, useMockData, Actor, ct);
                Message = row.UseMockData
                    ? $"Enterprise '{row.Slug}' added (mock data, disabled). Enable it below when ready."
                    : $"Enterprise '{row.Slug}' added (disabled). Seed its PAT (./deploy.ps1 -Task set-pat -Enterprise {row.Slug}), " +
                      "then enable it, verify the first snapshot, and map principals.";
            }
            catch (Exception ex) { Error = ex.Message; }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostUpdateEnterpriseAsync(long id, string slug, string? displayName, string? patSecretName, bool useMockData, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                await _registry.UpdateAsync(id, slug, displayName, patSecretName, useMockData, Actor, ct);
                Message = $"Enterprise '{slug}' updated.";
            }
            catch (Exception ex) { Error = ex.Message; }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostSetEnterpriseEnabledAsync(long id, bool enabled, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                await _registry.SetEnabledAsync(id, enabled, Actor, ct);
                Message = enabled
                    ? "Enterprise enabled — it will be included in the next snapshot cycle (no restart needed)."
                    : "Enterprise disabled — snapshots stop; existing data remains until removed.";
            }
            catch (Exception ex) { Error = ex.Message; }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteEnterpriseAsync(long id, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                var purged = await _registry.DeleteWithDataAsync(id, ct);
                Message = $"Enterprise removed. Purged {purged.UsageRows} usage, {purged.BudgetRows} budget, " +
                          $"{purged.DirectoryRows} directory, {purged.MappingRows} mapping and {purged.RunRows} run rows.";
            }
            catch (Exception ex) { Error = ex.Message; }
            return RedirectToPage();
        }

        // ── Mappings ─────────────────────────────────────────────────────────────

        // A principal can be mapped to MULTIPLE cost centers, across multiple enterprises (many-to-
        // many — the unique constraint is on the (principal, enterprise, cost center) triple). The
        // form's cost-center control is a multi-select of enterprise-qualified keys
        // ("<enterpriseId>:<ccId>"), so an admin can grant a group/user visibility into several cost
        // centers (e.g. an exec who needs a top-down view) in a single submit.
        public async Task<IActionResult> OnPostAddMappingAsync(string principalType, string principalObjectId, string? principalName, string[] costCenterKeys, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                var known = await _svc.GetKnownCostCentersAsync(ct);
                var chosen = (costCenterKeys ?? Array.Empty<string>()).Select(k => k?.Trim()).Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
                if (chosen.Count == 0) throw new ArgumentException("Select at least one cost center.");

                // Only (enterprise, cost-center) pairs the snapshot job has actually discovered may
                // be mapped. The form is a select built from `known`, so a value outside it did not
                // come from the UI — accepting one would persist an attacker-chosen id (and its
                // display name) into the mappings table, where the admin console later renders it.
                var unknown = chosen.Where(k => !known.Any(c => c.Key == k)).ToList();
                if (unknown.Count > 0)
                {
                    throw new ArgumentException(
                        $"Unknown cost center(s): {string.Join(", ", unknown)}. Pick from the discovered cost centers.");
                }

                var names = new List<string>();
                foreach (var key in chosen)
                {
                    var cc = known.First(c => c.Key == key);
                    await _svc.UpsertMappingAsync(principalType, principalObjectId, principalName, cc.EnterpriseId, cc.Id, cc.Name, Actor, ct);
                    names.Add(cc.Name ?? cc.Id);
                }
                Message = $"Mapped {principalType.ToLowerInvariant()} '{principalName ?? principalObjectId}' -> {names.Count} cost center(s): {string.Join(", ", names)}.";
            }
            catch (Exception ex) { Error = ex.Message; }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteMappingAsync(long id, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            await _svc.DeleteMappingAsync(id, ct);
            Message = "Mapping removed.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostAddAdminAsync(string principalType, string principalObjectId, string? principalName, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                await _svc.AddAdminPrincipalAsync(principalType, principalObjectId, principalName, Actor, ct);
                Message = $"Admin {principalType.ToLowerInvariant()} '{principalName ?? principalObjectId}' added.";
            }
            catch (Exception ex) { Error = ex.Message; }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteAdminAsync(long id, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            await _svc.DeleteAdminPrincipalAsync(id, ct);
            Message = "Admin principal removed.";
            return RedirectToPage();
        }
    }
}
