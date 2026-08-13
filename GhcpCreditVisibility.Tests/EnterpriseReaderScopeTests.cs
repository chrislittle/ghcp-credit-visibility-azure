using System.Security.Claims;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Enterprise Reader: everything within a granted enterprise, nothing outside it, and no console
    /// access implied either way.
    ///
    /// Runs against SQLITE rather than the EF in-memory provider, for the reason
    /// <see cref="RelationalTranslationTests"/> documents at length: ApplyScope builds hand-written
    /// expression trees, and this change adds a new OR branch to them. In-memory evaluates LINQ
    /// client-side and would pass whether or not the predicate can become SQL. These tests therefore
    /// assert BOTH the access decision and its translatability — an access-control filter that throws
    /// in production is not a filter.
    ///
    /// TWO enterprises with data in each is the whole point of the fixture: a filter that returns the
    /// right rows for a single-enterprise deployment proves nothing about isolation.
    /// </summary>
    public sealed class EnterpriseReaderScopeTests : IDisposable
    {
        private const long Contoso = 1;
        private const long Fabrikam = 2;

        private readonly SqliteConnection _conn;
        private readonly IDbContextFactory<BillingDbContext> _factory;

        private sealed class SqliteFactory : IDbContextFactory<BillingDbContext>
        {
            private readonly DbContextOptions<BillingDbContext> _options;
            public SqliteFactory(SqliteConnection conn) =>
                _options = new DbContextOptionsBuilder<BillingDbContext>().UseSqlite(conn).Options;
            public BillingDbContext CreateDbContext() => new(_options);
        }

        public EnterpriseReaderScopeTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _factory = new SqliteFactory(_conn);

            using var db = _factory.CreateDbContext();
            db.Database.EnsureCreated();

            db.Enterprises.Add(new Enterprise { Id = Contoso, Slug = "contoso", DisplayName = "Contoso" });
            db.Enterprises.Add(new Enterprise { Id = Fabrikam, Slug = "fabrikam", DisplayName = "Fabrikam" });

            AddUsage(db, Contoso, "aokafor", "cc-eng", 25m);
            AddUsage(db, Contoso, "mlindqvist", "cc-data", 40m);
            // Unattributed spend: no cost center at all. A pair grant can never match this row, but an
            // Enterprise Reader must see it — otherwise the reader's total silently under-reports.
            AddUsage(db, Contoso, "svc-build", null, 10m);
            AddUsage(db, Fabrikam, "rchaudhary", "cc-eng", 70m);

            db.OrgUsageSnapshots.Add(NewOrg(Contoso, "contoso-platform", 30m));
            db.OrgUsageSnapshots.Add(NewOrg(Fabrikam, "fabrikam-core", 70m));

            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = Contoso, GitHubBudgetId = "c-org", Scope = BudgetScopes.Org, Amount = 1000m });
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = Contoso, GitHubBudgetId = "c-organization", Scope = BudgetScopes.Organization, EntityName = "contoso-platform", Amount = 200m });
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = Contoso, GitHubBudgetId = "c-cc", Scope = BudgetScopes.CostCenter, CostCenterId = "cc-eng", CostCenterName = "Engineering", Amount = 300m });
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = Fabrikam, GitHubBudgetId = "f-org", Scope = BudgetScopes.Org, Amount = 500m });
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = Fabrikam, GitHubBudgetId = "f-organization", Scope = BudgetScopes.Organization, EntityName = "fabrikam-core", Amount = 150m });

            db.SaveChanges();
        }

        public void Dispose() => _conn.Dispose();

        private static void AddUsage(BillingDbContext db, long ent, string login, string? costCenter, decimal net)
        {
            db.UsageSnapshots.Add(new UsageSnapshot
            {
                EnterpriseId = ent, Year = 2026, Month = 8, Day = 1, UserLogin = login,
                CostCenterId = costCenter, Product = "copilot", Sku = "Copilot AI Credits",
                Model = "gpt-5", NetAmount = net, GrossAmount = net + 5m
            });
            db.DailyUsageSnapshots.Add(new DailyUsageSnapshot
            {
                EnterpriseId = ent, Year = 2026, Month = 8, Day = 1, UserLogin = login,
                CostCenterId = costCenter, Product = "copilot", Sku = "Copilot AI Credits",
                Model = "gpt-5", NetAmount = net, GrossAmount = net + 5m
            });
        }

        private static OrgUsageSnapshot NewOrg(long ent, string org, decimal net) => new()
        {
            EnterpriseId = ent, Year = 2026, Month = 8, Day = 1, OrganizationName = org,
            Product = "Copilot", Sku = "Copilot AI Credits", Quantity = 10, GrossAmount = net + 5m, NetAmount = net
        };

        private UsageQueryService Query() => new(_factory);
        private BudgetService Budgets() => new(_factory, new UsageQueryService(_factory));

        private static UserScope Manager(long ent, string cc) =>
            new(false, new[] { new EnterpriseCostCenter(ent, cc) }, Array.Empty<string>());

        // ── Usage isolation ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Reader_sees_every_user_in_the_granted_enterprise_and_none_outside_it()
        {
            var rows = await Query().GetUserTotalsAsync(2026, 8, UserScope.Reader(Contoso));

            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(Contoso, r.EnterpriseId));
            Assert.DoesNotContain(rows, r => r.UserLogin == "rchaudhary");
        }

        /// <summary>
        /// The reader branch is a whole-enterprise clause, not a widened pair match, so rows with NO
        /// cost center are included. A pair-based implementation would silently drop them and the
        /// reader's total would not reconcile against the invoice.
        /// </summary>
        [Fact]
        public async Task Reader_sees_usage_that_has_no_cost_center()
        {
            var rows = await Query().GetUserTotalsAsync(2026, 8, UserScope.Reader(Contoso));
            Assert.Contains(rows, r => r.UserLogin == "svc-build" && r.CostCenterId == null);
        }

        [Fact]
        public async Task Reader_and_cost_center_grants_union_rather_than_override()
        {
            // Reader for Contoso, plus a manager grant for one Fabrikam cost center.
            var scope = new UserScope(false, new[] { new EnterpriseCostCenter(Fabrikam, "cc-eng") }, Array.Empty<string>())
            { ReadAllEnterpriseIds = new[] { Contoso } };

            var rows = await Query().GetUserTotalsAsync(2026, 8, scope);

            Assert.Equal(4, rows.Count);                                        // 3 Contoso + 1 Fabrikam
            Assert.Contains(rows, r => r.UserLogin == "rchaudhary");            // via the pair grant
            Assert.Contains(rows, r => r.UserLogin == "svc-build");             // via the reader grant
        }

        [Fact]
        public async Task Scope_with_no_grants_at_all_sees_nothing()
        {
            var rows = await Query().GetUserTotalsAsync(2026, 8, UserScope.None());
            Assert.Empty(rows);
        }

        [Fact]
        public async Task Global_reader_still_sees_both_enterprises()
        {
            var rows = await Query().GetUserTotalsAsync(2026, 8, UserScope.All());
            Assert.Equal(4, rows.Count);
        }

        // ── Organization dimension ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task Reader_sees_only_the_granted_enterprises_organizations()
        {
            var series = await Query().GetSeriesAsync(UsageQueryService.SeriesDimension.Organization,
                UsageQueryService.TimeGranularity.Month, 12, null, null, null, UserScope.Reader(Contoso));

            Assert.Contains(series, s => s.Key == "contoso-platform");
            Assert.DoesNotContain(series, s => s.Key == "fabrikam-core");
        }

        /// <summary>
        /// Hiding the dropdown option enforces nothing — this is the hand-edited-URL case, where a
        /// reader for Contoso asks for Fabrikam. The intersection of grant and filter is empty, which
        /// must yield NOTHING rather than falling back to "no restriction".
        /// </summary>
        [Fact]
        public async Task Reader_filtering_to_an_ungranted_enterprise_gets_nothing()
        {
            var scope = UserScope.Reader(Contoso) with { EnterpriseFilter = Fabrikam };

            var series = await Query().GetSeriesAsync(UsageQueryService.SeriesDimension.Organization,
                UsageQueryService.TimeGranularity.Month, 12, null, null, null, scope);

            Assert.Empty(series);
        }

        [Fact]
        public async Task Cost_center_manager_gets_no_organization_series()
        {
            var series = await Query().GetSeriesAsync(UsageQueryService.SeriesDimension.Organization,
                UsageQueryService.TimeGranularity.Month, 12, null, null, null, Manager(Contoso, "cc-eng"));

            Assert.Empty(series);
        }

        // ── Budgets ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Reader_sees_enterprise_and_organization_budgets_only_for_the_granted_enterprise()
        {
            var rows = await Budgets().GetStatusesAsync(UserScope.Reader(Contoso), 2026, 8);

            Assert.All(rows, r => Assert.Equal(Contoso, r.EnterpriseId));
            Assert.Contains(rows, r => r.IsOrg);            // enterprise-wide budget
            Assert.Contains(rows, r => r.IsOrganization);   // organization budget
        }

        [Fact]
        public async Task Manager_sees_neither_enterprise_wide_nor_organization_budgets()
        {
            var rows = await Budgets().GetStatusesAsync(Manager(Contoso, "cc-eng"), 2026, 8);

            Assert.DoesNotContain(rows, r => r.IsOrg);
            Assert.DoesNotContain(rows, r => r.IsOrganization);
        }

        // ── Everything else must still translate for a reader ───────────────────────────────────

        [Theory]
        [InlineData(UsageQueryService.SeriesDimension.Total)]
        [InlineData(UsageQueryService.SeriesDimension.User)]
        [InlineData(UsageQueryService.SeriesDimension.Model)]
        [InlineData(UsageQueryService.SeriesDimension.CostCenter)]
        [InlineData(UsageQueryService.SeriesDimension.Enterprise)]
        [InlineData(UsageQueryService.SeriesDimension.Organization)]
        public async Task Reader_series_translate_for_every_dimension(UsageQueryService.SeriesDimension dim)
        {
            var q = Query();
            var scope = UserScope.Reader(Contoso);
            _ = await q.GetSeriesAsync(dim, UsageQueryService.TimeGranularity.Month, 12, null, null, null, scope);
            _ = await q.GetSeriesAsync(dim, UsageQueryService.TimeGranularity.Day, 30, null, null, null, scope);
        }

        [Fact]
        public async Task Reader_aggregates_translate()
        {
            var q = Query();
            foreach (var s in new[] { UserScope.Reader(Contoso), UserScope.Reader(Contoso, Fabrikam) })
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
                _ = await q.GetCollectingSinceAsync(s);
            }
        }

        [Fact]
        public async Task Reader_scope_description_names_the_granted_enterprise()
        {
            var desc = await Query().GetScopeDescriptionAsync(UserScope.Reader(Contoso));
            Assert.Contains("Contoso", desc.Label);
        }

        [Fact]
        public async Task Reader_enterprise_picker_lists_only_granted_enterprises()
        {
            var options = await Query().GetVisibleEnterprisesAsync(UserScope.Reader(Contoso));
            Assert.Single(options);
            Assert.Equal(Contoso, options[0].Id);
        }

        // ── Resolver: admin no longer implies visibility ────────────────────────────────────────

        private static ClaimsPrincipal Principal(params Claim[] claims) =>
            new(new ClaimsIdentity(claims, authenticationType: "test"));

        /// <summary>
        /// The heart of the change. A DB-designated administrator with no reader grant resolves to
        /// NO scope — administering the console and seeing spend are now separate rights.
        /// </summary>
        [Fact]
        public async Task Db_admin_principal_alone_grants_no_visibility()
        {
            await using (var db = _factory.CreateDbContext())
            {
                db.AdminPrincipals.Add(new AdminPrincipal { PrincipalType = PrincipalTypes.Group, PrincipalObjectId = "g-admin" });
                await db.SaveChangesAsync();
            }

            var scope = await new DbGroupScopeResolver(_factory)
                .ResolveAsync(Principal(new Claim("groups", "g-admin")));

            Assert.False(scope.SeesAll);
            Assert.Empty(scope.ReadAllEnterpriseIds);
            Assert.Empty(scope.CostCenters);
        }

        /// <summary>The Entra app role stays a see-all bootstrap, or a fresh deployment with an empty
        /// grants table could never see its own data.</summary>
        [Fact]
        public async Task Entra_admin_app_role_still_sees_everything()
        {
            var scope = await new DbGroupScopeResolver(_factory)
                .ResolveAsync(Principal(new Claim(ClaimTypes.Role, "Admin")));

            Assert.True(scope.SeesAll);
        }

        [Fact]
        public async Task Null_enterprise_grant_resolves_to_see_all()
        {
            await using (var db = _factory.CreateDbContext())
            {
                db.PrincipalEnterpriseGrants.Add(new PrincipalEnterpriseGrant
                { PrincipalType = PrincipalTypes.Group, PrincipalObjectId = "g-finance", EnterpriseId = null });
                await db.SaveChangesAsync();
            }

            var scope = await new DbGroupScopeResolver(_factory)
                .ResolveAsync(Principal(new Claim("groups", "g-finance")));

            // SeesAll rather than an expanded id list: the grant must cover enterprises registered later.
            Assert.True(scope.SeesAll);
        }

        [Fact]
        public async Task Per_enterprise_grant_resolves_to_a_reader_for_that_enterprise_only()
        {
            await using (var db = _factory.CreateDbContext())
            {
                db.PrincipalEnterpriseGrants.Add(new PrincipalEnterpriseGrant
                { PrincipalType = PrincipalTypes.User, PrincipalObjectId = "u-1", EnterpriseId = Contoso });
                await db.SaveChangesAsync();
            }

            var scope = await new DbGroupScopeResolver(_factory)
                .ResolveAsync(Principal(new Claim("oid", "u-1")));

            Assert.False(scope.SeesAll);
            Assert.Equal(new[] { Contoso }, scope.ReadAllEnterpriseIds);
            Assert.True(scope.CanReadEnterprise(Contoso));
            Assert.False(scope.CanReadEnterprise(Fabrikam));
        }
    }
}
