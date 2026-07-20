using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Pages.Admin
{
    /// <summary>
    /// Admin console: manage the Entra group -> GitHub cost-center mappings and admin-group
    /// designations. Group MEMBERSHIP is owned by Entra; this page owns the MAPPING. Gated to
    /// application administrators (Entra "Admin" role or a DB-designated admin group).
    /// </summary>
    public class MappingsModel : PageModel
    {
        private readonly AdminMappingService _svc;
        private readonly IAppAdminChecker _admin;
        private readonly IConfiguration _config;

        public MappingsModel(AdminMappingService svc, IAppAdminChecker admin, IConfiguration config)
        {
            _svc = svc;
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

        // Read-only runtime configuration (surfaced to admins only).
        public string ScopingStrategy => "DbGroupMapping";
        public int RetentionMonths { get; private set; } = 12;
        public bool UseMock { get; private set; } = true;
        public string? Enterprise { get; private set; }
        public bool UsingSqlServer { get; private set; }
        public string ScopingStrategyLabel => "DB-backed principal → cost-center mapping (admin console)";

        [TempData] public string? Message { get; set; }
        [TempData] public string? Error { get; set; }

        private string? Actor => User?.Identity?.Name;

        private async Task LoadAsync(CancellationToken ct)
        {
            IsAdmin = await _admin.IsAdminAsync(User, ct);
            if (!IsAdmin) return;
            Mappings = await _svc.GetMappingsAsync(ct);
            AdminPrincipals = await _svc.GetAdminPrincipalsAsync(ct);
            CostCenters = await _svc.GetKnownCostCentersAsync(ct);
            MyGroups = GroupClaims.GetGroupObjectIds(User);
            MyUserObjectId = GroupClaims.GetUserObjectId(User);
            OrgDisplayName = await _svc.GetSettingAsync(AdminMappingService.OrgDisplayNameKey, ct);

            RetentionMonths = _config.GetValue("Retention:Months", 12);
            UseMock = _config.GetValue("GitHub:UseMock", true);
            Enterprise = _config["GitHub:Enterprise"];
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

        // A principal can be mapped to MULTIPLE cost centers (a many-to-many relationship — the
        // unique constraint is on the (principal, cost center) pair, not the principal alone). The
        // form's cost-center control is a multi-select, so an admin can grant a group/user visibility
        // into several cost centers (e.g. an exec who needs a top-down view) in a single submit.
        public async Task<IActionResult> OnPostAddMappingAsync(string principalType, string principalObjectId, string? principalName, string[] costCenterIds, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                var known = await _svc.GetKnownCostCentersAsync(ct);
                var chosen = (costCenterIds ?? Array.Empty<string>()).Select(id => id?.Trim()).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
                if (chosen.Count == 0) throw new ArgumentException("Select at least one cost center.");

                var names = new List<string>();
                foreach (var costCenterId in chosen)
                {
                    var cc = known.FirstOrDefault(c => c.Id == costCenterId);
                    await _svc.UpsertMappingAsync(principalType, principalObjectId, principalName, costCenterId!, cc?.Name, Actor, ct);
                    names.Add(cc?.Name ?? costCenterId!);
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
