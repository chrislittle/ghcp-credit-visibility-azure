using GhcpCreditVisibility.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Cover for the gross/discount/net billing detail on usage rows.
    ///
    /// GitHub reports gross, discount and net for every line item; the app previously stored only
    /// gross and net. A discount lapsing raises the bill with NO change in usage — without the
    /// discount column that presents as an unexplained increase no breakdown can account for.
    ///
    /// These columns are NULLABLE by design: NULL means "not captured" (rows written before the
    /// columns existed, and months already frozen by then), 0 means "GitHub reported zero".
    /// The distinction is what stops the app reporting a $0 discount for a month it simply never
    /// recorded — and it is precisely the marker a backfill would search for, since GitHub serves a
    /// rolling 24-month window that this app does not yet re-request.
    /// </summary>
    public class UsageBillingDetailTests
    {
        private static IDbContextFactory<BillingDbContext> NewFactory()
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();
        }

        [Fact]
        public async Task Billing_detail_round_trips()
        {
            var factory = NewFactory();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.UsageSnapshots.Add(new UsageSnapshot
                {
                    EnterpriseId = 1, Year = 2026, Month = 8, Day = 1,
                    UserLogin = "dkim", Product = "copilot", Sku = "ai_credits", Model = "gpt-5",
                    GrossQuantity = 100m, GrossAmount = 50m,
                    DiscountQuantity = 10m, DiscountAmount = 5m,
                    NetQuantity = 90m, NetAmount = 45m,
                    PricePerUnit = 0.5m,
                });
                await db.SaveChangesAsync();
            }

            await using var check = await factory.CreateDbContextAsync();
            var row = await check.UsageSnapshots.SingleAsync();
            Assert.Equal(10m, row.DiscountQuantity);
            Assert.Equal(5m, row.DiscountAmount);
            Assert.Equal(0.5m, row.PricePerUnit);
            Assert.Equal(100m, row.GrossQuantity);
            // The identity that makes a discount visible at all.
            Assert.Equal(row.GrossAmount - row.DiscountAmount, row.NetAmount);
        }

        /// <summary>
        /// The reason these columns are nullable. A pre-migration row must not claim a $0 discount —
        /// finance would read that as "we received no discount", not "this was never recorded".
        /// </summary>
        [Fact]
        public async Task Not_captured_is_distinguishable_from_genuinely_zero()
        {
            var factory = NewFactory();
            await using (var db = await factory.CreateDbContextAsync())
            {
                // Written before the columns existed: amounts known, detail never captured.
                db.UsageSnapshots.Add(new UsageSnapshot
                {
                    EnterpriseId = 1, Year = 2026, Month = 7, Day = 1,
                    UserLogin = "legacy", Product = "copilot", Sku = "ai_credits", Model = "gpt-5",
                    GrossAmount = 30m, NetAmount = 30m,
                });
                // Captured, and GitHub genuinely reported no discount.
                db.UsageSnapshots.Add(new UsageSnapshot
                {
                    EnterpriseId = 1, Year = 2026, Month = 8, Day = 1,
                    UserLogin = "current", Product = "copilot", Sku = "ai_credits", Model = "gpt-5",
                    GrossAmount = 30m, NetAmount = 30m,
                    DiscountAmount = 0m, DiscountQuantity = 0m, GrossQuantity = 60m, PricePerUnit = 0.5m,
                });
                await db.SaveChangesAsync();
            }

            await using var check = await factory.CreateDbContextAsync();
            var legacy = await check.UsageSnapshots.SingleAsync(x => x.UserLogin == "legacy");
            var current = await check.UsageSnapshots.SingleAsync(x => x.UserLogin == "current");

            Assert.Null(legacy.DiscountAmount);      // unknown
            Assert.Equal(0m, current.DiscountAmount); // known to be zero
            Assert.NotEqual(legacy.DiscountAmount, current.DiscountAmount);
        }

        /// <summary>
        /// A discount lapsing with usage held constant: net rises while gross and quantity are
        /// unchanged. This is the scenario the app could not previously explain.
        /// </summary>
        [Fact]
        public async Task A_lapsed_discount_is_visible_without_any_usage_change()
        {
            var factory = NewFactory();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.UsageSnapshots.Add(new UsageSnapshot
                {
                    EnterpriseId = 1, Year = 2026, Month = 7, Day = 1,
                    UserLogin = "dkim", Product = "copilot", Sku = "ai_credits", Model = "gpt-5",
                    GrossQuantity = 100m, GrossAmount = 50m,
                    DiscountAmount = 20m, DiscountQuantity = 40m,
                    NetQuantity = 60m, NetAmount = 30m, PricePerUnit = 0.5m,
                });
                db.UsageSnapshots.Add(new UsageSnapshot
                {
                    EnterpriseId = 1, Year = 2026, Month = 8, Day = 1,
                    UserLogin = "dkim", Product = "copilot", Sku = "ai_credits", Model = "gpt-5",
                    GrossQuantity = 100m, GrossAmount = 50m,
                    DiscountAmount = 0m, DiscountQuantity = 0m,
                    NetQuantity = 100m, NetAmount = 50m, PricePerUnit = 0.5m,
                });
                await db.SaveChangesAsync();
            }

            await using var check = await factory.CreateDbContextAsync();
            var jul = await check.UsageSnapshots.SingleAsync(x => x.Month == 7);
            var aug = await check.UsageSnapshots.SingleAsync(x => x.Month == 8);

            Assert.Equal(jul.GrossQuantity, aug.GrossQuantity);   // identical consumption
            Assert.Equal(jul.GrossAmount, aug.GrossAmount);       // identical list price
            Assert.Equal(jul.PricePerUnit, aug.PricePerUnit);     // identical unit price
            Assert.True(aug.NetAmount > jul.NetAmount);           // yet the bill went up
            Assert.True(jul.DiscountAmount > aug.DiscountAmount); // and only this explains it
        }

        /// <summary>
        /// Within a month the snapshot job rewrites the SAME row on every run. A field populated on
        /// INSERT but forgotten on UPDATE would freeze at its first-of-month value while the amounts
        /// beside it kept moving — a silent inconsistency, so it is asserted explicitly.
        /// </summary>
        [Fact]
        public async Task In_place_update_refreshes_the_detail_alongside_the_amounts()
        {
            var factory = NewFactory();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.UsageSnapshots.Add(new UsageSnapshot
                {
                    EnterpriseId = 1, Year = 2026, Month = 8, Day = 1,
                    UserLogin = "dkim", Product = "copilot", Sku = "ai_credits", Model = "gpt-5",
                    GrossQuantity = 10m, GrossAmount = 5m, DiscountAmount = 1m,
                    DiscountQuantity = 2m, NetQuantity = 8m, NetAmount = 4m, PricePerUnit = 0.5m,
                });
                await db.SaveChangesAsync();
            }

            // Second run of the month: month-to-date has grown.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var row = await db.UsageSnapshots.SingleAsync();
                row.GrossQuantity = 20m; row.GrossAmount = 10m;
                row.DiscountQuantity = 4m; row.DiscountAmount = 2m;
                row.NetQuantity = 16m; row.NetAmount = 8m;
                await db.SaveChangesAsync();
            }

            await using var check = await factory.CreateDbContextAsync();
            var updated = await check.UsageSnapshots.SingleAsync();
            Assert.Equal(2m, updated.DiscountAmount);
            Assert.Equal(4m, updated.DiscountQuantity);
            Assert.Equal(20m, updated.GrossQuantity);
            Assert.Equal(updated.GrossAmount - updated.DiscountAmount, updated.NetAmount);
        }
    }
}
