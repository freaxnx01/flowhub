# FlowHub routing review — 2026-10-05 run (in progress)

State of the `/flowhub-review` run started 2026-10-05, interrupted mid-interview for a root-cause
investigation. Resume from here; the ledger (`docs/ai-notes/routing-review-ledger.md`) has **no
entries from this run yet**, and no 🤨 reactions have been set for it.

Production: CT 136 runs `ghcr.io/freaxnx01/flowhub:0.10.0` (container recreated 2026-09-29 21:01).

## The 10 Captures under review (newest first)

| # | id | when | content | got | verdict so far |
|---|---|---|---|---|---|
| 1 | `b9000998-6f3e-4cff-a071-509f6009ea4b` | 10-05 21:22 | `rhyflitzer: Hallenbad Sissila: Rutschbahn aussen am Gebäude fehlt` | Orphan, no trace | **wrong** → issue on `game-rhyflitzer` (operator-confirmed). Root cause below. Telegram msg 434 |
| 2 | `ffb8ebab-…` | 10-04 15:17 | `hlab: Agdev ubuntu: do-release-upgrade` | Vikunja IT Homelab #2256 | looks correct, not yet confirmed |
| 3 | `01b04b40-9de6-4f98-922c-e7d93e32da20` | 10-04 14:08 | photo-432 (Authentik passkey QR dialog), caption `Add Passkey for authentik` | Paperless (unwired) | **wrong** → Vikunja *IT Homelab* (operator-confirmed). msg 432 |
| 4 | `5f31baf6-…` | 10-04 09:52 | `Bernaqua \| Erlebnisbad \| Fitness \| Wellness` + share link | Wallabag (unwired) | **ask** — outing idea (#118)? msg 431 |
| 5 | `fb1fc387-…` | 10-03 17:56 | `https://www.dvdone.ch/details.php?id=44430` | Vikunja Kaufen #2254 | looks correct (shop link → Kaufen), confirm in ✓ batch |
| 6 | `c877f619-741a-4b6b-92d6-68dd74b28563` | 10-02 15:21 | photo-429: LEGO Creator 31382 "Playful Puppy", 34.90 CHF | Paperless (unwired) | **ask** — the operator interrupted at this question. Recommend Kaufen. msg 429 |
| 7 | `98d82525-…` | 10-02 11:54 | `Rhyflitzer: - farben für fahrzeuge …` (1 bullet) | Bridge → game-rhyflitzer#25 | looks correct, confirm in ✓ batch |
| 8 | `76dd309a-b298-4b4b-be80-bbc38f59e288` | 10-02 11:30 | `Rhyflitzer: - B = blick zurück - Rückspiegel möglich? - neuer kameramodus … - weitere jump orte …` | Bridge → **one** issue game-rhyflitzer#24 | **wrong by operator rule** (one issue per top-level `-`, see #133) → split into 4, close #24. msg 427 |
| 9 | `e1c7a339-…` | 10-02 09:23 | photo-426: screenshot of a private chat (video message) | Paperless (unwired) | **ask** — can't call. Do not put names or details in the public ledger. msg 426 |
| 10 | `653fccc1-…` | 09-29 09:39 | photo-420 | Paperless (unwired) | **file lost**: the 2026/09 attachment directory vanished when the container was recreated on 09-29. That is #117 in practice; all September photos are gone. msg 420 |

## Root cause found for #1, and very likely for #134 and #115 as a whole

OTel spans in `docker logs flowhub` around 2026-10-05T19:22Z:

- Sensitivity screen: `max_tokens 200`, finish `stop`, 103 output tokens, **85 reasoning**.
- Classification: `max_tokens 300` (`Ai:MaxOutputTokens`, default 300 in
  `source/FlowHub.AI/AiServiceCollectionExtensions.cs:121`), finish **`length`**, 300 output tokens, **299 reasoning**.
  The JSON is truncated, `TryGetResult` fails, `AiClassifier` throws `schema_violation`, the keyword fallback matches nothing, and the Capture becomes an Orphan with no trace.

`anthropic/claude-sonnet-5` via OpenRouter reasons by default, and reasoning tokens count against
`max_tokens`. How much it reasons varies per call, which explains the intermittent Orphans on
identical-shaped notes. Docker logs reset on container recreate, so only the post-09-29 case is verifiable.

## Proposed actions (awaiting operator decisions)

1. **Immediate mitigation (prod write, operator must run or allow):** `Ai__MaxOutputTokens=1500` in
   `/home/admin/mydocker/.env` on CT 136, then `docker compose up -d flowhub`. The sensitivity screen's 200 is hard-coded and stays at risk.
2. **Code fix issue:** disable reasoning for routing calls (OpenRouter `reasoning: {enabled:false}` via
   `ChatOptions.AdditionalProperties`) or reserve a budget for it. Report `finish_reason=length` as `truncated` and not
   `schema_violation`. An AI failure should end `Unhandled` (retryable), not `Orphan`. Post the diagnosis on #134 and #115.
3. **Skill change (`.ai/skills/flowhub-review.md`):** for every flagged Capture that is Orphan, Unhandled or has no trace, read the
   OTel spans around `CreatedAt` (`gen_ai.response.finish_reasons`, `gen_ai.usage.reasoning.output_tokens` vs
   `gen_ai.request.max_tokens`, HTTP status codes, `fell back (reason=…)`) and write the root cause into the ledger `why`.
   Note that docker logs disappear on container recreate, so review promptly.
4. **Security (discovery, out of scope):** the OTel console exporter writes `url.full` including the Telegram bot token
   (`/bot<token>/getUpdates`) to `docker logs`. Needs an issue (redact `url.full`) and a decision on rotating the token.
   Never paste the token anywhere.
5. **Provider question (operator asked):** why OpenRouter rather than the Claude subscription? Answered: a Pro/Max subscription covers
   personal use in claude.ai/Claude Code, not a server backend, so FlowHub needs API billing. Open: stay on OpenRouter, or
   open an issue to switch to a direct Anthropic API key (direct thinking control, reliable prompt caching #79).

## Remaining review steps

- Interview (one `AskUserQuestion` per capture): #6, #4, #9, then the ✓ batch (#2, #5, #7). #10 only needs a ledger note.
- Ledger entries for every flagged row, 🤨 reactions (Step 5.5; Telegram ids in the table).
- Actions already agreed in principle by precedent: create a `game-rhyflitzer` issue for #1, split #24 into 4 issues and close it (#8),
  and the operator decides whether to create the Vikunja IT Homelab task for #3 (the skill itself never writes to Vikunja).
- Leftover question from the 2026-09-28 run: move the misrouted Poseidonia items (Vikunja 2249/2250) to ideas-lab and put the
  puzzle and Supertoskana on Kaufen? Close #132 as done by #136?
