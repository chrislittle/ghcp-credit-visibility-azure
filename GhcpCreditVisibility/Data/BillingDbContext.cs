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
        public static readonly IReadOnlySet<string> Displayable =
            new HashSet<string>(StringComparer.Ordinal) { Org, CostCenter };
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
    /// An Entra principal (group OR user) whose members/self are application administrators
    /// (see-all + manage the console). The Entra "Admin" app role also grants admin as a bootstrap.
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
