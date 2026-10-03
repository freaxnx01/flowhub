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

## 2026-09-27 — f019b003-f1db-4d1e-9925-b99b418dcf7a  (Telegram, 2026-09-27 12:50)
- **content:** `Kit racer: / - gas geben: space taste / - bei donuts machen: kamera nicht hin und her sondern fix / - gegen CPU: gegner in minimap anzeigen`
- **got:** stage `Unhandled`, no `MatchedSkill` persisted, no trace, `FailureReason: bridge candidate — repo undetermined` (log: `bridge action undetermined (alias=)` — alias is `""`, not null, so the stored reason disagrees with the log)
- **expected:** 3 Bridge issues on `freaxnx01/game-kit-racer`, one per `-` line. Operator-confirmed 2026-09-27.
- **why:** `Kit racer` → `game-kit-racer` is a straight name match the resolver missed. Operator rule, stated 2026-09-27: **each top-level `-` line in a note is its own topic → its own issue.**
- **status:** issues game-kit-racer#16, #17, #18 filed by hand; cause → evidence on flowhub#97 (header → repo name). Correction: the `alias=` log line is the fixed wording of `LogBridgeUndetermined`, not a stored/logged mismatch; the stored reason is right.

## 2026-09-27 — b7beee3c-5c92-4ed7-8b65-96c2ce6714bb  (Telegram, 2026-09-27 12:26)
- **content:** `Barrel blast physics engine von kit racer?`
- **got:** stage `Orphan`, no trace. Log: `AiClassifier fell back to keyword classifier (reason=schema_violation)`
- **expected:** Bridge issue on `freaxnx01/game-barrel-shooter` — Barrel blast is a barrel-shooter feature; reuse Kit Racer's physics engine there (ref https://github.com/isaac-mason/crashcat). Operator-confirmed 2026-09-27.
- **why:** the AI call threw `schema_violation`; the keyword fallback has no rule for it, so a real note became an Orphan with nothing recorded. The cross-repo reference (`kit racer` named, `barrel-shooter` meant) is a hard case even when the AI works.
- **status:** issue game-barrel-shooter#9 filed by hand; cause → https://github.com/freaxnx01/flowhub/issues/134

## 2026-09-27 — b5c5f8d7-a30d-48b4-ad32-dc258aa2fcbe  (Telegram, 2026-09-27 12:17)
- **content:** `Sky fury: / - vergrössern um faktor 1.5 / 2 / - modus unendliche viele bomben und unverwundbar / - zielhilfe bomben / - bombenteppich`
- **got:** stage `Orphan`, no trace. Log: `schema_violation` → keyword fallback
- **expected:** 4 Bridge issues on `freaxnx01/game-sky-fury`, one per `-` line. Operator-confirmed 2026-09-27.
- **why:** same `schema_violation` fallback as `b7beee3c-…`.
- **status:** issues game-sky-fury#2–#5 filed by hand; cause → https://github.com/freaxnx01/flowhub/issues/134 + #97

## 2026-09-27 — 9f25dcad-0efd-4370-8fc5-e6748a02f16f  (Telegram, 2026-09-27 12:14)
- **content:** `All browser games: / Add symbol to enter fullscreen`
- **got:** `Bridge`, `Completed`, Ai sonnet-5 10.2 s → `freaxnx01/ideas-lab/ideas.md`
- **expected:** Bridge issue on **`freaxnx01/freaxnx01.github.io`** (the games hub, https://github.freaxnx01.ch/games/). Operator rule, stated 2026-09-27: **issues that apply to all games go to `freaxnx01.github.io`.**
- **why:** the classifier treated a cross-game feature request as an idea. There is no rule mapping "all games" to the hub repo.
- **status:** issue freaxnx01.github.io#25 filed by hand; fixed in repo by https://github.com/freaxnx01/flowhub/pull/135 (CT 136 still runs 0.9.0 until redeployed). The ideas-lab/ideas.md entry it produced is still there.

## 2026-09-27 — 9a495a4a-bad0-41e7-a0f1-f27477f49ab8  (Telegram, 2026-09-27 11:53)
- **content:** `Wipfelkratzer / - Wohnungen anschreiben können: z. B. Musikzimmer / - Aussichtsturm: Bei Klick auf Tiere Text länger stehen lassen … / - wenn elster pool füllt macht es komisches geräusch … / - Hauptbutton für (Wald)karte`
- **got:** stage `Orphan`, no trace. Log: `schema_violation` → keyword fallback
- **expected:** 4 Bridge issues on `freaxnx01/game-wipfelkratzer`, one per `-` line. Operator-confirmed 2026-09-27.
- **why:** same `schema_violation` fallback. Captures of the same shape minutes earlier (`ded34ec6-…`, `66e160f2-…`) routed fine, so the fallback is intermittent.
- **status:** issues game-wipfelkratzer#103–#106 filed by hand; cause → https://github.com/freaxnx01/flowhub/issues/134

## 2026-09-27 — 272ffb23-da64-4e6e-ae44-19181d2c5cdd  (Telegram, 2026-09-27 11:46)
- **content:** `Wipfelkratzer / - Hineingehen: Fenster soll Blick nach Aussen freigeben`
- **got:** stage `Orphan`, no trace. Log: `schema_violation` → keyword fallback
- **expected:** 1 Bridge issue on `freaxnx01/game-wipfelkratzer`. Operator-confirmed 2026-09-27.
- **why:** same `schema_violation` fallback.
- **status:** issue game-wipfelkratzer#102 filed by hand; cause → https://github.com/freaxnx01/flowhub/issues/134

## 2026-09-27 — 66e160f2-1b93-4071-b2ad-797b3ae9b7d5  (Telegram, 2026-09-27 11:40)
- **content:** `Wipfelkratzer: / - Möbel um faktoren vergrößern, z.B. x1.5, x2 / - Tisch, Sofa in die Länge ziehen oder kürzen / - Harfe sieht komisch aus, auch keine Saiten`
- **got:** `Bridge`, `Completed` → **one** issue `game-wipfelkratzer#92` bundling all three lines
- **expected:** 3 issues on `game-wipfelkratzer`, one per `-` line. Operator rule, stated 2026-09-27.
- **why:** right repo, wrong granularity. Bridge creates one issue per Capture; nothing splits a bullet list into topics.
- **status:** split by hand into game-wipfelkratzer#99–#101, #92 closed; cause → https://github.com/freaxnx01/flowhub/issues/133

## 2026-09-27 — 48085662-f2b3-4bdd-9496-6d3937f10ef1  (Telegram, 2026-09-27 11:33)
- **content:** photo `photo-406.jpg`, caption `Wipfelkratzer: / Tapete soll durchgängig sein und nicht so unterbrochen`
- **got:** `MatchedSkill: Paperless`, stage `Unhandled`, `no integration registered for skill 'Paperless'`, no trace (attachment branch)
- **expected:** Bridge issue on `game-wipfelkratzer` with the photo attached as a screenshot. Operator-confirmed 2026-09-27.
- **why:** here the caption **does** carry the meaning — it names the repo outright — but the attachment branch returns to Paperless before the classifier ever sees it. This is the caption half of #113.
- **status:** issue game-wipfelkratzer#98 filed by hand (photo not attachable via gh); evidence on flowhub#113

## 2026-09-27 — 9b0548d9-90f1-414e-aee6-5ca0ad8898ef  (Telegram, 2026-09-27 11:32)
- **content:** `Wipfelkratzer / - fix: Sichern, mit fotos: Sichern hat nicht geklappt / - Hineingehen: Wände weg soll auch von innen funktionieren / - Hineingehen: Lücke zwischen wand und Decke / - Raum einrichten: /   - Alles entfernen, Wirklich sicher? /   - Zufällige Einrichtung (mit Fenstern)`
- **got:** `Bridge`, `Completed` → **one** issue `game-wipfelkratzer#91`
- **expected:** 4 issues on `game-wipfelkratzer`, one per **top-level** `-` line. The indented sub-bullets belong to `Raum einrichten`. Operator rule, stated 2026-09-27.
- **why:** same granularity defect as `66e160f2-…`.
- **status:** split by hand into game-wipfelkratzer#94–#97, #91 closed; cause → https://github.com/freaxnx01/flowhub/issues/133

## 2026-09-27 — b6eb998e-f12e-429b-a554-3ec195ce7d87  (Telegram, 2026-09-27 10:52)
- **content:** `Kit Racer: / - Sound aus dem geforkten Original fehlt / - Spiel gegen CPU: / (4 sub-bullets) / Tracks: / - Progress zeigen bei Laden … / - Kann Ladezeit verkürzt werden?`
- **got:** stage `Orphan`, no trace. Log: `schema_violation` → keyword fallback
- **expected:** 4 issues on `game-kit-racer`, one per top-level `-` line. `Tracks:` is a header for the two lines under it, and the CPU sub-bullets stay with their parent (operator rule 2026-09-27).
- **why:** same `schema_violation` fallback as the entries above. It was outside the first `--limit 10` window and was picked up by the operator's "all captures from today" sweep.
- **status:** issues game-kit-racer#12–#15 filed by hand; cause → https://github.com/freaxnx01/flowhub/issues/134 + #97

## 2026-09-27 — 35e388a7-ed1c-4e54-bd79-d2e380c517de  (Telegram, 2026-09-27 22:15)
- **content:** `Acronym quiz: / YAGNI`
- **got:** stage `Unhandled`, no trace, `FailureReason: bridge candidate — repo undetermined`
- **expected:** issue on `game-acronym-quiz`: add the acronym YAGNI
- **why:** the header names the repo without its `game-` prefix, the same gap as `Kit racer:`.
- **status:** issue game-acronym-quiz#4 filed by hand; cause → evidence on flowhub#97

## 2026-09-27 — 17b04ec6-959a-4b58-a60a-13787b7ff9d6  (Telegram, 2026-09-27 07:58)
- **content:** `Flowhub telegram: slash cmds, e.g. /legende (in english) for list with used Emojis`
- **got:** `Bridge`, Ai sonnet-5, stage `Unhandled`, `exhausted retries: … 500 (Internal Server Error)`
- **expected:** issue on `freaxnx01/flowhub`. Classification was right.
- **why:** a routing failure (Bridge returned 500), not a misclassification. Recorded because the issue was never created.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/132 filed by hand; the Bridge 500 itself was not investigated

## 2026-09-27 — 37d73359-b8ea-4a7c-a792-25cda0332b9b, 37679357-…, f998af0b-…  (Telegram, 2026-09-27 08:36)
- **content:** photos `photo-401.jpg`–`photo-403.jpg`, no caption: cover of *Alles Liebe* (Barbara Schmutz, Kein & Aber) + two pages with red-bracketed passages (pp. 107, 278)
- **got:** `MatchedSkill: Paperless`, stage `Unhandled`, `no integration registered for skill 'Paperless'`, no trace (attachment branch)
- **expected:** **book notes**. Operator-confirmed 2026-09-27.
- **why:** the meaning is in the pixels (#39, #113), and FlowHub has no book-notes destination (same family as #118, #121).
- **recorded as one entry** by exception: same batch, same disposition.
- **status:** parked: no destination exists; evidence on https://github.com/freaxnx01/flowhub/issues/39

## 2026-09-28 — 8fac77ce-69d1-41a4-b0b9-2251e1440c1e  (Telegram, 2026-09-27 23:10)
- **content:** `/menu`
- **got:** stage `Orphan`, no trace, `no skill matched during classification`
- **expected:** **not a Capture.** A Telegram bot command (a leading `/`) should be handled by the bot and never enter the pipeline. Operator-confirmed 2026-09-28.
- **why:** the Telegram channel turns every message into a Capture, commands included. This is the input side of #132 (slash commands such as `/legend`).
- **status:** fixed by #136 and live on CT 136 in v0.10.0: a `/`-message is a bot command and never a Capture. This one arrived while 0.9.0 was still deployed.

## 2026-09-28 — a8d7fecc-303a-49e1-bf4d-d98ba1f92b01  (Telegram, 2026-09-24 13:33)
- **content:** `Game idea: / Name: Poseidonia / Ship Anchor Simulator / Ein Arbeitskollege hat mir von Segeltrip … erzählt und wie es mit dem Ankern vor sich geht …`
- **got:** `Vikunja`, `Completed`, project **Games Ideen**, task 2249
- **expected:** **`ideas-lab`** (Bridge idea). Operator-confirmed 2026-09-28: the ideas-lab rule (2026-09-20) covers game ideas on the Vikunja path too, not only Bridge ideas that were picked for the wrong repo.
- **why:** the classifier chose Vikunja, so the ideas-lab rule in `RepoResolver` (#124) never ran; it only applies once Bridge is chosen. A leading `Game idea:` should decide Bridge → idea before the Vikunja/Bridge choice is made.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/140

## 2026-09-28 — 9b909a68-f924-4ff1-9b5c-4bc19b1081ee  (Telegram, 2026-09-24 13:51)
- **content:** `Ergänzung poseidonia / Katamaran Segelboot / Und folgendes foto`
- **got:** `Vikunja`, `Completed`, project **Names Ships**, a **new** task 2250
- **expected:** an addition to the Poseidonia idea in `ideas-lab`, same entry as `a8d7fecc-…`. Operator-confirmed 2026-09-28.
- **why:** two defects. `Ergänzung <name>` announces an addition to an existing item, and FlowHub has no notion of amending one. And "Names Ships" is a topic match on "Katamaran Segelboot", with no link to Poseidonia.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/142 (amend) + #140 (ideas-lab)

## 2026-09-28 — a1478b64-7d41-499b-adcd-cfab108d47ce  (Telegram, 2026-09-24 13:52)
- **content:** photo `photo-394.jpg`, no caption: a notepad titled "Ankermanöver" (`Ankerkette go / landleinen optional wenn eng oder andere bote / Bug in Wind beim Ankern / Ankerkette halt`)
- **got:** `Paperless`, `Unhandled` (not wired), no trace (attachment branch)
- **expected:** part of the Poseidonia idea in `ideas-lab`. It is the "folgendes foto" that the text capture one minute earlier announces. Operator-confirmed 2026-09-28.
- **why:** the meaning is in the pixels (#39, #113) and in the capture one minute before it. Nothing links a photo to the message that announces it.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/142 (announced photo); evidence on #39

## 2026-09-28 — afd4c8fc-dd50-4182-b0ba-a31adfa28137  (Telegram, 2026-09-25 19:55)
- **content:** photo `photo-396.jpg`, no caption: Van Gogh "Sternennacht" 1000-piece puzzle (Eurographics) on a shop shelf, price 24.–
- **got:** `Paperless`, `Unhandled` (not wired), no trace (attachment branch)
- **expected:** Vikunja **Kaufen** / wish list. Operator-confirmed 2026-09-28.
- **why:** the meaning is in the pixels (#39, #113).
- **status:** evidence on https://github.com/freaxnx01/flowhub/issues/39 (vision path)

## 2026-09-28 — 8e27b240-742c-4a20-a545-9b0c58a70c65  (Telegram, 2026-09-27 07:54)
- **content:** `Supertoskana von Max Küng: E-Book kaufen | Ex Libris` + exlibris.ch product link
- **got:** `Wallabag`, `Unhandled` (`no integration registered for skill 'Wallabag'`), Ai 3.9 s
- **expected:** Vikunja **Kaufen**. Operator-confirmed 2026-09-28.
- **why:** the unwired skill masked a misclassification (same pattern as `d482fd28-…`). "A URL means read-later" overrode an explicit `kaufen` and a shop link. This is the URL over-application `TODO.md` already notes for Claude Sonnet 5.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/141

## 2026-09-28 — 26ef994e-8e55-4a68-b1ce-e9152c6740b7  (Telegram, 2026-09-23 18:50)
- **content:** `Odysseus AI - Self-Hosted AI Workspace Setup Guide` + https://odysseusai.dev/
- **got:** `Wallabag`, `Unhandled` (not wired), Ai 2.6 s
- **expected:** a **homelab to-do** (something to self-host and try). Operator-confirmed 2026-09-28, the same call as the Vaultwarden article `d3db4580-…`.
- **why:** "URL means read-later" again. The rule the operator applies is that a self-hosting guide is a homelab task, not reading material. That makes it the second instance of the same rule.
- **status:** issue https://github.com/freaxnx01/flowhub/issues/141
