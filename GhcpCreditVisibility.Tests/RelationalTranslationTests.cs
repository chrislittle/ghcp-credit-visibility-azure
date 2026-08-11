using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Runs the real read-path queries against a RELATIONAL provider (SQLite), purely to prove they
    /// TRANSLATE TO SQL.
    ///
    /// Why this file exists: every other test here uses the EF in-memory provider, which evaluates
    /// LINQ client-side and therefore never attempts translation. A query that cannot be expressed
    /// as SQL passes all of those tests and then throws
    /// "The LINQ expression … could not be translated" the first time it runs against SQL Server.
    ///
    /// That is not hypothetical — it shipped. `BudgetScopes.Displayable` was an
    /// IReadOnlySet&lt;string&gt;, and `set.Contains(column)` has no EF mapping (unlike
    /// `array.Contains`, which becomes SQL `IN`). 129 green tests, a clean local run, and a 500 on
    /// the dashboard for the first real user.
    ///
    /// SQLite is not SQL Server, so this cannot catch provider-specific SQL differences. It DOES
    /// catch the far more common failure: a LINQ construct with no relational translation at all.
    /// Add a case here whenever a query gains a new construct — especially collection membership,
    /// hand-built expression trees, or anything with an interface-typed receiver.
    /// </summary>
    public sealed class RelationalTranslationTests : IDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly IDbContextFactory<BillingDbContext> _factory;

        private sealed class SqliteFactory : IDbContextFactory<BillingDbContext>
        {
            private readonly DbContextOptions<BillingDbContext> _options;
            public SqliteFactory(SqliteConnection conn) =>
                _options = new DbContextOptionsBuilder<BillingDbContext>().UseSqlite(conn).Options;
            public BillingDbContext CreateDbContext() => new(_options);
        }

        public RelationalTranslationTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _factory = new SqliteFactory(_conn);

            using var db = _factory.CreateDbContext();
            db.Database.EnsureCreated();

            db.Enterprises.Add(new Enterprise { Id = 1, Slug = "contoso", DisplayName = "Contoso" });
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 1, GitHubBudgetId = "b1", Scope = BudgetScopes.Org, Amount = 1000m });
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 1, GitHubBudgetId = "b2", Scope = BudgetScopes.CostCenter, CostCenterId = "cc-eng", CostCenterName = "Engineering", Amount = 300m });
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 1, GitHubBudgetId = "b3", Scope = BudgetScopes.Organization, EntityName = "contoso-platform", Amount = 200m });
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 1, GitHubBudgetId = "b4", Scope = BudgetScopes.User, UserLogin = "dkim", Amount = 40m });
            db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 8, Day = 1, UserLogin = "dkim", CostCenterId = "cc-eng", Product = "copilot", Sku = "Copilot AI Credits", Model = "gpt-5", NetAmount = 25m, GrossAmount = 30m });
            db.DailyUsageSnapshots.Add(new DailyUsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 8, Day = 1, UserLogin = "dkim", CostCenterId = "cc-eng", Product = "copilot", Sku = "Copilot AI Credits", Model = "gpt-5", NetAmount = 25m, GrossAmount = 30m });
            db.OrgUsageSnapshots.Add(new OrgUsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 8, Day = 1, OrganizationName = "contoso-platform", Product = "Copilot", Sku = "Copilot AI Credits", Quantity = 10, GrossAmount = 30m, NetAmount = 25m });
            db.SaveChanges();
        }

        public void Dispose() => _conn.Dispose();

        private static UserScope Manager() =>
            new(false, new[] { new EnterpriseCostCenter(1, "cc-eng") }, Array.Empty<string>());

        /// <summary>The exact query that threw in production. Admin path.</summary>
        [Fact]
        public async Task Budget_statuses_translate_for_an_admin()
        {
            var svc = new BudgetService(_factory, new UsageQueryService(_factory));
            var rows = await svc.GetStatusesAsync(UserScope.All(), 2026, 8);
            Assert.NotEmpty(rows);
            Assert.Contains(rows, r => r.IsOrganization);   // admin sees the org budget
        }

        /// <summary>
        /// The NON-ADMIN path, which adds the AdminOnly exclusion — a second set-membership filter
        /// that would have failed exactly the same way, on the access-control path.
        /// </summary>
        [Fact]
        public async Task Budget_statuses_translate_for_a_cost_centre_manager()
        {
            var svc = new BudgetService(_factory, new UsageQueryService(_factory));
            var rows = await svc.GetStatusesAsync(Manager(), 2026, 8);
            Assert.DoesNotContain(rows, r => r.IsOrganization);
            Assert.DoesNotContain(rows, r => r.IsOrg);
        }

        /// <summary>
        /// ApplyScope builds its predicates as hand-written expression trees. Those had never been
        /// handed to a relational provider either — only ever to the in-memory one.
        /// </summary>
        [Theory]
        [InlineData(UsageQueryService.SeriesDimension.Total)]
        [InlineData(UsageQueryService.SeriesDimension.User)]
        [InlineData(UsageQueryService.SeriesDimension.Model)]
        [InlineData(UsageQueryService.SeriesDimension.CostCenter)]
        [InlineData(UsageQueryService.SeriesDimension.Enterprise)]
        [InlineData(UsageQueryService.SeriesDimension.Organization)]
        public async Task Series_queries_translate_for_every_dimension(UsageQueryService.SeriesDimension dim)
        {
            var q = new UsageQueryService(_factory);
            _ = await q.GetSeriesAsync(dim, UsageQueryService.TimeGranularity.Month, 12, null, null, null, UserScope.All());
            _ = await q.GetSeriesAsync(dim, UsageQueryService.TimeGranularity.Day, 30, null, null, null, UserScope.All());
        }

        /// <summary>The scoped path exercises BuildPairPredicate — the OR-of-per-enterprise trees.</summary>
        [Fact]
        public async Task Scoped_series_queries_translate()
        {
            var q = new UsageQueryService(_factory);
            _ = await q.GetSeriesAsync(UsageQueryService.SeriesDimension.User,
                UsageQueryService.TimeGranularity.Month, 12, null, null, null, Manager());
        }

        [Fact]
        public async Task Dashboard_and_report_aggregates_translate()
        {
            var q = new UsageQueryService(_factory);
            var scopes = new[] { UserScope.All(), Manager() };
            foreach (var s in scopes)
            {
                _ = await q.GetUserTotalsAsync(2026, 8, s);
                _ = await q.GetCostCenterTotalsAsync(2026, 8, s);
                _ = await q.GetModelTotalsAsync(2026, 8, s);
                _ = await q.GetMonthTotalAsync(2026, 8, s);
                _ = await q.GetTrendAsync(12, s);
                _ = await q.GetAvailableMonthsAsync(s);
                _ = await q.GetFilterOptionsAsync(s);
                _ = await q.GetUserTotalsPagedAsync(2026, 8, s, null, 1, 25);
                _ = await q.GetVisibleEnterprisesAsync(s);
                _ = await q.GetScopeDescriptionAsync(s);
            }
        }

        /// <summary>An enterprise filter narrows every one of those; it uses a built expression too.</summary>
        [Fact]
        public async Task Enterprise_filtered_queries_translate()
        {
            var q = new UsageQueryService(_factory);
            var scoped = UserScope.All() with { EnterpriseFilter = 1 };
            _ = await q.GetUserTotalsAsync(2026, 8, scoped);
            _ = await q.GetCostCenterTotalsAsync(2026, 8, scoped);
            _ = await q.GetSeriesAsync(UsageQueryService.SeriesDimension.Organization,
                UsageQueryService.TimeGranularity.Month, 12, null, null, null, scoped);

            var svc = new BudgetService(_factory, q);
            _ = await svc.GetStatusesAsync(scoped, 2026, 8);
        }

        /// <summary>The retention purge uses ExecuteDelete, which only exists on relational providers.</summary>
        [Fact]
        public async Task Retention_purge_translates()
        {
            await using var db = _factory.CreateDbContext();
            var (y, m) = SnapshotService.ComputeRetentionCutoff(new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc), 12);

            _ = await db.UsageSnapshots.Where(x => x.Year < y || (x.Year == y && x.Month < m)).ExecuteDeleteAsync();
            _ = await db.DailyUsageSnapshots.Where(x => x.Year < y || (x.Year == y && x.Month < m)).ExecuteDeleteAsync();
            _ = await db.OrgUsageSnapshots.Where(x => x.Year < y || (x.Year == y && x.Month < m)).ExecuteDeleteAsync();
        }
    }
}
