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

    /// <summary>Config-only PAT resolver (no Key Vault) — mirrors the real resolver's first two steps.</summary>
    private sealed class ConfigPatResolver : IGitHubPatResolver
    {
        private readonly IConfiguration _config;
        public ConfigPatResolver(IConfiguration config) => _config = config;
        public Task<(string? Token, bool Resolved)> TryResolveAsync(Enterprise enterprise, CancellationToken ct = default)
        {
            var token = _config["GitHub:Token"];
            var ok = !string.IsNullOrWhiteSpace(token) && !token!.StartsWith("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(ok ? ((string?)token, true) : ((string?)null, false));
        }
    }

    private static SreDiagnosticsCollector Collector(
        IDbContextFactory<BillingDbContext> factory, IConfiguration config, GitHubRateLimitRegistry? rates = null)
        => new(factory, config, rates ?? new GitHubRateLimitRegistry(), new ConfigPatResolver(config));

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
            db.UsageSnapshots.Add(new UsageSnapshot { Year = 2026, Month = 6, Day = 1, UserLogin = "a", Product = "copilot", Sku = "Copilot AI Credits", Model = "gpt-5" });
            db.UsageSnapshots.Add(new UsageSnapshot { Year = 2026, Month = 7, Day = 1, UserLogin = "a", Product = "copilot", Sku = "Copilot AI Credits", Model = "gpt-5" });
            db.UsageSnapshots.Add(new UsageSnapshot { Year = 2026, Month = 7, Day = 1, UserLogin = "b", Product = "copilot", Sku = "Copilot AI Credits", Model = "gpt-5" });
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
        var rates = new GitHubRateLimitRegistry();

        // Before any GitHub call, remaining is null (distinct from a real 0 = exhausted).
        var before = await Collector(factory, Config(useMock: false), rates).CollectAsync();
        Assert.Null(before.GitHubRateLimitRemaining);

        rates.For("test-enterprise").Record(1234, 5000);
        var after = await Collector(factory, Config(useMock: false), rates).CollectAsync();

        Assert.Equal(1234, after.GitHubRateLimitRemaining);
        Assert.NotNull(after.GitHubRateLimitSeenUtc);
    }

    [Fact]
    public async Task Bootstrap_placeholder_row_is_never_reported()
    {
        // The migration seeds row #1 with a placeholder slug that the first registry bootstrap
        // renames from config. A diagnostics tick can race that rename; emitting the placeholder
        // as an "enterprise" fired a false Sev1 token_unresolved alert in a real deployment.
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Enterprises.Add(new Enterprise { Slug = Enterprise.BootstrapPlaceholderSlug, DisplayName = "Default enterprise", UseMockData = false, Enabled = true });
            db.Enterprises.Add(new Enterprise { Slug = "contoso", DisplayName = "Contoso", UseMockData = true, Enabled = true });
            await db.SaveChangesAsync();
        }

        var snap = await Collector(factory, Config(useMock: true)).CollectAsync();

        var reported = Assert.Single(snap.Enterprises);
        Assert.Equal("contoso", reported.Slug);
    }

    [Fact]
    public async Task Reports_per_enterprise_health_independently()
    {
        var factory = NewFactory();
        long healthyId, staleId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var healthy = new Enterprise { Slug = "contoso", DisplayName = "Contoso", UseMockData = true, Enabled = true };
            var stale = new Enterprise { Slug = "fabrikam", DisplayName = "Fabrikam", UseMockData = true, Enabled = true };
            db.Enterprises.AddRange(healthy, stale);
            await db.SaveChangesAsync();
            (healthyId, staleId) = (healthy.Id, stale.Id);

            // contoso ran 2h ago and succeeded; fabrikam's last run was 40h ago and failed.
            db.SnapshotRuns.Add(new SnapshotRun { EnterpriseId = healthyId, StartedUtc = DateTime.UtcNow.AddHours(-2), CompletedUtc = DateTime.UtcNow.AddHours(-2), Status = "succeeded", RowsWritten = 10 });
            db.SnapshotRuns.Add(new SnapshotRun { EnterpriseId = staleId, StartedUtc = DateTime.UtcNow.AddHours(-40), CompletedUtc = DateTime.UtcNow.AddHours(-40), Status = "failed", RowsWritten = 0 });
            await db.SaveChangesAsync();
        }

        var snap = await Collector(factory, Config(useMock: true)).CollectAsync();

        // The GLOBAL age (most recent run anywhere) is healthy — only the per-enterprise view
        // exposes fabrikam's 40h staleness. This is exactly why the alert rules split by enterprise.
        Assert.NotNull(snap.SnapshotAgeHours);
        Assert.InRange(snap.SnapshotAgeHours!.Value, 1.9, 2.1);

        Assert.Equal(2, snap.Enterprises.Count);
        var contoso = snap.Enterprises.Single(e => e.Slug == "contoso");
        var fabrikam = snap.Enterprises.Single(e => e.Slug == "fabrikam");
        Assert.Equal("succeeded", contoso.LastSnapshotStatus);
        Assert.InRange(contoso.SnapshotAgeHours!.Value, 1.9, 2.1);
        Assert.Equal("failed", fabrikam.LastSnapshotStatus);
        Assert.InRange(fabrikam.SnapshotAgeHours!.Value, 39.9, 40.1);
        // Mock enterprises need no PAT — token status is "not applicable", never "missing".
        Assert.Null(contoso.TokenResolved);
    }
}
