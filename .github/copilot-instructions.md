# Instructions for AI coding agents

Read this before changing anything in this repository. It applies to GitHub Copilot, Claude and any
other coding agent, in this repo and in forks of it.

## What this tool is

A **GitHub Copilot spend** tool for Azure. It collects Copilot usage and billing from GitHub's
enterprise billing API into SQL, then shows it per user, cost center, model, organization and
enterprise, with access controlled by Entra ID.

It is **not** a general GitHub billing tool. Numbers labeled "Copilot" or "AI credits" must mean
exactly that.

## Rules that break things if ignored

Each rule below was learned from a real incident. If a task seems to require breaking one, **stop
and ask the human** instead of working around it.

1. **Never remove the Entra `groups` claim** (`infra/entra.tf`, `group_membership_claims`).
   Group-based access, admin groups and cost-center mappings all depend on it
   (`Authorization/AppAdmin.cs`, `Authorization/UserScope.cs`). Without it, group users silently
   see empty pages. To fix **HTTP 431** (headers too large), set
   `group_membership_claims = "ApplicationGroup"` instead.
2. **The billing feed holds every GitHub product**, not just Copilot: Actions, Advanced Security,
   Codespaces, LFS and so on (`OrgUsageSnapshots`, from `/settings/billing/usage`). Classify rows
   with `Services/CopilotBillingLines.cs`. Never write your own `Product == "copilot"` check (GitHub
   sends `Copilot` and `copilot`), and never remove the filter. Non-Copilot spend may be shown only
   as its own clearly labeled figure, never inside a Copilot total.
3. **License costs are never allocated** to users, models or cost centers. GitHub doesn't provide
   that split, and inventing one produces wrong chargebacks.
4. **Access control:** data with no cost center (organization usage, the Copilot bill, the allowance
   pool, organization budgets) is shown only to Enterprise Readers
   (`scope.HasEnterpriseRead`), restricted to their enterprises (`EnterpriseReadFilter()`).
   Cost-center managers must get **nothing** from it, not a subset. Filter every usage query
   through the existing scope helpers in `UsageQueryService`.
5. **Never sum `DailyUsageSnapshots`.** Each row is a running month-to-date total; summing them
   multiplies spend by the number of days. Use `UsageQueryService.ToPerDayRows`.
6. **Null is not zero.** A null `GrossQuantity` / `DiscountAmount` means "not recorded". Don't
   coalesce it to 0 where that would show "no usage" for a month that simply wasn't captured.
7. **Net vs gross:** net is what's billed after the included allowance; gross is real consumption.
   A month fully covered by the allowance has net $0 with real usage. Keep both, and label which is
   which.

## When numbers don't match GitHub

**Before changing any code, check that both numbers measure the same thing.** Common mismatches that
are *not* bugs:

- Copilot bill vs GitHub's **whole** invoice (the difference is Actions, GHAS and other products).
- Net (after allowance) vs gross (before allowance).
- Per-user AI-credit total vs the billing feed (licenses exist only in the billing feed).
- Different months, or different enterprises in scope.

If the numbers still differ after that, report it with both figures and where each came from. Don't
remove filters to make totals match.

## Before you finish any change

Run the full test suite from the repository root (requires the .NET 10 SDK):

```bash
dotnet test GhcpCreditVisibility.Tests
```

- **Every test must pass.** Never delete, skip or weaken a test to make a change pass. A failing
  test usually means a rule above was broken.
- `RelationalTranslationTests` runs the real queries against SQLite. Most other tests use EF's
  in-memory provider, which **does not** catch LINQ that can't translate to SQL; that kind of bug
  passes locally and then fails in production. If you add or change a query, add it to
  `RelationalTranslationTests`.
- Add tests for new behavior, especially anything touching the rules above.
- **Schema changes** need an EF Core migration in `GhcpCreditVisibility/Migrations`. Also update
  the SRE docs under `sre/`, which describe the table shapes.
- **Terraform changes:** run `terraform validate` in `infra/`.

To see the UI locally, run the command below from the repository root. With no connection string,
it uses an in-memory database and mock GitHub data (`GitHub:UseMock`), so it needs no secrets:

```bash
dotnet run --project GhcpCreditVisibility
```

## Style

Match the surrounding code, including its comments: they explain *why* a rule exists, often with
the incident behind it. Keep them when editing, and read them before "simplifying" something.
