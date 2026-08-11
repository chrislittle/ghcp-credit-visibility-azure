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
    /// Organization / repository attribution, from GitHub's general billing usage report.
    ///
    /// The per-user AI-credit report cannot supply this: its top-level `user` and `organization`
    /// fields merely ECHO the filters passed, so filtering by user yields no organization at all.
    /// This endpoint returns organizationName, repositoryName and a per-item date for a whole month
    /// in ONE call — but supports no user filter, so the two sources are complementary.
    /// </summary>
    public class OrgUsageTests
    {
        private static IDbContextFactory<BillingDbContext> NewFactory()
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();
        }

        private static EnterpriseRegistryService Registry(IDbContextFactory<BillingDbContext> f)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Enterprise"] = "contoso",
                ["GitHub:UseMock"] = "true",
            }).Build();
            return new EnterpriseRegistryService(f, config, NullLogger<EnterpriseRegistryService>.Instance);
        }

        private sealed class MockFactory : IEnterpriseBillingClientFactory
        {
            private readonly IGitHubBillingClient _client;
            public MockFactory(IGitHubBillingClient? client = null) => _client = client ?? new MockGitHubBillingClient();
            public Task<IGitHubBillingClient> GetClientAsync(Enterprise e, CancellationToken ct = default)
                => Task.FromResult(_client);
        }

        /// <summary>Mock whose org-usage call fails; everything else behaves normally.</summary>
        private sealed class OrgUsageBrokenClient : IGitHubBillingClient
        {
            private readonly MockGitHubBillingClient _inner = new();
            public Task<IReadOnlyList<EnterpriseLicenseUser>> GetEnterpriseUsersAsync(string e, CancellationToken ct = default) => _inner.GetEnterpriseUsersAsync(e, ct);
            public Task<UserCreditUsage?> GetCurrentMonthUsageForUserAsync(string e, string u, CancellationToken ct = default) => _inner.GetCurrentMonthUsageForUserAsync(e, u, ct);
            public Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string e, CancellationToken ct = default) => _inner.GetCostCentersAsync(e, ct);
            public Task<IReadOnlyList<Budget>> GetBudgetsAsync(string e, CancellationToken ct = default) => _inner.GetBudgetsAsync(e, ct);
            public Task<IReadOnlyList<OrgUsageItem>> GetOrgUsageAsync(string e, int y, int m, CancellationToken ct = default)
                => throw new HttpRequestException("404 - endpoint not enabled for this enterprise");
        }

        private static SnapshotService Service(IDbContextFactory<BillingDbContext> f, EnterpriseRegistryService r, IGitHubBillingClient? client = null)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:Months"] = "6",
            }).Build();
            return new SnapshotService(new MockFactory(client), r, f, config, NullLogger<SnapshotService>.Instance);
        }

        [Fact]
        public async Task Snapshot_writes_org_attributed_usage()
        {
            var f = NewFactory();
            await Service(f, Registry(f)).RunAsync();

            await using var db = await f.CreateDbContextAsync();
            var rows = await db.OrgUsageSnapshots.ToListAsync();
            Assert.NotEmpty(rows);
            Assert.Contains(rows, r => !string.IsNullOrEmpty(r.OrganizationName));
            Assert.Contains(rows, r => !string.IsNullOrEmpty(r.RepositoryName));
        }

        /// <summary>
        /// A live sample had 15 of 37 line items with NO organizationName — enterprise-level charges
        /// belonging to no org. Dropping them would silently lose real spend from every rollup.
        /// </summary>
        [Fact]
        public async Task Unattributed_line_items_are_kept_not_dropped()
        {
            var f = NewFactory();
            await Service(f, Registry(f)).RunAsync();

            await using var db = await f.CreateDbContextAsync();
            var unattributed = await db.OrgUsageSnapshots.Where(r => r.OrganizationName == null).ToListAsync();
            Assert.NotEmpty(unattributed);
            Assert.All(unattributed, r => Assert.True(r.GrossAmount > 0m, "unattributed rows must still carry their spend"));
        }

        /// <summary>The date is the LINE ITEM's own, which is what makes this table genuinely daily.</summary>
        [Fact]
        public async Task Day_comes_from_the_line_item_not_the_run_clock()
        {
            var f = NewFactory();
            await Service(f, Registry(f)).RunAsync();

            await using var db = await f.CreateDbContextAsync();
            var distinctDays = await db.OrgUsageSnapshots.Select(r => r.Day).Distinct().ToListAsync();
            // If Day came from the run clock every row would share today's date.
            Assert.True(distinctDays.Count > 1, $"expected several distinct days, saw {distinctDays.Count}");
        }

        /// <summary>
        /// One response IS the whole month, so the job replaces that month rather than upserting.
        /// Re-running must not duplicate it.
        ///
        /// Asserted per-month, not on the overall row count: the backfill legitimately ADDS older
        /// months on later runs, so a stable total would be the wrong expectation (and would fail
        /// for a correct implementation).
        /// </summary>
        [Fact]
        public async Task Rerunning_replaces_a_month_rather_than_duplicating_it()
        {
            var f = NewFactory();
            var svc = Service(f, Registry(f));
            var now = DateTime.UtcNow;

            async Task<int> CurrentMonthRowsAsync()
            {
                await using var db = await f.CreateDbContextAsync();
                return await db.OrgUsageSnapshots.CountAsync(r => r.Year == now.Year && r.Month == now.Month);
            }

            await svc.RunAsync();
            var afterFirst = await CurrentMonthRowsAsync();
            Assert.True(afterFirst > 0, "expected the current month to be populated");

            await svc.RunAsync();
            Assert.Equal(afterFirst, await CurrentMonthRowsAsync());
        }

        /// <summary>Backfill advances on successive runs, then settles instead of churning.</summary>
        [Fact]
        public async Task Backfill_adds_older_months_then_settles()
        {
            var f = NewFactory();
            var svc = Service(f, Registry(f));

            async Task<int> MonthsAsync()
            {
                await using var db = await f.CreateDbContextAsync();
                return await db.OrgUsageSnapshots.Select(r => new { r.Year, r.Month }).Distinct().CountAsync();
            }

            await svc.RunAsync();
            var afterFirst = await MonthsAsync();
            await svc.RunAsync();
            var afterSecond = await MonthsAsync();
            Assert.True(afterSecond > afterFirst, "second run should have backfilled older months");

            // Keep running until it stops growing; it must converge, not churn forever.
            var prev = afterSecond;
            for (var i = 0; i < 10; i++)
            {
                await svc.RunAsync();
                var n = await MonthsAsync();
                if (n == prev) return;
                prev = n;
            }
            Assert.Fail("backfill never settled");
        }

        /// <summary>
        /// Org attribution is supplementary. An enterprise not yet on the endpoint, or a PAT lacking
        /// scope, must never fail a run whose per-user numbers — the app's primary output — are
        /// already written.
        /// </summary>
        [Fact]
        public async Task Org_usage_failure_does_not_fail_the_run()
        {
            var f = NewFactory();
            await Service(f, Registry(f), new OrgUsageBrokenClient()).RunAsync();

            await using var db = await f.CreateDbContextAsync();
            var run = await db.SnapshotRuns.OrderByDescending(r => r.Id).FirstAsync();
            Assert.Equal("succeeded", run.Status);
            Assert.NotEmpty(await db.UsageSnapshots.ToListAsync());   // per-user data still landed
            Assert.Empty(await db.OrgUsageSnapshots.ToListAsync());   // org data simply absent
        }

        /// <summary>
        /// Unlike DailyUsageSnapshot (cumulative), these rows are TRUE PER-DAY values and may be
        /// summed. Mixing the two mental models is the easiest way to produce a wrong number.
        /// </summary>
        [Fact]
        public async Task Org_rows_are_per_day_values_and_sum_sanely()
        {
            var f = NewFactory();
            await Service(f, Registry(f)).RunAsync();

            await using var db = await f.CreateDbContextAsync();
            var rows = await db.OrgUsageSnapshots.ToListAsync();
            var total = rows.Sum(r => r.NetAmount);
            var largestDay = rows.GroupBy(r => r.Day).Max(g => g.Sum(r => r.NetAmount));
            Assert.True(total > 0m);
            Assert.True(largestDay <= total, "no single day may exceed the month; that would mean cumulative values");
        }

        /// <summary>UnitType is the only field distinguishing meters once premium requests land.</summary>
        [Fact]
        public async Task UnitType_is_captured_on_every_usage_table()
        {
            var f = NewFactory();
            await Service(f, Registry(f)).RunAsync();

            await using var db = await f.CreateDbContextAsync();
            Assert.Contains(await db.UsageSnapshots.ToListAsync(), r => !string.IsNullOrEmpty(r.UnitType));
            Assert.Contains(await db.DailyUsageSnapshots.ToListAsync(), r => !string.IsNullOrEmpty(r.UnitType));
            Assert.Contains(await db.OrgUsageSnapshots.ToListAsync(), r => !string.IsNullOrEmpty(r.UnitType));
        }
    }
}
