# Where does the demo data come from?

Short answer: it's **synthetic data generated in code** (`MockGitHubBillingClient.cs`) —
deterministic, seeded from fixed fake usernames — not a copy of anyone's real GitHub Copilot
usage, and not pulled from any live service. This page explains exactly what it is, where it
lives, and how the switch to real GitHub data works, for both **local dev** and **Azure**.

## The mock client

[`GhcpCreditVisibility/Services/MockGitHubBillingClient.cs`](../GhcpCreditVisibility/Services/MockGitHubBillingClient.cs)
implements the same `IGitHubBillingClient` interface the real GitHub billing API client
(`RealGitHubBillingClient.cs`) implements. It serves **distinct data per enterprise slug**,
deliberately exercising the multi-enterprise collision cases:

- **`contoso`** — 20 fake users (e.g. `ahernandez` / Ana Hernandez) across three cost centers
  (**Engineering**, **Product**, **Finance**)
- **`fabrikam`** — 8 fake users, **two of whom (`dkim`, `jchen`) share logins with contoso users**
  (the same GitHub login legitimately existing in two enterprises — billed separately in each), and
  a cost center **also named "Engineering"** (name collision by design; the ids differ)
- **`demo-broken`** — always throws a simulated outage. This is the **fire-drill enterprise**:
  register it (admin console) to rehearse per-enterprise failure isolation, the split-by-enterprise
  alerts, and SRE-agent triage with zero risk to real data
- **any other slug** — the classic `Cost Center A/B/C` seed, so pre-multi-enterprise
  single-enterprise demos look unchanged
- **3 fake AI models** (`gpt-5`, `claude-sonnet-4.5`, `o4-mini`) with illustrative per-credit prices
- **Illustrative monthly budgets** — one enterprise-wide budget per enterprise plus one per cost center

For each enterprise/user/model/month, a **seeded `Random`** (seeded from a hash of the
enterprise + username + year + month) produces a quantity and cost. "Seeded" means the same user
always gets the same numbers for the same month — so the numbers are stable across app restarts
and deploys (not truly random noise), but they are **entirely made up**; no code path calls out to
GitHub, a database of real usage, or any external source to produce them. A login that exists in
two enterprises gets **different** spend in each, as with real billing.

A second helper, `BuildHistorySnapshots(months, asOfUtc, enterpriseId, slug)`, fabricates **daily** history rows
(with mild weekday/weekend seasonality) for the last N months, purely so the **Reports** page's
day/week/month granularity toggle has something meaningful to show locally — the real GitHub
billing API only ever returns **month-level** aggregates (see below), so this daily fabrication
is a local-preview-only convenience and isn't part of the `IGitHubBillingClient` contract.

## How mock data reaches the dashboard — the pipeline is identical either way

The dashboard **never** calls GitHub (mock or real) directly. A background job
(`SnapshotService` / `SnapshotHostedService`) is the **only** caller of `IGitHubBillingClient`; it
runs on startup and every 12 hours, writes rows into the `UsageSnapshot`/`BudgetSnapshot` tables,
and the UI reads only from those tables. The mock/real choice is made **per enterprise registry
row** (`UseMockData` flag, managed in the admin console; `GitHub:UseMock` only sets the default for
the first row) — everything downstream (persistence, the admin console, scoping, the
dashboard/report pages) is unaware which client produced the data. Because routing is per row, a
**hybrid** deployment — your real enterprise plus mock demo/fire-drill enterprises in the same
tables and dashboards — is a supported, first-class configuration.

```
                       ┌─►  MockGitHubBillingClient   (registry rows with UseMockData = true)
EnterpriseBillingClientFactory (routes PER ENTERPRISE)
                       └─►  RealGitHubBillingClient   (one per real enterprise: own PAT, own rate limit)
        ▲
   SnapshotService (the ONLY caller; loops ENABLED registry enterprises, isolated per enterprise)
        │                ──► writes UsageSnapshot / BudgetSnapshot / CostCenterDirectory rows (EnterpriseId-stamped)
   UsageQueryService / dashboard pages ──► read ONLY from the database, never live
```

## Local dev specifics

When you `dotnet run` with no `ConnectionStrings:BillingDb` configured (the default — see
[RUN_LOCALLY.md](RUN_LOCALLY.md)), `Program.cs`:
1. Falls back to an **EF Core in-memory database** (`UseInMemoryDatabase`) instead of SQL Server —
   purely so the app runs with zero external dependencies.
2. Calls `EnsureCreated()` and, if empty, seeds:
   - **Two mock enterprises** (`contoso` and `fabrikam`) in the enterprise registry, so the
     multi-enterprise UI (enterprise filter, enterprise breakdown, per-enterprise budgets, the
     admin registry table) renders with content out of the box.
   - 12 months of fabricated daily history **per enterprise** via
     `MockGitHubBillingClient.BuildHistorySnapshots(...)`
   - Example `PrincipalCostCenterMapping` rows (including one group mapped into BOTH enterprises —
     the cross-enterprise exec view) and one `AdminPrincipal` row with **made-up GUIDs**
     (`11111111-…`, `22222222-…`, etc.) so the Admin console has something to show —
     these are placeholder object IDs, not real Entra identities.
   - Budget snapshots from the mock client's `GetBudgetsAsync`, per enterprise.
3. Auto-signs you in as a synthetic `dev-admin` identity (see the `if (app.Environment.IsDevelopment())`
   block near the bottom of `Program.cs`) so there's no Entra sign-in to configure just to look
   around. This **never** runs outside `Development` — Azure deployments always go through Easy
   Auth/Entra.

## Azure ("demo mode") specifics

`terraform.tfvars`'s `use_mock_data` variable (default `true`) sets the app setting
`GitHub:UseMock`, which seeds the enterprise registry's first row with `UseMockData = true` — so
the client factory routes that enterprise to the mock. (Additional enterprises choose mock or real
per row in the admin console.) Everything else about the deployment (Entra sign-in,
Key Vault, Azure SQL, private networking, the admin console) is **fully real** — only the GitHub
usage/budget numbers are synthetic. This is what makes it possible to stand up a completely
functional, Entra-authenticated demo environment with realistic-looking multi-month trend data
**before** anyone has a GitHub Copilot Business/Enterprise PAT to hand over — useful for
pilots/demos where the infra needs sign-off before the GitHub side is ready.

## Switching to real data

Set `use_mock_data = false` (Terraform) or `GitHub:UseMock=false` (app setting) **and** provide a
GitHub enterprise PAT — or, on an already-live deployment, just add a real enterprise in the admin
console and seed its PAT (`./deploy.ps1 -Task set-pat -Enterprise <slug>`); mock and real
enterprises can coexist. See the root [README](../README.md#going-live-against-real-github-data)
and [infra/README.md](../infra/README.md#going-live-against-real-github-data) for the exact
steps (Key Vault secret name, required PAT scopes, etc.). Once switched, `RealGitHubBillingClient`
calls GitHub's actual [billing usage report](https://docs.github.com/en/rest/billing) and
[cost centers](https://docs.github.com/en/rest/orgs/cost-centers)/
[budgets](https://docs.github.com/en/copilot/concepts/billing/budgets-for-usage-based-billing)
endpoints, with retry/backoff and a circuit breaker (`AddStandardResilienceHandler()`), and the
**same** snapshot pipeline persists real numbers instead of fabricated ones — no other code path
changes.
