using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Models;
using GhcpCreditVisibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Opt-in per-user history backfill.
    ///
    /// Three separate floors bound how far back this can go, and the planner must respect the most
    /// recent of them: the app's own retention window (fetching rows the purge would immediately
    /// delete is pure waste), GitHub's rolling 24-month window, and the AI-credits epoch — that
    /// meter did not exist before 1 June 2026, so months before it hold nothing to fetch regardless
    /// of what the other two allow.
    ///
    /// The other property under test is CONVERGENCE: the plan must empty out and stay empty. An
    /// enterprise whose history is complete must not re-plan the same months every cycle, which is
    /// what happens if row-absence rather than a watermark is used as the "not done" signal.
    /// </summary>
    public class UserBackfillTests
    {
        private static readonly DateTime Now = new(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc);

        // ── Planner ──────────────────────────────────────────────────────────────

        [Fact]
        public void Plans_whole_past_months_newest_first_never_the_current_month()
        {
            var plan = SnapshotService.PlanUserBackfill(Now, retentionMonths: 24, oldestDone: null);

            Assert.DoesNotContain((2026, 8), plan);            // the live path owns the current month
            Assert.Equal((2026, 7), plan[0]);                  // newest first
            Assert.Equal(plan.OrderByDescending(p => p.Year).ThenByDescending(p => p.Month), plan);
        }

        /// <summary>
        /// The binding floor today. Retention (24 months) and the API window (24 months) would both
        /// allow reaching back to 2024, but AI credits did not exist then, so the plan must stop at
        /// June 2026 rather than spending a call per user per month on guaranteed-empty periods.
        /// </summary>
        [Fact]
        public void Never_plans_months_before_the_ai_credits_epoch()
        {
            var plan = SnapshotService.PlanUserBackfill(Now, retentionMonths: 24, oldestDone: null);

            Assert.Equal(new[] { (2026, 7), (2026, 6) }, plan);
            Assert.All(plan, p =>
                Assert.True(new DateTime(p.Year, p.Month, 1) >= SnapshotService.AiCreditEpoch,
                    $"{p.Year}-{p.Month:00} precedes the AI-credits epoch"));
        }

        /// <summary>
        /// Once the epoch is old enough not to bind, retention takes over — there is no point
        /// fetching a month the purge deletes on the same run.
        /// </summary>
        [Fact]
        public void Retention_becomes_the_floor_once_the_epoch_is_far_enough_back()
        {
            var later = new DateTime(2028, 3, 10, 0, 0, 0, DateTimeKind.Utc);

            var plan = SnapshotService.PlanUserBackfill(later, retentionMonths: 6, oldestDone: null);

            Assert.Equal((2028, 2), plan[0]);
            Assert.Equal((2027, 9), plan[^1]);   // 2028-03 minus 6 months
            Assert.Equal(6, plan.Count);
        }

        /// <summary>
        /// The floor must be the month retention KEEPS, not one either side of it. Asserted against
        /// the purge's own cutoff so the two can never drift apart — a planner one month too greedy
        /// re-fetches rows the same run deletes, forever.
        /// </summary>
        [Fact]
        public void Plan_floor_matches_the_retention_purge_cutoff()
        {
            var later = new DateTime(2028, 3, 10, 0, 0, 0, DateTimeKind.Utc);
            var plan = SnapshotService.PlanUserBackfill(later, retentionMonths: 6, oldestDone: null);
            var cutoff = SnapshotService.ComputeRetentionCutoff(later, 6);

            Assert.Equal((cutoff.Year, cutoff.Month), plan[^1]);
        }

        /// <summary>GitHub refuses anything over two years old, so neither may a plan.</summary>
        [Fact]
        public void Never_plans_beyond_githubs_24_month_window()
        {
            var far = new DateTime(2030, 1, 15, 0, 0, 0, DateTimeKind.Utc);

            var plan = SnapshotService.PlanUserBackfill(far, retentionMonths: 120, oldestDone: null);

            Assert.All(plan, p =>
                Assert.True(new DateTime(p.Year, p.Month, 1) >= far.AddMonths(-24).AddDays(-far.Day + 1),
                    $"{p.Year}-{p.Month:00} is outside GitHub's 24-month window"));
        }

        [Fact]
        public void Resumes_from_the_watermark_rather_than_redoing_finished_months()
        {
            var plan = SnapshotService.PlanUserBackfill(Now, retentionMonths: 24, oldestDone: (2026, 7));

            Assert.Equal(new[] { (2026, 6) }, plan);
        }

        [Fact]
        public void Converges_to_empty_once_the_floor_is_reached()
        {
            var plan = SnapshotService.PlanUserBackfill(Now, retentionMonths: 24, oldestDone: (2026, 6));

            Assert.Empty(plan);
        }

        [Theory]
        [InlineData(7, 2, 14)]           // the small case: a handful of users, two months
        [InlineData(5000, 24, 120000)]   // the large case: the reason this is opt-in
        [InlineData(0, 3, 0)]
        [InlineData(10, 0, 0)]
        public void Estimate_is_users_times_months(int users, int months, int expected)
            => Assert.Equal(expected, SnapshotService.EstimateUserBackfillCalls(users, months));

        // ── End to end through the snapshot job ──────────────────────────────────

        private static IDbContextFactory<BillingDbContext> NewFactory()
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();
        }

        private sealed class MockFactory : IEnterpriseBillingClientFactory
        {
            private readonly MockGitHubBillingClient _client = new();
            public Task<IGitHubBillingClient> GetClientAsync(Enterprise e, CancellationToken ct = default)
                => Task.FromResult<IGitHubBillingClient>(_client);
        }

        private static (SnapshotService Svc, EnterpriseRegistryService Registry) Build(IDbContextFactory<BillingDbContext> f)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Enterprise"] = "contoso",
                ["GitHub:UseMock"] = "true",
                ["Retention:Months"] = "24",
            }).Build();
            var registry = new EnterpriseRegistryService(f, config, NullLogger<EnterpriseRegistryService>.Instance);
            var svc = new SnapshotService(new MockFactory(), registry, f, config,
                NullLogger<SnapshotService>.Instance, new GitHubRateLimitRegistry());
            return (svc, registry);
        }

        private static async Task<Enterprise> SeededEnterpriseAsync(
            IDbContextFactory<BillingDbContext> f, EnterpriseRegistryService registry, SnapshotService svc)
        {
            await svc.RunAsync();   // establishes the enterprise and the current month
            await using var db = await f.CreateDbContextAsync();
            return await db.Enterprises.FirstAsync(e => e.Slug != Enterprise.BootstrapPlaceholderSlug);
        }

        /// <summary>
        /// Off by default. An enterprise that never asked for backfill must never spend a call per
        /// user per month on it — that cost is the whole reason this is opt-in.
        /// </summary>
        [Fact]
        public async Task Does_nothing_unless_enabled()
        {
            var f = NewFactory();
            var (svc, registry) = Build(f);
            var ent = await SeededEnterpriseAsync(f, registry, svc);

            await svc.RunAsync();

            await using var db = await f.CreateDbContextAsync();
            var months = await db.UsageSnapshots.Select(r => new { r.Year, r.Month }).Distinct().CountAsync();
            Assert.Equal(1, months);
            Assert.Null((await db.Enterprises.FindAsync(ent.Id))!.UserBackfillOldestYear);
        }

        [Fact]
        public async Task Enabled_backfill_adds_older_months_and_records_the_watermark()
        {
            var f = NewFactory();
            var (svc, registry) = Build(f);
            var ent = await SeededEnterpriseAsync(f, registry, svc);

            await registry.SetUserBackfillEnabledAsync(ent.Id, true);
            await svc.RunAsync();

            await using var db = await f.CreateDbContextAsync();
            var months = await db.UsageSnapshots.Select(r => new { r.Year, r.Month }).Distinct().ToListAsync();
            Assert.True(months.Count > 1, $"expected history beyond the current month, saw {months.Count} month(s)");

            var after = (await db.Enterprises.FindAsync(ent.Id))!;
            Assert.NotNull(after.UserBackfillOldestYear);
            Assert.NotNull(after.UserBackfillOldestMonth);

            // The watermark must name the OLDEST month present — if it ran ahead of the data, a
            // resume would skip months that were never actually collected.
            var oldest = months.OrderBy(m => m.Year).ThenBy(m => m.Month).First();
            Assert.Equal(oldest.Year, after.UserBackfillOldestYear);
            Assert.Equal(oldest.Month, after.UserBackfillOldestMonth);
        }

        /// <summary>
        /// Every user in a completed month must be present. A month missing users is worse than a
        /// missing month: it renders as a real drop in spend rather than as absent history.
        /// </summary>
        [Fact]
        public async Task Completed_months_cover_every_user_the_current_month_has()
        {
            var f = NewFactory();
            var (svc, registry) = Build(f);
            var ent = await SeededEnterpriseAsync(f, registry, svc);

            await registry.SetUserBackfillEnabledAsync(ent.Id, true);
            await svc.RunAsync();

            await using var db = await f.CreateDbContextAsync();
            var rows = await db.UsageSnapshots.Where(r => r.EnterpriseId == ent.Id).ToListAsync();
            var current = rows.Where(r => r.Year == DateTime.UtcNow.Year && r.Month == DateTime.UtcNow.Month)
                              .Select(r => r.UserLogin).Distinct().OrderBy(u => u).ToList();
            Assert.NotEmpty(current);

            foreach (var m in rows.Select(r => new { r.Year, r.Month }).Distinct())
            {
                var users = rows.Where(r => r.Year == m.Year && r.Month == m.Month)
                                .Select(r => r.UserLogin).Distinct().OrderBy(u => u).ToList();
                Assert.Equal(current, users);
            }
        }

        /// <summary>
        /// Finishing must turn the flag off by itself. Left on, every subsequent cycle would re-plan
        /// and re-fetch the same completed months forever.
        /// </summary>
        [Fact]
        public async Task Clears_its_own_flag_when_there_is_nothing_left_to_fetch()
        {
            var f = NewFactory();
            var (svc, registry) = Build(f);
            var ent = await SeededEnterpriseAsync(f, registry, svc);

            await registry.SetUserBackfillEnabledAsync(ent.Id, true);
            await svc.RunAsync();   // fills everything reachable
            await svc.RunAsync();   // finds nothing left, disables itself

            await using var db = await f.CreateDbContextAsync();
            Assert.False((await db.Enterprises.FindAsync(ent.Id))!.UserBackfillEnabled);
        }

        /// <summary>Re-running a completed month refreshes rows; it must not duplicate them.</summary>
        [Fact]
        public async Task Rerunning_does_not_duplicate_backfilled_rows()
        {
            var f = NewFactory();
            var (svc, registry) = Build(f);
            var ent = await SeededEnterpriseAsync(f, registry, svc);

            await registry.SetUserBackfillEnabledAsync(ent.Id, true);
            await svc.RunAsync();

            async Task<int> RowsAsync()
            {
                await using var db = await f.CreateDbContextAsync();
                return await db.UsageSnapshots.CountAsync(r => r.EnterpriseId == ent.Id);
            }
            var afterFirst = await RowsAsync();

            // Rewind the watermark so the same months are planned again.
            await registry.SetUserBackfillEnabledAsync(ent.Id, true);
            await using (var db = await f.CreateDbContextAsync())
            {
                var row = await db.Enterprises.FindAsync(ent.Id);
                row!.UserBackfillOldestYear = null;
                row.UserBackfillOldestMonth = null;
                await db.SaveChangesAsync();
            }
            await svc.RunAsync();

            Assert.Equal(afterFirst, await RowsAsync());
        }

        /// <summary>
        /// Backfilled months must NOT write daily rows. A past month gets one reading, and recording
        /// it as a day would attribute the entire month's spend to a single date on the trend chart.
        /// </summary>
        [Fact]
        public async Task Backfill_writes_no_daily_rows_for_past_months()
        {
            var f = NewFactory();
            var (svc, registry) = Build(f);
            var ent = await SeededEnterpriseAsync(f, registry, svc);

            await registry.SetUserBackfillEnabledAsync(ent.Id, true);
            await svc.RunAsync();

            await using var db = await f.CreateDbContextAsync();
            var now = DateTime.UtcNow;
            var pastDaily = await db.DailyUsageSnapshots
                .Where(r => r.EnterpriseId == ent.Id && (r.Year != now.Year || r.Month != now.Month))
                .CountAsync();
            Assert.Equal(0, pastDaily);
        }

        /// <summary>
        /// The licensed-user count is stored during snapshots so the admin console can price a
        /// backfill without calling GitHub from a page request.
        /// </summary>
        [Fact]
        public async Task Snapshot_records_the_licensed_user_count_for_the_estimate()
        {
            var f = NewFactory();
            var (svc, registry) = Build(f);
            var ent = await SeededEnterpriseAsync(f, registry, svc);

            await using var db = await f.CreateDbContextAsync();
            var stored = (await db.Enterprises.FindAsync(ent.Id))!.LicensedUserCount;
            var actual = await db.UsageSnapshots.Where(r => r.EnterpriseId == ent.Id)
                                 .Select(r => r.UserLogin).Distinct().CountAsync();
            Assert.Equal(actual, stored);
        }
    }
}
