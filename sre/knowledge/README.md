# Knowledge files

The SRE Agent RAG-indexes reference docs it can search automatically. **`deploy.ps1 -Task sre-sync`
uploads these for you** — it reads [`files.txt`](files.txt) and POSTs each listed file to the agent's
AgentMemory (`/api/v1/AgentMemory/upload`). Nothing manual.

To change what gets uploaded, edit [`files.txt`](files.txt) (repo-relative paths or globs, one per
line). The files stay single-sourced in the repo — the manifest points at them, it doesn't copy them.

Currently uploaded:

| File | Why the agent wants it |
|---|---|
| [`../../README.md`](../../README.md) | What the app is, the request/data path, the access model |
| [`../../infra/README.md`](../../infra/README.md) | Full resource list, networking modes, the SQL grant / PAT model |
| [`../../docs/DEMO_DATA.md`](../../docs/DEMO_DATA.md) | Mock vs. real data pipeline — so the agent knows when numbers are synthetic |
| [`../../SECURITY.md`](../../SECURITY.md) | Reporting model |
| [`../../docs/SRE_AGENT.md`](../../docs/SRE_AGENT.md) | This integration's own runbook |
| [`README.md`](README.md) | The agent's own config layout |
| `CLAUDE-SECURITY-*/CLAUDE-SECURITY-RESULTS.md` | Past security findings — so the agent recognizes the `enable_easy_auth` history if it sees odd `/Admin/Mappings` traffic |

Caps: ≤16 MB per file, ≤100 MB per sync. Architecture diagrams under `docs/images/` **cannot** be
uploaded (images aren't a supported knowledge type) — they're described in text in `docs/SRE_AGENT.md`.
