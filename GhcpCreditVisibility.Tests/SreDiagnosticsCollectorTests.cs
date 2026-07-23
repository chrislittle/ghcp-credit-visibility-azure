using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GhcpCreditVisibility.Tests;

/// <summary>
/// The diagnostics collector is the load-bearing piece: it turns the app's private-DB failure
/// modes into a shape an out-of-network agent (or an Azure Monitor alert) can act on. These tests
/// pin the readings that alert rules and the SRE skills key off — especially the ones with subtle
/// semantics (token-resolution detection, "no run yet" vs "run failed").
/// </summary>
public class SreDiagnosticsCollectorTests
{
    private static IDbContextFactory<BillingDbContext> NewFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();
    }

    private static IConfiguration Config(bool useMock, string? token = null)
    {
        var dict = new Dictionary<string, string?> { ["GitHub:UseMock"] = useMock.ToString() };
        if (token is not null) dict["GitHub:Token"] = token;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static SreDiagnosticsCollector Collector(
        IDbContextFactory<BillingDbContext> factory, IConfiguration config, GitHubRateLimitState? rate = null)
        => new(factory, config, rate ?? new GitHubRateLimitState());

    [Fact]
    public async Task Empty_database_reports_no_run_and_zero_data()
    {
        var factory = NewFactory();

        var snap = await Collector(factory, Config(useMock: true)).CollectAsync();

        Assert.Null(snap.SnapshotAgeHours);
        Assert.Null(snap.LastSnapshotStatus);
        Assert.Equal(0, snap.CostCenters);
        Assert.Equal(0, snap.Budgets);
        Assert.Equal(0, snap.MonthsWithData);
    }

    [Fact]
    public async Task Reports_age_status_and_rows_from_the_most_recent_run()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            // An older failed run and a newer succeeded one — the collector must report the newest.
            db.SnapshotRuns.Add(new SnapshotRun { StartedUtc = DateTime.UtcNow.AddHours(-30), CompletedUtc = DateTime.UtcNow.AddHours(-30), Status = "failed", RowsWritten = 0 });
            db.SnapshotRuns.Add(new SnapshotRun { StartedUtc = DateTime.UtcNow.AddHours(-3), CompletedUtc = DateTime.UtcNow.AddHours(-3), Status = "succeeded", RowsWritten = 42 });
            await db.SaveChangesAsync();
        }

        var snap = await Collector(factory, Config(useMock: true)).CollectAsync();

        Assert.Equal("succeeded", snap.LastSnapshotStatus);
        Assert.Equal(42, snap.LastSnapshotRowsWritten);
        Assert.NotNull(snap.SnapshotAgeHours);
        Assert.InRange(snap.SnapshotAgeHours!.Value, 2.9, 3.1);
    }

    [Fact]
    public async Task Counts_distinct_months_cost_centers_and_budgets()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            // Three usage rows spanning two distinct months.
            db.UsageSnapshots.Add(new UsageSnapshot { Year = 2026, Month = 6, Day = 1, UserLogin = "a", Product = "copilot", Sku = "premium_request", Model = "gpt-5" });
            db.UsageSnapshots.Add(new UsageSnapshot { Year = 2026, Month = 7, Day = 1, UserLogin = "a", Product = "copilot", Sku = "premium_request", Model = "gpt-5" });
            db.UsageSnapshots.Add(new UsageSnapshot { Year = 2026, Month = 7, Day = 1, UserLogin = "b", Product = "copilot", Sku = "premium_request", Model = "gpt-5" });
            db.CostCenterDirectory.Add(new CostCenterDirectoryEntry { CostCenterId = "cc-1", CurrentName = "Alpha" });
            db.CostCenterDirectory.Add(new CostCenterDirectoryEntry { CostCenterId = "cc-2", CurrentName = "Beta" });
            db.BudgetSnapshots.Add(new BudgetSnapshot { Scope = BudgetScopes.Org, CostCenterId = "", Amount = 100 });
            await db.SaveChangesAsync();
        }

        var snap = await Collector(factory, Config(useMock: true)).CollectAsync();

        Assert.Equal(2, snap.MonthsWithData);
        Assert.Equal(2, snap.CostCenters);
        Assert.Equal(1, snap.Budgets);
    }

    [Theory]
    // In mock mode the token is irrelevant — resolution status is null (not applicable).
    [InlineData(true, null, null)]
    // Real mode: a resolved secret is a raw PAT → true.
    [InlineData(false, "ghp_realtokenvalue", true)]
    // Real mode: App Service left the unresolved Key Vault reference literal in place → false.
    [InlineData(false, "@Microsoft.KeyVault(SecretUri=https://kv/secrets/github-pat)", false)]
    // Real mode: empty token → false (not provided).
    [InlineData(false, "", false)]
    public async Task Detects_unresolved_key_vault_reference(bool useMock, string? token, bool? expected)
    {
        var factory = NewFactory();

        var snap = await Collector(factory, Config(useMock, token)).CollectAsync();

        Assert.Equal(expected, snap.GitHubTokenResolved);
        Assert.Equal(useMock, snap.UseMock);
    }

    [Fact]
    public async Task Surfaces_the_last_seen_github_rate_limit()
    {
        var factory = NewFactory();
        var rate = new GitHubRateLimitState();

        // Before any GitHub call, remaining is null (distinct from a real 0 = exhausted).
        var before = await Collector(factory, Config(useMock: false), rate).CollectAsync();
        Assert.Null(before.GitHubRateLimitRemaining);

        rate.Record(1234, 5000);
        var after = await Collector(factory, Config(useMock: false), rate).CollectAsync();

        Assert.Equal(1234, after.GitHubRateLimitRemaining);
        Assert.NotNull(after.GitHubRateLimitSeenUtc);
    }
}
