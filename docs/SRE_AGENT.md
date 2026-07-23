# Azure SRE Agent integration (optional)

An optional [Azure SRE Agent](https://learn.microsoft.com/azure/sre-agent/overview) — an AI
reliability agent — scoped to this deployment, giving deep troubleshooting over the app, its Azure
SQL database, and supporting services. **Off by default** (`enable_sre_agent = false`); it's a
preview feature that incurs an always-on cost while it exists.

- [Why this app needs more than HTTP monitoring](#why-this-app-needs-more-than-http-monitoring)
- [Architecture](#architecture)
- [What gets built](#what-gets-built)
- [Deploying it](#deploying-it)
- [Cost](#cost)
- [Tearing it down](#tearing-it-down)
- [Constraints & caveats](#constraints--caveats)

## Why this app needs more than HTTP monitoring

The dashboard never calls GitHub live — a background job snapshots usage into Azure SQL every 12h,
and the UI only reads the DB. So the failures that actually matter here are **invisible to HTTP
monitoring**:

- the snapshot job silently stops → the site is 200 OK while serving month-old numbers
- a run "succeeds" but writes 0 rows, or the wrong rows → the app is fully up while wrong
- the Key Vault PAT reference never resolves → a GitHub 401 three layers from its cause

A billing app can be **100% available while 100% wrong**, and nothing in stock availability
monitoring catches it. The integration solves this in two layers: first make those failures
observable (Phase 0), then put an agent on top that can diagnose them.

## Architecture

The agent's reasoning runs in a Microsoft-managed sandbox and reaches this deployment through ARM,
Log Analytics, and Application Insights — all global control-plane endpoints. So it can run in a
**different region** from the app (`Microsoft.App/agents` isn't available in Germany West Central)
and still monitor it.

```
   Azure SRE Agent  (rg-sre-<base>, e.g. Sweden Central)
   managed identity · Reader + Monitoring Contributor · mode: Review
        │  ARM · Log Analytics query · App Insights query · Azure Monitor alerts
        ▼
   Log Analytics + Application Insights   ◄── platform logs/metrics (already wired)
        ▲                                 ◄── custom metrics + events  (Phase 0, NEW)
        │
   App Service · Azure SQL · Key Vault · ACR   (the app, in Germany West Central)
```

The load-bearing new piece is **Phase 0**: `SreDiagnosticsPublisher` pushes the app's private-DB
health to Application Insights as custom metrics, and `SnapshotService` emits lifecycle events.
Without that, an out-of-network agent has nothing to see. Phase 0 is valuable on its own and ships
regardless of whether the agent is ever enabled.

### Phase 0 telemetry (always emitted)

| Metric / event | Catches |
|---|---|
| `ghcp.snapshot.age_hours` | snapshot job stopped (>26h) |
| `ghcp.snapshot.rows_written` | succeeded-but-wrote-zero |
| `ghcp.github.token_resolved` | Key Vault PAT reference didn't resolve |
| `ghcp.github.rate_limit_remaining` | GitHub throttling pressure |
| `ghcp.db.pending_migrations` | schema warm-up / missing DDL grant |
| `ghcp.data.{costcenters,budgets,months_with_data}` | data-integrity floor |
| `SnapshotRunCompleted` / `SnapshotFailed` events | per-run detail (instanceId, duration, error) |

Reach it directly at `GET /health/diag` (authenticated) for a JSON dump of the same values.

## What gets built

| Layer | Where | Managed by |
|---|---|---|
| The agent + its RG + App Insights + two managed identities | `infra/sreagent.tf` (azapi) | Terraform |
| RBAC (agent identities read your resources; operator gets SRE Agent Administrator) | `infra/sreagent.tf` | Terraform |
| **Data connectors** (App Insights + Log Analytics — the "Logs" source) | `infra/sreagent.tf` (azapi) | Terraform |
| 6 alert rules on the Phase 0 telemetry | `infra/sreagent.tf` | Terraform |
| 6 skills (troubleshooting guides) | `sre/skills/` | `deploy.ps1 -Task sre-sync` |
| 4 custom agents (domain specialists) | `sre/agents/` | `sre-sync` |
| 2 hooks (policy gate + quality gate) | `sre/hooks/` | `sre-sync` |
| Knowledge files (existing docs) | `sre/knowledge/files.txt` | `sre-sync` |

Only the agent + its infra are ARM resources; skills/agents/hooks/knowledge are data-plane config
kept in git under `sre/` — see [`sre/README.md`](../sre/README.md).

### Identities (two, by design)

- A **user-assigned identity** (`id-sre-*`) — referenced by `knowledgeGraphConfiguration.identity`
  and `actionConfiguration.identity`. It's REQUIRED at create time (a system-assigned identity
  doesn't exist yet during creation → `InvalidIdentity` 400), and carries the resource/log/KV-Reader
  roles + Monitoring Contributor.
- The agent's **system-assigned identity** — used by the data **connectors** (`identity = "system"`),
  so it carries its own Log Analytics Reader / Monitoring Reader / Reader on the app RG.

### Safety posture

- **Read-only by default.** Reader-tier RBAC; `sre_agent_mode = "Review"` (proposes, waits for
  approval). No sponsor group → no OBO write-elevation path.
- **Key Vault: control-plane Reader only.** The agent can see the vault's config but **never** read
  the `github-pat` secret. The policy hook is a second line of defence — it blocks
  `keyvault secret show/set`, appsettings mutation, and resource deletion regardless of run mode.
- **Quality gate.** The Stop hook makes DATA/INCIDENT investigations cite their resource, time
  window, query evidence, and blind spots — but **passes refusals and knowledge/architecture answers
  without an evidence demand** (so a security refusal isn't forced into a tangent). `maxRejections: 1`.

## Deploying it

Prerequisites: the app itself must be deployed (the agent monitors it, and the alerts/skills key off
Phase 0 telemetry), and the `Microsoft.App` provider registered (`deploy.ps1 -Task provision` does
this automatically when enabled).

1. Choose a supported region and enable the agent in `terraform.tfvars`:
   ```hcl
   enable_sre_agent   = true
   sre_agent_location = "swedencentral"   # NOT germanywestcentral — see the validation list
   # sre_agent_mode   = "Review"          # default; never Autonomous for SQL/Key Vault
   # sre_agent_sponsor_group_id = ""       # optional; leave empty for strictly read-only
   ```
2. Provision the agent (isolated from the app — a failure here never blocks the core deploy):
   ```
   ./deploy.ps1 -Task sre-provision
   ```
3. Grant the agent read-only SQL access (for DMV / Query Store depth):
   ```
   ./deploy.ps1 -Task grant-sre-sql
   ```
4. Sync the skills, custom agents, hooks, **and knowledge files** — all PUT/POSTed to the data-plane
   API. This needs the **SRE Agent Administrator** role on the agent (for the `https://azuresre.dev`
   token) — which `sre-provision` **grants to the deploying principal automatically** (subscription
   Owner is NOT sufficient; the data plane has its own RBAC). Set `sre_agent_admin_object_id` to point
   it at a shared ops group instead. If it's ever missing, `sre-sync` prints the exact `az role
   assignment create` command as a fallback.
   ```
   ./deploy.ps1 -Task sre-sync
   ```
5. Open the agent at [sre.azure.com](https://sre.azure.com) and try a question:
   *"Is the GHCP dashboard data current, and did the last snapshot succeed?"*

The `all` deploy (`./deploy.ps1`, answer yes to the SRE prompt) runs steps 1–4 for you — the agent is
fully configured end to end, no manual portal steps. The agent is **isolated from the core app
deploy**: it provisions last and non-fatally, so a preview-API hiccup can never block the app.

## Cost

Usage-based, billed from agent **creation until deletion** (stopping halts only the variable part):

| Component | Rate (≈, verify on the pricing calculator) |
|---|---|
| Always-on | ~4 AAU/agent-hour ≈ **$0.40/hr** (~$292/mo if left running) |
| A quick question | ~3.8 AAU ≈ $0.38 |
| An incident investigation | ~35 AAU ≈ $3.53 |

A short **test session** (spin up, ask a few questions, tear down the same day) is **~$5–10**. The
$0.10/AAU used here is an unofficial estimate from the docs' cost example — confirm for your region.

## Tearing it down

The always-on charge only stops on **deletion**, so don't leave it running. To remove just the agent
(keep the app), set `enable_sre_agent = false` and re-provision — `provision` reads the region from
tfvars, so no `-Location` needed:

```hcl
enable_sre_agent = false
```
```
./deploy.ps1 -Task provision      # destroys the agent, its identities, connectors, RBAC, rg-sre-*
```

Or `cd infra ; terraform destroy` to remove the whole environment.

## Constraints & caveats

- **Preview feature.** `Microsoft.App/agents` and the VNet-integration path are preview; region
  availability and API shapes can change. Re-check supported regions with
  `az provider show -n Microsoft.App --query "resourceTypes[?resourceType=='agents'].locations"`.
- **Private SQL access.** In the private networking pattern, a Microsoft-managed sandbox can't reach
  the private SQL endpoint directly. Phase 0's metrics are the primary DB visibility; the optional
  VNet-integration path (deliberately NOT built here) would add live DMV access.
- **Max 5 active skills** at once (the agent loads the relevant ones per question); English-only chat.
- **Workspace-based telemetry schema.** This App Insights is workspace-based, so custom metrics/events
  land in the Log Analytics workspace under the `App*` tables (`AppMetrics`/`AppEvents`) — the classic
  `customMetrics`/`customEvents` tables are empty. The alert rules and the skills' KQL are written for
  the `App*` schema and scoped to the workspace (verified against live telemetry). If you point this
  at a *classic* (non-workspace) App Insights, the KQL would need reverting to `customMetrics`.
- **Query via the connector, not raw `az`.** The skills steer the agent to the "Monitor Workspace Log
  Query" tool (backed by the Log Analytics connector). Raw `az monitor log-analytics query` needs the
  workspace **GUID** (customerId, not the name) + the `log-analytics` extension — documented as a
  fallback in `ghcp-snapshot-pipeline`.
- **App-side telemetry (`TelemetryClient`).** On App Service Linux the AI SDK's
  `AddApplicationInsightsTelemetry()` didn't register `TelemetryClient`, which crashed the app on
  startup; `Program.cs` now registers it explicitly and `SreDiagnosticsPublisher` resolves it softly
  (observability never crashes the app).
