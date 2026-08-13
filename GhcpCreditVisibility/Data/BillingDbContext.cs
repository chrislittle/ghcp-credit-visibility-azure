using Microsoft.EntityFrameworkCore;

namespace GhcpCreditVisibility.Data
{
    /// <summary>
    /// Persistence for usage snapshots so the dashboard can show >= 3 months of
    /// history/trend and so the UI reads from the database instead of making live
    /// per-user GitHub calls. Backed by Azure SQL; the app
    /// connects using its managed identity (connection string sets
    /// Authentication=Active Directory Managed Identity).
    /// </summary>
    public sealed class BillingDbContext : DbContext
    {
        public BillingDbContext(DbContextOptions<BillingDbContext> options) : base(options) { }

        public DbSet<UsageSnapshot> UsageSnapshots => Set<UsageSnapshot>();
        public DbSet<DailyUsageSnapshot> DailyUsageSnapshots => Set<DailyUsageSnapshot>();
        public DbSet<OrgUsageSnapshot> OrgUsageSnapshots => Set<OrgUsageSnapshot>();
        public DbSet<SnapshotRun> SnapshotRuns => Set<SnapshotRun>();

        // ── Enterprise registry: the single source of truth for which GitHub enterprises this
        // deployment snapshots. Adding an enterprise is a DAY-2 RUNTIME operation (admin console +
        // Key Vault secret), never a redeploy — Terraform owns no per-enterprise resources. The
        // first row is seeded by migration/bootstrap from the GitHub:Enterprise config value so
        // existing single-enterprise deployments upgrade losslessly.
        public DbSet<Enterprise> Enterprises => Set<Enterprise>();

        // ── Admin-managed authorization (the "glue" between Entra principals and GitHub cost centers) ──
        // A "principal" is an Entra security GROUP or an individual USER (object ID). Membership of
        // groups is managed in Entra; the MAPPING of a principal to a GitHub cost center — and which
        // principals are app admins — is managed in-app via the admin console. Individual-user mapping
        // covers cases with no suitable group (e.g. a single manager who should see one cost center).
        public DbSet<PrincipalCostCenterMapping> PrincipalCostCenterMappings => Set<PrincipalCostCenterMapping>();
        public DbSet<AdminPrincipal> AdminPrincipals => Set<AdminPrincipal>();

        /// <summary>Enterprise Reader grants. SEPARATE from <see cref="AdminPrincipals"/> on purpose:
        /// administering this deployment and seeing every cost center's spend are different rights,
        /// and conflating them forced report consumers to be made administrators.</summary>
        public DbSet<PrincipalEnterpriseGrant> PrincipalEnterpriseGrants => Set<PrincipalEnterpriseGrant>();
        /// <summary>Assigned Copilot seats per plan, refreshed every snapshot run. The ONLY correct
        /// input to the included-allowance pool — <see cref="Enterprise.LicensedUserCount"/> counts a
        /// different population (see <see cref="EnterpriseCopilotSeat"/>).</summary>
        public DbSet<EnterpriseCopilotSeat> EnterpriseCopilotSeats => Set<EnterpriseCopilotSeat>();

        public DbSet<AppSetting> AppSettings => Set<AppSetting>();
        public DbSet<BudgetSnapshot> BudgetSnapshots => Set<BudgetSnapshot>();
        public DbSet<CostCenterDirectoryEntry> CostCenterDirectory => Set<CostCenterDirectoryEntry>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Enterprise>(e =>
            {
                e.HasKey(x => x.Id);
                // HARD unique constraint: an accidental duplicate registration would silently double
                // every number for that enterprise — exactly the "wrong numbers are worse than
                // downtime" failure this app exists to prevent. A rename in GitHub is an UPDATE to
                // the existing row's slug, which this does not block.
                e.HasIndex(x => x.Slug).IsUnique();
                e.Property(x => x.Slug).HasMaxLength(200).IsRequired();
                e.Property(x => x.DisplayName).HasMaxLength(255);
                e.Property(x => x.PatSecretName).HasMaxLength(128).IsRequired();
                e.Property(x => x.ModifiedBy).HasMaxLength(255);
            });

            b.Entity<UsageSnapshot>(e =>
            {
                e.HasKey(x => x.Id);
                // Natural key: one row per enterprise/user/model/sku per day. Day = 1 for whole-month
                // (monthly) rows from the live GitHub aggregate; daily rows use the real day.
                // EnterpriseId leads the key: the SAME GitHub login can legitimately exist in two
                // enterprises, and without the qualifier those rows would silently collapse into one.
                e.HasIndex(x => new { x.EnterpriseId, x.Year, x.Month, x.Day, x.UserLogin, x.Model, x.Sku }).IsUnique();
                e.HasIndex(x => new { x.Year, x.Month });
                e.HasIndex(x => x.CostCenterId);
                e.HasIndex(x => x.EnterpriseId);
                e.Property(x => x.UserLogin).HasMaxLength(255);
                e.Property(x => x.UserName).HasMaxLength(255);
                e.Property(x => x.CostCenterId).HasMaxLength(128);
                e.Property(x => x.CostCenterName).HasMaxLength(255);
                e.Property(x => x.Product).HasMaxLength(64);
                e.Property(x => x.Sku).HasMaxLength(64);
                e.Property(x => x.Model).HasMaxLength(128);
                e.Property(x => x.UnitType).HasMaxLength(64);
                // Read-path carrier only (see the property doc) — never a column.
                e.Ignore(x => x.OrganizationName);
                e.Property(x => x.NetAmount).HasPrecision(18, 4);
                e.Property(x => x.GrossAmount).HasPrecision(18, 4);
                e.Property(x => x.NetQuantity).HasPrecision(18, 4);
                e.Property(x => x.DiscountAmount).HasPrecision(18, 4);
                e.Property(x => x.DiscountQuantity).HasPrecision(18, 4);
                e.Property(x => x.GrossQuantity).HasPrecision(18, 4);
                // Unit prices are small and can carry more significant digits than an amount
                // (fractions of a cent per credit), so this one gets extra scale rather than (18,4).
                e.Property(x => x.PricePerUnit).HasPrecision(18, 6);
            });

            b.Entity<DailyUsageSnapshot>(e =>
            {
                e.HasKey(x => x.Id);
                // One observation per enterprise/user/model/sku per DAY. Re-running a day overwrites
                // that row rather than appending, which is what makes the job idempotent.
                e.HasIndex(x => new { x.EnterpriseId, x.Year, x.Month, x.Day, x.UserLogin, x.Model, x.Sku }).IsUnique();
                // The read path filters an enterprise to a month and orders by day; this covers it.
                e.HasIndex(x => new { x.EnterpriseId, x.Year, x.Month, x.Day });
                e.Property(x => x.UserLogin).HasMaxLength(255);
                e.Property(x => x.UserName).HasMaxLength(255);
                e.Property(x => x.CostCenterId).HasMaxLength(128);
                e.Property(x => x.CostCenterName).HasMaxLength(255);
                e.Property(x => x.Product).HasMaxLength(64);
                e.Property(x => x.Sku).HasMaxLength(64);
                e.Property(x => x.Model).HasMaxLength(128);
                e.Property(x => x.UnitType).HasMaxLength(64);
                e.Property(x => x.NetAmount).HasPrecision(18, 4);
                e.Property(x => x.GrossAmount).HasPrecision(18, 4);
                e.Property(x => x.NetQuantity).HasPrecision(18, 4);
                e.Property(x => x.GrossQuantity).HasPrecision(18, 4);
            });

            b.Entity<OrgUsageSnapshot>(e =>
            {
                e.HasKey(x => x.Id);
                // NO unique index, deliberately. The natural key would have to include
                // OrganizationName and RepositoryName, both of which are NULLABLE — and SQL Server
                // treats NULLs as EQUAL in a unique index, so it would permit only ONE unattributed
                // row per enterprise/month/product/sku and reject the rest. Since the endpoint
                // returns the WHOLE month in a single response, the snapshot job replaces each
                // month wholesale instead of upserting. That is idempotent and sidesteps the
                // nullable-key problem entirely.
                e.HasIndex(x => new { x.EnterpriseId, x.Year, x.Month, x.Day });
                e.HasIndex(x => new { x.EnterpriseId, x.OrganizationName });
                e.Property(x => x.OrganizationName).HasMaxLength(255);
                e.Property(x => x.RepositoryName).HasMaxLength(255);
                e.Property(x => x.Product).HasMaxLength(64);
                e.Property(x => x.Sku).HasMaxLength(64);
                e.Property(x => x.UnitType).HasMaxLength(64);
                e.Property(x => x.Quantity).HasPrecision(18, 4);
                e.Property(x => x.PricePerUnit).HasPrecision(18, 6);
                e.Property(x => x.GrossAmount).HasPrecision(18, 4);
                e.Property(x => x.DiscountAmount).HasPrecision(18, 4);
                e.Property(x => x.NetAmount).HasPrecision(18, 4);
            });

            b.Entity<SnapshotRun>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.StartedUtc);
                // Per-enterprise run history: one enterprise's failure never marks another's
                // snapshot bad, and freshness is judged per enterprise.
                e.HasIndex(x => new { x.EnterpriseId, x.StartedUtc });
                e.Property(x => x.Status).HasMaxLength(32);
                e.Property(x => x.Error).HasMaxLength(2000);
            });

            b.Entity<PrincipalCostCenterMapping>(e =>
            {
                e.HasKey(x => x.Id);
                // One mapping row per (principal-type, principal, enterprise, cost center). A principal
                // may map to several cost centers, across any number of enterprises.
                e.HasIndex(x => new { x.PrincipalType, x.PrincipalObjectId, x.EnterpriseId, x.CostCenterId }).IsUnique();
                e.HasIndex(x => x.PrincipalObjectId);
                e.Property(x => x.PrincipalType).HasMaxLength(16).IsRequired();
                e.Property(x => x.PrincipalObjectId).HasMaxLength(64).IsRequired();
                e.Property(x => x.PrincipalDisplayName).HasMaxLength(255);
                e.Property(x => x.CostCenterId).HasMaxLength(128).IsRequired();
                e.Property(x => x.CostCenterName).HasMaxLength(255);
                e.Property(x => x.ModifiedBy).HasMaxLength(255);
            });

            b.Entity<AdminPrincipal>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.PrincipalType, x.PrincipalObjectId }).IsUnique();
                e.Property(x => x.PrincipalType).HasMaxLength(16).IsRequired();
                e.Property(x => x.PrincipalObjectId).HasMaxLength(64).IsRequired();
                e.Property(x => x.PrincipalDisplayName).HasMaxLength(255);
                e.Property(x => x.ModifiedBy).HasMaxLength(255);
            });

            b.Entity<EnterpriseCopilotSeat>(e =>
            {
                e.HasKey(x => x.Id);
                // One row per (enterprise, plan, month). All four columns are non-nullable, so this
                // needs none of the nullable-key care the grants table above required.
                e.HasIndex(x => new { x.EnterpriseId, x.PlanType, x.Year, x.Month }).IsUnique();
                // The read path asks for one enterprise's current month; this covers it.
                e.HasIndex(x => new { x.EnterpriseId, x.Year, x.Month });
                e.Property(x => x.PlanType).HasMaxLength(64).IsRequired();
            });

            b.Entity<PrincipalEnterpriseGrant>(e =>
            {
                e.HasKey(x => x.Id);
                // One grant per (principal-type, principal, enterprise). SQL Server treats NULLs as
                // EQUAL in a unique index — a liability for OrgUsageSnapshot above, but exactly what
                // is wanted here: it makes a second "all enterprises" row for the same principal
                // impossible, so the all-grant can never be duplicated.
                //
                // HasFilter(null) is LOAD-BEARING. EF Core's SQL Server default for a nullable column
                // in a unique index is a filtered index ("WHERE [EnterpriseId] IS NOT NULL"), which
                // excludes exactly the rows this constraint exists to police — duplicate all-grants
                // would then be permitted. Removing the filter restores the intended behaviour.
                e.HasIndex(x => new { x.PrincipalType, x.PrincipalObjectId, x.EnterpriseId })
                    .IsUnique()
                    .HasFilter(null);
                e.HasIndex(x => x.PrincipalObjectId);
                e.Property(x => x.PrincipalType).HasMaxLength(16).IsRequired();
                e.Property(x => x.PrincipalObjectId).HasMaxLength(64).IsRequired();
                e.Property(x => x.PrincipalDisplayName).HasMaxLength(255);
                e.Property(x => x.ModifiedBy).HasMaxLength(255);
            });

            b.Entity<AppSetting>(e =>
            {
                e.HasKey(x => x.Key);
                e.Property(x => x.Key).HasMaxLength(64);
                e.Property(x => x.Value).HasMaxLength(512);
            });

            b.Entity<BudgetSnapshot>(e =>
            {
                e.HasKey(x => x.Id);
                // One row per (enterprise, GitHub budget id). The previous key —
                // (EnterpriseId, Scope, CostCenterId) — collapsed EVERY non-cost-center scope onto
                // (Scope="Org", CostCenterId=""), so enterprise + organization + multi_user_customer
                // + user budgets all fought over a single row and the survivor was displayed as the
                // enterprise-wide budget. Keying on GitHub's own id makes that impossible, and keeps
                // working when GitHub introduces scopes we have never seen.
                // Populated from GitHub's budgets by the snapshot job — never edited in-app.
                e.HasIndex(x => new { x.EnterpriseId, x.GitHubBudgetId }).IsUnique();
                // Non-unique lookup index: the display path filters by scope.
                e.HasIndex(x => new { x.EnterpriseId, x.Scope });
                e.Property(x => x.GitHubBudgetId).HasMaxLength(128).IsRequired();
                e.Property(x => x.Scope).HasMaxLength(32).IsRequired();
                e.Property(x => x.CostCenterId).HasMaxLength(128);
                e.Property(x => x.CostCenterName).HasMaxLength(255);
                e.Property(x => x.EntityName).HasMaxLength(255);
                e.Property(x => x.UserLogin).HasMaxLength(255);
                e.Property(x => x.Amount).HasPrecision(18, 2);
                e.Property(x => x.ConsumedAmount).HasPrecision(18, 2);
            });

            b.Entity<CostCenterDirectoryEntry>(e =>
            {
                // Keyed by (enterprise, GitHub's stable cost-center GUID) — the single source of truth
                // for the CURRENT display name, and for which enterprise owns a cost center (two
                // enterprises WILL both have an "Engineering"). Refreshed from the live GitHub
                // cost-centers call on every snapshot run, so a rename in GitHub is reflected
                // everywhere within one run cycle, without rewriting the frozen historical name stored
                // on individual UsageSnapshot / BudgetSnapshot rows (which remain point-in-time
                // accurate for auditing).
                e.HasKey(x => new { x.EnterpriseId, x.CostCenterId });
                e.Property(x => x.CostCenterId).HasMaxLength(128);
                e.Property(x => x.CurrentName).HasMaxLength(255);
            });
        }
    }

    /// <summary>Budget scopes: an organization-wide monthly budget, or a per-cost-center budget.</summary>
    public static class BudgetScopes
    {
        /// <summary>Enterprise-wide budget (GitHub <c>budget_scope: "enterprise"</c>). Named "Org"
        /// for historical reasons — renaming would rewrite existing rows for no functional gain.</summary>
        public const string Org = "Org";
        public const string CostCenter = "CostCenter";

        /// <summary>GitHub <c>budget_scope: "user"</c> — an individual's personal spending limit.
        /// STORED but deliberately NOT DISPLAYED: personal limits are personal data and the
        /// access policy for them has not been decided. See <see cref="Displayable"/>.</summary>
        public const string User = "User";

        /// <summary>GitHub <c>budget_scope: "organization"</c>. Stored, but utilization cannot be
        /// computed until UsageSnapshot carries an Organization dimension.</summary>
        public const string Organization = "Organization";

        /// <summary>GitHub <c>budget_scope: "multi_user_customer"</c>. Semantics not yet
        /// established; stored faithfully so it is never mistaken for the enterprise budget.</summary>
        public const string MultiUserCustomer = "MultiUserCustomer";

        /// <summary>A scope GitHub returned that we do not recognize. Stored so nothing is lost,
        /// never displayed, and — critically — never conflated with the enterprise-wide budget.</summary>
        public const string Unknown = "Unknown";

        /// <summary>
        /// Scopes the UI may render. This is an ALLOWLIST by design: a newly-stored scope must be
        /// consciously added here, so storing a scope can never accidentally surface it. Both
        /// entries below are scopes whose actual spend we can genuinely compute today.
        /// </summary>
        /// <remarks>
        /// ARRAY, not a HashSet/IReadOnlySet — deliberately, and do not "improve" it back.
        /// This is used inside an EF <c>Where</c>, and EF Core translates
        /// <c>array.Contains(column)</c> to SQL <c>IN (…)</c> but has NO mapping for
        /// <c>IReadOnlySet&lt;T&gt;.Contains</c>. A set version threw
        /// "The LINQ expression … could not be translated" on SQL Server while passing every local
        /// test, because the in-memory provider evaluates client-side and never exercises
        /// translation. Membership is checked on a handful of items; the set semantics bought
        /// nothing and cost a production 500.
        /// </remarks>
        public static readonly string[] Displayable = { Org, CostCenter, Organization };

        /// <summary>
        /// Scopes that cannot be narrowed BELOW AN ENTERPRISE, and so require enterprise-grain read
        /// (Enterprise Reader) rather than a cost center grant. <see cref="Organization"/> qualifies
        /// because its actuals come from OrgUsageSnapshots, which carries no cost center — there is
        /// nothing to filter on, so a cost-center-scoped manager would otherwise see spend for
        /// organizations they have no grant for.
        ///
        /// Named AdminOnly until Enterprise Reader existed, when "admin" was the only way to see
        /// anything enterprise-wide. It never meant administration — it meant this grain.
        /// </summary>
        /// <remarks>Array for the same EF-translation reason as <see cref="Displayable"/>.</remarks>
        public static readonly string[] EnterpriseGrainOnly = { Organization };
    }

    /// <summary>
    /// GitHub's units of measure, as they arrive on usage line items. Quantities are only comparable
    /// WITHIN a unit type, so anything that sums quantities must filter on one — a constant rather
    /// than a repeated literal because a typo would not fail, it would silently sum nothing.
    /// </summary>
    public static class UsageUnitTypes
    {
        /// <summary>Confirmed live. The unit the included-allowance pool is denominated in.</summary>
        public const string AiCredits = "ai-credits";
    }

    /// <summary>Principal kinds an admin can map / designate.</summary>
    public static class PrincipalTypes
    {
        public const string Group = "Group";
        public const string User = "User";
    }

    /// <summary>
    /// One GitHub enterprise this deployment snapshots. The registry is the single source of truth:
    /// the snapshot job iterates ENABLED rows each cycle, so adding an enterprise is a runtime
    /// operation (admin console + a Key Vault secret named <see cref="PatSecretName"/>) with no
    /// redeploy and no Terraform. <see cref="Slug"/> is "how to call GitHub" (the enterprise slug in
    /// API URLs), not the row's identity — everything downstream keys off <see cref="Id"/>, which is
    /// what keeps historical data stable across a GitHub-side enterprise rename.
    /// </summary>
    public sealed class Enterprise
    {
        /// <summary>The EnterpriseId that pre-multi-enterprise rows are backfilled to by migration.</summary>
        public const long DefaultId = 1;
        /// <summary>Placeholder slug the migration seeds; replaced from GitHub:Enterprise config at first startup.</summary>
        public const string BootstrapPlaceholderSlug = "__bootstrap__";
        /// <summary>The single-enterprise deployments' Key Vault secret name (kept as the row-1 default).</summary>
        public const string DefaultPatSecretName = "github-pat";

        public long Id { get; set; }
        public string Slug { get; set; } = "";
        public string? DisplayName { get; set; }
        /// <summary>Key Vault secret holding this enterprise's PAT (convention: github-pat-&lt;slug&gt;).
        /// The secret VALUE is never entered through this app — see the admin console notes.</summary>
        public string PatSecretName { get; set; } = DefaultPatSecretName;
        /// <summary>True = this enterprise is served by the synthetic mock client (no PAT needed).
        /// Enables hybrid deployments: real enterprises and demo/fire-drill mock enterprises side by side.</summary>
        public bool UseMockData { get; set; }
        /// <summary>Disabled enterprises are skipped by the snapshot job and hidden from non-admin UI.
        /// New enterprises start disabled so the first snapshot can be verified before anyone sees data.</summary>
        public bool Enabled { get; set; } = true;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastSnapshotUtc { get; set; }
        public string? ModifiedBy { get; set; }

        // ── Organization-usage backfill watermark ──
        // The OLDEST month already fetched into OrgUsageSnapshots for this enterprise. The snapshot
        // job walks backwards a few months per cycle until this reaches the retention floor, then
        // stops. A WATERMARK rather than "months with no rows" because a month can legitimately
        // contain zero usage — using row-absence as the signal would re-query those months forever.
        public int? OrgBackfillOldestYear { get; set; }
        public int? OrgBackfillOldestMonth { get; set; }

        // ── Per-user history backfill ──
        // OPT-IN, unlike the organization backfill. Org history costs one call per month regardless
        // of headcount; per-user costs one call PER USER per month, which is 14 calls for a small
        // enterprise and tens of thousands for a large one. An operator sees the estimate and starts
        // it deliberately.
        //
        // A flag rather than a fire-and-forget task: the work can span many snapshot cycles, so it
        // has to survive restarts, resume by itself, and be cancellable by clearing the flag.

        /// <summary>True while per-user history is being filled. Set from the admin console; cleared
        /// automatically on completion, or by the operator to cancel.</summary>
        public bool UserBackfillEnabled { get; set; }

        /// <summary>Oldest month whose per-user history has been fetched. Whole months only — see
        /// <see cref="Services.SnapshotService.PlanUserBackfill"/> for why the boundary matters.</summary>
        public int? UserBackfillOldestYear { get; set; }
        public int? UserBackfillOldestMonth { get; set; }

        /// <summary>Licensed users seen on the last snapshot. Stored so the admin console can show a
        /// cost estimate WITHOUT calling GitHub — pages in this app never call GitHub live. Null
        /// means no snapshot has run yet, so no estimate can be offered.</summary>
        public int? LicensedUserCount { get; set; }
    }

    /// <summary>
    /// Admin-managed mapping of an Entra PRINCIPAL (security group OR individual user) to a GitHub
    /// cost center. Group membership lives in Entra; this row (what a principal can see) is managed
    /// in the app. User-type rows cover cases with no suitable group (e.g. a lone manager).
    /// </summary>
    public sealed class PrincipalCostCenterMapping
    {
        public long Id { get; set; }
        public string PrincipalType { get; set; } = PrincipalTypes.Group; // "Group" | "User"
        public string PrincipalObjectId { get; set; } = "";               // Entra group or user objectId
        public string? PrincipalDisplayName { get; set; }
        public long EnterpriseId { get; set; } = Enterprise.DefaultId;    // which enterprise owns the cost center
        public string CostCenterId { get; set; } = "";
        public string? CostCenterName { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public string? ModifiedBy { get; set; }
    }

    /// <summary>
    /// An Entra principal (group OR user) whose members/self may MANAGE THE CONSOLE — mappings, the
    /// enterprise registry, admin principals, reader grants and backfill triggers.
    ///
    /// This grants NO data visibility. It used to grant see-all as well, which meant anyone who
    /// needed enterprise-wide reporting had to be made an administrator; that visibility now comes
    /// from <see cref="PrincipalEnterpriseGrant"/> and is granted independently. The Entra "Admin"
    /// app role remains a bootstrap that grants BOTH, so a fresh deployment is never locked out.
    /// </summary>
    public sealed class AdminPrincipal
    {
        public long Id { get; set; }
        public string PrincipalType { get; set; } = PrincipalTypes.Group;
        public string PrincipalObjectId { get; set; } = "";
        public string? PrincipalDisplayName { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string? ModifiedBy { get; set; }
    }

    /// <summary>
    /// An Entra principal (group OR user) granted ENTERPRISE READER: everything within an enterprise
    /// — every cost center's per-user spend, the organization dimension, enterprise-wide budgets —
    /// with no configuration rights.
    ///
    /// Enterprise grain is not an arbitrary choice: <see cref="OrgUsageSnapshot"/> carries no cost
    /// center and no user, so an organization rollup cannot be narrowed below the enterprise. This
    /// role is the level those features always needed.
    /// </summary>
    public sealed class PrincipalEnterpriseGrant
    {
        public long Id { get; set; }
        public string PrincipalType { get; set; } = PrincipalTypes.Group; // "Group" | "User"
        public string PrincipalObjectId { get; set; } = "";
        public string? PrincipalDisplayName { get; set; }

        /// <summary>
        /// The enterprise this grant covers, or NULL for ALL enterprises — including ones registered
        /// LATER. That distinction is the point: enterprises are added at runtime, and a grant
        /// enumerated over today's list would silently fail to cover tomorrow's. NULL is used rather
        /// than a "*" sentinel so the column stays a real foreign key.
        /// </summary>
        public long? EnterpriseId { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string? ModifiedBy { get; set; }
    }

    /// <summary>
    /// How many assigned Copilot seats an enterprise has ON EACH PLAN, as of the last snapshot run.
    ///
    /// This exists because <see cref="Enterprise.LicensedUserCount"/> is NOT a Copilot seat count —
    /// it comes from <c>consumed-licenses</c> and counts GHEC licence holders, a different and larger
    /// population (a live enterprise showed 8 licences against 3 Copilot seats). Sizing the included
    /// allowance from licences overstated capacity 5.5x, and overstatement is the dangerous direction:
    /// an enterprise about to exhaust its credit pool renders as comfortable.
    ///
    /// Split BY PLAN because the included allowance differs — Copilot Business includes 1,900 credits
    /// per seat, Copilot Enterprise 3,900 — so a mixed enterprise's capacity is a sum over plans, not
    /// one multiplication.
    ///
    /// KEPT PER MONTH. GitHub reports only the seats assigned RIGHT NOW — there is no historical
    /// seat API — so a month that goes uncaptured can never be reconstructed, while the cost of
    /// keeping it is a handful of rows. That asymmetry is the whole argument: it makes "were we
    /// close to the ceiling before we tipped over?" and "did adding seats fix the overage, or did
    /// usage simply grow?" answerable later, and neither is answerable from spend alone.
    ///
    /// Rows are replaced wholesale per enterprise PER MONTH rather than upserted, matching how
    /// <see cref="OrgUsageSnapshot"/> replaces a month.
    /// </summary>
    public sealed class EnterpriseCopilotSeat
    {
        public long Id { get; set; }
        public long EnterpriseId { get; set; }

        // The month this count describes. The pool reads the CURRENT month; earlier rows exist so a
        // capacity trend can be drawn once a few months have accrued.
        public int Year { get; set; }
        public int Month { get; set; }

        /// <summary>GitHub's own <c>plan_type</c>, stored verbatim (lower-cased): "business",
        /// "enterprise", or whatever GitHub introduces next. NOT mapped to an enum — an unrecognised
        /// plan must be RECORDED so it can be surfaced, never silently dropped from the capacity sum.
        /// Same reasoning as <see cref="BudgetScopes.Unknown"/>.</summary>
        public string PlanType { get; set; } = "";

        /// <summary>
        /// Seats on this plan, as of <see cref="SnapshotUtc"/>.
        ///
        /// LAST OBSERVED, not an average. Seats change mid-month, and every run overwrites the
        /// month's row — so a month in which ten seats were added on the 28th reports the END STATE
        /// for the whole month. That is the right basis for a capacity ceiling (what you are
        /// entitled to now), but it is NOT a seat-months figure and must never be read as one.
        /// Saying so here rather than leaving it to be discovered: an unlabelled seat number is
        /// exactly the trap that made the licence count look usable.
        /// </summary>
        public int Seats { get; set; }

        public DateTime SnapshotUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Simple admin-editable key/value app settings (e.g. organization display name).</summary>
    public sealed class AppSetting
    {
        public string Key { get; set; } = "";
        public string? Value { get; set; }
    }

    /// <summary>
    /// A budget read from GitHub (cost-center or enterprise/org budget) and snapshotted to the DB by
    /// the snapshot job. Budgets are GOVERNED IN GITHUB — this app only reads and displays them; there
    /// is no in-app budget editing. Alerting (email/notifications) is handled by GitHub, not this portal.
    /// </summary>
    public sealed class BudgetSnapshot
    {
        public long Id { get; set; }
        public long EnterpriseId { get; set; } = Enterprise.DefaultId;

        /// <summary>
        /// GitHub's own stable budget id — the per-enterprise unique key for this row.
        /// Rows were previously keyed by (Scope, CostCenterId), which collapsed every
        /// non-cost-center scope onto a single key; see <see cref="Services.BudgetScopeMapper"/>.
        /// Falls back to a deterministic synthetic key when GitHub supplies no id.
        /// </summary>
        public string GitHubBudgetId { get; set; } = "";

        /// <summary>One of the <see cref="BudgetScopes"/> constants.</summary>
        public string Scope { get; set; } = BudgetScopes.Org;
        public string CostCenterId { get; set; } = "";          // "" for any non-cost-center scope
        public string? CostCenterName { get; set; }

        /// <summary>Raw <c>budget_entity_name</c> from GitHub. Retained because organization- and
        /// multi-user-scoped budgets name an entity that is not a cost center, so it cannot be
        /// represented by <see cref="CostCenterId"/>.</summary>
        public string? EntityName { get; set; }

        /// <summary>For user-scoped budgets, the GitHub login the limit applies to.
        /// PERSONAL DATA — stored, not currently displayed (see <see cref="BudgetScopes.Displayable"/>).</summary>
        public string? UserLogin { get; set; }

        public decimal Amount { get; set; }                     // monthly budget from GitHub
        public decimal ConsumedAmount { get; set; }             // consumed-to-date as reported by GitHub (may be 0)

        /// <summary>GitHub's <c>prevent_further_usage</c>: true means hitting this budget HARD-STOPS
        /// usage rather than merely alerting — i.e. it can block a developer mid-task. Operationally
        /// the most consequential budget field, so it is captured even ahead of being surfaced.</summary>
        public bool PreventFurtherUsage { get; set; }

        public DateTime SnapshotUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Single source of truth for a GitHub cost center's CURRENT display name, keyed by its stable
    /// GUID id. Refreshed from GitHub on every snapshot run. Historical UsageSnapshot/BudgetSnapshot
    /// rows keep whatever name was live when they were written (point-in-time); read paths that want
    /// to show the up-to-date name (e.g. reports, trends, the admin mapping dropdown) resolve it via
    /// this table instead, so a rename in GitHub doesn't leave old and new names scattered across
    /// historical months.
    /// </summary>
    public sealed class CostCenterDirectoryEntry
    {
        public long EnterpriseId { get; set; } = Enterprise.DefaultId;
        public string CostCenterId { get; set; } = "";
        public string? CurrentName { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>One usage line item for a user, for a given month, captured at snapshot time.</summary>
    public sealed class UsageSnapshot
    {
        public long Id { get; set; }
        public long EnterpriseId { get; set; } = Enterprise.DefaultId;
        public DateTime SnapshotUtc { get; set; }
        public int Year { get; set; }
        public int Month { get; set; }
        public int Day { get; set; } = 1;   // 1 = whole-month (monthly aggregate); daily rows use the real day
        public string UserLogin { get; set; } = "";
        public string? UserName { get; set; }
        public string? CostCenterId { get; set; }
        public string? CostCenterName { get; set; }
        public string Product { get; set; } = "";
        public string Sku { get; set; } = "";
        public string Model { get; set; } = "";

        /// <summary>
        /// GitHub's unit of measure for this line item — confirmed live as "ai-credits".
        ///
        /// Quantities are only comparable within a unit type, so this is what stops a future SKU
        /// measured in something else being silently added to a credit total. GitHub has changed the
        /// billing unit once already (premium requests gave way to AI credits on 1 June 2026), so
        /// recording the unit rather than assuming it is cheap insurance: any historical row keeps
        /// saying what it was actually measured in.
        /// </summary>
        public string? UnitType { get; set; }

        public decimal NetQuantity { get; set; }
        public decimal NetAmount { get; set; }
        public decimal GrossAmount { get; set; }

        // ── Billing detail behind the gross/net headline ──
        // GitHub reports gross, discount and net for every line item; we previously stored only
        // gross and net, discarding the discount. A discount lapsing raises the bill with NO change
        // in usage, which without these columns presents as an unexplained increase that no
        // breakdown in the app can account for. There is no way to recover this after the fact:
        // GitHub's usage API serves the CURRENT MONTH only, so every month these go unstored is
        // permanently lost.

        // NULLABLE ON PURPOSE. NULL means "not captured" — rows written before these columns
        // existed, and rows from months already frozen by then. 0 means "GitHub reported zero".
        // Collapsing the two would make the app answer "what was July's discount?" with $0 when the
        // truth is that we never recorded it, and that is a number finance would act on.

        /// <summary>Quantity covered by a discount (e.g. an included allowance), from GitHub.</summary>
        public decimal? DiscountQuantity { get; set; }

        /// <summary>Discount applied to this line item. GrossAmount - DiscountAmount = NetAmount.</summary>
        public decimal? DiscountAmount { get; set; }

        /// <summary>Unit price GitHub billed at. Lets a spend change be attributed to a PRICE move
        /// rather than a usage move — the two are indistinguishable from amounts alone.</summary>
        public decimal? PricePerUnit { get; set; }

        /// <summary>Quantity before discount. Pairs with NetQuantity to show consumption against
        /// any included allowance.</summary>
        public decimal? GrossQuantity { get; set; }

        /// <summary>
        /// NOT PERSISTED (ignored in the model configuration). A carrier used only by the read path:
        /// organization-attributed rows live in <see cref="OrgUsageSnapshot"/>, and the reporting
        /// pipeline projects them onto this type so the existing bucketing, windowing, zero-filling
        /// and top-N logic applies unchanged rather than being duplicated for a second row shape.
        /// Always null on rows loaded from the database.
        /// </summary>
        public string? OrganizationName { get; set; }
    }

    /// <summary>
    /// One observation of a user's CUMULATIVE month-to-date usage, as reported by GitHub on a given
    /// day. This is the intra-month history <see cref="UsageSnapshot"/> cannot hold: that table keeps
    /// exactly one row per user/model/sku per MONTH and rewrites it in place on every run, so by
    /// month end it knows the total but not how the month got there.
    ///
    /// VALUES ARE CUMULATIVE, NOT PER-DAY. A row for the 6th holds everything spent from the 1st
    /// through the 6th. Per-day spend is derived by differencing consecutive days at READ time
    /// (see UsageQueryService), deliberately rather than storing pre-computed deltas:
    ///   * Restatements self-heal. If GitHub revises a figure, the next run overwrites that day's
    ///     cumulative value and every derived difference is instantly correct again. A stored delta
    ///     would already be baked in and silently wrong.
    ///   * Re-running a day is idempotent — it overwrites one row rather than double-counting.
    ///   * A missed run shows as a GAP rather than a phantom spike, because the difference then
    ///     legitimately spans several days instead of being attributed to one.
    ///
    /// Never sum these rows. Summing cumulative values inflates totals by roughly the number of days
    /// observed; monthly figures come from <see cref="UsageSnapshot"/>, which stays authoritative.
    /// </summary>
    public sealed class DailyUsageSnapshot
    {
        public long Id { get; set; }
        public long EnterpriseId { get; set; } = Enterprise.DefaultId;
        /// <summary>When this observation was taken.</summary>
        public DateTime SnapshotUtc { get; set; }
        public int Year { get; set; }
        public int Month { get; set; }
        /// <summary>The day this cumulative reading is AS OF. Always a real day, never a 1 sentinel.</summary>
        public int Day { get; set; }
        public string UserLogin { get; set; } = "";
        public string? UserName { get; set; }
        public string? CostCenterId { get; set; }
        public string? CostCenterName { get; set; }
        public string Product { get; set; } = "";
        public string Sku { get; set; } = "";
        public string Model { get; set; } = "";
        /// <summary>Unit of measure ("ai-credits", "requests"). Mirrors
        /// <see cref="UsageSnapshot.UnitType"/> — quantities from different meters must never be
        /// added together, and that applies just as much to daily rows.</summary>
        public string? UnitType { get; set; }
        /// <summary>CUMULATIVE month-to-date quantity as of <see cref="Day"/>.</summary>
        public decimal NetQuantity { get; set; }

        /// <summary>
        /// CUMULATIVE month-to-date quantity BEFORE the included-allowance discount, as of
        /// <see cref="Day"/> — i.e. credits actually consumed, which is what the allowance pool is
        /// denominated in. <see cref="NetQuantity"/> is post-discount and is therefore ZERO for any
        /// month the allowance fully covered, so it cannot draw a burn-down curve.
        ///
        /// NULLABLE ON PURPOSE, same convention as <see cref="UsageSnapshot.GrossQuantity"/>: NULL
        /// means "not captured" (rows written before this column existed), 0 means GitHub reported
        /// zero. A month of NULLs must render as "no curve available", never as a flat zero line —
        /// the latter would assert that nothing was consumed.
        /// </summary>
        public decimal? GrossQuantity { get; set; }
        /// <summary>CUMULATIVE month-to-date net amount as of <see cref="Day"/>.</summary>
        public decimal NetAmount { get; set; }
        /// <summary>CUMULATIVE month-to-date gross amount as of <see cref="Day"/>.</summary>
        public decimal GrossAmount { get; set; }
    }

    /// <summary>
    /// Usage attributed to an ORGANIZATION and REPOSITORY, from GitHub's general billing usage
    /// report (<c>/enterprises/{ent}/settings/billing/usage</c>).
    ///
    /// A different grain from <see cref="UsageSnapshot"/>, which is why it is a separate table
    /// rather than more columns:
    ///  * It has NO user. That endpoint does not support filtering by user, so this can never
    ///    answer "who spent this" — it complements the per-user loop rather than replacing it.
    ///  * It HAS organization, repository, and a real per-item DATE, none of which the per-user
    ///    ai_credit endpoint returns. Daily granularity comes free here; no differencing needed.
    ///  * It has no model, and reports a single <c>quantity</c> rather than gross/net quantities.
    ///
    /// One cheap call per enterprise per month fills this, versus the N-calls-per-user loop that
    /// feeds UsageSnapshot. Rows are TRUE PER-DAY values (unlike
    /// <see cref="DailyUsageSnapshot"/>, which is cumulative) — these may be summed.
    /// </summary>
    public sealed class OrgUsageSnapshot
    {
        public long Id { get; set; }
        public long EnterpriseId { get; set; } = Enterprise.DefaultId;
        public DateTime SnapshotUtc { get; set; }

        // Taken from the line item's own `date`, not from the run clock.
        public int Year { get; set; }
        public int Month { get; set; }
        public int Day { get; set; }

        /// <summary>GitHub's <c>organizationName</c>. NULL for enterprise-level charges that belong
        /// to no organization — 15 of 37 line items in a live sample had none, so a rollup must
        /// carry them as "unattributed" rather than silently dropping them.</summary>
        public string? OrganizationName { get; set; }

        /// <summary>GitHub's <c>repositoryName</c>. NULL where the charge is not repo-scoped.</summary>
        public string? RepositoryName { get; set; }

        public string Product { get; set; } = "";
        public string Sku { get; set; } = "";
        public string? UnitType { get; set; }

        /// <summary>The endpoint's single <c>quantity</c> field — it does not split gross/net.</summary>
        public decimal Quantity { get; set; }

        public decimal? PricePerUnit { get; set; }
        public decimal GrossAmount { get; set; }
        public decimal? DiscountAmount { get; set; }
        public decimal NetAmount { get; set; }
    }

    /// <summary>Audit row for each per-enterprise snapshot execution. One row per enterprise per
    /// cycle — an enterprise's failure never marks another's run bad.</summary>
    public sealed class SnapshotRun
    {
        public long Id { get; set; }
        public long EnterpriseId { get; set; } = Enterprise.DefaultId;
        public DateTime StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public int RowsWritten { get; set; }
        public int RowsPurged { get; set; }
        public string Status { get; set; } = "running";
        public string? Error { get; set; }
    }
}
