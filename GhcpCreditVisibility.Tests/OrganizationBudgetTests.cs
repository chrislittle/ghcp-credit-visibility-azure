using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Organization-scoped budgets.
    ///
    /// These were stored but not displayed until OrgUsageSnapshots existed, because there was no
    /// per-organization actual to compare against and a budget without a real utilisation figure is
    /// worse than one not shown. The join — a budget's <c>budget_entity_name</c> to usage's
    /// <c>organizationName</c> — was verified against the live API as an EXACT match, so no
    /// cost-centre-style name-to-id resolution is needed.
    ///
    /// Access is the load-bearing concern: organization actuals come from a table with no cost
    /// centre, so the viewer's scope cannot narrow them and the whole scope is admin-only.
    /// </summary>
    public class OrganizationBudgetTests
    {
        private static IDbContextFactory<BillingDbContext> NewFactory()
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();
        }

        private static async Task<IDbContextFactory<BillingDbContext>> SeededAsync()
        {
            var f = NewFactory();
            await using var db = await f.CreateDbContextAsync();
            db.Enterprises.Add(new Enterprise { Id = 1, Slug = "contoso", DisplayName = "Contoso" });

            db.BudgetSnapshots.Add(new BudgetSnapshot
            {
                EnterpriseId = 1, GitHubBudgetId = "b-org", Scope = BudgetScopes.Organization,
                EntityName = "contoso-platform", Amount = 300m, PreventFurtherUsage = true,
            });
            db.BudgetSnapshots.Add(new BudgetSnapshot
            {
                EnterpriseId = 1, GitHubBudgetId = "b-cc", Scope = BudgetScopes.CostCenter,
                CostCenterId = "cc-eng", CostCenterName = "Engineering", Amount = 500m,
            });

            void Usage(string? org, decimal net) => db.OrgUsageSnapshots.Add(new OrgUsageSnapshot
            {
                EnterpriseId = 1, Year = 2026, Month = 7, Day = 1,
                OrganizationName = org, Product = "Copilot", Sku = "Copilot AI Credits",
                Quantity = 1, GrossAmount = net, NetAmount = net,
            });

            Usage("contoso-platform", 120m);
            Usage("contoso-platform", 30m);   // 150 total for the budgeted org
            Usage("contoso-apps", 999m);      // a DIFFERENT org — must not count
            Usage(null, 42m);                 // unattributed — must not count either
            await db.SaveChangesAsync();
            return f;
        }

        private static BudgetService Service(IDbContextFactory<BillingDbContext> f)
            => new(f, new UsageQueryService(f));

        [Fact]
        public async Task Organization_budget_is_shown_to_admins_with_real_actuals()
        {
            var f = await SeededAsync();
            var statuses = await Service(f).GetStatusesAsync(UserScope.All(), 2026, 7);

            var org = Assert.Single(statuses, s => s.IsOrganization);
            Assert.Equal("contoso-platform", org.Label);
            Assert.Equal(150m, org.Actual);          // only the budgeted org's spend
            Assert.Equal(300m, org.Amount);
            Assert.Equal(50.0, org.Pct);
        }

        /// <summary>The access gate. A manager must get nothing, not a filtered subset.</summary>
        [Fact]
        public async Task Cost_centre_scoped_viewers_never_see_organization_budgets()
        {
            var f = await SeededAsync();
            var manager = new UserScope(false,
                new[] { new EnterpriseCostCenter(1, "cc-eng") },
                Array.Empty<string>());

            var statuses = await Service(f).GetStatusesAsync(manager, 2026, 7);

            Assert.DoesNotContain(statuses, s => s.IsOrganization);
            Assert.Contains(statuses, s => s.Scope == BudgetScopes.CostCenter);   // their own still shows
        }

        /// <summary>Another organization's spend must never satisfy this budget.</summary>
        [Fact]
        public async Task Other_organizations_spend_does_not_count()
        {
            var f = await SeededAsync();
            var org = Assert.Single(
                await Service(f).GetStatusesAsync(UserScope.All(), 2026, 7),
                s => s.IsOrganization);

            // contoso-apps had 999 and unattributed had 42; neither may leak in.
            Assert.Equal(150m, org.Actual);
        }

        /// <summary>
        /// Two enterprises can each have an organization of the same name. Pooling their spend would
        /// overstate both budgets — the key is the (enterprise, org) PAIR.
        /// </summary>
        [Fact]
        public async Task Same_organization_name_in_two_enterprises_is_not_pooled()
        {
            var f = await SeededAsync();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.Enterprises.Add(new Enterprise { Id = 2, Slug = "fabrikam", DisplayName = "Fabrikam" });
                db.BudgetSnapshots.Add(new BudgetSnapshot
                {
                    EnterpriseId = 2, GitHubBudgetId = "b-org-2", Scope = BudgetScopes.Organization,
                    EntityName = "contoso-platform", Amount = 300m,
                });
                db.OrgUsageSnapshots.Add(new OrgUsageSnapshot
                {
                    EnterpriseId = 2, Year = 2026, Month = 7, Day = 1,
                    OrganizationName = "contoso-platform", Product = "Copilot", Sku = "Copilot AI Credits",
                    Quantity = 1, GrossAmount = 7m, NetAmount = 7m,
                });
                await db.SaveChangesAsync();
            }

            var statuses = await Service(f).GetStatusesAsync(UserScope.All(), 2026, 7);
            Assert.Equal(150m, statuses.Single(s => s.IsOrganization && s.EnterpriseId == 1).Actual);
            Assert.Equal(7m, statuses.Single(s => s.IsOrganization && s.EnterpriseId == 2).Actual);
        }

        /// <summary>A budget naming an organization with no usage reads 0%, not a crash.</summary>
        [Fact]
        public async Task Budget_for_an_organization_with_no_usage_reads_zero()
        {
            var f = NewFactory();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.Enterprises.Add(new Enterprise { Id = 1, Slug = "contoso" });
                db.BudgetSnapshots.Add(new BudgetSnapshot
                {
                    EnterpriseId = 1, GitHubBudgetId = "b", Scope = BudgetScopes.Organization,
                    EntityName = "quiet-org", Amount = 100m,
                });
                await db.SaveChangesAsync();
            }

            var org = Assert.Single(await Service(f).GetStatusesAsync(UserScope.All(), 2026, 7), s => s.IsOrganization);
            Assert.Equal(0m, org.Actual);
            Assert.Equal("ok", org.Level);
        }

        /// <summary>Hard stops BLOCK developers, so the flag must survive to the view.</summary>
        [Fact]
        public async Task Hard_stop_flag_reaches_the_view_model()
        {
            var f = await SeededAsync();
            var statuses = await Service(f).GetStatusesAsync(UserScope.All(), 2026, 7);

            Assert.True(statuses.Single(s => s.IsOrganization).PreventFurtherUsage);
            Assert.False(statuses.Single(s => s.Scope == BudgetScopes.CostCenter).PreventFurtherUsage);
        }

        /// <summary>Scopes without computable actuals or a decided access policy stay hidden.</summary>
        [Fact]
        public async Task User_and_multi_user_budgets_remain_undisplayed()
        {
            var f = await SeededAsync();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 1, GitHubBudgetId = "b-u", Scope = BudgetScopes.User, UserLogin = "dkim", Amount = 40m });
                db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 1, GitHubBudgetId = "b-m", Scope = BudgetScopes.MultiUserCustomer, Amount = 40m });
                db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 1, GitHubBudgetId = "b-x", Scope = BudgetScopes.Unknown, Amount = 40m });
                await db.SaveChangesAsync();
            }

            var statuses = await Service(f).GetStatusesAsync(UserScope.All(), 2026, 7);
            Assert.DoesNotContain(statuses, s => s.Scope == BudgetScopes.User);
            Assert.DoesNotContain(statuses, s => s.Scope == BudgetScopes.MultiUserCustomer);
            Assert.DoesNotContain(statuses, s => s.Scope == BudgetScopes.Unknown);
        }
    }
}
