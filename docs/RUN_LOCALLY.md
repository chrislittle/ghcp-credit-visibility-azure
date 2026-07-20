# Run it locally — a complete walkthrough

This walks a **first-time** user, step by step, through running the dashboard on their own
laptop with **synthetic (mock) data** — no Azure subscription, no GitHub PAT, no SQL Server
install required. Both **bash** (macOS/Linux) and **PowerShell** (Windows) commands are given
for every step. Pick the column that matches your OS.

> **Why is this so simple?** When the app finds no database connection string configured, it
> automatically falls back to an in-memory database and seeds it with realistic mock usage data
> on startup — see [Where the demo data comes from](DEMO_DATA.md) for details. There is nothing
> to install beyond the .NET SDK.

## 1. Install the prerequisite: .NET 10 SDK

You need the **.NET 10 SDK** (not just the runtime — the SDK includes `dotnet build`/`dotnet run`).

**Check if you already have it:**

| Windows (PowerShell) | macOS/Linux (bash) |
|---|---|
| `dotnet --version` | `dotnet --version` |

If that prints `10.x.x` (or higher), skip to step 2. If it errors with "not recognized"/"command
not found", or prints a version starting with `6`, `7`, `8`, or `9`, install the .NET 10 SDK:

- **Windows:** download and run the installer from <https://dotnet.microsoft.com/download/dotnet/10.0>
  (choose the **SDK**, x64, for Windows), or via `winget`:
  ```powershell
  winget install Microsoft.DotNet.SDK.10
  ```
- **macOS:** download the SDK installer from the link above, or via Homebrew:
  ```bash
  brew install --cask dotnet-sdk
  ```
- **Linux (Ubuntu/Debian example):**
  ```bash
  wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
  chmod +x dotnet-install.sh
  ./dotnet-install.sh --channel 10.0
  # Add the printed install path (usually ~/.dotnet) to your PATH, e.g.:
  echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc && source ~/.bashrc
  ```

**Verify again** with `dotnet --version` in a **new** terminal window (PATH changes need a fresh
shell) before continuing.

## 2. Get the code

If you haven't already cloned the repository:

| Windows (PowerShell) | macOS/Linux (bash) |
|---|---|
| `git clone <repo-url>` <br> `cd ghcp-credit-visibility-azure` | `git clone <repo-url>` <br> `cd ghcp-credit-visibility-azure` |

## 3. Run it

That's genuinely it — one command:

| Windows (PowerShell) | macOS/Linux (bash) |
|---|---|
| `cd GhcpCreditVisibility` <br> `dotnet run` | `cd GhcpCreditVisibility` <br> `dotnet run` |

The first run restores NuGet packages and compiles, so it can take a minute. Watch the console —
you're looking for a line like:

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:5001
```

(the exact port varies — it's printed on your screen).

## 4. Open it in a browser

Go to the HTTPS URL printed in the console (e.g. `https://localhost:5001`).

- **First time only:** your browser/OS may warn about the local ASP.NET Core developer
  certificate being untrusted. That's expected for local dev. Trust it once with:
  | Windows (PowerShell) | macOS/Linux (bash) |
  |---|---|
  | `dotnet dev-certs https --trust` | `dotnet dev-certs https --trust` |
  Then restart `dotnet run` and reload the page.
- **In Development, you're automatically signed in** as a synthetic "dev-admin" identity with
  full Admin rights — there is **no** Entra/Microsoft sign-in prompt locally, and none is needed.
  (Easy Auth/Entra sign-in only applies once the app is deployed behind Azure App Service — see
  the root [README](../README.md) and [infra/README.md](../infra/README.md).)
- You should land on the **Usage** dashboard, pre-populated with 12 months of synthetic AI-credit
  spend across 12 mock users and 3 mock cost centers. Try the **Reports** page (break down by
  user/model/cost-center, by day/week/month) and **Admin → Access mappings** (map a fake Entra
  group/user object ID to a cost center — no real Entra tenant needed to try the console itself).

To stop the app, go back to the terminal and press `Ctrl+C`.

## Optional: point it at a real SQL Server instead of in-memory

The in-memory fallback is great for a quick UI preview, but it resets every time you stop the
app (and a couple of admin-console background jobs need a real relational provider — see the
note below). To persist data across restarts, give it a connection string via
[user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) (kept out of source
control) pointing at a local SQL instance:

**Option A — SQL Server in Docker (works identically on Windows/macOS/Linux):**
| Windows (PowerShell) | macOS/Linux (bash) |
|---|---|
| `docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<YourStrong!Passw0rd>" -p 1433:1433 --name sql-dev -d mcr.microsoft.com/mssql/server:2022-latest` | `docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<YourStrong!Passw0rd>" -p 1433:1433 --name sql-dev -d mcr.microsoft.com/mssql/server:2022-latest` |

Then, from the `GhcpCreditVisibility` folder:
| Windows (PowerShell) | macOS/Linux (bash) |
|---|---|
| `dotnet user-secrets set "ConnectionStrings:BillingDb" "Server=localhost;Database=ghcpvisibility;User Id=sa;Password=<YourStrong!Passw0rd>;TrustServerCertificate=True"` | `dotnet user-secrets set "ConnectionStrings:BillingDb" "Server=localhost;Database=ghcpvisibility;User Id=sa;Password=<YourStrong!Passw0rd>;TrustServerCertificate=True"` |

**Option B — SQL Server LocalDB (Windows only, if you have SQL Server Express/LocalDB installed):**
```powershell
dotnet user-secrets set "ConnectionStrings:BillingDb" "Server=(localdb)\MSSQLLocalDB;Database=ghcpvisibility;Trusted_Connection=True;TrustServerCertificate=True"
```

Run `dotnet run` again — the app detects the connection string, applies EF Core migrations
automatically on startup, and switches from in-memory mock seeding to the same snapshot pipeline
Azure uses (still on mock GitHub data unless you also configure a real PAT — see
[DEMO_DATA.md](DEMO_DATA.md) and the root README's *Going live against real GitHub data* section).

## Troubleshooting

| Symptom | Fix |
|---|---|
| `dotnet: command not found` / not recognized | .NET SDK isn't installed or isn't on PATH — see step 1, then open a **new** terminal. |
| Browser says the connection is not private / cert untrusted | Run `dotnet dev-certs https --trust`, restart `dotnet run`. |
| Port already in use | Another process is using the port. Stop it, or run `dotnet run --urls "https://localhost:5050"` to pick a different port. |
| Console shows repeated `SnapshotService` retry warnings | Harmless with the in-memory local provider — a background maintenance query (`ExecuteDelete`) isn't supported by the in-memory EF provider, so the retention-purge step logs a warning and retries every 12h. The dashboard itself still works from the data seeded at startup. Point at a real SQL Server (see above) to avoid it entirely. |
| Changes to `.cshtml` files don't show up | Stop (`Ctrl+C`) and re-run `dotnet run`, or use `dotnet watch run` for hot-reload during active development. |
| `dotnet run` fails restoring packages (no internet / corporate proxy) | Configure your NuGet proxy/credentials per your organization's policy, or run `dotnet restore` first with the right network access, then `dotnet run`. |

## What's next

- **Deploy to Azure:** see the root [README](../README.md#deploying-to-azure) and
  [infra/README.md](../infra/README.md) for the full Terraform-based deployment (public or
  fully private networking, optional jump box + Bastion for testing the private path, mock or
  real GitHub data).
- **Understand the mock data:** see [DEMO_DATA.md](DEMO_DATA.md).
