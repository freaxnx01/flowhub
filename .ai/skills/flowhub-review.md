# flowhub-review — Slash Command

Review how the **deployed** FlowHub instance classified and routed the most recent **Captures**, flag the ones it got wrong into a ledger, and turn the ledger into repo fixes or issues.

**Input:** `$ARGUMENTS`

**Flags:**

- `--limit N` — how many Captures to review (**default 10**), newest first.
- `--since <date>` — only Captures created on/after `<date>` (any value Postgres accepts, e.g. `2026-09-01`, `'7 days'` → pass as `now() - interval '7 days'`).
- `--stage <list>` — comma-separated `LifecycleStage` filter (`Unhandled,Failed,Orphan,Completed,…`).
- `--failed-only` — shorthand for "rows with a failed `SkillRun` or a non-null `FailureReason`".
- `--all` — do not hide Captures already recorded in the ledger (default: seen ids are skipped).

> **Sibling skills:** `/flowhub-capture` writes Captures, `/flowhub-triage` reorganises them. This skill **only reads production** and acts on the *repo* — it never writes to the deployed database, the container, or Vikunja.

---

## Steps

### Step 1 — Open a read path to the deployed database

The deployed API (`https://flowhub.home.freaxnx01.ch/api/v1/*`) is behind Authentik OIDC and needs a Bearer JWT; the UI is behind ForwardAuth. The container port `5070` is **not** published on the CT host, so `curl localhost:5070` from inside CT 136 fails. The working path is the Postgres sidecar, reached through the Proxmox host:

```bash
FH_PSQL() {
  ssh -o BatchMode=yes cirrus-pve \
    "pct exec 136 -- docker exec flowhub-postgres psql -U flowhub -d flowhub -P pager=off -A -F '|' -c \"$1\""
}
```

- `cirrus-pve` resolves from `~/.ssh/config` (192.168.1.111, key `~/.ssh/break_glass`). FlowHub is **CT 136**.
- Credentials: database `flowhub`, user `flowhub` — no password needed via `docker exec` (local socket trust).
- If the ssh hop fails, stop with `Could not reach cirrus-pve — is the homelab up?` Do **not** fall back to the HTTPS API without asking; it needs an OIDC token the skill does not hold.

Sanity check before anything else:

```bash
FH_PSQL 'select count(*), max(\"CreatedAt\") from \"Captures\";'
```

### Step 2 — Discover the deployed schema (do not assume it matches `main`)

The CT runs a pinned image that is usually **older than the working tree**. As of 2026-09-17 it has no `Bridge*` columns even though `Capture` in `main` does. So ask the database which columns exist, and select only those:

```bash
FH_PSQL "select column_name from information_schema.columns where table_name='Captures' order by ordinal_position;"
```

Build the projection from the intersection of what you want and what exists. Always available: `Id`, `Content`, `Source`, `Stage`, `CreatedAt`, `MatchedSkill`, `Title`, `FailureReason`, `ExternalRef`, `VikunjaProject`, `EnrichmentDescription`, `ClassifierTrace_*`. Optional (newer images): `BridgeAlias`, `BridgeAction`, `BridgeTarget`, `BridgeBody`.

**Never `select *`** — the `Embedding` column is a 384-dim vector and will flood the output.

### Step 3 — Read the Captures + their skill runs

One query, newest first, left-joined to the latest `SkillRuns` row per Capture:

```sql
select c."Id", c."CreatedAt", c."Source", c."Stage", c."MatchedSkill",
       c."VikunjaProject", c."ExternalRef", c."FailureReason",
       c."ClassifierTrace_Kind", c."ClassifierTrace_Model", c."ClassifierTrace_LatencyMs",
       left(replace(c."Content", chr(10), ' '), 70) as snippet,
       r."SkillName", r."Success", r."FailureReason" as run_failure
from "Captures" c
left join lateral (
  select * from "SkillRuns" s where s."CaptureId" = c."Id" order by s."StartedAt" desc limit 1
) r on true
order by c."CreatedAt" desc
limit <N>;
```

Apply `--since` / `--stage` / `--failed-only` as extra `where` clauses. Escape the inner double quotes for the ssh hop (they survive the `\"` form in `FH_PSQL` above).

Two things learned the hard way: use `chr(10)`, **not** `E'\n'` — the backslash does not survive the ssh + `pct exec` + `docker exec` quoting chain. And `SkillRuns` is often empty even for routed Captures (the deployed image records the outcome on the Capture's own `FailureReason`/`ExternalRef` instead), so never treat a missing run row as a failure.

### Step 4 — Present the review table

One row per Capture, newest first:

```
#  when              src       snippet                          stage      skill      target         classifier        run
1  2026-09-16 15:16  Telegram  spieler 1 hat als letzte karte…   Unhandled  Paperless  —              —                 —          ⚠
2  2026-09-16 12:59  Telegram  Wipfelkratzer : - maus Objekte…   Completed  Bridge     —              Ai llama-3.1 840ms ok
```

**Pre-flag (`⚠`) rows that are suspicious on their face** — say why, one short clause each:

- `Stage` is `Unhandled`/`Orphan`/`Failed` **but** `MatchedSkill` is set → classified, never routed.
- `ClassifierTrace_Kind` is null → no classification ran at all (or the row predates tracing).
- `ClassifierTrace_Kind = Keyword` on a non-trivial Capture → the AI classifier errored and fell back.
- A `SkillRun` with `Success = false`, or any non-null `FailureReason`.
- `MatchedSkill` set but no target at all (`VikunjaProject`, `ExternalRef`, `Bridge*` all null).
- The same `Content` appearing more than once (duplicate delivery).

**Separate *misclassification* from *unwired skill* before asking the user.** A `FailureReason` of `no integration registered for skill '<X>'` means the classifier was probably right and the deployment simply has that Skill switched off (the CT runs with Skills partly disabled). Report those rows as **config**, not as classification errors — they belong in the deployment follow-up, not the ledger, unless the user disagrees. Same for `exhausted retries: … 404` — the routing target is wrong or gone, which is a Skill/config problem, not a classification one.

A `⚠` is a *suggestion*, not a verdict — the user decides. Then ask:

> Which rows were classified or routed wrong? (numbers, or `none`)

For each number, ask what it **should** have been (target skill / project / action) and a one-line reason. Keep it to one short exchange per row.

### Step 5 — Append to the ledger

Ledger lives at `docs/ai-notes/routing-review-ledger.md` (create with a `# FlowHub routing review ledger` heading on first use). **Append-only** — never rewrite or delete existing entries; a handled entry gets its status line updated in place, nothing more.

One entry per flagged Capture:

```markdown
## 2026-09-17 — 5f3c1a2e-…  (Telegram, 2026-09-16 15:16)
- **content:** spieler 1 hat als letzte karte eine sieben…
- **got:** skill `Paperless`, stage `Unhandled`, no target, no classifier trace
- **expected:** skill `Vikunja`, project `Inbox`
- **why:** game-rule note, nothing document-like about it
- **status:** open
```

`status` is one of `open` · `fixed <commit-or-PR>` · `issue <url>` · `parked <reason>`.

Before presenting Step 4, read the ledger and (unless `--all`) drop Captures whose id already appears — re-runs stay quiet about ground already covered.

### Step 6 — Turn ledger entries into action

For each `open` entry, propose one of three and ask the user to confirm per entry:

**a) Direct fix** — a small, obvious repo change. Where the fix usually belongs:

| Symptom | Where to fix |
|---|---|
| Deterministic rule wrong / missing (url, todo keyword) | `source/FlowHub.Core/Classification/KeywordClassifier.cs` |
| AI picked the wrong skill, prompt/bucket wording | `AiPrompts` used by `source/FlowHub.AI/AiClassifier.cs` |
| A domain term is misread | glossary (`source/FlowHub.AI/FileGlossarySource.cs`, file mounted into the container) |
| Repo alias not matched / matched too eagerly | `source/FlowHub.Core/Classification/BridgeAliasMatcher.cs` + the Bridge catalog |
| Classified fine but never routed / routing errored | `source/FlowHub.Skills/<Skill>/` |

**TDD applies** (`CLAUDE.md`): write the failing test first — `tests/FlowHub.Core.Tests`, `tests/FlowHub.AI.IntegrationTests`, or `tests/FlowHub.Skills.Tests` as appropriate — using the real Capture content from the ledger as the test case. That turns each misclassification into a permanent regression test. Then implement, then run that project's tests.

**b) Issue** — anything that needs design, touches the AI prompt structurally, or is bigger than one test + one edit. Use `/new` (labels it `needs-enrichment`) against `freaxnx01/flowhub` on GitHub. Title the real behaviour, and paste the ledger entry into the body so the Capture content survives.

**c) Park** — record `status: parked <reason>` and move on.

Update each entry's `status` line as soon as its action completes. A fix is **not** deployed by this skill: the CT runs a pinned image, so say so explicitly — `fixed in repo; CT 136 still runs <image tag> until redeployed`.

### Step 7 — Summary

```
reviewed: <N> captures (<from> … <to>)
flagged:  <n>  → fixed <a>, issues <b>, parked <c>
ledger:   docs/ai-notes/routing-review-ledger.md
note:     fixes are repo-side; CT 136 keeps running the pinned image until redeployed
```

---

## Rules

- **Read-only against production.** No `insert`/`update`/`delete`, no `docker restart`, no writes to the CT. Every fix is a normal repo change that ships through CI.
- **Never `select *` on `Captures`** — the `Embedding` vector destroys the output.
- **Never assume the deployed schema equals the working tree.** Step 2 is not optional.
- **Never invent an expected classification** — the user says what a Capture should have been; the skill only proposes and asks.
- **The ledger is append-only.** Handled entries change their `status` line and nothing else.
- Escape every user-supplied string going into SQL (`--since`, `--stage`) — reject anything containing a quote or semicolon rather than interpolating it.
- If `$ARGUMENTS` names something other than a known flag, print the usage block and stop:

```
usage: /flowhub-review [--limit N] [--since <date>] [--stage <list>] [--failed-only] [--all]
```

---

If you run into blockers, find a solution and update this skill for the future — especially access paths (the ssh/pct/psql hop above was itself derived this way) and new suspicion heuristics worth pre-flagging in Step 4.
