using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Month arithmetic for the organization-usage backfill.
    ///
    /// One call fetches a whole month, so two years of organization history costs ~24 calls — unlike
    /// per-user daily history, where the same span would run to millions and is not worth planning
    /// around. The job walks backwards a few months per cycle so a new enterprise catches up over
    /// several runs rather than firing a burst on its first.
    ///
    /// The constraint these tests exist to pin down: fetching past the RETENTION FLOOR is pure
    /// waste, because the purge deletes those rows on the very same run — forever, every cycle.
    /// </summary>
    public class OrgBackfillTests
    {
        private static readonly DateTime Aug2026 = new(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void First_run_starts_at_last_month_and_walks_backwards()
        {
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, retentionMonths: 6, oldestDone: null);
            Assert.Equal(new[] { (2026, 7), (2026, 6), (2026, 5) }, plan);
        }

        [Fact]
        public void Never_fetches_the_current_month()
        {
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, 6, null);
            Assert.DoesNotContain((2026, 8), plan);   // the normal snapshot path owns it
        }

        [Fact]
        public void Resumes_from_the_watermark()
        {
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, 6, oldestDone: (2026, 5));
            Assert.Equal(new[] { (2026, 4), (2026, 3), (2026, 2) }, plan);
        }

        /// <summary>
        /// The whole point. Anything older than the floor is deleted by the purge on the same run,
        /// so fetching it would repeat forever to no effect.
        ///
        /// The floor is the RETENTION CUTOFF MONTH ITSELF, which the purge KEEPS (it deletes rows
        /// strictly older). For August with six months' retention that is February — so February is
        /// fetched, January is not.
        /// </summary>
        [Fact]
        public void Stops_at_the_retention_floor()
        {
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, retentionMonths: 6, oldestDone: (2026, 4));
            Assert.Equal(new[] { (2026, 3), (2026, 2) }, plan);
            Assert.DoesNotContain((2026, 1), plan);   // beyond the floor
        }

        /// <summary>
        /// The floor must track the purge exactly. Planning a month the purge deletes means fetching
        /// it on every cycle forever; stopping a month early leaves a permanent hole in a month that
        /// is being retained. Pinned against the real cutoff rather than a hardcoded date.
        /// </summary>
        [Fact]
        public void Plan_floor_matches_the_retention_purge_cutoff()
        {
            var (cutY, cutM) = SnapshotService.ComputeRetentionCutoff(Aug2026, 6);
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, 6, oldestDone: null, maxMonths: 240);

            var oldest = plan.Last();
            Assert.Equal((cutY, cutM), oldest);                       // reaches the kept cutoff month
            Assert.DoesNotContain(plan, p =>                          // and never past it
                p.Year < cutY || (p.Year == cutY && p.Month < cutM));
        }

        [Fact]
        public void Yields_nothing_once_the_floor_is_reached()
        {
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, 6, oldestDone: (2026, 2));
            Assert.Empty(plan);
        }

        /// <summary>A watermark left behind by a shortened retention window must not re-trigger work.</summary>
        [Fact]
        public void Yields_nothing_when_the_watermark_is_already_older_than_the_floor()
        {
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, 6, oldestDone: (2025, 1));
            Assert.Empty(plan);
        }

        /// <summary>
        /// Retention and backfill depth are NOT independent settings. Holding two years of
        /// organization history requires raising retention first — widening the backfill alone
        /// achieves nothing, because the purge removes whatever it fetches.
        /// </summary>
        [Fact]
        public void Raising_retention_deepens_the_backfill()
        {
            var shallow = SnapshotService.PlanOrgBackfill(Aug2026, 6, (2026, 2));
            var deep = SnapshotService.PlanOrgBackfill(Aug2026, 24, (2026, 2));

            Assert.Empty(shallow);        // floor already reached at six months
            Assert.NotEmpty(deep);        // same watermark, much further to go at 24
            Assert.Equal(new[] { (2026, 1), (2025, 12), (2025, 11) }, deep);
        }

        /// <summary>GitHub rejects anything beyond two years, so the plan must never ask.</summary>
        [Fact]
        public void Never_plans_beyond_githubs_two_year_window()
        {
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, retentionMonths: 120, oldestDone: null, maxMonths: 200);
            var oldest = plan.Last();
            var oldestDate = new DateTime(oldest.Year, oldest.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(oldestDate >= new DateTime(2024, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                $"planned {oldestDate:yyyy-MM}, which is more than 24 months before {Aug2026:yyyy-MM}");
        }

        [Fact]
        public void Respects_the_per_run_cap()
        {
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, 24, null);
            Assert.Equal(SnapshotService.OrgBackfillMonthsPerRun, plan.Count);
        }

        [Fact]
        public void Walks_correctly_across_a_year_boundary()
        {
            var jan = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
            var plan = SnapshotService.PlanOrgBackfill(jan, 6, null);
            Assert.Equal(new[] { (2025, 12), (2025, 11), (2025, 10) }, plan);
        }

        /// <summary>Retention below the floor is clamped, exactly as the purge clamps it.</summary>
        [Fact]
        public void Sub_floor_retention_is_clamped_not_honoured()
        {
            var plan = SnapshotService.PlanOrgBackfill(Aug2026, retentionMonths: 1, oldestDone: null);
            Assert.Equal(SnapshotService.MinRetentionMonths, plan.Count);
            Assert.Equal(new[] { (2026, 7), (2026, 6), (2026, 5) }, plan);
        }

        /// <summary>Repeatedly applying the plan converges and then stops — no perpetual churn.</summary>
        [Fact]
        public void Converges_to_empty_after_successive_runs()
        {
            (int, int)? watermark = null;
            var runs = 0;
            while (runs++ < 20)
            {
                var plan = SnapshotService.PlanOrgBackfill(Aug2026, 6, watermark);
                if (plan.Count == 0) break;
                watermark = plan[^1];
            }
            Assert.True(runs < 20, "backfill never settled");
            Assert.Empty(SnapshotService.PlanOrgBackfill(Aug2026, 6, watermark));
        }
    }
}
