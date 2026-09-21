# TODO — post-split plumbing

Open operational items for the freshly-split `flowhub` product repo.

## Session 2026-09-21 (v0.9.0 — the classifier runs on Claude)

- [x] **~~#111 classifier entities schema~~ — merged (PR #119) and released as `v0.9.0`.**
      CT 136 runs `ghcr.io/freaxnx01/flowhub:0.9.0` with
      `Ai__OpenRouter__Model=anthropic/claude-sonnet-5` (`.env.bak-20260921-…`).
      Verified live: `trace.kind: Ai`, `model: anthropic/claude-sonnet-5`, entities
      round-tripping (`{quote, author}` off a Goethe line), zero 4xx/5xx and zero keyword
      fallbacks in the logs. Anthropic was never reachable before this — a
      `Dictionary<string,string>` made Microsoft.Extensions.AI emit `additionalProperties`
      as a schema object, which Anthropic rejects with 400.

- [ ] **Accuracy moved 2/10 → 4/10, and it is not a clean win — read before replaying.**
      Full table in `~/flowhub-capture-run/CONVENTIONS.md` under *Re-score on Claude
      Sonnet 5*. Claude wins every *semantic* case llama missed (action verb → task,
      person marker → scoped project, repo name → Bridge, quote → Zitate). But it
      **over-applies "a URL means read-later"** — two captures llama filed correctly
      (Movies, Restaurants) now go to Wallabag — and it returns **no skill at all** on
      terse captures (`budget Unterhalt 1500`, `Sandra … besuchen`), which become Orphans.
      The glossary was verified loaded, so this is model behaviour, not config.
      **Next:** build fixtures for the URL-exception and terse-capture cases before the
      backlog is replayed on any model; the existing ten no longer cover what breaks.

- [ ] **#126** — `payload.Tags` is unguarded on the main classification path; a null from
      the model NREs *outside* the try, so it never reaches the keyword fallback. Found
      reviewing PR #119, predates it.

- [x] **~~Self-fix~~ — armed (PR #127).** `self-fix: true`, `self-fix-max-iterations: 1`.
      Capped at 1 because the review job's `timeout-minutes: 10` is hardcoded upstream and
      the step inherits it; #111's review took 1m49s, and a fix pass here is a full .NET
      build+test. Only helps the *next* dispatch — self-fix runs inside the original run.

- [ ] **The generated CHANGELOG drops anything committed as `chore:`.** v0.9.0 has no
      `### Security` section, because the `JsonRequired` change on
      `CapturePreviewResponse.sensitivity` was committed as a chore. git-cliff maps by
      commit type, and hand edits do not survive the next `--tag` regeneration. It is
      recorded in `RELEASENOTES.md` instead. Worth remembering when committing anything
      security-relevant: the commit *type* decides whether it is ever seen.

## Session 2026-09-18 (#103 shipped; AI provider outage) — resolved, one blocker filed

- [x] **~~#103 operator visibility~~ — merged (PR #109, `804bd99`).** Implemented inline
      rather than by the pipeline: the first dispatch died at 81/80 turns ($6.65, nothing
      pushed) because `classify-turns.sh` sizes by task count in coarse tiers — **≥6 → 160,
      ≥4 → 120, ≥2 → 80** — so a 3-task plan got 80, and folding D10 in added work inside
      the same tier. `Withheld` now has 🙊, a badge label and a dashboard count;
      `CapturePreviewResponse.sensitivity` is `[JsonRequired]`; reactions are skill-coded
      (👨‍💻 Bridge, ✍ Vikunja, 👀 Wallabag, 👌 Paperless, 👍 unknown) with 🫡 on ingest.

- [x] **~~AI provider outage~~ — resolved 2026-09-18. It was never the credit.**
      Two captures parked as `Withheld` with `sensitivity screen unavailable —
      ClientResultException`. The screen is fail-closed and total, so a provider failure
      parks *everything*; nothing routed for hours, and it was silent because #103 had not
      shipped yet. Cause: OpenRouter's upstream returned **429
      `meta-llama/llama-3.1-70b-instruct is temporarily rate-limited`**. The key was
      healthy throughout (`is_free_tier: false`, 29.92/30 remaining, $0.08 used monthly),
      so loading credit changed nothing.
      **CT 136 now runs `Ai__OpenRouter__Model=meta-llama/llama-3.3-70b-instruct`**
      (`.env.bak-20260918-192139`). Verified via `/api/v1/captures/preview`:
      `Batterien kaufen` → Vikunja `Kaufen`, both Ausflüge links → Wallabag with titles,
      the synthesised medication note → `Sensitive` with no target, all `trace.kind: Ai`,
      and zero 4xx/5xx or keyword fallbacks in the logs since.

- [ ] **Re-send the two Ausflüge links** (`spieleland.de`, `affenberg-salem.de`). Their
      captures are still `Withheld`, and `POST /captures/{id}/retry` returns **409** —
      `Withheld` is excluded from `RetryableStages` by #93's AC. They classify cleanly now;
      they just have to come in again from Telegram.

- [ ] **#111 — the classifier's schema blocks Claude.** `AiClassificationResponse.Entities`
      is a `Dictionary<string, string>?`, which Microsoft.Extensions.AI renders as
      `additionalProperties: {"type":"string"}`. Anthropic requires `false` and returns
      **400**, so `anthropic/*` models silently degrade every capture to the keyword
      classifier — the screen keeps working (no dictionary in its DTO), which makes it look
      like bad classification rather than a provider rejection. Probed construct by
      construct: plain schema 200, `[AllowedValues]` enum 200, the dictionary 400.
      **Fix this and Claude is a one-line env change** — the most direct lever on #92's
      2–3/10 accuracy and #97's coin flip.

- [ ] **#110 — a provider failure parks a capture permanently.** `Unsure` conflates "the
      model judged it uncertain" (park, correctly) with "the provider was unreachable"
      (should back off and retry). Today's two captures were destroyed by a transient 429
      and cannot be recovered. Higher priority than the rest of #103's follow-ups: it loses
      captures rather than hiding them.

- [ ] **Decide: purge `.claude/handoff-main.md` from history?** It was swept into the #103
      branch by a `git add -A` and merged in #109, then untracked in `014ade3` and ignored
      in `eb2487f` (`.claude/handoff-*.md`, scoped so `.claude/commands/` stays tracked).
      No credentials — the bot token was already rotated and the Passbolt id is an opaque
      reference — but it carries homelab topology (container ids, internal hostnames, an
      `.env` path) of the kind this repo's history was deliberately redacted of. The blob
      is still reachable through the merged commit; removing it means a history rewrite and
      a force-push to protected `main`.

## Session 2026-09-17 (#93 sensitive-capture parking) — shipped and deployed (v0.8.0)

- [x] **~~Cut a release and redeploy CT 136~~ — done 2026-09-17, the guard IS live.**
      `v0.8.0` released and deployed; CT 136 runs `ghcr.io/freaxnx01/flowhub:0.8.0`,
      healthy. Migrations ran clean (`No migrations were applied` — `Stage` persists as a
      string, so #93 needs no schema change). `.env` backed up to `.env.bak-20260917-195459`;
      `0.7.1` images retained locally for rollback.
      **Verified against the live preview endpoint:** a synthesised medication note returns
      `Sensitivity: Sensitive`, reason `health detail about a named person`, and *no
      proposed target*; `Batterien kaufen` → `Safe` → Vikunja `Kaufen`; a URL → `Safe` →
      Wallabag. **Emptying the Telegram chat and replaying an export is now safe.**
      Caveat: a `Withheld` capture is still invisible on every operator surface until #103
      ships, so parks are silent — check the API, not the dashboard.
- [ ] **Dispatch #103** (operator visibility for `Withheld`, CHANGELOG, dead code). It was
      split out of #93 because the combined plan exceeded the 160-turn ceiling. Its
      dependency is now on `main`, so it is unblocked — it carries its own dependency
      check and will stop if `LifecycleStage.Withheld` is missing.
- [ ] **Decide whether `MAX_ATTEMPTS` stays at 3.** #104 raised it 2 → 3 repo-wide so #93
      could be redispatched. #93 was then finished by hand instead, so the raise is
      unused. **In flight: PR #108 restores the default of 2** — merge it to close this.
- [ ] **agent-workflow#366** — a local session and the pipeline both implemented #93 at
      once (~$2 wasted, two competing PRs). `agent-implement.yml:378` already serialises
      pipeline-vs-pipeline; the gap is a claim visible *outside* Actions. Needs enrichment.

### Notes worth keeping

- **Four of five failures this session were spec/plan defects, not agent code.** Scoping
  the guard by pipeline position rather than destination audience; a test that passed
  because the consumer never constructed; an "apply on top of the existing
  implementation" instruction that is false on a fresh branch; and task headings that
  `classify-turns.sh` does not count. The pipeline reads these documents literally.
- **`classify-turns.sh` counts `^### Task` headings only** and caps at **160 turns** for
  six or more tasks. A plan beyond ~6 tasks must be split at authoring time, and a
  heading that does not match that pattern is silently not budgeted for.
- **A closed PR's branch is auto-deleted here**, but its commits stay reachable via
  `git fetch origin refs/pull/<N>/head` — that is how #99's work was recovered.


- [ ] **Re-add GitHub Actions secrets** the workflows need:
  - `EMBEDDINGS__APIKEY` — embeddings provider key (semantic search)
  - `Ai__Anthropic__ApiKey` / `Ai__OpenRouter__ApiKey` — LLM provider keys
  - (GHCR auth uses the built-in `GITHUB_TOKEN` — nothing to add)
  - Set via: `gh secret set <NAME> --repo freaxnx01/flowhub`
- [ ] (Optional) Deep de-CAS pass of the ADRs + `docs/spec/*` — strip "Block N /
      Nachbereitung" provenance and dead `vault/`/`docs/insights/` links, if you don't
      want them kept as historical record.
- [ ] (Cosmetic) Bump pinned GitHub Actions off Node-20 (checkout@v4, setup-dotnet@v4,
      docker/*@v3–5, action-gh-release@v2) when convenient — deprecation warning only.
- [ ] **Make the UI nicer with Claude Design** — polish the Blazor dashboard's visual
      design (typography, layout, cards) via the `frontend-design` skill.
      Take design inspiration from the Moon Lander game?
- [ ] **Wire Skills routing to the real homelab services** (route classified captures out
      instead of `Unhandled`). Set env on CT 136 (`~/mydocker/.env` via `pct push`) +
      compose refs, then recreate `flowhub`. Targets (NOT the demo.* ones):
  - **Vikunja** → `https://todo.home.freaxnx01.ch` — *ready*: `Skills__Vikunja__BaseUrl`,
    `Skills__Vikunja__ApiToken` (Passbolt `76a43ce8` "Vikunja API Token 'Task management'",
    write-scope verified), `Skills__Vikunja__FallbackProject=Inbox`,
    `Skills__Vikunja__FallbackProjectId=2` (the real Inbox project id).
  - **Wallabag** → `https://read-later.home.freaxnx01.ch` — *needs provisioning first*
    (no creds in Passbolt; local-auth instance, CT 126). Create a dedicated user +
    OAuth2 client in the `wallabag` container: `php bin/console --env=prod fos:user:create …`
    and `… fos:oauth-server:create-client --grant-type=password --grant-type=refresh_token`.
    Then set `Skills__Wallabag__{BaseUrl,ClientId,ClientSecret,Username,Password}` (password
    grant — FlowHub's `WallabagTokenProvider` mints/refreshes the token itself) and store the
    client creds in Passbolt. Verify by submitting a capture that classifies to each skill.
    **Status 2026-09-17:** decided to provision *production* creds (not the
    `flowhub-test-services` instances). **Blocked on me:** create the user + OAuth2 client
    on CT 126 and store the four values in Passbolt, then Claude sets the env on CT 136 and
    redeploys `flowhub` only. Read-later is ~24.6% of capture traffic, currently `Unhandled`.

- [ ] **Paperless — deliberately left unconfigured until #96 is fixed.** An attachment
      currently overrides the capture's text and forces Paperless
      (`CaptureEnrichmentConsumer.cs:53`, moved by #93 — the sensitivity screen now runs
      *after* that branch per spec D7, so #96 is unchanged), so configuring it now would turn today's
      visible stalls into silently misfiled documents. Only ~6.9% of traffic, so waiting is
      cheap. Keys when it's time: `Skills__Paperless__{BaseUrl,ApiToken}` (`dms.home`).
- [ ] **Confirm the rotated Telegram bot token reached CT 136.** The token was rotated on
      2026-09-17 after it had been logged in plaintext since 2026-09-15 (fixed in v0.7.1).
      As of the v0.7.1 deploy check the token in `/home/admin/mydocker/.env` still
      authenticated against `getUpdates` — if BotFather issued a new one, update
      `TELEGRAM_BOT_TOKEN` there and `docker compose up -d flowhub`, else polling 401s.
- [ ] **Set up a second Telegram bot, `flowhub-test`, for testing.** `getUpdates` is
      exclusive — one token can only be polled by one consumer — so every experiment
      against the live bot competes with production capture and with
      `/flowhub-triage`'s drain. A dedicated test bot gives a throwaway chat to push
      probe captures through without polluting the real corpus or the ledger. Create it
      via BotFather, store the token in Passbolt, and point a local/test FlowHub at it
      rather than CT 136.

## Done (2026-07-07 / 08)

- History split from `FlowHub-CAS-AISE` (CAS scrubbed from tree + history).
- Full-history PII/homelab redaction (address, DOB, internal IPs, host, admin email).
- Bus defaults to in-memory; RabbitMQ opt-in overlay.
- GHCR web image renamed `flowhub-web` → `flowhub`.
- Migrations image renamed `flowhub-migrations` → `flowhub-db-migrations` (fresh package
  the repo owns; the old name is held by the CAS archive).
- CI `build-and-test` green on `main`.
- Branch protection on `main` (require `build-and-test` for collaborators/bots, no
  force-push/deletions; `enforce_admins: false`, so the owner can push docs directly).
- **Release green**: `v0.1.0` tag → `ghcr.io/freaxnx01/flowhub` + `ghcr.io/freaxnx01/flowhub-db-migrations`
  published, and the `v0.1.0` GitHub Release created.
- Roadmap issues #1–#4 opened.
