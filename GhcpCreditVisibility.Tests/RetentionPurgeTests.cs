using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhcpCreditVisibility.Tests;

/// <summary>
/// The retention purge deletes usage history permanently, and GitHub's billing API only serves the
/// CURRENT month — so anything this purge removes cannot be re-fetched from anywhere. A wrong
/// cutoff is silent and unrecoverable, which is why the arithmetic is pinned down here.
/// </summary>
public class RetentionCutoffTests
{
    [Theory]
    // now(year, month), configured months, expected first KEPT (year, month)
    [InlineData(2026, 7, 12, 2025, 7)]
    [InlineData(2026, 7, 6, 2026, 1)]
    [InlineData(2026, 7, 3, 2026, 4)]
    [InlineData(2026, 1, 3, 2025, 10)]  // crosses the year boundary
    [InlineData(2026, 3, 3, 2025, 12)]  // lands exactly on December
    [InlineData(2026, 1, 12, 2025, 1)]
    [InlineData(2026, 2, 14, 2024, 12)] // >12 months, crosses two year boundaries
    public void Computes_the_first_kept_month(int nowYear, int nowMonth, int months, int expectedYear, int expectedMonth)
    {
        var now = new DateTime(nowYear, nowMonth, 15, 9, 30, 0, DateTimeKind.Utc);

        var cutoff = SnapshotService.ComputeRetentionCutoff(now, months);

        Assert.Equal((expectedYear, expectedMonth), cutoff);
    }

    [Theory]
    [InlineData(-12)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Never_keeps_less_than_the_floor(int configuredMonths)
    {
        var now = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

        var cutoff = SnapshotService.ComputeRetentionCutoff(now, configuredMonths);

        Assert.Equal(SnapshotService.ComputeRetentionCutoff(now, SnapshotService.MinRetentionMonths), cutoff);
        Assert.Equal((2026, 4), cutoff);
    }

    [Fact]
    public void Day_of_month_does_not_affect_the_cutoff()
    {
        var firstOfMonth = SnapshotService.ComputeRetentionCutoff(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), 6);
        var lastOfMonth = SnapshotService.ComputeRetentionCutoff(new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc), 6);

        Assert.Equal(firstOfMonth, lastOfMonth);
    }

    /// <summary>
    /// Regression guard for the bug called out in SnapshotService's own comments: the predicate has
    /// to compare the Year/Month COLUMNS as integers. Building a DateTime from column values inside
    /// the query compiles happily and then throws at runtime against SQL Server. Translation is
    /// offline, so this needs no database.
    /// </summary>
    [Fact]
    public void Purge_predicate_is_translatable_by_the_sql_server_provider()
    {
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseSqlServer("Server=tcp:not-contacted,1433;Database=none;")
            .Options;
        using var db = new BillingDbContext(options);
        var (cutoffYear, cutoffMonth) = SnapshotService.ComputeRetentionCutoff(
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), 6);

        var sql = db.UsageSnapshots
            .Where(x => x.Year < cutoffYear || (x.Year == cutoffYear && x.Month < cutoffMonth))
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// End-to-end coverage of the purge through <see cref="SnapshotService.RunAsync"/> against the
/// in-memory provider — which is also the provider whose missing ExecuteDelete support made the
/// whole run fail locally, so these tests cover that fallback path too.
/// </summary>
public class RetentionPurgeTests
{
    private const int MonthsOfHistorySeeded = 24;

    private static async Task<IDbContextFactory<BillingDbContext>> SeedMonthlyHistoryAsync(DateTime now)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        var factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();

        var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var db = await factory.CreateDbContextAsync();
        for (var i = 0; i < MonthsOfHistorySeeded; i++)
        {
            var m = thisMonth.AddMonths(-i);
            db.UsageSnapshots.Add(new UsageSnapshot
            {
                SnapshotUtc = m,
                Year = m.Year,
                Month = m.Month,
                Day = 1,
                UserLogin = $"seed-user-{i}",
                Product = "copilot",
                Sku = "premium_request",
                Model = "gpt-5",
                NetQuantity = 1m,
                NetAmount = 1m,
                GrossAmount = 1m
            });
        }
        await db.SaveChangesAsync();
        return factory;
    }

    private static SnapshotService BuildService(IDbContextFactory<BillingDbContext> factory, int retentionMonths)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Enterprise"] = "test-enterprise",
                ["Retention:Months"] = retentionMonths.ToString()
            })
            .Build();

        return new SnapshotService(
            new MockGitHubBillingClient(), factory, config, NullLogger<SnapshotService>.Instance);
    }

    [Fact]
    public async Task Purges_everything_older_than_the_cutoff_and_nothing_newer()
    {
        var now = DateTime.UtcNow;
        const int retentionMonths = 6;
        var (cutoffYear, cutoffMonth) = SnapshotService.ComputeRetentionCutoff(now, retentionMonths);
        var factory = await SeedMonthlyHistoryAsync(now);

        await BuildService(factory, retentionMonths).RunAsync();

        await using var db = await factory.CreateDbContextAsync();
        var survivors = await db.UsageSnapshots.ToListAsync();

        Assert.NotEmpty(survivors);
        Assert.All(survivors, row => Assert.True(
            row.Year > cutoffYear || (row.Year == cutoffYear && row.Month >= cutoffMonth),
            $"{row.Year}-{row.Month:00} is older than the cutoff {cutoffYear}-{cutoffMonth:00} and should have been purged"));

        // The cutoff month itself is INCLUSIVE — losing it would silently shorten retention by a month.
        Assert.Contains(survivors, r => r.Year == cutoffYear && r.Month == cutoffMonth);

        // ...and the month immediately before it must be gone, or the purge did nothing.
        var lastPurged = new DateTime(cutoffYear, cutoffMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        Assert.DoesNotContain(survivors, r => r.Year == lastPurged.Year && r.Month == lastPurged.Month);
    }

    [Fact]
    public async Task Retention_configured_below_the_floor_still_keeps_three_months()
    {
        var now = DateTime.UtcNow;
        var factory = await SeedMonthlyHistoryAsync(now);

        // A misconfigured Retention__Months=1 must NOT be able to destroy the quarter of history
        // that reports and trends depend on.
        await BuildService(factory, retentionMonths: 1).RunAsync();

        await using var db = await factory.CreateDbContextAsync();
        var survivors = await db.UsageSnapshots.ToListAsync();

        var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < SnapshotService.MinRetentionMonths; i++)
        {
            var kept = thisMonth.AddMonths(-i);
            Assert.Contains(survivors, r => r.Year == kept.Year && r.Month == kept.Month);
        }
    }

    /// <summary>
    /// Pins the off-by-one: "retain N months" keeps N whole prior months PLUS the current, still
    /// incomplete, month — N + 1 calendar months in total. Easy to shift by one in a refactor, and
    /// shifting it the wrong way deletes a month of history that cannot be re-fetched.
    /// </summary>
    [Fact]
    public async Task Keeps_the_current_month_plus_N_prior_months()
    {
        const int retentionMonths = 6;
        const int expectedKept = retentionMonths + 1;
        var now = DateTime.UtcNow;
        var factory = await SeedMonthlyHistoryAsync(now);

        await BuildService(factory, retentionMonths).RunAsync();

        await using var db = await factory.CreateDbContextAsync();
        var run = await db.SnapshotRuns.OrderByDescending(r => r.Id).FirstAsync();
        var survivingMonths = await db.UsageSnapshots
            .Where(r => r.UserLogin.StartsWith("seed-user-"))
            .Select(r => new { r.Year, r.Month })
            .Distinct()
            .ToListAsync();

        Assert.Equal("succeeded", run.Status);
        Assert.Equal(expectedKept, survivingMonths.Count);
        Assert.Equal(MonthsOfHistorySeeded - expectedKept, run.RowsPurged);
    }
}
