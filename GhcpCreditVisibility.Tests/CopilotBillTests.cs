using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GhcpCreditVisibility.Services.UsageQueryService;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Copilot license costs, AI-credit quantity, and the Copilot-only filter on GitHub's billing feed.
    ///
    /// The billing feed (<see cref="OrgUsageSnapshot"/>) carries EVERY product GitHub bills — the
    /// Actions and GHAS rows below are there to prove nothing Copilot-labelled ever sums them.
    /// </summary>
    public class CopilotBillTests
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
            db.Enterprises.Add(new Enterprise { Id = 2, Slug = "fabrikam", DisplayName = "Fabrikam" });

            void Feed(long ent, string? org, string product, string sku, string? unit, decimal net) =>
                db.OrgUsageSnapshots.Add(new OrgUsageSnapshot
                {
                    EnterpriseId = ent, Year = 2026, Month = 8, Day = 1, OrganizationName = org,
                    Product = product, Sku = sku, UnitType = unit, Quantity = 1, GrossAmount = net, NetAmount = net,
                });

            Feed(1, "platform", "Copilot", "Copilot AI Credits", "ai-credits", 40m);
            Feed(1, null, "Copilot", "Copilot Business", "UserMonths", 190m);        // seats: enterprise-level
            Feed(1, "platform", "Actions", "Actions Linux", "minutes", 500m);        // not Copilot
            Feed(1, "platform", "GHAS", "Advanced Security", "UserMonths", 300m);   // not Copilot, month unit
            Feed(2, "fab-org", "Copilot", "Copilot AI Credits", "ai-credits", 7m);
            Feed(2, null, "Copilot", "Copilot Enterprise", "UserMonths", 39m);

            db.BudgetSnapshots.Add(new BudgetSnapshot
            {
                EnterpriseId = 1, GitHubBudgetId = "b-org", Scope = BudgetScopes.Organization,
                EntityName = "platform", Amount = 100m,
            });

            void User(string login, decimal net, decimal? grossQty, string unit = UsageUnitTypes.AiCredits) =>
                db.UsageSnapshots.Add(new UsageSnapshot
                {
                    EnterpriseId = 1, Year = 2026, Month = 8, Day = 1, UserLogin = login, CostCenterId = "cc-eng",
                    Product = "copilot", Sku = "ai_credits", Model = "gpt-5", UnitType = unit,
                    NetAmount = net, GrossAmount = net, GrossQuantity = grossQty,
                });
            User("alice", 0m, 300m);           // fully covered by the allowance — still used credits
            User("bob", 5m, 100m);
            User("carol", 1m, 999m, "requests"); // not AI credits: must not count as credits
            User("dave", 0m, null);             // not captured: contributes nothing, not an error
            await db.SaveChangesAsync();
            return f;
        }

        private static UserScope Manager() =>
            new(false, new[] { new EnterpriseCostCenter(1, "cc-eng") }, Array.Empty<string>());

        [Theory]
        [InlineData("Copilot", "Copilot AI Credits", "ai-credits", CopilotLineKind.AiCredits)]
        [InlineData("copilot", "ai_credits", "ai-credits", CopilotLineKind.AiCredits)]
        [InlineData("Copilot", "Copilot Business", "UserMonths", CopilotLineKind.License)]
        [InlineData("Copilot", "Copilot Enterprise", null, CopilotLineKind.License)]
        [InlineData("Copilot", "Copilot Premium Request", "requests", CopilotLineKind.OtherCopilot)]
        [InlineData("Actions", "Actions Linux", "minutes", CopilotLineKind.NotCopilot)]
        [InlineData("GHAS", "Advanced Security", "UserMonths", CopilotLineKind.NotCopilot)]
        [InlineData("GHEC", "Enterprise", "UserMonths", CopilotLineKind.NotCopilot)]
        public void Billing_lines_are_classified(string product, string sku, string? unit, CopilotLineKind expected) =>
            Assert.Equal(expected, CopilotBillingLines.Classify(product, sku, unit));

        [Fact]
        public async Task Copilot_bill_splits_licenses_from_ai_credits_and_ignores_other_products()
        {
            var f = await SeededAsync();
            var bill = await new UsageQueryService(f).GetCopilotBillAsync(2026, 8, UserScope.Reader(1));

            Assert.NotNull(bill);
            Assert.Equal(190m, bill!.Licenses);
            Assert.Equal(40m, bill.AiCredits);
            Assert.Equal(230m, bill.Total);   // Actions (500) and GHAS (300) excluded
        }

        [Fact]
        public async Task Copilot_bill_is_restricted_to_readable_enterprises()
        {
            var f = await SeededAsync();
            var q = new UsageQueryService(f);

            Assert.Equal(230m + 46m, (await q.GetCopilotBillAsync(2026, 8, UserScope.All()))!.Total);
            Assert.Equal(46m, (await q.GetCopilotBillAsync(2026, 8, UserScope.Reader(2)))!.Total);
            // The feed has no cost center, so a manager must get nothing rather than the whole enterprise.
            Assert.Null(await q.GetCopilotBillAsync(2026, 8, Manager()));
        }

        [Fact]
        public async Task Credits_are_gross_ai_credit_quantity_per_user()
        {
            var f = await SeededAsync();
            var page = await new UsageQueryService(f).GetUserTotalsPagedAsync(2026, 8, UserScope.All(), null, 1, 25);

            Assert.Equal(400m, page.TotalCredits);   // alice 300 + bob 100; carol's requests and dave's null excluded
            Assert.Equal(300m, page.Items.Single(u => u.UserLogin == "alice").Credits);
            Assert.Equal(0m, page.Items.Single(u => u.UserLogin == "carol").Credits);
        }

        [Fact]
        public async Task Organization_budget_counts_only_copilot_ai_credits()
        {
            var f = await SeededAsync();
            var svc = new BudgetService(f, new UsageQueryService(f));
            var org = (await svc.GetStatusesAsync(UserScope.All(), 2026, 8)).Single(b => b.IsOrganization);

            Assert.Equal(40m, org.Actual);   // not 40 + 500 Actions + 300 GHAS
        }

        [Fact]
        public async Task Organization_report_excludes_other_products_and_adds_licenses_only_on_the_bill_basis()
        {
            var f = await SeededAsync();
            var q = new UsageQueryService(f);
            var reader = UserScope.Reader(1);

            var ai = await q.GetSeriesAsync(SeriesDimension.Organization, TimeGranularity.Month, 12, null, null, null, reader);
            Assert.Equal(40m, ai.Sum(s => s.Total));

            var bill = await q.GetSeriesAsync(SeriesDimension.Organization, TimeGranularity.Month, 12, null, null, null, reader,
                basis: SpendBasis.CopilotBill);
            Assert.Equal(230m, bill.Sum(s => s.Total));
            Assert.Contains(bill, s => s.Key == "Unattributed" && s.Total == 190m);   // seats belong to no org
        }

        [Fact]
        public async Task Total_and_enterprise_reports_use_the_billing_feed_on_the_bill_basis()
        {
            var f = await SeededAsync();
            var q = new UsageQueryService(f);

            var total = await q.GetSeriesAsync(SeriesDimension.Total, TimeGranularity.Month, 12, null, null, null, UserScope.All(),
                basis: SpendBasis.CopilotBill);
            Assert.Equal(276m, Assert.Single(total).Total);

            var byEnt = await q.GetSeriesAsync(SeriesDimension.Enterprise, TimeGranularity.Month, 12, null, null, null, UserScope.All(),
                basis: SpendBasis.CopilotBill);
            Assert.Equal(230m, byEnt.Single(s => s.Key == "Contoso").Total);
            Assert.Equal(46m, byEnt.Single(s => s.Key == "Fabrikam").Total);

            // A manager can't see enterprise-grain billing, so the bill basis yields nothing.
            Assert.Empty(await q.GetSeriesAsync(SeriesDimension.Total, TimeGranularity.Month, 12, null, null, null, Manager(),
                basis: SpendBasis.CopilotBill));
        }
    }
}
