# Telegram Capture Taxonomy — mined from ChatExport_2026-08-24

**Date:** 2026-08-25
**Author:** Claude Code session (analysis only — no code changed)
**Source:** `ChatExport_2026-08-24.zip` — full history of the `@flowhub_intelliflow_bot` chat
**Status:** Findings + recommendation. Not a spec. Acting on this is a separate, architectural task.

---

## 1. Why this exists

FlowHub today ships four skills — **Vikunja**, **Wallabag**, **Paperless**, **Bridge**. Everything the
classifier cannot route lands as `Unhandled` / `Orphan` in the Inbox. The open question was: *what is
actually in the Capture stream, and which Classifications / Skills / Integrations would it take to route
it?*

This report answers that from the real corpus rather than from guesses.

**Requested follow-up (2026-08-25):** the next routing target is **Capture text/photo → Forge Issue**.
Section 5 addresses that directly.

---

## 2. Method & limits

- Parsed `messages.html` into structured records (timestamp, text, media) — **203 messages**,
  12.02.2026 → 24.08.2026.
- Read **28 of the 41 media items** with vision, including the PDF.
- **Sampling caveat, stated explicitly:** photos 23–35 are a 13-page scan of one children's picture book
  (*"Willi baut"*, Pixi/Carlsen), all uploaded in the same second. I viewed **3 of those 13** and treated
  the rest as one homogeneous cluster. No other cluster was sampled — every other message and photo was
  read individually.
- Clusters were assigned **by hand**, not by an LLM pass, so the counts below are auditable. Every index
  0–202 is assigned to exactly one cluster; there is no "other" bucket hiding leftovers.
- **Redaction:** this repo is public. Personal names, medical details and third-party content have been
  removed from clusters 9, 10 and 11 — counts and cluster shapes are kept, specifics are not. The
  redaction affects no conclusion in this report; see §7 for why it is also a design input.

---

## 3. What is actually in the stream

| # | Cluster | Count | Share | Target system | Status today |
|---|---|---:|---:|---|---|
| 1 | **dev-idea** (own projects) | 41 | 20.2% | Forge issue / `ideas.md` | ❌ gap (partial via Bridge) |
| 2 | **tool-link** (repos, product pages) | 29 | 14.3% | Wallabag / bookmarks | ⚠️ partial |
| 3 | **read-later** (articles) | 21 | 10.3% | Wallabag | ✅ covered |
| 4 | **movie** | 17 | 8.4% | Vikunja *Movies* | ⚠️ generic task only |
| 5 | **shopping** | 16 | 7.9% | Vikunja shopping list | ⚠️ generic task only |
| 6 | **document-scan** | 14 | 6.9% | Paperless | ✅ covered |
| 7 | **game-idea** | 12 | 5.9% | Forge issue (`game-*` repos) | ❌ gap |
| 8 | **event-dated** | 8 | 3.9% | Google Calendar (`gws`) | ❌ gap |
| 9 | **person / contact** | 7 | 3.4% | Contacts / CRM | ❌ gap |
| 10 | **health** (incl. pet) | 7 | 3.4% | Notes | ❌ gap |
| 11 | **family-personal** (sensitive) | 7 | 3.4% | Private notes | ❌ gap |
| 12 | **unclear** | 6 | 3.0% | Manual triage | — |
| 13 | **travel-idea** | 4 | 2.0% | Vikunja *Ausflüge* | ⚠️ generic task only |
| 14 | **quiz-content** | 3 | 1.5% | Forge issue (content repo) | ❌ gap |
| 15 | **noise** (`/start`, tests) | 3 | 1.5% | Drop | ❌ not filtered |
| 16 | **finance** | 2 | 1.0% | Budget notes | ❌ gap |
| 17 | **reading-highlight** | 2 | 1.0% | Notes / Obsidian | ❌ gap |
| 18 | **inventory-hw** | 2 | 1.0% | Inventory | ❌ gap |
| 19 | **bug-report** (own app) | 1 | 0.5% | **Forge issue** | ❌ gap |
| 20 | **howto / knowledge** | 1 | 0.5% | Notes | ❌ gap |
| | **Total** | **203** | | | |

**Coverage today:** Wallabag plausibly handles clusters 2+3 (**50 msgs, 24.6%**), Paperless cluster 6
(**14 msgs, 6.9%**), Vikunja acts as the catch-all. Bridge covers part of cluster 1 — see §5.

So roughly **a third of the stream has a skill that genuinely fits**, which matches the observed
`Unhandled`/`Orphan` rate.

---

## 4. Cluster detail (evidence)

**1 — dev-idea (41).** The single largest cluster and the reason the bot exists. Examples:
`"Flowhub make cmds test categorizing / tokenizing"`, `"flowhub: skill for Google Calendar (gws)"`,
`"Bridge webui overview issues epics"`, `"Auto dispatcher Issues cross repo and milestone aligned"`,
`"Reihenfolge for milestones and only one milestone can be active at once"`,
`"Test repo to test autom. Kimi 3, GLM... and compares result and storing favorite LLM"`.
Two are **photos of dev context**, not text: a terminal running `claude --model google/gemma-4-e4b`
against a local LM Studio endpoint, and a screenshot of a *"Jobbb"* project's `features/INDEX.md`
roadmap table.

**2 — tool-link (29).** GitHub repos and product landing pages: `openfang`, `obsidian-skills`,
`OpenLoco`, `stop-slop`, `worldmonitor`, plus `ghostty.org`, `herdr.dev`, `upscayl.org`, `canitrun.dev`,
`koillection.github.io`. Distinct from read-later: these are *tools to evaluate*, not *articles to read*.
Note `koillection` is itself an inventory tool — a candidate Integration the stream is asking for.

**3 — read-later (21).** Dominated by **heise.de/c't (9)**, plus `towardsdatascience`,
`blog.cloudflare.com`, `the-decoder.de`, `golem.de`, `xda-developers`, Raschka's two book sites.

**4 — movie (17).** Three source shapes: `dvdone.ch` (3), `moviepilot.de` (3), and free text
(`"Sherlock Holmes action und frau"`, `"Dany boon filme"`, `"Mr bean filme j"`). Message 53 is the
feature request for this cluster, in your own words: *"Movies flowhub: Schon in library vorhanden?
Verfügbar auf streaming, tv programm, zattoo? Nox.to? Bei kinderfilmen geeignet für alter & sensibel?"*

**5 — shopping (16).** Text (`"Ofinto stuehl"`, `"Hose leinenmix"`, `"iPhone 13 Pro nat"`), links
(Jumbo, Ex Libris, Amazon, ARMEDANGELS), and **photos of physical products** (2× LEGO boxes in a shop
aisle, a Gallo rosé bottle, a c't hardware review of the Elgato Stream Deck+ XL).

**6 — document-scan (14).** The 13-page Pixi book scan + a school social-work newsletter PDF
(*"Zwischen Schulhaus & Zuhause"*, Juni 26). Paperless already fits.

**7 — game-idea (12).** A strikingly consistent pattern, almost all prefixed `Game:` or `Game browser`:
minigolf, breakout clone, Giana Sisters platformer, micro machines, oil imperium, a Forrest Gump game,
a baking/cooking game, `"Klubb, Wikingerschach"`. Plus one photo of handwritten notes
(`youtube playables`, `stealth master`, `Vehicle masters`).

**8 — event-dated (8).** Every one carries a **concrete date** and most are **photos of paper**:
Street Food Park Aarau (22.–25.05.), an 80th/81st birthday invitation with RSVP deadline
(01.08., RSVP by 15.06.), Quartierfest (21.08., appears **twice** — save-the-date + full flyer),
Fischessen Laufenburg (29.+30.08.), Parc du Petit Prince, a Google birthday reminder screenshot.
None of these can reach a calendar today.

**9 — person (7).** *(Details redacted — personal data.)* Shape: a named person plus a recurring slot or
a place (`"<name> <weekday> <town>"`), a reminder to visit someone, a shift time, a Google Maps card for
a local business with opening hours, and two messages holding structured biographical data about one
individual. The routing-relevant property is that **a person name is the primary key** and the rest is
an attribute of them.

**10 — health (7).** *(Human entries redacted — medical data.)* Shape: a dated `"<treatment> received"`
note. The pet half is unremarkable and shows the pattern clearly: a vet instruction to reduce dry food,
then a 4-photo evidence set — feeding-plan app screen, food-bag dosage table, wet-food pouch front/back,
kibble on a kitchen scale reading 5 g. **One instruction, four supporting photos, one logical Capture.**

**11 — family-personal (7).** *(Content redacted — sensitive personal and third-party data.)* The
cluster contains correspondence about a family member, care documentation, a child's drawing, and
packing lists. **This cluster is sensitive** and is a routing constraint rather than just another
bucket (see §7). It is described here only to establish that the constraint exists and how large it is.

**19 — bug-report (1).** Message 184: a photo of `…ub.freaxnx01.ch` running a Jass card game, with
*"Jetz hani de bewiis. 2x 8i gspilt vom compi. Nachenand. Ich spile jede abig. Sogar jetzt i de ferie in
Nantes. Lg"* — a **real user reporting a real defect in your own hosted app**, with a screenshot as
evidence. Exactly one of these in six months, but it is the highest-value single Capture in the corpus.

---

## 5. The requested route: Capture → Forge Issue

**Size of the target.** Clusters 1 + 7 + 14 + 19 = **57 messages, 28.1% of the corpus.** That is the
largest single routing opportunity in the stream, and it is roughly the size of Wallabag's entire share.

**What already exists.** More than expected:

- `BridgeAction` (`source/FlowHub.Core/Classification/BridgeAction.cs`) already has **`Issue`** and
  **`Idea`** members, with `Unknown` parking the capture for manual triage.
- `BridgeSkillIntegration` already posts to bridge's `POST /api/capture/issue` / `/api/capture/idea`.
- `ClassificationResult` already carries `BridgeAlias`, `BridgeAction`, `BridgeBody`.

**Why it does not fire on this corpus.** `AiClassifier.cs:53-56` gates the whole Bridge branch behind
`BridgeAliasMatcher.TryMatch(content, aliases, …)` — an **explicit alias must appear in the message**.
Measured against the 57 candidates:

- **22 of 57 (39%)** begin with something alias-shaped: `Flowhub …`, `Bridge …`, `Homelab: …`,
  `Quicktask: …`, `Claude-dev: …`, `Immich - …`, `Game: …`, `Game-* …`.
- **35 of 57 (61%)** carry **no alias at all** — `"Auto dispatcher Issues cross repo and milestone
  aligned"`, `"Reihenfolge for milestones…"`, `"Try out Claude /design (game idea?)"`, the Jass bug
  report, both dev screenshots, all three quiz items.

So the mechanism is built; the **trigger is too narrow**. The corpus says the alias is the exception,
not the rule — you write the project name inline, or omit it entirely because context is obvious to you.

**Three shapes the route has to handle** (they are not one problem):

1. **Alias-prefixed text** — already works. 22 messages.
2. **Alias-free text where the repo must be inferred** from vocabulary (`milestone`, `dispatcher`,
   `PR`, `subagent` → agent-workflow; `Game:` → a `game-*` repo). 33 messages.
3. **Photo-as-issue** — the Jass screenshot and the two dev screenshots. The image *is* the issue body;
   the repo must come from what is visible in it (`…ub.freaxnx01.ch` in the URL bar). 3 messages.

Shape 3 is the one that has no path at all today, and it is the one your request explicitly named
("text/photo").

**Recommended scoping.** Treat "infer the repo without an alias" as the core problem and the `Game:`
prefix as a cheap early win — 12 of the 57 are already machine-detectable by a literal prefix, and they
all target the same repo family.

---

## 6. Cross-cutting signals worth designing for

These showed up repeatedly and are independent of any single cluster:

- **Person suffixes are routing metadata.** `nat` appears as a trailing token in four messages
  (`"iPhone 13 Pro nat"`, `"Röstifarm nat"`, `"Gag 1800 > nat"`, `"Cafi mit geschmack für na"`), and
  `j` in six (`"Mr bean filme j"`, `"Reiseliste j"`, `"Migros führung j"`). You are already tagging
  captures by person, informally. An entity extractor should pick this up rather than treat it as noise.

- **Bursts are one logical Capture.** The 13 book pages share a timestamp to the second; the 4 cat-food
  photos share one minute; 3 movie links span 3 minutes. Routing them as 13/4/3 separate Captures is
  wrong — burst grouping by timestamp proximity would collapse them.

- **Duplicates exist and are not detected.** `github.com/kepano/obsidian-skills` was sent twice
  (24.05. and 17.08.); `"Reiseliste j: boombox"` duplicates `"J reisen boombox"`; photo_14 is a
  byte-different re-shot of photo_12. There is no dedup guard anywhere in the pipeline.

- **Dates in images are actionable.** Four of the eight dated events exist **only** as a photo of paper.
  Any calendar skill that reads only text will miss half of its own cluster.

- **Noise is real but tiny.** `/start` and two `"test message from Telegram Bot XYZ"` — 3 messages.
  A trivial filter, worth having so they never reach the classifier.

- **Language is mixed.** German, Swiss German (`"Jetz hani de bewiis"`), and English, often inside one
  message. Any keyword rule set has to be trilingual or it will silently under-match.

---

## 7. Constraint: the sensitive cluster

Clusters 9, 10 and 11 (16 messages, **7.9%** of the corpus) carry personal, medical and third-party
data — including material about family members who never consented to any of it being processed. Their
specifics are deliberately redacted from this report, which is itself the point: **the same reticence
has to exist in the pipeline.**

Design consequences:

- These must not be routed to any external or shared system by default — not Vikunja, not a forge, not
  an LLM provider.
- The classifier needs an explicit **"private — local only"** terminal class.
- That class should be the **default on low confidence** whenever a capture names a person, rather than
  falling through to the generic Inbox.
- Worth deciding separately: whether such captures should reach a *cloud* classifier at all, or be
  filtered before the AI call by a local rule.

---

## 8. Recommendations, ranked

1. **Widen the Bridge trigger (highest value).** 28.1% of the corpus wants to be a forge issue and 61%
   of it cannot reach the existing, already-built `BridgeAction.Issue` path. Infer the repo from content
   when no alias matches; keep `Unknown` → triage as the safe default.
2. **`Game:` prefix → `game-*` repo.** 12 messages, literal prefix, one repo family. Cheapest possible
   win inside recommendation 1.
3. **Photo-as-issue.** Vision-derived body + repo inferred from what is visible. Needed for the single
   most valuable Capture type (a user bug report with evidence).
4. **Calendar skill (`gws`).** 8 dated events, half of them image-only, all currently lost. You already
   asked for this yourself in message 56.
5. **Split Wallabag's cluster.** `tool-link` (29) and `read-later` (21) behave differently — one is an
   evaluation queue, the other a reading queue. Same integration, different target.
6. **Typed Vikunja targets.** Movies (17), shopping (16), travel (4) already have projects; they arrive
   as generic tasks. Message 53 is a written spec for the Movies case.
7. **Burst grouping + dedup.** Cheap, mechanical, and prevents the 13-page-book failure mode.
8. **Noise filter.** 3 messages. Trivial.

Deferred as too thin to justify a skill yet: finance (2), inventory (2), reading-highlight (2),
howto (1).

---

## 9. Open questions

- Does `bridge`'s repo catalog expose enough metadata (topics, language, description) to infer a repo
  from free text, or does inference need its own mapping table?
- For shape 3, is the issue body the vision transcript, the image as an attachment, or both?
- Should `unclear` (6) stay a manual-triage bucket, or is that the correct permanent behaviour?

---

## 10. Artifacts

- Parsed corpus: `messages.json` (203 records) in the session scratchpad — **not committed**; regenerate
  from the zip with the parser in this session's history if needed.
- Source zip: `~/LocalSend/ChatExport_2026-08-24.zip` (9.6 MB, 132 files).
