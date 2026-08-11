using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

// ── Persistence ──
//   Azure: SQL Server via managed identity (connection string set by Terraform as
//   ConnectionStrings:BillingDb).
//   Local dev: if NO connection string is set, fall back to a file-based SQLite database so the
//   app runs on a laptop with zero external SQL — handy for previewing the UI (`dotnet run`).
var conn = builder.Configuration.GetConnectionString("BillingDb")
           ?? builder.Configuration["ConnectionStrings:BillingDb"];
var useLocalDevDb = string.IsNullOrWhiteSpace(conn);
builder.Services.AddDbContextFactory<BillingDbContext>(o =>
{
    if (useLocalDevDb)
    {
        o.UseInMemoryDatabase("ghcp-localdev"); // local UI preview only; no external SQL needed
    }
    else
    {
        o.UseSqlServer(conn, sql => sql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null)); // transparently handles serverless auto-resume transients (e.g., 40613)
    }
});

// Per-enterprise last-seen GitHub rate-limit state (rate limits are per PAT), shared between the
// HTTP clients (writers) and the SRE diagnostics collector (reader). Singleton so it survives
// across scoped snapshot runs.
builder.Services.AddSingleton<GitHubRateLimitRegistry>();

// ── GitHub billing clients: routed PER REGISTRY ENTERPRISE by the client factory — the shared
// synthetic mock for UseMockData rows (no PAT/Copilot needed), or a per-enterprise real client.
// This is what enables hybrid deployments (real + demo/fire-drill enterprises side by side).
var useMock = builder.Configuration.GetValue("GitHub:UseMock", true);
builder.Services.AddSingleton<MockGitHubBillingClient>();
builder.Services.AddSingleton<IGitHubPatResolver, GitHubPatResolver>();
builder.Services.AddSingleton<IEnterpriseBillingClientFactory, EnterpriseBillingClientFactory>();
// Standard resilience (retry w/ exponential backoff + jitter honouring Retry-After, circuit
// breaker, total-request timeout) on every factory-produced HttpClient. The pipeline is built PER
// CLIENT NAME and the factory names clients "github:<slug>", so each enterprise gets its OWN
// circuit breaker — enterprise A tripping never blocks enterprise B. (Names are dynamic registry
// data, so per-name AddHttpClient registration at startup isn't possible.)
builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

// ── Enterprise registry + snapshot pipeline (the only GitHub caller; UI reads from the DB) ──
builder.Services.AddScoped<EnterpriseRegistryService>();
builder.Services.AddScoped<SnapshotService>();
builder.Services.AddHostedService<SnapshotHostedService>();

// Apply EF Core migrations in the background (retry/backoff) on the SQL path so the app never
// crash-loops if the DB isn't ready or the identity hasn't been granted DDL yet. The in-memory
// dev DB uses EnsureCreated below instead.
if (!useLocalDevDb)
{
    builder.Services.AddHostedService<DatabaseMigratorHostedService>();
}

// ── Read model + DB-backed access scoping ──
builder.Services.AddScoped<UsageQueryService>();
// Admin-managed authorization glue (group→cost-center mapping console).
builder.Services.AddScoped<IAppAdminChecker, AppAdminChecker>();
builder.Services.AddScoped<AdminMappingService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<IUserScopeResolver, DbGroupScopeResolver>();

builder.Services.AddApplicationInsightsTelemetry();

// Guarantee a resolvable TelemetryClient. AddApplicationInsightsTelemetry() registers one on
// Windows/local, but on App Service Linux (.NET 10 + this SDK version) it was observed NOT to —
// so the custom-metric publisher had nothing to resolve. TryAdd is a no-op when the SDK already
// registered TelemetryClient; otherwise it builds one from the SDK's TelemetryConfiguration (or a
// default configured from the connection string if that's missing too). This is what keeps the
// SRE diagnostics metrics flowing regardless of the host's AI-SDK quirks.
builder.Services.TryAddSingleton(sp =>
{
    var cfg = sp.GetService<Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration>();
    if (cfg is null)
    {
        cfg = Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration.CreateDefault();
        var cs = sp.GetRequiredService<IConfiguration>()["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(cs)) cfg.ConnectionString = cs;
    }
    return new Microsoft.ApplicationInsights.TelemetryClient(cfg);
});

// ── SRE observability: surface the failures that live only in the private DB (stalled snapshot,
// wrong data, unresolved Key Vault reference) as App Insights metrics + a JSON endpoint, so an
// out-of-network reliability agent or an Azure Monitor alert can see them. See docs/SRE_AGENT.md.
builder.Services.AddScoped<SreDiagnosticsCollector>();
builder.Services.AddHostedService<SreDiagnosticsPublisher>();

// Health checks: liveness (process up) + readiness (DB reachable + schema applied). Readiness
// reflects the private-DNS / SQL-grant / migration warm-up window.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });

// Register a no-op authentication scheme so that if the FallbackPolicy below ever needs
// to Challenge() (e.g., a request reaches the container without a valid Easy Auth
// principal), there's a registered DefaultChallengeScheme to redirect with — instead of
// throwing "No authenticationScheme was specified..." and 500ing. See
// EasyAuthAuthenticationHandler for details; real identity hydration still happens via
// EasyAuthClaimsMiddleware below, not through this scheme's own AuthenticateAsync.
builder.Services
    .AddAuthentication(GhcpCreditVisibility.Authorization.EasyAuthAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, GhcpCreditVisibility.Authorization.EasyAuthAuthenticationHandler>(
        GhcpCreditVisibility.Authorization.EasyAuthAuthenticationHandler.SchemeName, _ => { });

// Require an authenticated user for every page. Behind Easy Auth the platform performs
// the Entra challenge; the middleware below hydrates the identity from its header.
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build());

var app = builder.Build();

// ── Schema automation ──
//   Azure SQL: migrations are applied by DatabaseMigratorHostedService (background retry/backoff),
//   so a not-yet-granted identity or a warming database never blocks/crash-loops startup.
//   Local dev (in-memory): EnsureCreated is synchronous and always succeeds — do it here.

// Local dev (in-memory) convenience: ensure the schema exists and seed TWO mock enterprises
// (contoso + fabrikam — overlapping user logins and a colliding "Engineering" cost-center name,
// by design) plus example principal mappings and an admin group so the Admin console and the
// multi-enterprise UI render with content. Never runs in Azure (SQL Server path).
if (app.Environment.IsDevelopment() && useLocalDevDb)
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BillingDbContext>>();
    using var db = factory.CreateDbContext();
    db.Database.EnsureCreated();

    if (!db.Enterprises.Any())
    {
        db.Enterprises.AddRange(
            new GhcpCreditVisibility.Data.Enterprise { Slug = "contoso", DisplayName = "Contoso", UseMockData = true, Enabled = true, ModifiedBy = "seed" },
            new GhcpCreditVisibility.Data.Enterprise { Slug = "fabrikam", DisplayName = "Fabrikam", UseMockData = true, Enabled = true, ModifiedBy = "seed" });
        db.SaveChanges();
    }
    var seedEnterprises = db.Enterprises.OrderBy(e => e.Id).ToList();

    // Backfill synthetic usage history per enterprise so the trend + month selector have data
    // locally — but only up to the END OF LAST MONTH. The snapshot job owns the current month
    // (it writes the monthly aggregate rows on startup); seeding daily rows for the current month
    // too would DOUBLE-COUNT it, spiking the trend chart and flagging every user as a big
    // month-over-month mover.
    if (!db.UsageSnapshots.Any())
    {
        var endOfLastMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-1);
        foreach (var e in seedEnterprises)
        {
            db.UsageSnapshots.AddRange(
                GhcpCreditVisibility.Services.MockGitHubBillingClient.BuildHistorySnapshots(12, endOfLastMonth, e.Id, e.Slug));
        }
    }
    if (!db.PrincipalCostCenterMappings.Any())
    {
        var contosoId = seedEnterprises.First(e => e.Slug == "contoso").Id;
        var fabrikamId = seedEnterprises.First(e => e.Slug == "fabrikam").Id;
        db.PrincipalCostCenterMappings.AddRange(
            new GhcpCreditVisibility.Data.PrincipalCostCenterMapping { PrincipalType = "Group", PrincipalObjectId = "11111111-1111-1111-1111-111111111111", PrincipalDisplayName = "SG-Finance", EnterpriseId = contosoId, CostCenterId = "cc-contoso-finance", CostCenterName = "Finance", ModifiedBy = "seed" },
            new GhcpCreditVisibility.Data.PrincipalCostCenterMapping { PrincipalType = "Group", PrincipalObjectId = "22222222-2222-2222-2222-222222222222", PrincipalDisplayName = "SG-Engineering", EnterpriseId = contosoId, CostCenterId = "cc-contoso-eng", CostCenterName = "Engineering", ModifiedBy = "seed" },
            // Cross-enterprise exec view: one group mapped into BOTH enterprises' cost centers.
            new GhcpCreditVisibility.Data.PrincipalCostCenterMapping { PrincipalType = "Group", PrincipalObjectId = "22222222-2222-2222-2222-222222222222", PrincipalDisplayName = "SG-Engineering", EnterpriseId = fabrikamId, CostCenterId = "cc-fabrikam-eng", CostCenterName = "Engineering", ModifiedBy = "seed" },
            new GhcpCreditVisibility.Data.PrincipalCostCenterMapping { PrincipalType = "User", PrincipalObjectId = "33333333-3333-3333-3333-333333333333", PrincipalDisplayName = "Dana Manager (individual)", EnterpriseId = fabrikamId, CostCenterId = "cc-fabrikam-research", CostCenterName = "Research", ModifiedBy = "seed" });
    }
    if (!db.AdminPrincipals.Any())
    {
        db.AdminPrincipals.Add(new GhcpCreditVisibility.Data.AdminPrincipal { PrincipalType = "Group", PrincipalObjectId = "99999999-9999-9999-9999-999999999999", PrincipalDisplayName = "SG-GHCP-Admins", ModifiedBy = "seed" });
    }
    if (!db.BudgetSnapshots.Any())
    {
        // Budgets are GitHub-governed; in local dev we snapshot them from the mock client
        // exactly as the snapshot job would in Azure — per enterprise.
        var mock = scope.ServiceProvider.GetRequiredService<GhcpCreditVisibility.Services.MockGitHubBillingClient>();
        foreach (var e in seedEnterprises)
        {
            var ccName = mock.GetCostCentersAsync(e.Slug).Result.ToDictionary(c => c.Id, c => c.Name);
            var dirNow = DateTime.UtcNow;
            foreach (var cc in ccName)
            {
                if (!db.CostCenterDirectory.Any(d => d.EnterpriseId == e.Id && d.CostCenterId == cc.Key))
                    db.CostCenterDirectory.Add(new GhcpCreditVisibility.Data.CostCenterDirectoryEntry { EnterpriseId = e.Id, CostCenterId = cc.Key, CurrentName = cc.Value, LastSeenUtc = dirNow });
            }
            // Same mapper the snapshot job uses — this seed previously carried a SECOND copy of the
            // binary cost_center/everything-else logic, free to drift from the real one.
            var seedCostCenters = mock.GetCostCentersAsync(e.Slug).Result;
            foreach (var gb in mock.GetBudgetsAsync(e.Slug).Result)
            {
                var row = new GhcpCreditVisibility.Data.BudgetSnapshot { EnterpriseId = e.Id };
                GhcpCreditVisibility.Services.BudgetScopeMapper.Apply(row, gb, seedCostCenters, dirNow);
                db.BudgetSnapshots.Add(row);
            }
        }
    }
    db.SaveChanges();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

// Hydrate HttpContext.User from the Easy Auth X-MS-CLIENT-PRINCIPAL header.
app.UseMiddleware<EasyAuthClaimsMiddleware>();

// Local dev convenience: with no Easy Auth in front, inject an admin identity so the
// app is runnable locally against mock data. Never triggers in Azure (Easy Auth sets User).
if (app.Environment.IsDevelopment())
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            var id = new ClaimsIdentity("dev", ClaimTypes.Name, ClaimTypes.Role);
            id.AddClaim(new Claim(ClaimTypes.Name, "dev-admin"));
            id.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
            ctx.User = new ClaimsPrincipal(id);
        }
        await next();
    });
}

app.UseAuthorization();

// Health endpoints — anonymous (also excluded from Easy Auth via infra). Liveness = process up;
// readiness = DB reachable + migrations applied (reflects private-DNS / grant / migration warm-up).
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    },
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        };
        await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(payload));
    }
}).AllowAnonymous();

// Deep diagnostics for ops / the SRE agent: the same signals the metric publisher emits, as JSON,
// on demand. Requires an authenticated user (inherits the fallback auth policy) — unlike the
// deliberately-anonymous liveness/readiness probes, this exposes internal state (data counts,
// token-resolution status), so it must not be public.
// ADMIN ONLY. RequireAuthorization() alone resolves to the fallback policy — merely
// "authenticated" — which let ANY signed-in user read this. That crosses the scope boundary the
// whole access model exists to enforce: the payload names every enterprise (slug and display name)
// and reports its cost-center count, budget count and months of data, including enterprises the
// caller has no grant for. No dollar amounts, but a cost-center-scoped manager could enumerate the
// estate. Easy Auth gates it in Azure (infra excludes only /health/live and /health/ready), so this
// was never anonymous — but authenticated is not the same as entitled.
app.MapGet("/health/diag", async (
        SreDiagnosticsCollector collector,
        GhcpCreditVisibility.Authorization.IAppAdminChecker admin,
        HttpContext ctx,
        CancellationToken ct) =>
    await admin.IsAdminAsync(ctx.User, ct)
        ? Results.Json(await collector.CollectAsync(ct))
        : Results.Forbid())
    .RequireAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();
