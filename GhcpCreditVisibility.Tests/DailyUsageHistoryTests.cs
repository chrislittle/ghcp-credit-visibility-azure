using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Cover for intra-month history.
    ///
    /// UsageSnapshot keeps ONE row per user/model/sku per month and rewrites it in place on every
    /// run, so by month end it knows the total but not how the month got there — "which day did it
    /// jump?" was unanswerable. DailyUsageSnapshot records the CUMULATIVE month-to-date reading
    /// observed each day; per-day spend is derived by differencing at read time.
    ///
    /// The cardinal rule these tests protect: cumulative rows must NEVER be summed. Summing them
    /// inflates totals by roughly the number of days observed.
    /// </summary>
    public class DailyUsageHistoryTests
    {
        private static DailyUsageSnapshot Cum(int day, decimal netToDate, string user = "dkim", string model = "gpt-5") =>
            new()
            {
                EnterpriseId = 1, Year = 2026, Month = 8, Day = day,
                UserLogin = user, Model = model, Sku = "ai_credits", Product = "copilot",
                NetAmount = netToDate, GrossAmount = netToDate, NetQuantity = netToDate,
                SnapshotUtc = new DateTime(2026, 8, day, 3, 0, 0, DateTimeKind.Utc),
            };

        [Fact]
        public void Cumulative_readings_become_per_day_spend()
        {
            // MTD: 10, 18, 21  ->  per-day: 10, 8, 3
            var perDay = UsageQueryService.ToPerDayRows(new[] { Cum(1, 10m), Cum(2, 18m), Cum(3, 21m) })
                .OrderBy(r => r.Day).ToList();

            Assert.Equal(3, perDay.Count);
            Assert.Equal(10m, perDay[0].NetAmount);
            Assert.Equal(8m, perDay[1].NetAmount);
            Assert.Equal(3m, perDay[2].NetAmount);

            // The invariant that keeps the daily chart agreeing with the monthly total.
            Assert.Equal(21m, perDay.Sum(r => r.NetAmount));
        }

        /// <summary>
        /// The whole point. A flat month then a spike must be visible on the day it happened —
        /// this is the question the app previously could not answer at all.
        /// </summary>
        [Fact]
        public void A_mid_month_spike_is_attributable_to_its_day()
        {
            var perDay = UsageQueryService.ToPerDayRows(new[]
            {
                Cum(12, 60m), Cum(13, 65m), Cum(14, 300m), Cum(15, 305m),
            }).OrderBy(r => r.Day).ToList();

            var worst = perDay.OrderByDescending(r => r.NetAmount).First();
            Assert.Equal(14, worst.Day);
            Assert.Equal(235m, worst.NetAmount);
        }

        /// <summary>Series are independent: one user's curve must never leak into another's.</summary>
        [Fact]
        public void Series_are_differenced_independently()
        {
            var perDay = UsageQueryService.ToPerDayRows(new[]
            {
                Cum(1, 10m, user: "a"), Cum(2, 30m, user: "a"),
                Cum(1, 5m,  user: "b"), Cum(2, 6m,  user: "b"),
            });

            Assert.Equal(20m, perDay.Single(r => r.UserLogin == "a" && r.Day == 2).NetAmount);
            Assert.Equal(1m, perDay.Single(r => r.UserLogin == "b" && r.Day == 2).NetAmount);
        }

        [Fact]
        public void Models_within_one_user_are_differenced_independently()
        {
            var perDay = UsageQueryService.ToPerDayRows(new[]
            {
                Cum(1, 10m, model: "gpt-5"), Cum(2, 25m, model: "gpt-5"),
                Cum(1, 4m,  model: "claude"), Cum(2, 5m,  model: "claude"),
            });

            Assert.Equal(15m, perDay.Single(r => r.Model == "gpt-5" && r.Day == 2).NetAmount);
            Assert.Equal(1m, perDay.Single(r => r.Model == "claude" && r.Day == 2).NetAmount);
        }

        /// <summary>
        /// First observation of a month carries everything accrued up to it. If the first run lands
        /// on the 3rd, that row holds days 1-3 — there is no data available to split it, and
        /// inventing a split would be fabrication.
        /// </summary>
        [Fact]
        public void First_observation_of_a_month_carries_the_whole_accrual()
        {
            var perDay = UsageQueryService.ToPerDayRows(new[] { Cum(3, 45m), Cum(4, 50m) })
                .OrderBy(r => r.Day).ToList();

            Assert.Equal(45m, perDay[0].NetAmount);
            Assert.Equal(5m, perDay[1].NetAmount);
            Assert.Equal(50m, perDay.Sum(r => r.NetAmount));  // still reconciles to the month
        }

        /// <summary>
        /// A missed run yields a difference spanning the gap, attributed to the day readings
        /// resumed. Honest — spreading it across days we never observed would be invention. The
        /// month total must still reconcile.
        /// </summary>
        [Fact]
        public void A_missed_run_spans_the_gap_without_losing_spend()
        {
            // Ran on the 1st, down 2nd-4th, resumed on the 5th.
            var perDay = UsageQueryService.ToPerDayRows(new[] { Cum(1, 10m), Cum(5, 90m) })
                .OrderBy(r => r.Day).ToList();

            Assert.Equal(2, perDay.Count);
            Assert.Equal(80m, perDay.Single(r => r.Day == 5).NetAmount);
            Assert.Equal(90m, perDay.Sum(r => r.NetAmount));
        }

        /// <summary>
        /// GitHub restating downward produces a NEGATIVE day. It is preserved rather than clamped:
        /// hiding a correction would leave the daily series silently disagreeing with the month.
        /// </summary>
        [Fact]
        public void A_downward_restatement_is_preserved_not_clamped()
        {
            var perDay = UsageQueryService.ToPerDayRows(new[] { Cum(1, 50m), Cum(2, 80m), Cum(3, 70m) })
                .OrderBy(r => r.Day).ToList();

            Assert.Equal(-10m, perDay[2].NetAmount);
            Assert.Equal(70m, perDay.Sum(r => r.NetAmount));
        }

        /// <summary>Months are separate accrual periods; August must not difference against July.</summary>
        [Fact]
        public void Months_do_not_difference_across_the_boundary()
        {
            var rows = new[]
            {
                new DailyUsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 7, Day = 31, UserLogin = "dkim", Model = "gpt-5", Sku = "ai_credits", NetAmount = 500m },
                new DailyUsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 8, Day = 1,  UserLogin = "dkim", Model = "gpt-5", Sku = "ai_credits", NetAmount = 12m },
            };

            var perDay = UsageQueryService.ToPerDayRows(rows);
            // August's first day is its own accrual, NOT 12 - 500.
            Assert.Equal(12m, perDay.Single(r => r.Month == 8).NetAmount);
            Assert.Equal(500m, perDay.Single(r => r.Month == 7).NetAmount);
        }

        /// <summary>The same login in two enterprises is two series — never differenced together.</summary>
        [Fact]
        public void Enterprises_are_differenced_independently()
        {
            var rows = new[]
            {
                new DailyUsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 8, Day = 1, UserLogin = "dkim", Model = "gpt-5", Sku = "ai_credits", NetAmount = 10m },
                new DailyUsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 8, Day = 2, UserLogin = "dkim", Model = "gpt-5", Sku = "ai_credits", NetAmount = 30m },
                new DailyUsageSnapshot { EnterpriseId = 2, Year = 2026, Month = 8, Day = 1, UserLogin = "dkim", Model = "gpt-5", Sku = "ai_credits", NetAmount = 7m },
                new DailyUsageSnapshot { EnterpriseId = 2, Year = 2026, Month = 8, Day = 2, UserLogin = "dkim", Model = "gpt-5", Sku = "ai_credits", NetAmount = 9m },
            };

            var perDay = UsageQueryService.ToPerDayRows(rows);
            Assert.Equal(20m, perDay.Single(r => r.EnterpriseId == 1 && r.Day == 2).NetAmount);
            Assert.Equal(2m, perDay.Single(r => r.EnterpriseId == 2 && r.Day == 2).NetAmount);
        }

        /// <summary>Out-of-order input must not corrupt the differencing.</summary>
        [Fact]
        public void Input_order_does_not_matter()
        {
            var perDay = UsageQueryService.ToPerDayRows(new[] { Cum(3, 21m), Cum(1, 10m), Cum(2, 18m) })
                .OrderBy(r => r.Day).ToList();

            Assert.Equal(new[] { 10m, 8m, 3m }, perDay.Select(r => r.NetAmount).ToArray());
        }

        [Fact]
        public void Empty_input_is_handled()
            => Assert.Empty(UsageQueryService.ToPerDayRows(Array.Empty<DailyUsageSnapshot>()));

        // ── Retention ──────────────────────────────────────────────────────────────

        [Fact]
        public void Daily_retention_defaults_to_the_monthly_window()
            => Assert.Equal(6, SnapshotService.ResolveDailyRetentionMonths(6, null));

        [Fact]
        public void Daily_retention_can_be_shortened_independently()
            => Assert.Equal(3, SnapshotService.ResolveDailyRetentionMonths(12, 3));

        /// <summary>Nothing in this app refetches purged rows (GitHub's 24-month window would allow
        /// it, but no backfill job exists), so the floor is absolute.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-5)]
        public void Daily_retention_never_drops_below_the_floor(int requested)
            => Assert.Equal(SnapshotService.MinRetentionMonths,
                            SnapshotService.ResolveDailyRetentionMonths(6, requested));
    }
}
