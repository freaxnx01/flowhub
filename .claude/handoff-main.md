# Handoff — FlowHub daily Telegram capture

**Written:** 2026-09-17 · **Resume with:** `/pickup`

## Where we are

**Daily Telegram capture is LIVE and working.** FlowHub `0.7.0` on CT 136 polls
`@flowhub_intelliflow_bot`; allow-listed messages become Captures and route on their own.

Real traffic, 2026-09-16:

| Capture | Skill | Outcome |
|---|---|---|
| `Wipfelkratzer: maus Objekte verschieben…` | Bridge | game-wipfelkratzer#64 ✅ |
| `Wipfelkratzer: grösse von objekten…` | Vikunja | task 2244 — **should have been Bridge** |
| Tschau Sepp rule bug + photo | Paperless | **wrong** — attachment overrode the text |

Registered skills: **Bridge ✅, Vikunja ✅**. Wallabag and Paperless are **not** configured.

## Open decision (blocking nothing, but asked and unanswered)

Wallabag + Paperless are up (`read-later.home`, `dms.home`, both via `.162`; Wallabag is CT 126)
but **Passbolt only has credentials for the `flowhub-test-services` instances**. Choose:

1. Provision production creds (Wallabag OAuth2 client + user on CT 126; Paperless API token)
2. Point FlowHub at test-services
3. Leave unconfigured during the trial — read-later (~24.6%) and scans (~6.9%) land `Unhandled`

Note: leaving Paperless unconfigured is what *revealed* the attachment bug, by stalling the
capture instead of silently filing it as a document.

## Open issues from this work

- ~~**#95**~~ — Telegram bot token printed in container logs on every poll. **Released in
  v0.7.1 and deployed to CT 136 (2026-09-17)**; `api.telegram.org` no longer appears in the
  logs. The PR was red because the explanatory `_comment_HttpClient` key inside
  `Logging:LogLevel` was parsed as a logger category and crashed host startup — removed.
- **#96** — an attachment overrides the capture's text and forces Paperless
  (`CaptureEnrichmentConsumer.cs:50-54`). This is why the Tschau Sepp report was misrouted.
- **#97** — Bridge-vs-Vikunja is a coin flip at `Temperature = 0.2`, and the classifier never
  learns that a leading token like `Wipfelkratzer` is a catalogue repo.
- **#39** — vision path. Adjacent to #96 but independently fixable.
- **#57** — pre-preview follow-ups incl. `ClassifierTrace` token undercount.
- **#34** — `CaptureRetryEndpoint` drops `HasAttachment`.
- **game-tschau-sepp#27** — the rule bug, filed by hand.

## The user's plan (agreed, in order)

1. Silence HttpClient logging — **done and deployed (v0.7.1)**
2. Configure Wallabag + Paperless — **blocked on the credentials decision above**
3. Fresh chat export → verify it opens → **then** empty the chat (one-way)
4. Rebuild `~/flowhub-capture-run/` ledger from the new export, carrying the 9 resolved ids
5. Run daily capture for a few days
6. Build a CC skill to review recent routings — design it *after* seeing real traffic
7. Filter outdated captures, process the remainder

## Things that will bite if forgotten

- **`getUpdates` is exclusive.** FlowHub now polls it, so `/flowhub-triage`'s Step 4a drain is
  dead. Do not run both.
- **Do not tune the classifier by re-routing captures.** Measure offline against the hand
  labels in `docs/ai-notes/2026-08-25-telegram-capture-taxonomy.md`. Routing creates real
  issues and tasks; labelling is free and repeatable. This is how #64 was validated.
- **`~/flowhub-capture-run/`** holds 230 rows (1 done, 8 skipped) and will overlap a new
  export almost entirely. Decide merge-vs-rebuild before re-processing anything.
- **API auth for scripts:** Authentik `client_credentials` against
  `https://auth.home.freaxnx01.ch/application/o/token/` using the OIDC app in Passbolt
  `826f4d09` → Bearer token for `https://flowhub.home.freaxnx01.ch/api/v1/*`.
- **`createdAt` is UTC** (`+00:00`). Local is CEST, +2.
- **CT 136 deploys:** `docker compose up -d flowhub` only — a full `up -d` tries to recreate
  traefik and fails on a stale secrets path. The CT's `docker-compose.yml` has **diverged**
  from the infra repo (Vikunja + Telegram vars exist only on the CT).
- **`mydocker-compose` is 6+ commits ahead of origin** with unrelated work; my flowhub commit
  `493b1fc` is unpushed there.

## Next action

Answer the Wallabag/Paperless credentials question, then step 3 (fresh export).
Bot token rotation: **done** (2026-09-17). It had sat in CT 136's logs from 2026-09-15
until the v0.7.1 deploy. Note: as of the deploy check, the token in CT 136's env still
authenticates against `getUpdates` — if the rotation replaced it in BotFather, update
`TELEGRAM_BOT_TOKEN` in `/home/admin/mydocker/.env` and restart `flowhub`, or polling
will start failing with 401.
