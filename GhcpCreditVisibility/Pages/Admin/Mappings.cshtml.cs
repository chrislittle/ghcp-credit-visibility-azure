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
        private readonly ManualSnapshotTrigger _snapshotTrigger;

        public MappingsModel(
            AdminMappingService svc,
            EnterpriseRegistryService registry,
            IGitHubPatResolver patResolver,
            IDbContextFactory<BillingDbContext> dbFactory,
            IAppAdminChecker admin,
            IConfiguration config,
            ManualSnapshotTrigger snapshotTrigger)
        {
            _svc = svc;
            _registry = registry;
            _patResolver = patResolver;
            _dbFactory = dbFactory;
            _admin = admin;
            _config = config;
            _snapshotTrigger = snapshotTrigger;
        }

        public bool IsAdmin { get; private set; }
        public IReadOnlyList<PrincipalCostCenterMapping> Mappings { get; private set; } = Array.Empty<PrincipalCostCenterMapping>();
        public IReadOnlyList<AdminPrincipal> AdminPrincipals { get; private set; } = Array.Empty<AdminPrincipal>();
        /// <summary>Enterprise Reader grants — data visibility, granted independently of admin rights.</summary>
        public IReadOnlyList<PrincipalEnterpriseGrant> ReaderGrants { get; private set; } = Array.Empty<PrincipalEnterpriseGrant>();
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
        /// <summary>?EditEnterprise=&lt;id&gt; swaps the add-enterprise form for a pre-filled edit form —
        /// fixing a display name, PAT secret name, or a wrong data source must not require
        /// remove-with-purge and re-add.</summary>
        [BindProperty(SupportsGet = true)] public long? EditEnterprise { get; set; }
        public Enterprise? EditingEnterprise { get; private set; }

        /// <summary>?BackfillEnterprise=&lt;id&gt; opens the per-user history panel — same place the edit
        /// form appears. Backfill is opt-in because it costs one GitHub call per user per month, so
        /// the panel exists to show that cost BEFORE anything is committed.</summary>
        [BindProperty(SupportsGet = true)] public long? BackfillEnterprise { get; set; }

        /// <summary>What the backfill panel shows: how far back is reachable, and what it will cost.</summary>
        public sealed record BackfillPlan(
            Enterprise Enterprise,
            int LicensedUsers,
            IReadOnlyList<(int Year, int Month)> Months,
            int EstimatedCalls)
        {
            public bool HasWork => Months.Count > 0;
            public (int Year, int Month)? Oldest => Months.Count > 0 ? Months[^1] : null;

            /// <summary>
            /// Whether the whole job can plausibly finish in a single run. GitHub allows 5,000
            /// requests per hour per token and the job holds part of that in reserve for the regular
            /// snapshot. Below the line it finishes in one go; above it, it stages across cycles —
            /// which is not a failure, just slower, and worth saying before someone starts it.
            /// </summary>
            public bool FitsInOneRun => EstimatedCalls <= 5000 - SnapshotService.BackfillRateLimitReserve;
        }

        public BackfillPlan? Backfill { get; private set; }
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
            ReaderGrants = await _svc.GetEnterpriseGrantsAsync(ct);
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
            EditingEnterprise = EditEnterprise is long editId
                ? enterprises.FirstOrDefault(e => e.Id == editId)
                : null;

            RetentionMonths = _config.GetValue("Retention:Months", 12);
            UseMock = _config.GetValue("GitHub:UseMock", true);

            if (BackfillEnterprise is long backfillId
                && enterprises.FirstOrDefault(e => e.Id == backfillId) is { } target)
            {
                var watermark = target.UserBackfillOldestYear is int by && target.UserBackfillOldestMonth is int bm
                    ? (by, bm)
                    : ((int, int)?)null;
                var months = SnapshotService.PlanUserBackfill(DateTime.UtcNow, RetentionMonths, watermark);
                // Licensed-user count comes from the last snapshot, NOT a live GitHub call: this page
                // must render without network I/O, and an enterprise that has never run has no count
                // to show — which is itself the right thing to say.
                var licensed = target.LicensedUserCount ?? 0;
                Backfill = new BackfillPlan(target, licensed, months,
                    SnapshotService.EstimateUserBackfillCalls(licensed, months.Count));
            }
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

        /// <summary>
        /// Runs a snapshot for one enterprise immediately, instead of waiting up to 12 hours for the
        /// timer or restarting the container to force one. Returns as soon as the work is scheduled;
        /// the row's "last snapshot" column reflects the outcome on the next page load.
        /// </summary>
        public async Task<IActionResult> OnPostRunSnapshotAsync(long id, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                var ent = (await _registry.GetAllAsync(ct)).FirstOrDefault(e => e.Id == id);
                if (ent is null) { Error = "That enterprise is no longer in the registry."; return RedirectToPage(); }
                if (!ent.Enabled) { Error = $"'{ent.Slug}' is disabled — enable it before running a snapshot."; return RedirectToPage(); }

                Message = _snapshotTrigger.TryStart(id, Actor) switch
                {
                    ManualSnapshotTrigger.StartResult.Started =>
                        $"Snapshot started for '{ent.Slug}'. It runs in the background — refresh in a few seconds to see the result. " +
                        "If another run holds the snapshot lease, this one is skipped rather than queued.",
                    _ => $"A snapshot for '{ent.Slug}' is already running on this instance.",
                };
            }
            catch (Exception ex) { Error = ex.Message; }
            return RedirectToPage();
        }

        /// <summary>
        /// Starts per-user history backfill: sets the flag AND kicks off a snapshot to act on it.
        ///
        /// Enabling alone used to be the whole handler, which meant "Start backfill" started nothing
        /// until someone also pressed Run now — two clicks for one intent, and a button whose label
        /// was a lie until the second one. The work still belongs to the snapshot job, not to this
        /// request: a web request must not sit on hundreds of sequential GitHub calls, and the job
        /// owns the distributed lease that stops two instances collecting the same months. So this
        /// SCHEDULES rather than performs — exactly what Run now does.
        /// </summary>
        public async Task<IActionResult> OnPostStartBackfillAsync(long id, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                var ent = (await _registry.GetAllAsync(ct)).FirstOrDefault(e => e.Id == id);
                if (ent is null) { Error = "That enterprise is no longer in the registry."; return RedirectToPage(); }
                if (!ent.Enabled) { Error = $"'{ent.Slug}' is disabled — enable it before backfilling."; return RedirectToPage(); }

                // Flag first: a run started before it is set would not see it. If the trigger then
                // declines because a run is already in flight, the flag still stands and the next
                // cycle picks it up — the work is deferred, never lost.
                await _registry.SetUserBackfillEnabledAsync(id, true, ct);

                Message = _snapshotTrigger.TryStart(id, Actor) switch
                {
                    ManualSnapshotTrigger.StartResult.Started =>
                        $"History backfill started for '{ent.Slug}'. It runs in the background, filling whole months " +
                        "newest first, and stops on its own when there is nothing left — refresh in a few seconds to " +
                        "see how far it got.",
                    _ => $"History backfill enabled for '{ent.Slug}'. A snapshot is already running, so it starts on " +
                         "the next run rather than joining the one in progress.",
                };
            }
            catch (Exception ex) { Error = ex.Message; }
            return RedirectToPage();
        }

        /// <summary>
        /// Stops backfill. Months already completed are KEPT — the watermark records where it got to,
        /// so restarting later resumes rather than redoing.
        /// </summary>
        public async Task<IActionResult> OnPostCancelBackfillAsync(long id, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                await _registry.SetUserBackfillEnabledAsync(id, false, ct);
                Message = "History backfill stopped. Months already collected are kept; starting it again resumes where it left off.";
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

        public async Task<IActionResult> OnPostAddReaderAsync(string principalType, string principalObjectId,
            string? principalName, string? enterpriseId, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            try
            {
                // Empty string from the "All enterprises" <option> means NULL, not zero.
                long? entId = string.IsNullOrWhiteSpace(enterpriseId) ? null : long.Parse(enterpriseId);
                await _svc.AddEnterpriseGrantAsync(principalType, principalObjectId, principalName, entId, Actor, ct);
                var where = entId is long id
                    ? EnterpriseNames.GetValueOrDefault(id, $"enterprise {id}")
                    : "all enterprises";
                Message = $"Enterprise Reader granted to {principalType.ToLowerInvariant()} '{principalName ?? principalObjectId}' for {where}.";
            }
            catch (Exception ex) { Error = ex.Message; }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeleteReaderAsync(long id, CancellationToken ct)
        {
            if (!await _admin.IsAdminAsync(User, ct)) return Forbid();
            await _svc.DeleteEnterpriseGrantAsync(id, ct);
            Message = "Enterprise Reader grant removed.";
            return RedirectToPage();
        }
    }
}
