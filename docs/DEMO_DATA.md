# Where does the demo data come from?

Short answer: it's **synthetic data generated in code** (`MockGitHubBillingClient.cs`) —
deterministic, seeded from fixed fake usernames — not a copy of anyone's real GitHub Copilot
usage, and not pulled from any live service. This page explains exactly what it is, where it
lives, and how the switch to real GitHub data works, for both **local dev** and **Azure**.

## The mock client

[`GhcpCreditVisibility/Services/MockGitHubBillingClient.cs`](../GhcpCreditVisibility/Services/MockGitHubBillingClient.cs)
implements the same `IGitHubBillingClient` interface the real GitHub billing API client
(`RealGitHubBillingClient.cs`) implements. It fabricates:

- **12 fake users** (e.g. `ahernandez` / Ana Hernandez) split evenly across
- **3 fake cost centers** (`Cost Center A`, `B`, `C`)
- **3 fake AI models** (`gpt-5`, `claude-sonnet-4.5`, `o4-mini`) with illustrative per-credit prices
- **Illustrative monthly budgets** — one enterprise-wide budget plus one per cost center

For each user/model/month, a **seeded `Random`** (seeded from a hash of the username + year +
month) produces a quantity and cost. "Seeded" means the same user always gets the same numbers
for the same month — so the numbers are stable across app restarts and deploys (not truly
random noise), but they are **entirely made up**; no code path calls out to GitHub, a database
of real usage, or any external source to produce them.

A second helper, `BuildHistorySnapshots(months, asOfUtc)`, fabricates **daily** history rows
(with mild weekday/weekend seasonality) for the last N months, purely so the **Reports** page's
day/week/month granularity toggle has something meaningful to show locally — the real GitHub
billing API only ever returns **month-level** aggregates (see below), so this daily fabrication
is a local-preview-only convenience and isn't part of the `IGitHubBillingClient` contract.

## How mock data reaches the dashboard — the pipeline is identical either way

The dashboard **never** calls GitHub (mock or real) directly. A background job
(`SnapshotService` / `SnapshotHostedService`) is the **only** caller of `IGitHubBillingClient`; it
runs on startup and every 12 hours, writes rows into the `UsageSnapshot`/`BudgetSnapshot` tables,
and the UI reads only from those tables. This means the mock/real switch is a **single
dependency-injection decision** in `Program.cs` — everything downstream (persistence, the admin
console, scoping, the dashboard/report pages) is unaware which client produced the data.

```
IGitHubBillingClient  ──►  MockGitHubBillingClient   (GitHub:UseMock = true — default)
        ▲                  RealGitHubBillingClient    (GitHub:UseMock = false — needs a PAT)
        │
   SnapshotService (the ONLY caller) ──► writes UsageSnapshot / BudgetSnapshot / CostCenterDirectory rows
        │
   UsageQueryService / dashboard pages ──► read ONLY from the database, never live
```

## Local dev specifics

When you `dotnet run` with no `ConnectionStrings:BillingDb` configured (the default — see
[RUN_LOCALLY.md](RUN_LOCALLY.md)), `Program.cs`:
1. Falls back to an **EF Core in-memory database** (`UseInMemoryDatabase`) instead of SQL Server —
   purely so the app runs with zero external dependencies.
2. Calls `EnsureCreated()` and, if empty, seeds:
   - 12 months of fabricated daily history via `MockGitHubBillingClient.BuildHistorySnapshots(...)`
   - Three example `PrincipalCostCenterMapping` rows and one `AdminPrincipal` row with **made-up
     GUIDs** (`11111111-…`, `22222222-…`, etc.) so the Admin console has something to show —
     these are placeholder object IDs, not real Entra identities.
   - Budget snapshots from the mock client's `GetBudgetsAsync`.
3. Auto-signs you in as a synthetic `dev-admin` identity (see the `if (app.Environment.IsDevelopment())`
   block near the bottom of `Program.cs`) so there's no Entra sign-in to configure just to look
   around. This **never** runs outside `Development` — Azure deployments always go through Easy
   Auth/Entra.

## Azure ("demo mode") specifics

`terraform.tfvars`'s `use_mock_data` variable (default `true`) sets the app setting
`GitHub:UseMock`, which drives the exact same `if (useMock)` branch in `Program.cs` — the mock
client is registered instead of the real one. Everything else about the deployment (Entra sign-in,
Key Vault, Azure SQL, private networking, the admin console) is **fully real** — only the GitHub
usage/budget numbers are synthetic. This is what makes it possible to stand up a completely
functional, Entra-authenticated demo environment with realistic-looking multi-month trend data
**before** anyone has a GitHub Copilot Business/Enterprise PAT to hand over — useful for
pilots/demos where the infra needs sign-off before the GitHub side is ready.

## Switching to real data

Set `use_mock_data = false` (Terraform) or `GitHub:UseMock=false` (app setting) **and** provide a
GitHub enterprise PAT — see the root [README](../README.md#going-live-against-real-github-data)
and [infra/README.md](../infra/README.md#going-live-against-real-github-data) for the exact
steps (Key Vault secret name, required PAT scopes, etc.). Once switched, `RealGitHubBillingClient`
calls GitHub's actual [billing usage report](https://docs.github.com/en/rest/billing) and
[cost centers](https://docs.github.com/en/rest/orgs/cost-centers)/
[budgets](https://docs.github.com/en/copilot/concepts/billing/budgets-for-usage-based-billing)
endpoints, with retry/backoff and a circuit breaker (`AddStandardResilienceHandler()`), and the
**same** snapshot pipeline persists real numbers instead of fabricated ones — no other code path
changes.
