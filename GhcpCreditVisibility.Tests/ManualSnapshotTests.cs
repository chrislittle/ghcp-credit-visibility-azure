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
    /// On-demand snapshot of a single enterprise, for the admin console's "Run now".
    ///
    /// Exists because the scheduled job is an in-process 12-hour timer with no manual trigger, so
    /// forcing a run meant restarting the container — which is a poor answer in production and made
    /// onboarding an enterprise a matter of either bouncing the app or waiting half a day.
    /// </summary>
    public class ManualSnapshotTests
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
            private readonly MockGitHubBillingClient _mock = new();
            public Task<IGitHubBillingClient> GetClientAsync(Enterprise e, CancellationToken ct = default)
                => Task.FromResult<IGitHubBillingClient>(_mock);
        }

        private static SnapshotService Service(IDbContextFactory<BillingDbContext> f, EnterpriseRegistryService r)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:Months"] = "6",
            }).Build();
            return new SnapshotService(new MockFactory(), r, f, config, NullLogger<SnapshotService>.Instance);
        }

        [Fact]
        public async Task Running_one_enterprise_writes_only_that_enterprises_data()
        {
            var f = NewFactory();
            var registry = Registry(f);
            var svc = Service(f, registry);

            await registry.EnsureBootstrapAsync();
            var all = await registry.GetAllAsync();
            var target = all.First();

            await svc.RunOneAsync(target.Id);

            await using var db = await f.CreateDbContextAsync();
            var written = await db.UsageSnapshots.Select(u => u.EnterpriseId).Distinct().ToListAsync();
            Assert.Equal(new[] { target.Id }, written);
        }

        /// <summary>A run leaves the same audit trail as a scheduled one — the UI reads that column.</summary>
        [Fact]
        public async Task Manual_run_records_a_snapshot_run_row()
        {
            var f = NewFactory();
            var registry = Registry(f);
            var svc = Service(f, registry);

            await registry.EnsureBootstrapAsync();
            var target = (await registry.GetAllAsync()).First();
            await svc.RunOneAsync(target.Id);

            await using var db = await f.CreateDbContextAsync();
            var run = await db.SnapshotRuns.SingleAsync(r => r.EnterpriseId == target.Id);
            Assert.Equal("succeeded", run.Status);
            Assert.True(run.RowsWritten > 0);
        }

        /// <summary>
        /// A DISABLED enterprise is deliberately excluded from the scheduled loop. Running one by
        /// hand would quietly reintroduce data the operator chose to stop collecting, so it is
        /// refused rather than silently allowed.
        /// </summary>
        [Fact]
        public async Task Disabled_enterprises_are_refused()
        {
            var f = NewFactory();
            var registry = Registry(f);
            var svc = Service(f, registry);

            await registry.EnsureBootstrapAsync();
            var target = (await registry.GetAllAsync()).First();
            await registry.SetEnabledAsync(target.Id, false, "test");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunOneAsync(target.Id));
            Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);

            await using var db = await f.CreateDbContextAsync();
            Assert.Empty(await db.UsageSnapshots.ToListAsync());
        }

        [Fact]
        public async Task An_unknown_enterprise_id_is_refused()
        {
            var f = NewFactory();
            var svc = Service(f, Registry(f));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunOneAsync(99999));
            Assert.Contains("registry", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Re-running is safe: the same month is upserted, not duplicated. An operator clicking
        /// "Run now" twice must not double anyone's spend.
        /// </summary>
        [Fact]
        public async Task Running_twice_does_not_duplicate_rows()
        {
            var f = NewFactory();
            var registry = Registry(f);
            var svc = Service(f, registry);

            await registry.EnsureBootstrapAsync();
            var target = (await registry.GetAllAsync()).First();

            await svc.RunOneAsync(target.Id);
            int afterFirst;
            await using (var db = await f.CreateDbContextAsync())
                afterFirst = await db.UsageSnapshots.CountAsync();

            await svc.RunOneAsync(target.Id);
            await using var db2 = await f.CreateDbContextAsync();
            Assert.Equal(afterFirst, await db2.UsageSnapshots.CountAsync());
        }
    }
}
