# SRE Agent configuration (`sre/`)

Data-plane configuration for the optional Azure SRE Agent. **Only the agent itself is an ARM
resource** (`infra/sreagent.tf`); skills, custom agents, and hooks are agent data-plane config, so
they live here in git and are pushed with `./deploy.ps1 -Task sre-sync` rather than through
Terraform. See [`docs/SRE_AGENT.md`](../docs/SRE_AGENT.md) for the full picture.

```
sre/
  skills/        6 skills — procedural troubleshooting guides the agent loads automatically
    ghcp-snapshot-pipeline/    (skill.yaml + SKILL.md)
    ghcp-private-network-path/
    ghcp-sql-deep-dive/
    ghcp-identity-and-auth/
    ghcp-data-integrity/
    ghcp-cost-and-sizing/
  agents/        4 custom agents — domain specialists invoked with /agent
  hooks/         policy-gate.yaml (blocks secret exposure) + quality-gate.yaml
  knowledge/     README listing which repo docs to upload as agent knowledge files
```

## Constraints to remember

- **Max 5 skills active at once** (oldest auto-unloads; cleared on conversation compaction). Six
  are defined here deliberately — they don't all load simultaneously, the agent picks the relevant
  ones per question. Don't add more without consolidating.
- Skills load **automatically** by description match; custom agents are **explicit** (`/agent`).
- Run mode (ReadOnly / Review / Autonomous) is set per response-plan / scheduled-task in the portal,
  **not** in these files.

## Syncing

```
./deploy.ps1 -Task sre-sync
```

resolves the agent's data-plane endpoint from Terraform output and **PUTs every skill, custom agent,
and hook** to `{endpoint}/api/v2/extendedAgent/{kind}/{name}` — the same data-plane API the official
[microsoft/sre-agent](https://github.com/microsoft/sre-agent) tooling uses. It needs the **SRE Agent
Administrator** role on the agent (for the `https://azuresre.dev` token); if that or the endpoint
isn't available yet it degrades gracefully with guidance instead of failing.

Only **knowledge files** remain a separate step (binary upload) — see `knowledge/README.md`.

> API version `2025-05-01-preview`. Both the control- and data-plane APIs are in preview, so pin to
> this version and re-verify after upgrades. The hook envelope (`type: GlobalHook`, `properties =
> spec`) is the least-settled shape — verify hooks first after a sync.
