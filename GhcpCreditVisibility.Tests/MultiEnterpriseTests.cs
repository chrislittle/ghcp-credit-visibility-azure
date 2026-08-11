using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhcpCreditVisibility.Tests;

/// <summary>
/// Pins the multi-enterprise schema semantics — the exact collision cases the EnterpriseId
/// qualifiers exist for. The same GitHub login CAN exist in two enterprises, two enterprises DO
/// both have an org-scope budget row, and cost centers in different enterprises must never merge.
/// These are deterministic model/index facts, testable with no deployment and no second real
/// enterprise.
/// </summary>
public class MultiEnterpriseSchemaTests
{
    private static IDbContextFactory<BillingDbContext> NewFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();
    }

    [Fact]
    public void Usage_natural_key_is_enterprise_qualified()
    {
        // Model-level assertion: the unique index over usage rows must LEAD with EnterpriseId —
        // without it, the same login/month/model/sku in two enterprises silently collapses.
        using var db = NewFactory().CreateDbContext();
        var entity = db.Model.FindEntityType(typeof(UsageSnapshot))!;
        var unique = entity.GetIndexes().Single(i => i.IsUnique);
        var props = unique.Properties.Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "EnterpriseId", "Year", "Month", "Day", "UserLogin", "Model", "Sku" }, props);
    }

    [Fact]
    public void Budget_key_and_mapping_key_are_enterprise_qualified()
    {
        using var db = NewFactory().CreateDbContext();

        // Budgets are keyed by GitHub's own budget id. The key MUST stay enterprise-qualified:
        // budget ids are unique within an enterprise, and two enterprises' budgets must never
        // contend for a row. (The previous key was (EnterpriseId, Scope, CostCenterId), which was
        // enterprise-qualified but collapsed every non-cost-center scope onto a single row — see
        // BudgetScopeMappingTests.)
        var budget = db.Model.FindEntityType(typeof(BudgetSnapshot))!;
        var budgetKey = budget.GetIndexes().Single(i => i.IsUnique).Properties.Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "EnterpriseId", "GitHubBudgetId" }, budgetKey);
        Assert.Equal("EnterpriseId", budgetKey[0]);

        var mapping = db.Model.FindEntityType(typeof(PrincipalCostCenterMapping))!;
        Assert.Equal(new[] { "PrincipalType", "PrincipalObjectId", "EnterpriseId", "CostCenterId" },
            mapping.GetIndexes().Single(i => i.IsUnique).Properties.Select(p => p.Name).ToArray());

        var directory = db.Model.FindEntityType(typeof(CostCenterDirectoryEntry))!;
        Assert.Equal(new[] { "EnterpriseId", "CostCenterId" },
            directory.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());

        var enterprise = db.Model.FindEntityType(typeof(Enterprise))!;
        Assert.True(enterprise.GetIndexes().Single(i => i.Properties.Single().Name == "Slug").IsUnique,
            "Enterprise.Slug must be uniquely constrained — an accidental duplicate registration would double every number for that enterprise.");
    }

    [Fact]
    public async Task Same_login_same_month_persists_as_two_rows_across_enterprises()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 7, Day = 1, UserLogin = "dkim", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 10m });
            db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 2, Year = 2026, Month = 7, Day = 1, UserLogin = "dkim", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 99m });
            await db.SaveChangesAsync();
        }

        await using var check = await factory.CreateDbContextAsync();
        var rows = await check.UsageSnapshots.Where(x => x.UserLogin == "dkim").ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(109m, rows.Sum(r => r.NetAmount)); // distinct spend per enterprise, never merged
    }

    [Fact]
    public async Task Each_enterprise_keeps_its_own_org_budget_row()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 1, Scope = BudgetScopes.Org, CostCenterId = "", Amount = 700m });
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 2, Scope = BudgetScopes.Org, CostCenterId = "", Amount = 450m });
            await db.SaveChangesAsync();
        }

        await using var check = await factory.CreateDbContextAsync();
        Assert.Equal(2, await check.BudgetSnapshots.CountAsync(b => b.Scope == BudgetScopes.Org));
    }
}

/// <summary>End-to-end behavior of the registry + snapshot loop against the in-memory provider.</summary>
public class MultiEnterprisePipelineTests
{
    private static IDbContextFactory<BillingDbContext> NewFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();
    }

    private static EnterpriseRegistryService Registry(IDbContextFactory<BillingDbContext> factory, string? slug = "test-enterprise", bool useMock = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GitHub:Enterprise"] = slug,
            ["GitHub:UseMock"] = useMock.ToString()
        }).Build();
        return new EnterpriseRegistryService(factory, config, NullLogger<EnterpriseRegistryService>.Instance);
    }

    private sealed class RoutingMockFactory : IEnterpriseBillingClientFactory
    {
        private readonly MockGitHubBillingClient _mock = new();
        public Task<IGitHubBillingClient> GetClientAsync(Enterprise enterprise, CancellationToken ct = default)
            => Task.FromResult<IGitHubBillingClient>(_mock);
    }

    private static SnapshotService Service(IDbContextFactory<BillingDbContext> factory, EnterpriseRegistryService registry)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Retention:Months"] = "6"
        }).Build();
        return new SnapshotService(new RoutingMockFactory(), registry, factory, config, NullLogger<SnapshotService>.Instance);
    }

    [Fact]
    public async Task Bootstrap_seeds_exactly_one_row_from_config_and_is_idempotent()
    {
        var factory = NewFactory();
        var registry = Registry(factory);

        await registry.EnsureBootstrapAsync();
        await registry.EnsureBootstrapAsync(); // idempotent

        var all = await registry.GetAllAsync();
        var row = Assert.Single(all);
        Assert.Equal("test-enterprise", row.Slug);
        Assert.True(row.UseMockData);
        Assert.True(row.Enabled); // the bootstrap (pre-existing) enterprise stays enabled on upgrade
    }

    [Fact]
    public async Task Duplicate_slug_is_rejected()
    {
        var factory = NewFactory();
        var registry = Registry(factory);
        await registry.AddAsync("contoso", null, null, useMockData: true, "test");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.AddAsync("contoso", "Duplicate", null, useMockData: true, "test"));
    }

    [Fact]
    public async Task New_enterprises_start_disabled()
    {
        var factory = NewFactory();
        var registry = Registry(factory);

        var row = await registry.AddAsync("fabrikam", "Fabrikam", null, useMockData: true, "test");

        Assert.False(row.Enabled); // verify-before-visible onboarding: enable is an explicit step
        Assert.Equal("github-pat-fabrikam", row.PatSecretName);
    }

    [Fact]
    public async Task Snapshot_cycle_writes_each_enabled_enterprise_and_skips_disabled()
    {
        var factory = NewFactory();
        var registry = Registry(factory, slug: "contoso");
        await registry.EnsureBootstrapAsync();
        var fabrikam = await registry.AddAsync("fabrikam", "Fabrikam", null, useMockData: true, "test");
        await registry.SetEnabledAsync(fabrikam.Id, true, "test");
        var disabled = await registry.AddAsync("disabled-ent", null, null, useMockData: true, "test");

        await Service(factory, registry).RunAsync();

        await using var db = await factory.CreateDbContextAsync();
        var runs = await db.SnapshotRuns.ToListAsync();
        Assert.Equal(2, runs.Count); // one run row per ENABLED enterprise; the disabled one is skipped
        Assert.All(runs, r => Assert.Equal("succeeded", r.Status));
        Assert.DoesNotContain(runs, r => r.EnterpriseId == disabled.Id);

        // Every usage row is stamped with its enterprise, and both enterprises wrote data.
        var byEnterprise = await db.UsageSnapshots.GroupBy(x => x.EnterpriseId).Select(g => g.Key).ToListAsync();
        Assert.Equal(2, byEnterprise.Count);

        // The overlapping logins (dkim, jchen exist in both mock enterprises) stayed distinct rows.
        var fabrikamId = fabrikam.Id;
        Assert.True(await db.UsageSnapshots.AnyAsync(x => x.UserLogin == "dkim" && x.EnterpriseId == fabrikamId));
        Assert.True(await db.UsageSnapshots.AnyAsync(x => x.UserLogin == "dkim" && x.EnterpriseId != fabrikamId));
    }

    [Fact]
    public async Task One_broken_enterprise_never_aborts_the_others()
    {
        var factory = NewFactory();
        var registry = Registry(factory, slug: "contoso");
        await registry.EnsureBootstrapAsync();
        // The mock's fire-drill enterprise: every call throws a simulated outage.
        var broken = await registry.AddAsync(MockGitHubBillingClient.BrokenEnterpriseSlug, "Broken (fire drill)", null, useMockData: true, "test");
        await registry.SetEnabledAsync(broken.Id, true, "test");
        var fabrikam = await registry.AddAsync("fabrikam", "Fabrikam", null, useMockData: true, "test");
        await registry.SetEnabledAsync(fabrikam.Id, true, "test");

        // Must NOT throw: per-enterprise isolation records the failure and continues.
        await Service(factory, registry).RunAsync();

        await using var db = await factory.CreateDbContextAsync();
        var runs = await db.SnapshotRuns.ToListAsync();
        Assert.Equal(3, runs.Count);
        var brokenRun = Assert.Single(runs, r => r.EnterpriseId == broken.Id);
        Assert.Equal("failed", brokenRun.Status);
        Assert.Contains("Simulated outage", brokenRun.Error);
        // Both healthy enterprises completed and wrote rows despite the failure between them.
        Assert.All(runs.Where(r => r.EnterpriseId != broken.Id), r =>
        {
            Assert.Equal("succeeded", r.Status);
            Assert.True(r.RowsWritten > 0);
        });
    }

    [Fact]
    public async Task Budget_entity_names_resolve_to_cost_center_ids()
    {
        // The REAL GitHub budgets API reports budget_entity_name as the cost center's display
        // NAME; fabrikam's mock mirrors that. Rows must still be keyed by the stable ID — the
        // access-scope pair match and per-budget actuals both depend on it.
        var factory = NewFactory();
        var registry = Registry(factory, slug: "fabrikam");
        await registry.EnsureBootstrapAsync();

        await Service(factory, registry).RunAsync();

        await using var db = await factory.CreateDbContextAsync();
        var ccBudgets = await db.BudgetSnapshots.Where(b => b.Scope == BudgetScopes.CostCenter).ToListAsync();
        Assert.Equal(2, ccBudgets.Count);
        Assert.Contains(ccBudgets, b => b.CostCenterId == "cc-fabrikam-eng" && b.CostCenterName == "Engineering");
        Assert.Contains(ccBudgets, b => b.CostCenterId == "cc-fabrikam-research" && b.CostCenterName == "Research");
        // Never keyed by the display name the API sent.
        Assert.DoesNotContain(ccBudgets, b => b.CostCenterId == "Engineering" || b.CostCenterId == "Research");
    }

    [Fact]
    public async Task Stale_budget_rows_are_removed_on_the_next_run()
    {
        var factory = NewFactory();
        var registry = Registry(factory, slug: "fabrikam");
        await registry.EnsureBootstrapAsync();
        await using (var db = await factory.CreateDbContextAsync())
        {
            // Simulates a pre-fix row keyed by entity NAME (or a budget since deleted in GitHub).
            db.BudgetSnapshots.Add(new BudgetSnapshot { EnterpriseId = 1, Scope = BudgetScopes.CostCenter, CostCenterId = "Engineering", Amount = 220m });
            await db.SaveChangesAsync();
        }

        await Service(factory, registry).RunAsync();

        await using var check = await factory.CreateDbContextAsync();
        Assert.False(await check.BudgetSnapshots.AnyAsync(b => b.CostCenterId == "Engineering"),
            "the name-keyed row must be replaced by the id-keyed row, not left to linger");
        Assert.True(await check.BudgetSnapshots.AnyAsync(b => b.CostCenterId == "cc-fabrikam-eng"));
    }

    [Fact]
    public async Task Remove_enterprise_purges_exactly_its_own_data()
    {
        var factory = NewFactory();
        var registry = Registry(factory, slug: "contoso");
        await registry.EnsureBootstrapAsync();
        var fabrikam = await registry.AddAsync("fabrikam", "Fabrikam", null, useMockData: true, "test");
        await registry.SetEnabledAsync(fabrikam.Id, true, "test");
        await Service(factory, registry).RunAsync();

        var result = await registry.DeleteWithDataAsync(fabrikam.Id);

        Assert.True(result.UsageRows > 0);
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.Enterprises.AnyAsync(e => e.Id == fabrikam.Id));
        Assert.False(await db.UsageSnapshots.AnyAsync(x => x.EnterpriseId == fabrikam.Id));
        Assert.False(await db.BudgetSnapshots.AnyAsync(x => x.EnterpriseId == fabrikam.Id));
        Assert.False(await db.CostCenterDirectory.AnyAsync(x => x.EnterpriseId == fabrikam.Id));
        Assert.False(await db.SnapshotRuns.AnyAsync(x => x.EnterpriseId == fabrikam.Id));
        // The other enterprise's data is untouched.
        Assert.True(await db.UsageSnapshots.AnyAsync(x => x.EnterpriseId != fabrikam.Id));
    }
}

/// <summary>Scope enforcement: access is the exact (enterprise, cost-center) PAIR.</summary>
public class EnterpriseScopeTests
{
    private static IDbContextFactory<BillingDbContext> NewFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<BillingDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<BillingDbContext>>();
    }

    private static async Task<IDbContextFactory<BillingDbContext>> SeedTwoEnterprisesAsync()
    {
        var factory = NewFactory();
        await using var db = await factory.CreateDbContextAsync();
        db.Enterprises.Add(new Enterprise { Slug = "contoso", DisplayName = "Contoso", UseMockData = true });
        db.Enterprises.Add(new Enterprise { Slug = "fabrikam", DisplayName = "Fabrikam", UseMockData = true });
        // DELIBERATE worst case: the same cost-center ID exists in both enterprises.
        db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 7, Day = 1, UserLogin = "a", CostCenterId = "cc-shared-id", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 10m });
        db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 2, Year = 2026, Month = 7, Day = 1, UserLogin = "b", CostCenterId = "cc-shared-id", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 99m });
        db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 2, Year = 2026, Month = 7, Day = 1, UserLogin = "c", CostCenterId = "cc-other", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 5m });
        await db.SaveChangesAsync();
        return factory;
    }

    [Fact]
    public async Task Pair_scope_never_leaks_the_same_id_from_another_enterprise()
    {
        var factory = await SeedTwoEnterprisesAsync();
        var query = new UsageQueryService(factory);
        // Granted: cc-shared-id in enterprise 1 ONLY.
        var scope = new UserScope(false, new[] { new EnterpriseCostCenter(1, "cc-shared-id") }, Array.Empty<string>());

        var totals = await query.GetUserTotalsAsync(2026, 7, scope);

        var row = Assert.Single(totals);
        Assert.Equal("a", row.UserLogin);
        Assert.Equal(10m, row.NetAmount); // enterprise 2's cc-shared-id (99) must NOT leak in
    }

    [Fact]
    public async Task Enterprise_filter_narrows_even_an_admin_scope()
    {
        var factory = await SeedTwoEnterprisesAsync();
        var query = new UsageQueryService(factory);
        var adminScope = UserScope.All() with { EnterpriseFilter = 2 };

        var totals = await query.GetUserTotalsAsync(2026, 7, adminScope);

        Assert.Equal(2, totals.Count);
        Assert.All(totals, t => Assert.Equal(2, t.EnterpriseId));
        Assert.Equal(104m, totals.Sum(t => t.NetAmount));
    }

    [Fact]
    public async Task Scope_label_resolves_guid_ids_to_names_and_qualifies_enterprises()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Enterprises.Add(new Enterprise { Slug = "contoso", DisplayName = "Contoso", UseMockData = true });
            db.Enterprises.Add(new Enterprise { Slug = "demoent", DisplayName = "DemoEnt", UseMockData = false });
            // Real GitHub cost-center ids are GUIDs — the directory maps them to display names.
            db.CostCenterDirectory.Add(new CostCenterDirectoryEntry { EnterpriseId = 2, CostCenterId = "02ed281f-d3b9-4d98-b2fe-38fef467062f", CurrentName = "Platform Team" });
            db.CostCenterDirectory.Add(new CostCenterDirectoryEntry { EnterpriseId = 1, CostCenterId = "cc-contoso-eng", CurrentName = "Engineering" });
            await db.SaveChangesAsync();
        }
        var query = new UsageQueryService(factory);

        var scope = new UserScope(false, new[]
        {
            new EnterpriseCostCenter(1, "cc-contoso-eng"),
            new EnterpriseCostCenter(2, "02ed281f-d3b9-4d98-b2fe-38fef467062f"),
            new EnterpriseCostCenter(2, "11111111-2222-3333-4444-555555555555"), // not in directory yet
        }, Array.Empty<string>());

        var desc = await query.GetScopeDescriptionAsync(scope);

        // Three cost centers → the pill stays compact; the resolved names move to the tooltip.
        Assert.Equal("3 cost centers across 2 enterprises", desc.Label);
        Assert.NotNull(desc.Detail);
        // Names, enterprise-qualified (scope spans two enterprises) — never the raw GUID when a name exists.
        Assert.Contains("Platform Team · DemoEnt", desc.Detail);
        Assert.Contains("Engineering · Contoso", desc.Detail);
        Assert.DoesNotContain("02ed281f", desc.Detail);
        // An id the snapshot hasn't discovered yet falls back to the id itself rather than vanishing.
        Assert.Contains("11111111-2222-3333-4444-555555555555 · DemoEnt", desc.Detail);

        // One or two cost centers → names inline, no tooltip needed.
        var small = await query.GetScopeDescriptionAsync(
            new UserScope(false, new[] { new EnterpriseCostCenter(2, "02ed281f-d3b9-4d98-b2fe-38fef467062f") }, Array.Empty<string>()));
        Assert.Equal("Cost centers: Platform Team", small.Label);
        Assert.Null(small.Detail);

        Assert.Equal("All cost centers", (await query.GetScopeDescriptionAsync(UserScope.All())).Label);
        Assert.Equal("No assigned scope", (await query.GetScopeDescriptionAsync(UserScope.None())).Label);
    }

    [Fact]
    public async Task Prev_month_deltas_join_by_enterprise_and_login()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Enterprises.Add(new Enterprise { Slug = "contoso", DisplayName = "Contoso", UseMockData = true });
            db.Enterprises.Add(new Enterprise { Slug = "fabrikam", DisplayName = "Fabrikam", UseMockData = true });
            // July rows: dkim exists in BOTH enterprises; mtanaka only has July (a "new" user).
            db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 7, Day = 1, UserLogin = "dkim", CostCenterId = "cc-a", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 50m });
            db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 2, Year = 2026, Month = 7, Day = 1, UserLogin = "dkim", CostCenterId = "cc-b", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 40m });
            db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 7, Day = 1, UserLogin = "mtanaka", CostCenterId = "cc-a", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 10m });
            // June rows: dkim spent 25 in enterprise 1 and 80 in enterprise 2 — the deltas must NOT mix.
            db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 6, Day = 1, UserLogin = "dkim", CostCenterId = "cc-a", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 25m });
            db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 2, Year = 2026, Month = 6, Day = 1, UserLogin = "dkim", CostCenterId = "cc-b", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 80m });
            await db.SaveChangesAsync();
        }
        var query = new UsageQueryService(factory);

        var page = await query.GetUserTotalsPagedAsync(2026, 7, UserScope.All(), search: null, page: 1, pageSize: 25);

        Assert.True(page.HasPrevMonthData);
        var dkimEnt1 = page.Items.Single(i => i.UserLogin == "dkim" && i.EnterpriseId == 1);
        var dkimEnt2 = page.Items.Single(i => i.UserLogin == "dkim" && i.EnterpriseId == 2);
        Assert.Equal(25m, dkimEnt1.PrevMonthNetAmount); // enterprise 1's June, never enterprise 2's
        Assert.Equal(80m, dkimEnt2.PrevMonthNetAmount);
        Assert.Null(page.Items.Single(i => i.UserLogin == "mtanaka").PrevMonthNetAmount); // "new"
    }

    [Fact]
    public async Task First_month_of_data_reports_no_prev_month()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Enterprises.Add(new Enterprise { Slug = "contoso", DisplayName = "Contoso", UseMockData = true });
            db.UsageSnapshots.Add(new UsageSnapshot { EnterpriseId = 1, Year = 2026, Month = 7, Day = 1, UserLogin = "a", CostCenterId = "cc-a", Model = "gpt-5", Sku = "ai_credits", Product = "copilot", NetAmount = 10m });
            await db.SaveChangesAsync();
        }
        var query = new UsageQueryService(factory);

        var page = await query.GetUserTotalsPagedAsync(2026, 7, UserScope.All(), search: null, page: 1, pageSize: 25);

        // No June data at all: the UI must render em-dashes, not flag every user "new".
        Assert.False(page.HasPrevMonthData);
    }

    [Fact]
    public async Task Cross_enterprise_scope_unions_pairs()
    {
        var factory = await SeedTwoEnterprisesAsync();
        var query = new UsageQueryService(factory);
        // The exec case: one principal granted cost centers in TWO enterprises.
        var scope = new UserScope(false, new[]
        {
            new EnterpriseCostCenter(1, "cc-shared-id"),
            new EnterpriseCostCenter(2, "cc-other"),
        }, Array.Empty<string>());

        var totals = await query.GetUserTotalsAsync(2026, 7, scope);

        Assert.Equal(2, totals.Count);
        Assert.Equal(15m, totals.Sum(t => t.NetAmount)); // 10 (ent1) + 5 (ent2) — never ent2's 99
    }
}
