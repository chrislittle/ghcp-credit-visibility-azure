using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// The included-allowance pool: capacity from ASSIGNED COPILOT SEATS per plan, burn rate,
    /// projection, and the cases where the honest answer is "unknown" rather than a number.
    ///
    /// The bug these guard against actually happened. Capacity was originally
    /// `Enterprise.LicensedUserCount x one constant` — GHEC licence holders times an assumed Copilot
    /// Enterprise rate. Against a live enterprise that read 8 x 3,900 = 31,200 where the truth was
    /// 3 x 1,900 = 5,700: **5.5x overstated**, in the direction that makes an enterprise about to
    /// exhaust its pool look comfortable. Mock data hid it by giving every seeded user a seat.
    ///
    /// SQLite rather than EF in-memory, for the reason <see cref="RelationalTranslationTests"/>
    /// documents.
    /// </summary>
    public sealed class AllowancePoolTests : IDisposable
    {
        private const long Contoso = 1;
        private const long Fabrikam = 2;

        private static readonly IReadOnlyDictionary<string, decimal> Rates =
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            { ["business"] = 1900m, ["enterprise"] = 3900m };

        // Fixed clock: day 13 of a 31-day month. Real dates would make the projection assertions
        // pass or fail depending on when the suite runs.
        private static readonly DateTime AsOf = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _conn;
        private readonly IDbContextFactory<BillingDbContext> _factory;

        private sealed class SqliteFactory : IDbContextFactory<BillingDbContext>
        {
            private readonly DbContextOptions<BillingDbContext> _options;
            public SqliteFactory(SqliteConnection conn) =>
                _options = new DbContextOptionsBuilder<BillingDbContext>().UseSqlite(conn).Options;
            public BillingDbContext CreateDbContext() => new(_options);
        }

        public AllowancePoolTests()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
            _factory = new SqliteFactory(_conn);

            using var db = _factory.CreateDbContext();
            db.Database.EnsureCreated();

            // LicensedUserCount is deliberately set HIGH and left unused: several tests below assert
            // that capacity is NOT derived from it. If a future change reinstates that fallback,
            // these numbers make it fail loudly rather than merely look generous.
            db.Enterprises.Add(new Enterprise { Id = Contoso, Slug = "contoso", DisplayName = "Contoso", LicensedUserCount = 40 });
            db.Enterprises.Add(new Enterprise { Id = Fabrikam, Slug = "fabrikam", DisplayName = "Fabrikam", LicensedUserCount = 40 });
            db.SaveChanges();
        }

        public void Dispose() => _conn.Dispose();

        private void AddSeats(long ent, string planType, int seats)
        {
            using var db = _factory.CreateDbContext();
            db.EnterpriseCopilotSeats.Add(new EnterpriseCopilotSeat
            { EnterpriseId = ent, PlanType = planType, Seats = seats, SnapshotUtc = AsOf });
            db.SaveChanges();
        }

        private void AddCredits(long ent, string login, decimal? grossQuantity, string unitType = UsageUnitTypes.AiCredits)
        {
            using var db = _factory.CreateDbContext();
            db.UsageSnapshots.Add(new UsageSnapshot
            {
                EnterpriseId = ent, Year = 2026, Month = 8, Day = 1, UserLogin = login,
                Product = "copilot", Sku = "Copilot AI Credits", Model = "gpt-5",
                UnitType = unitType, GrossQuantity = grossQuantity, NetAmount = 0m, GrossAmount = 0m
            });
            db.SaveChanges();
        }

        private Task<IReadOnlyList<UsageQueryService.AllowancePool>> Pools(UserScope scope, int month = 8) =>
            new UsageQueryService(_factory).GetAllowancePoolsAsync(2026, month, scope, Rates, AsOf);

        // ── Capacity ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Capacity_is_copilot_seats_times_the_plan_rate()
        {
            AddSeats(Contoso, "business", 3);
            AddCredits(Contoso, "a", 1000m);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Equal(3, pool.TotalSeats);
            Assert.Equal(5_700m, pool.Capacity);       // 3 x 1900 — the live demo enterprise's real figure
        }

        /// <summary>The case that motivated the whole change.</summary>
        [Fact]
        public async Task Mixed_plan_enterprise_sums_each_plan_at_its_own_rate()
        {
            AddSeats(Contoso, "business", 10);
            AddSeats(Contoso, "enterprise", 4);
            AddCredits(Contoso, "a", 1000m);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Equal(14, pool.TotalSeats);
            Assert.Equal((10 * 1900m) + (4 * 3900m), pool.Capacity);   // 34,600
            Assert.Equal(2, pool.Plans.Count);
            Assert.False(pool.HasUnpricedPlans);
        }

        /// <summary>
        /// The regression guard. Capacity must come from seats, never from the licence count — that
        /// substitution is the original defect, and it fails silently because a licence count is
        /// always available and always plausible.
        /// </summary>
        [Fact]
        public async Task Capacity_is_never_derived_from_the_enterprise_licence_count()
        {
            // 40 licences on the enterprise, but no seat rows at all.
            AddCredits(Contoso, "a", 1000m);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Null(pool.Capacity);
            Assert.False(pool.IsComputable);
            Assert.Equal(0, pool.TotalSeats);
            // Specifically NOT 40 x either rate.
            Assert.NotEqual(40 * 1900m, pool.Capacity);
            Assert.NotEqual(40 * 3900m, pool.Capacity);
        }

        // ── Unpriced plans ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Seats on a plan configuration does not price are REPORTED, not dropped. Dropping them
        /// understates capacity, which overstates the percentage used and manufactures false alarms.
        /// </summary>
        [Fact]
        public async Task Seats_on_an_unpriced_plan_are_surfaced_and_excluded_from_capacity()
        {
            AddSeats(Contoso, "business", 10);
            AddSeats(Contoso, "copilot_pro_max", 5);   // a plan GitHub might add tomorrow
            AddCredits(Contoso, "a", 1000m);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Equal(19_000m, pool.Capacity);      // priced seats only
            Assert.True(pool.HasUnpricedPlans);
            Assert.Equal(5, pool.UnknownPlanSeats);
            Assert.Contains("copilot_pro_max", pool.UnknownPlanTypes);
            Assert.Equal(15, pool.TotalSeats);         // the total still counts them
        }

        [Fact]
        public async Task All_seats_unpriced_makes_the_pool_not_computable()
        {
            AddSeats(Contoso, "copilot_pro_max", 5);
            AddCredits(Contoso, "a", 1000m);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Null(pool.Capacity);
            Assert.False(pool.IsComputable);
            Assert.True(pool.HasUnpricedPlans);
        }

        // ── Burn rate, projection, level ────────────────────────────────────────────────────────

        [Fact]
        public async Task Projects_exhaustion_when_the_run_rate_outpaces_the_month()
        {
            AddSeats(Contoso, "business", 10);          // 19,000 capacity
            AddCredits(Contoso, "a", 12_000m);          // 12,000 over 13 days -> ~28,600 by month end

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.True(pool.IsProjectedToExceed);
            Assert.False(pool.IsExhausted);
            Assert.NotNull(pool.ExhaustionDay);
            Assert.Equal("critical", pool.Level);
        }

        [Fact]
        public async Task On_track_when_the_projection_stays_within_capacity()
        {
            AddSeats(Contoso, "enterprise", 10);        // 39,000 capacity
            AddCredits(Contoso, "a", 8_000m);           // ~19,000 by month end

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.False(pool.IsProjectedToExceed);
            Assert.Null(pool.ExhaustionDay);
            Assert.Equal("ok", pool.Level);
        }

        [Fact]
        public async Task Already_exhausted_reports_over_not_merely_projected()
        {
            AddSeats(Contoso, "business", 1);           // 1,900 capacity
            AddCredits(Contoso, "a", 5_000m);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.True(pool.IsExhausted);
            Assert.False(pool.IsProjectedToExceed);
            Assert.True(pool.Remaining < 0);
            Assert.Equal("over", pool.Level);
        }

        [Fact]
        public async Task Pace_marker_tracks_the_elapsed_month()
        {
            AddSeats(Contoso, "business", 3);
            AddCredits(Contoso, "a", 100m);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Equal(13, pool.DaysElapsed);
            Assert.Equal(31, pool.DaysInMonth);
            Assert.InRange(pool.PctElapsed, 41.9, 42.0);
        }

        // ── Honest unknowns on the consumption side ─────────────────────────────────────────────

        [Fact]
        public async Task Uncaptured_gross_quantity_is_unknown_not_zero()
        {
            AddSeats(Contoso, "business", 3);
            AddCredits(Contoso, "a", null);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Null(pool.Consumed);
            Assert.False(pool.IsComputable);
            Assert.Equal(0, pool.PctUsed);   // rendered as "not available", never as 0% used
        }

        [Fact]
        public async Task Partially_captured_data_is_flagged_as_a_lower_bound()
        {
            AddSeats(Contoso, "business", 3);
            AddCredits(Contoso, "a", 500m);
            AddCredits(Contoso, "b", null);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Equal(500m, pool.Consumed);
            Assert.True(pool.HasIncompleteData);
        }

        /// <summary>Quantities from different meters must never be added together.</summary>
        [Fact]
        public async Task Only_ai_credit_rows_count_toward_the_pool()
        {
            AddSeats(Contoso, "business", 3);
            AddCredits(Contoso, "a", 1_000m);
            AddCredits(Contoso, "b", 9_999m, unitType: "requests");

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Equal(1_000m, pool.Consumed);
        }

        [Fact]
        public async Task Seats_but_no_usage_still_reports_capacity()
        {
            AddSeats(Contoso, "business", 3);

            var pool = (await Pools(UserScope.Reader(Contoso))).Single();

            Assert.Equal(5_700m, pool.Capacity);
            Assert.Equal(0m, pool.Consumed);     // genuinely nothing consumed, distinct from unknown
            Assert.True(pool.IsComputable);
            Assert.Equal("ok", pool.Level);
        }

        // ── Applicability and scope ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task Past_months_have_no_pool()
        {
            AddSeats(Contoso, "business", 3);
            AddCredits(Contoso, "a", 1000m);
            Assert.Empty(await Pools(UserScope.Reader(Contoso), month: 7));
        }

        [Fact]
        public async Task Cost_center_managers_get_no_pool()
        {
            AddSeats(Contoso, "business", 3);
            var scope = new UserScope(false, new[] { new EnterpriseCostCenter(Contoso, "cc-eng") }, Array.Empty<string>());
            Assert.Empty(await Pools(scope));
        }

        [Fact]
        public async Task Reader_sees_only_the_granted_enterprises_pool()
        {
            AddSeats(Contoso, "business", 3);
            AddSeats(Fabrikam, "business", 9);

            var pools = await Pools(UserScope.Reader(Contoso));

            Assert.Single(pools);
            Assert.Equal(Contoso, pools[0].EnterpriseId);
        }

        [Fact]
        public async Task Global_reader_gets_one_pool_per_enterprise_never_a_combined_one()
        {
            AddSeats(Contoso, "business", 3);
            AddSeats(Fabrikam, "enterprise", 2);

            var pools = await Pools(UserScope.All());

            Assert.Equal(2, pools.Count);
            // Separate ceilings, separate overages — a summed pool would let one enterprise's
            // headroom mask another's breach.
            Assert.Equal(5_700m, pools.Single(p => p.EnterpriseId == Contoso).Capacity);
            Assert.Equal(7_800m, pools.Single(p => p.EnterpriseId == Fabrikam).Capacity);
        }

        [Fact]
        public async Task Reader_filtering_to_an_ungranted_enterprise_gets_no_pool()
        {
            AddSeats(Fabrikam, "business", 9);
            Assert.Empty(await Pools(UserScope.Reader(Contoso) with { EnterpriseFilter = Fabrikam }));
        }

        [Fact]
        public async Task Empty_rate_map_disables_the_pool_entirely()
        {
            AddSeats(Contoso, "business", 3);

            var pools = await new UsageQueryService(_factory).GetAllowancePoolsAsync(
                2026, 8, UserScope.All(), new Dictionary<string, decimal>(), AsOf);

            Assert.Empty(pools);
        }

        /// <summary>GitHub returns lowercase plan names; configuration should not have to know that.</summary>
        [Fact]
        public async Task Plan_rate_lookup_is_case_insensitive()
        {
            AddSeats(Contoso, "business", 2);

            var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["Business"] = 1900m };
            var pool = (await new UsageQueryService(_factory)
                .GetAllowancePoolsAsync(2026, 8, UserScope.Reader(Contoso), rates, AsOf)).Single();

            Assert.Equal(3_800m, pool.Capacity);
        }
    }
}
