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
- **status:** open

## 2026-09-18 — aa48fd56-9458-4c95-a4be-7e131566a3d1  (Telegram, 2026-09-03 20:08)
- **content:** `bridge mcp from outside LAN?`
- **got:** skill `Bridge` (correct), stage `Unhandled`, no target, `Ai` 9610 ms, `FailureReason: exhausted retries: System.Net.Http.HttpRequestException: Response status code does not indicate success: 404 (Not Found).`
- **expected:** one issue on `freaxnx01/bridge`, created once
- **why:** classification was right every time; **routing** is the defect, and it is two defects. (1) **No dedup:** the identical content produced `bridge#285` (09-03 20:27) and `bridge#289` (09-14 21:55) — two issues for one intent. (2) **404 retry path:** this run burned its retries against a 404 (9.6 s) and ended `Unhandled` instead of failing fast with a useful reason; a 404 is not a transient error worth retrying.
- **related:** `8a260f0a-…` (#285), `3048dae5-…` (#289) — correct outcomes, listed as the duplicate evidence
- **status:** open

## 2026-09-18 — 99fa282d-5823-4db9-80a7-1980578b65b2  (Telegram, 2026-09-01 22:18)
- **content:** `bridge mcp from outside LAN?`
- **got:** stage `Orphan`, no `MatchedSkill`, **no classifier trace**, `FailureReason: no skill matched during classification`
- **expected:** Bridge → issue on `freaxnx01/bridge`
- **why:** byte-identical content classified as `Bridge` on four other occasions. Classification is **non-deterministic** for the same input, and the failing run again wrote no `ClassifierTrace`, so there is nothing recorded to explain the divergence.
- **status:** open
