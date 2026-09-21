# FlowHub routing review ledger

Append-only. One entry per Capture the deployed instance classified or routed wrongly,
recorded by `/flowhub-review`. Entries are never rewritten or deleted — only their
`status` line changes (`open` · `fixed <ref>` · `issue <url>` · `parked <reason>`).

Source of truth for the observed values: the `Captures` table on CT 136 (read-only).

---

## 2026-09-18 — a7e2212e-7007-4c61-a250-128f3edf59a2  (Telegram, 2026-09-16 15:16)
- **content:** `spieler 1 hat als letzte karte eine sieben gelegt. SPIELER 2 2 karten aufgenommen darunter eine 7. Konnte nicht gestackt werden, spiel war vorbei.`
- **got:** skill `Paperless`, stage `Unhandled`, no target, **no classifier trace at all**, `FailureReason: no integration registered for skill 'Paperless'`
- **expected:** Bridge → issue on the relevant game repo (it is a bug report about card-stacking rules)
- **why:** a German description of a card-game round is neither a document nor a Paperless candidate. Two distinct defects in one row: the wrong skill was chosen, **and** no `ClassifierTrace` was written, so there is no record of which classifier ran. Note the Capture also carries no repo alias, so Bridge alias matching alone could not have reached the right repo — inferring the project from game vocabulary is the open design question.
- **correction (2026-09-20):** not a misclassification. The Capture carries attachment `photo-371.jpg`, and `CaptureEnrichmentConsumer` returns to Paperless on `HasAttachment` **before** calling `ClassifyAsync` — no classifier ran, so the null trace is expected, not a telemetry bug. The open question is whether a caption-bearing attachment should be classified on its caption. Issue #113 rewritten accordingly.
- **also (2026-09-20):** this Capture's attachment file `2026/09/bd5a4c89….jpg` is **no longer on disk** — `/app/App_Data/uploads` is not a volume, so attachments die on container recreate while their rows survive. Filed separately as issue #117.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/113

## 2026-09-18 — aa48fd56-9458-4c95-a4be-7e131566a3d1  (Telegram, 2026-09-03 20:08)
- **content:** `bridge mcp from outside LAN?`
- **got:** skill `Bridge` (correct), stage `Unhandled`, no target, `Ai` 9610 ms, `FailureReason: exhausted retries: System.Net.Http.HttpRequestException: Response status code does not indicate success: 404 (Not Found).`
- **expected:** one issue on `freaxnx01/bridge`, created once
- **why:** classification was right every time; **routing** is the defect, and it is two defects. (1) **No dedup:** the identical content produced `bridge#285` (09-03 20:27) and `bridge#289` (09-14 21:55) — two issues for one intent. (2) **404 retry path:** this run burned its retries against a 404 (9.6 s) and ended `Unhandled` instead of failing fast with a useful reason; a 404 is not a transient error worth retrying.
- **related:** `8a260f0a-…` (#285), `3048dae5-…` (#289) — correct outcomes, listed as the duplicate evidence
- **status:** issue https://github.com/freaxnx01/flowhub/issues/114

## 2026-09-18 — 99fa282d-5823-4db9-80a7-1980578b65b2  (Telegram, 2026-09-01 22:18)
- **content:** `bridge mcp from outside LAN?`
- **got:** stage `Orphan`, no `MatchedSkill`, **no classifier trace**, `FailureReason: no skill matched during classification`
- **expected:** Bridge → issue on `freaxnx01/bridge`
- **why:** byte-identical content classified as `Bridge` on four other occasions. Classification is **non-deterministic** for the same input, and the failing run again wrote no `ClassifierTrace`, so there is nothing recorded to explain the divergence.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/115

## 2026-09-20 — 089d35b8-8c38-4e3d-bdba-ee5ac5f7dd8a  (Telegram, 2026-09-18 17:08)
- **content:** `https://www.affenberg-salem.de/`
- **got:** stage `Withheld`, no `MatchedSkill`, no classifier trace, `FailureReason: sensitivity screen unavailable — ClientResultException`
- **expected:** an **Ausflugsidee** (outing idea for Juliska), not read-later. Confirmed by the operator 2026-09-20. No FlowHub Skill routes Ausflug ideas today — the nearest existing destination is the "Ideen Ausflüge" calendar the `juliska-ausflug` CC-skill writes to. Nothing about the link is sensitive either way.
- **why:** the sensitivity screen could not run and the pipeline fails closed, which is right in itself. The defect is that the capture is now **unrecoverable**: `Withheld` is excluded from `RetryableStages` by #93's AC, so `POST /captures/{id}/retry` returns 409, and re-sending from Telegram by hand is the only way back. Screen-unavailability and a real `Sensitive` verdict are treated identically even though only the latter is a judgement about the content. Provider root cause (OpenRouter 429 on llama-3.1) already resolved 2026-09-18 — see `TODO.md`; this entry is about the recovery gap only.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/116 (recovery gap) + #118 (no Ausflug destination)

## 2026-09-20 — 92bb2abe-581a-4196-ac39-eada5bd9d5a3  (Telegram, 2026-09-18 17:08)
- **content:** `https://spieleland.de/`
- **got:** identical to `089d35b8-…` above — `Withheld`, `sensitivity screen unavailable — ClientResultException`
- **expected:** an **Ausflugsidee**, same as `089d35b8-…` above — not Wallabag.
- **why:** same defect, same minute; recorded separately because the ledger tracks Captures, not defects. Both links still need re-sending by hand.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/116 (recovery gap) + #118 (no Ausflug destination)

## 2026-09-20 — ca83b848-9b49-4efa-a6a2-3000ad593cf3, e1166e5d-…, 623ca0b5-…  (Telegram, 2026-09-19)
- **content:** photos `photo-376.jpg` (**Zeiniger Märt**, Sa 26.09.2026), `photo-377.jpg` (**Graben Aarau**, 24.–27.09.2026), `photo-379.jpg` (**Dschungelbuch Musical**, 20.11., Emmen)
- **got:** all three `MatchedSkill: Paperless`, stage `Unhandled`, no classifier trace (attachment branch — expected, see #113)
- **expected:** Ausflugsideen. Operator-confirmed 2026-09-20.
- **why:** two defects compound here. The meaning is **in the pixels** — the capture text is just the filename — so even classifying on the caption would not help (#113). And there is no destination for an outing idea even if it were classified correctly (#118). The fourth photo of the batch, `photo-378.jpg` (c't Vaultwarden article), is genuinely archive material — Paperless is right for that one.
- **recorded as one entry** by exception: same batch, same defect pair, identical disposition.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/113 + https://github.com/freaxnx01/flowhub/issues/118

## 2026-09-20 — 3bf69421-9984-4bd0-96c5-735c64f7a384  (Telegram, 2026-09-20 09:19)
- **content:** `23 x 23 x 13`
- **got:** stage `Orphan`, no `MatchedSkill`, no classifier trace, `FailureReason: no skill matched during classification`
- **expected:** Vikunja — measurements of something to act on (buy / build / fit)
- **why:** operator-confirmed 2026-09-20. The review pass initially read `Orphan` as the correct outcome for a fragment; it is not. A bare measurement is a note the operator needs back, not noise. Nothing in the content marks it as a task, so this is a genuine hard case for the classifier rather than an obvious miss — worth deciding whether an unclassifiable-but-deliberate Capture should default to the Inbox instead of `Orphan`.
- **status:** open

## 2026-09-20 — d3db4580-52ab-41d0-8011-3c8cccc15fb3  (Telegram, 2026-09-19 08:28)
- **content:** photo `photo-378.jpg` — c't article "Sicher verwahrt — Passwortsafe Vaultwarden selbst hosten"
- **got:** `MatchedSkill: Paperless`, stage `Unhandled`, no classifier trace (attachment branch)
- **expected:** a **homelab to-do** — the article is a thing to try (self-host Vaultwarden), not a document to archive
- **why:** operator-confirmed 2026-09-20, correcting this review's own earlier call. The first pass marked this one "genuinely archive material" and excluded it from the batch entry above — wrong. That makes it **four of four** images from 2026-09-19 misrouted to Paperless, not three. Same root defect as #113: the meaning is in the picture, and nothing reads it.
- **status:** open

## 2026-09-20 — d482fd28-ac9c-4441-a8a1-8d85bc8d6ac6  (Telegram, 2026-09-19 13:59)
- **content:** `SOFTEC AG` + `https://www.softec.ch/`
- **got:** `MatchedSkill: Wallabag`, stage `Unhandled`, `Ai` 1580 ms, `FailureReason: no integration registered for skill 'Wallabag'`
- **expected:** something **job-related**, not read-later. Operator-confirmed 2026-09-20.
- **why:** the review pass filed this under "config gap — classification fine", which was wrong: the unwired Skill masked a misclassification. A company link captured for work reasons is not an article. FlowHub has no job-related destination, so this is a **missing-destination gap** in the same family as #118 (Ausflugsideen), not a rule that can be patched.
- **status:** open

## 2026-09-20 — 6c8e53e1-63b4-4a3f-ac6b-57954f96f780  (Telegram, 2026-09-20 09:52)
- **content:** `Game idea: GeoGuessr Clone / - Kindermodus 10 Jahre / - Länder ausschliessen können wie Ru…` (title: *GeoGuessr Clone Concept*)
- **got:** `MatchedSkill: Bridge`, stage `Completed`, `Ai` 5103 ms → appended to **`freaxnx01/game-geography-quiz/ideas.md`**
- **expected:** **`freaxnx01/ideas-lab/ideas.md`**. Operator rule, stated 2026-09-20: **an idea — game idea or idea in general — always goes to `ideas-lab`**, never to a topic-matched repo.
- **why:** the classifier inferred a destination repo from the idea's subject matter (geography quiz → `game-geography-quiz`) instead of applying the fixed rule. Note the sibling Capture `93310283-…` (*Tool Hub Concept*, 13:21 the same day) **did** land in `ideas-lab/ideas.md` — so the behaviour is inconsistent, not uniformly wrong, which points at prompt/inference rather than a hard rule in code. The review pass marked both `✓` without knowing the rule existed.
- **status:** fixed — `RepoResolver` now forces `ideas-lab` for every `BridgeAction.Idea` (test `ResolveAsync_ModelPicksARepoForAnIdea_TargetsIdeasLabAnyway`). Shipped in **v0.9.0** (tag `13e7770`, image `ghcr.io/freaxnx01/flowhub:0.9.0`); CT 136 recreated 2026-09-21 13:54 UTC and now runs it.

## 2026-09-21 — bd3ee30d-…  (Telegram, 2026-09-21 20:30:21)
- **content:** `Tschau Sepp Bug Report:` — 23 characters, a header and nothing else
- **got:** `MatchedSkill: Bridge`, stage `Completed`, `Ai` claude-sonnet-5 9536 ms → created **`freaxnx01/game-tschau-sepp#31`**, empty body, generic title
- **expected:** no issue at all. The operator hit Enter while trying to insert a newline; Telegram sent the header early and the real report followed 14 s later as `2569cd2f-…` (#32) and 30 s later as `0ac46bc2-…` (#33), both good issues.
- **why:** the send was operator error, but creating a forge issue out of a content-free capture is not. A capture whose entire content is a colon-terminated header carries nothing to act on, and the result is a junk issue in a real repo that nobody wrote on purpose. A minimum-content guard before Bridge creates an issue would have parked it instead. Worth noting the fix is only partly about length: the give-away is that the text is a title with no body, which the classifier itself recognised — it filled `Title` and left the body empty.
- **related:** #120 — with debug replies and a correction button, this would have been visible and reversible within seconds instead of surfacing in a review two hours later.
- **status:** fixed — `CaptureEnrichmentConsumer` now parks a colon-terminated single-line capture as `Unhandled` before classifying (tests `Consume_HeaderWithNoBody_MarksUnhandledWithoutClassifying` + `Consume_ShortButSubstantiveContent_IsStillClassified`). `game-tschau-sepp#31` closed.

## 2026-09-21 — 787532e9-…  (Telegram, 2026-09-20 17:13)
- **content:** `Baldrian`
- **got:** stage `Withheld`, no `MatchedSkill`, no classifier trace, `FailureReason: health detail about a named person`
- **expected:** Vikunja project **`Einkaufen Apo`** (Apotheke) — a shopping item. Operator-confirmed 2026-09-21.
- **why:** two defects. (1) The **sensitivity screen over-triggers**: a single word naming a herbal remedy, with no person in it at all, was read as "health detail about a named person". The screen is fail-closed and terminal, so an over-trigger costs the capture entirely — it is still stuck, since `Withheld` cannot be retried (#116). (2) Even unblocked, there is no evidence FlowHub knows an `Einkaufen Apo` destination; the `Skills` table holds Articles, Belege, Books, Knowledge, Movies, Zitate — see #123 on what a Skill even is.
- **status:** open
