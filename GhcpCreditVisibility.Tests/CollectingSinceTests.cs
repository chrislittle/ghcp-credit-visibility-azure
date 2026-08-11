using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// "Collecting since" — the marker that explains an empty or short history.
    ///
    /// Per-user usage is NOT backfilled: GitHub is asked only for the CURRENT month, every run. So a
    /// newly onboarded enterprise has an empty dashboard, and a 12-month report over three weeks of
    /// history looks like a collapse in spend rather than a short history.
    ///
    /// Partial backfill was rejected deliberately: filling some users/months and not others makes an
    /// incomplete month look like a month of genuinely low spend, which is worse than a visible gap
    /// in an app whose numbers get reconciled against invoices. Saying when collection started is
    /// the honest alternative.
    /// </summary>
    public class CollectingSinceTests
    {
        private static IDbContextFactory<BillingDbContext> NewFactory()
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();
        }

        private static UsageQueryService Query(IDbContextFactory<BillingDbContext> f) => new(f);

        [Fact]
        public async Task Reports_the_earliest_month_that_has_data()
        {
            var f = NewFactory();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.Enterprises.Add(new Enterprise { Id = 1, Slug = "contoso", CreatedUtc = new DateTime(2025, 1, 1) });
                db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 6, Day = 1, UserLogin = "a", Product = "copilot", Sku = "s", Model = "m", NetAmount = 5m });
                db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 8, Day = 1, UserLogin = "a", Product = "copilot", Sku = "s", Model = "m", NetAmount = 5m });
                await db.SaveChangesAsync();
            }

            // Earliest DATA month wins over registration date — that is when collection really began.
            Assert.Equal(new DateOnly(2026, 6, 1), await Query(f).GetCollectingSinceAsync(UserScope.All()));
        }

        /// <summary>
        /// The case that prompted this: an enterprise onboarded into a month with no usage. There
        /// are no rows at all, so "when did we start watching" is the only honest answer available.
        /// </summary>
        [Fact]
        public async Task Falls_back_to_registration_date_when_nothing_has_been_collected()
        {
            var f = NewFactory();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.Enterprises.Add(new Enterprise { Id = 1, Slug = "brand-new", CreatedUtc = new DateTime(2026, 8, 11, 14, 30, 0, DateTimeKind.Utc) });
                await db.SaveChangesAsync();
            }

            Assert.Equal(new DateOnly(2026, 8, 11), await Query(f).GetCollectingSinceAsync(UserScope.All()));
        }

        [Fact]
        public async Task Uses_the_earliest_registration_across_several_enterprises()
        {
            var f = NewFactory();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.Enterprises.Add(new Enterprise { Id = 1, Slug = "older", CreatedUtc = new DateTime(2026, 3, 2) });
                db.Enterprises.Add(new Enterprise { Id = 2, Slug = "newer", CreatedUtc = new DateTime(2026, 8, 11) });
                await db.SaveChangesAsync();
            }

            Assert.Equal(new DateOnly(2026, 3, 2), await Query(f).GetCollectingSinceAsync(UserScope.All()));
        }

        /// <summary>Narrowing to one enterprise must report THAT enterprise's start, not the estate's.</summary>
        [Fact]
        public async Task Enterprise_filter_narrows_the_answer()
        {
            var f = NewFactory();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.Enterprises.Add(new Enterprise { Id = 1, Slug = "older", CreatedUtc = new DateTime(2026, 3, 2) });
                db.Enterprises.Add(new Enterprise { Id = 2, Slug = "newer", CreatedUtc = new DateTime(2026, 8, 11) });
                await db.SaveChangesAsync();
            }

            var scoped = UserScope.All() with { EnterpriseFilter = 2 };
            Assert.Equal(new DateOnly(2026, 8, 11), await Query(f).GetCollectingSinceAsync(scoped));
        }

        /// <summary>The bootstrap placeholder row is not a real enterprise and must not set the date.</summary>
        [Fact]
        public async Task Bootstrap_placeholder_is_ignored()
        {
            var f = NewFactory();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.Enterprises.Add(new Enterprise { Id = 1, Slug = Enterprise.BootstrapPlaceholderSlug, CreatedUtc = new DateTime(2020, 1, 1) });
                db.Enterprises.Add(new Enterprise { Id = 2, Slug = "real", CreatedUtc = new DateTime(2026, 8, 11) });
                await db.SaveChangesAsync();
            }

            Assert.Equal(new DateOnly(2026, 8, 11), await Query(f).GetCollectingSinceAsync(UserScope.All()));
        }

        [Fact]
        public async Task Returns_null_when_there_is_nothing_at_all()
            => Assert.Null(await Query(NewFactory()).GetCollectingSinceAsync(UserScope.All()));

        /// <summary>A viewer with no grants gets nothing, not the estate's start date.</summary>
        [Fact]
        public async Task A_viewer_with_no_access_gets_nothing()
        {
            var f = NewFactory();
            await using (var db = await f.CreateDbContextAsync())
            {
                db.Enterprises.Add(new Enterprise { Id = 1, Slug = "contoso", CreatedUtc = new DateTime(2026, 3, 2) });
                await db.SaveChangesAsync();
            }

            Assert.Null(await Query(f).GetCollectingSinceAsync(UserScope.None()));
        }
    }
}
