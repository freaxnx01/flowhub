Review how the deployed FlowHub instance classified and routed the latest **Captures**, flag the wrong ones into a ledger, and turn the ledger into repo fixes or issues.

Args: $ARGUMENTS

**Flags:**
- `--limit N` (default **10**) — how many Captures to review, newest first.
- `--since <date>` — only Captures created on/after that date.
- `--stage <list>` — comma-separated `LifecycleStage` filter.
- `--failed-only` — only rows with a failed `SkillRun` or a `FailureReason`.
- `--all` — include Captures already recorded in the ledger (default: skipped).

Follow the canonical skill body in `.ai/skills/flowhub-review.md` exactly. It contains the database access path, the schema probe, the query, the review table format, the ledger format, and the fix/issue/park loop.

## Quick reference

### Reading production (read-only)

The API needs an Authentik OIDC Bearer token and container port 5070 is not published on the CT host. Go through Postgres instead:

```bash
ssh cirrus-pve "pct exec 136 -- docker exec flowhub-postgres psql -U flowhub -d flowhub -P pager=off -A -F '|' -c \"<sql>\""
```

`cirrus-pve` = 192.168.1.111 (key `~/.ssh/break_glass`), FlowHub = **CT 136**, db/user `flowhub`.

### Tables

- `Captures` — `Stage`, `MatchedSkill`, `VikunjaProject`, `ExternalRef`, `FailureReason`, `ClassifierTrace_{Kind,Model,Provider,LatencyMs,PromptTokens,CompletionTokens}`, plus `Bridge*` on newer images. **Never `select *`** (384-dim `Embedding` column).
- `SkillRuns` — `SkillName`, `CaptureId`, `Success`, `FailureReason`, `StartedAt`.

The deployed image usually lags the working tree — probe `information_schema.columns` before building the projection.

### Ledger

`docs/ai-notes/routing-review-ledger.md`, append-only. Per entry: capture id, content snippet, **got**, **expected**, **why**, and a `status` of `open` / `fixed <ref>` / `issue <url>` / `parked <reason>`.

### Where fixes usually land

| Symptom | File |
|---|---|
| Deterministic rule wrong | `source/FlowHub.Core/Classification/KeywordClassifier.cs` |
| AI picked the wrong skill | `AiPrompts` via `source/FlowHub.AI/AiClassifier.cs` |
| Domain term misread | glossary (`source/FlowHub.AI/FileGlossarySource.cs`) |
| Repo alias mismatch | `source/FlowHub.Core/Classification/BridgeAliasMatcher.cs` |
| Classified but not routed | `source/FlowHub.Skills/<Skill>/` |

TDD applies — the misclassified Capture's real content becomes the failing test first. Bigger than one test + one edit → `/new` issue on `freaxnx01/flowhub` instead.

Fixes are repo-side only; CT 136 keeps running its pinned image until redeployed.
