# TODO — post-split plumbing

Open operational items for the freshly-split `flowhub` product repo.

## Session 2026-09-17 (#93 sensitive-capture parking) — shipped, but not yet deployed

- [ ] **Cut a release and redeploy CT 136 — the privacy guard is NOT live.** #93 merged to
      `main` (PR #105), but CT 136 runs `ghcr.io/freaxnx01/flowhub:0.7.1`, which predates
      it. Until a release ships and is redeployed, captures are still routed unscreened.
      **This gates emptying the Telegram chat and replaying any export** — that is the
      exact disclosure path #93 exists to close, and the survey found therapy notes, a
      child's care journal and a third party's profile in the backlog.
- [ ] **Dispatch #103** (operator visibility for `Withheld`, CHANGELOG, dead code). It was
      split out of #93 because the combined plan exceeded the 160-turn ceiling. Its
      dependency is now on `main`, so it is unblocked — it carries its own dependency
      check and will stop if `LifecycleStage.Withheld` is missing.
- [ ] **Decide whether `MAX_ATTEMPTS` stays at 3.** #104 raised it 2 → 3 repo-wide so #93
      could be redispatched. #93 was then finished by hand instead, so the raise is
      unused — revert to 2 if it was meant as a one-off.
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
