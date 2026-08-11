using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// The Reports "Organization" breakdown, sourced from <see cref="OrgUsageSnapshot"/>.
    ///
    /// The load-bearing concern is ACCESS, not charting. That table carries EnterpriseId but no cost
    /// centre and no user, so the scope filter cannot narrow it below the enterprise — a
    /// cost-centre-scoped manager given this dimension would see every other team's spend. There is
    /// no partial version of the filter to fall back on, so the dimension is admin-only.
    /// </summary>
    public class OrgSeriesTests
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

            void Add(string? org, int day, decimal net) => db.OrgUsageSnapshots.Add(new OrgUsageSnapshot
            {
                EnterpriseId = 1, Year = 2026, Month = 7, Day = day,
                OrganizationName = org, RepositoryName = org is null ? null : $"{org}/repo",
                Product = "Copilot", Sku = "Copilot AI Credits", UnitType = "ai-credits",
                Quantity = 10, GrossAmount = net, NetAmount = net,
            });

            Add("platform", 1, 100m);
            Add("platform", 2, 50m);
            Add("apps", 1, 30m);
            Add(null, 1, 7m);        // enterprise-level charge with no owning organization
            await db.SaveChangesAsync();
            return f;
        }

        private static UsageQueryService Query(IDbContextFactory<BillingDbContext> f) => new(f);

        /// <summary>The reason this dimension is gated. A manager must get NOTHING, not a subset.</summary>
        [Fact]
        public async Task Cost_centre_scoped_viewers_get_no_organization_data()
        {
            var f = await SeededAsync();
            var manager = new UserScope(false,
                new[] { new EnterpriseCostCenter(1, "cc-eng") },
                Array.Empty<string>());

            var series = await Query(f).GetSeriesAsync(
                UsageQueryService.SeriesDimension.Organization, UsageQueryService.TimeGranularity.Month,
                12, null, null, null, manager);

            Assert.Empty(series);
        }

        [Fact]
        public async Task Admins_see_the_organization_breakdown()
        {
            var f = await SeededAsync();
            var series = await Query(f).GetSeriesAsync(
                UsageQueryService.SeriesDimension.Organization, UsageQueryService.TimeGranularity.Month,
                12, null, null, null, UserScope.All());

            Assert.NotEmpty(series);
            Assert.Contains(series, s => s.Key == "platform");
            Assert.Contains(series, s => s.Key == "apps");
        }

        /// <summary>
        /// Rows with no organization are REAL SPEND (enterprise-level charges — 15 of 37 in a live
        /// sample). Dropping them would silently understate the total.
        /// </summary>
        [Fact]
        public async Task Unattributed_spend_is_surfaced_not_dropped()
        {
            var f = await SeededAsync();
            var series = await Query(f).GetSeriesAsync(
                UsageQueryService.SeriesDimension.Organization, UsageQueryService.TimeGranularity.Month,
                12, null, null, null, UserScope.All());

            var unattributed = Assert.Single(series, s => s.Key == "Unattributed");
            Assert.Equal(7m, unattributed.Total);

            // And the series still reconciles to everything that was recorded.
            Assert.Equal(187m, series.Sum(s => s.Total));
        }

        /// <summary>Org rows are TRUE per-day values, so they sum directly — no differencing.</summary>
        [Fact]
        public async Task Org_totals_sum_per_day_values_directly()
        {
            var f = await SeededAsync();
            var series = await Query(f).GetSeriesAsync(
                UsageQueryService.SeriesDimension.Organization, UsageQueryService.TimeGranularity.Month,
                12, null, null, null, UserScope.All());

            var platform = Assert.Single(series, s => s.Key == "platform");
            Assert.Equal(150m, platform.Total);   // 100 on day 1 + 50 on day 2
        }

        [Fact]
        public async Task Daily_granularity_uses_the_line_items_own_days()
        {
            var f = await SeededAsync();
            var series = await Query(f).GetSeriesAsync(
                UsageQueryService.SeriesDimension.Organization, UsageQueryService.TimeGranularity.Day,
                31, null, null, null, UserScope.All());

            var platform = Assert.Single(series, s => s.Key == "platform");
            Assert.Equal(2, platform.Points.Count(p => p.NetAmount > 0m));
        }

        [Fact]
        public async Task Enterprise_filter_narrows_the_breakdown()
        {
            var f = await SeededAsync();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.Enterprises.Add(new Enterprise { Id = 2, Slug = "fabrikam", DisplayName = "Fabrikam" });
                db.OrgUsageSnapshots.Add(new OrgUsageSnapshot
                {
                    EnterpriseId = 2, Year = 2026, Month = 7, Day = 1,
                    OrganizationName = "fabrikam-only", Product = "Copilot", Sku = "Copilot AI Credits",
                    Quantity = 1, GrossAmount = 999m, NetAmount = 999m,
                });
                await db.SaveChangesAsync();
            }

            var scoped = UserScope.All() with { EnterpriseFilter = 1 };
            var series = await Query(f).GetSeriesAsync(
                UsageQueryService.SeriesDimension.Organization, UsageQueryService.TimeGranularity.Month,
                12, null, null, null, scoped);

            Assert.DoesNotContain(series, s => s.Key == "fabrikam-only");
        }

        [Fact]
        public async Task No_org_data_yields_an_empty_breakdown_rather_than_throwing()
        {
            var f = NewFactory();
            var series = await Query(f).GetSeriesAsync(
                UsageQueryService.SeriesDimension.Organization, UsageQueryService.TimeGranularity.Month,
                12, null, null, null, UserScope.All());

            Assert.Empty(series);
        }
    }
}
