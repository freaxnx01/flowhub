# Bridge Alias Capture Routing — Design

**Date:** 2026-07-12
**Status:** Approved (brainstorm)
**Spans:** FlowHub (this repo) + bridge (`~/repos/github/freaxnx01/public/bridge`)

---

## Problem

A capture that references one of my repos by a **short alias** (`br`, `agp`,
`ainstr`, …) should be routed to that repo as either a **new issue** or a **new
entry in the repo's `ideas.md`** — decided automatically from the wording. Today
FlowHub has no notion of repo aliases, and the only repo-issue path is the local
`/flowhub-issue` CC-skill that walks `~/repos/` by full repo name.

`bridge` is the natural target service: it is already my single cross-forge
source of truth for "what repos exist" (GitHub + Forgejo), and it already
exposes REST endpoints that create issues and append to a repo's `ideas.md`.

## Goal

Turn a capture like `br the login 500s on Safari sometimes` into an issue on the
`bridge` repo, and `br what if repos had a health score` into an `ideas.md`
entry on the `bridge` repo — with FlowHub classifying the action and bridge
doing the forge work.

## Non-goals

- Explicit action keywords (`issue:` / `idea:`) — the AI decides from wording.
- Per-repo idea-target overrides, labels, or other `.bridge.yaml` fields beyond
  `alias` (the file is designed to be extensible; only `alias` is in scope now).
- Reworking bridge's MCP surface. Integration is over bridge's **REST** API; the
  MCP surface stays as-is (it cannot write files anyway).
- A new UI. This rides the existing capture → classify → route pipeline.

---

## Decisions (locked during brainstorm)

1. **Surface:** a FlowHub **product `ISkillIntegration`** (`Name = "Bridge"`),
   not a CC-skill. Mirrors Wallabag/Vikunja.
2. **Cross-forge:** must work for both GitHub and Forgejo repos.
3. **Alias source of truth:** a **`.bridge.yaml` file in each repo**, indexed by
   bridge and surfaced on its catalog. Not README frontmatter (renders as a
   visible table on GitHub/Forgejo) and not GH topics (per-repo, per-forge
   upkeep).
4. **Action routing:** the **AI classifier decides** issue vs. idea from the
   wording — no explicit keyword.
5. **Transport:** bridge's **REST API** (`POST /api/capture/{issue,idea}`), with
   **bridge resolving the alias** internally (Approach A). FlowHub sends the
   alias; it never holds a repo catalog for routing.
6. **Low confidence:** if the AI is unsure issue-vs-idea, **do not guess** —
   leave the Capture in the Vikunja Inbox for `/flowhub-triage`.

---

## What already exists in bridge

| Capability | Location | Status |
|---|---|---|
| Append to a repo's `ideas.md` (read→append→commit via `PutFile`) | `internal/capture.CaptureIdea` | ✅ |
| Create an issue in a repo (GitHub + Forgejo) | `internal/capture.CaptureIssue` | ✅ |
| REST `POST /api/capture/idea {target,text}` / `issue {owner,repo,title}` | `internal/api/capture.go`, wired in `cmd/bridge/serve.go` | ✅ |
| `GET /api/repos` catalog | `internal/api/repos.go` | ✅ (no alias field yet) |
| Cross-forge clients | `internal/forge/*` | ✅ |

So the entire **target half** of the feature already works over REST. The gaps
are: alias indexing, an `alias` request field, an issue `body` field, and auth
on the write endpoints.

---

## Architecture

### End-to-end flow

```
Capture "br the login 500s on Safari sometimes"
 → Classifier (FlowHub)
     • KeywordClassifier: leading token "br" ∈ bridge alias set
         → MatchedSkill = "Bridge", BridgeAlias = "br"
     • AiClassifier: infers action from wording
         → BridgeAction = Issue (bug-like) | Idea (fuzzy/exploratory)
         → fills Title + Body (issue) or idea text; emits a confidence
     → ClassificationResult { MatchedSkill=Bridge, BridgeAlias, BridgeAction, Title, Body }
 → CaptureClassified event (MassTransit)
 → SkillRoutingConsumer → BridgeSkillIntegration (Name == "Bridge")
     → POST {BaseUrl}/api/capture/issue { alias, title, body }   (+ Bearer)
       or POST {BaseUrl}/api/capture/idea  { alias, text }       (+ Bearer)
     → bridge resolves alias → forge/owner/repo, creates issue / appends ideas.md, commits
     → SkillResult { success, url } → Capture → Completed
```

### Lifecycle

Reuses the existing states: `Submitted → Classified → Routed → Completed`, with
`Unhandled` as the terminal fallback (Capture stays in the Inbox). The
low-confidence gate and any bridge failure both land in `Unhandled`.

---

## Component design

### A. `.bridge.yaml` (in each repo)

```yaml
# .bridge.yaml — repo root
alias: br
```

- `alias` lowercased, matches `^[a-z0-9][a-z0-9-]*$`, unique across the catalog.
- Read by bridge from the **local checkout** during discovery — no forge API
  call. Repos without a local clone simply have no alias until cloned.
- **Duplicate alias** across repos → bridge logs a warning, the alias resolves
  to *none* (never silently picks one), surfaced via `bridge doctor`.
- The file is intentionally extensible (future: `idea-target`, `issue-labels`);
  only `alias` is consumed now.

### B. Bridge-side changes (bridge repo — separate tracked issue/PR)

1. **Index `.bridge.yaml`** during repo discovery; add `Alias string` to the
   repo model; surface it on `GET /api/repos`.
2. **Resolve by alias** in the capture handlers: accept `{alias}` as an
   alternative to `{owner,repo}` / `{target}`. Unknown alias → `404 unknown
   alias`. Duplicate/ambiguous alias → `409`/`404` (never silently pick).
3. **Add `body`** to `issueRequest` and thread it through `CaptureIssue`
   (currently title-only).
4. **Auth**: `BRIDGE_API_TOKEN` bearer check on `/api/capture/*` (the write
   endpoints), so CT 136 can call them without leaving them open on the LAN.
   Read endpoints unchanged.

### C. FlowHub-side changes (this repo)

- **`BridgeSkillIntegration : ISkillIntegration`** — `source/FlowHub.Skills/Bridge/`,
  `Name = "Bridge"`. `HandleAsync` POSTs to `/api/capture/issue` or
  `/api/capture/idea` by `BridgeAction`, via `IHttpClientFactory` + Bearer.
  Returns `SkillResult { success, url }`.
- **`IBridgeCatalog`** — thin client over `GET /api/repos`, short-TTL cache,
  exposes the alias set to the classifier.
- **Classifier changes**
  - `KeywordClassifier`: cheap leading-token match against the alias set →
    short-circuit `MatchedSkill=Bridge`, set `BridgeAlias`.
  - `AiClassifier`: fill `BridgeAction` (Issue/Idea) + Title/Body + a
    confidence. Below threshold → `BridgeAction=Unknown`.
  - Extend `ClassificationResult` with `BridgeAlias`, `BridgeAction`.
- **Low-confidence gate** — `BridgeAction=Unknown` is treated by
  `SkillRoutingConsumer` like a no-match → `MarkUnhandledAsync`; Capture stays
  in the Inbox for `/flowhub-triage`.
- **Config** — `Bridge__BaseUrl`, `Bridge__ApiToken`, `Bridge__CatalogTtl`.
  Registered in `SkillsServiceCollectionExtensions`.

### D. Request/response contracts (FlowHub → bridge)

```
POST /api/capture/issue
  { "alias": "br", "title": "...", "body": "..." }
  → 200 { forge, repo, number, title, url, ... }   (forge.Issue)

POST /api/capture/idea
  { "alias": "br", "text": "..." }
  → 200 { "url": "https://.../ideas.md#..." }

Errors: 400 (missing fields) · 401 (bad/absent bearer) ·
        404 (unknown alias) · 409 (ambiguous alias) · 5xx (forge failure)
```

---

## Error handling

- Bridge unreachable / 5xx / `401` / `404`/`409` alias → `SkillResult`
  non-success → MassTransit fault → `LifecycleFaultObserver` → `Unhandled`.
  Capture stays in the Inbox; nothing is lost; retried per existing policy.
- Stale-catalog race (classified against an alias bridge no longer knows) →
  `404` at bridge → `Unhandled`.
- Low AI confidence → `Unhandled` before any network call.

---

## Deployment (ship-time dependency)

`bridge serve` must run somewhere CT 136 can reach, with `GH_TOKEN`,
`FORGEJO_TOKEN`, and `BRIDGE_API_TOKEN` in its environment. Cleanest: a homelab
service (e.g. `bridge-serve.home.freaxnx01.ch` via the `homelab-service-routing`
skill). In dev, `Bridge__BaseUrl=http://localhost:<port>`. **FlowHub code is
identical both ways** — the endpoint and token are config-driven.

---

## Testing

Per the repo's non-negotiable testing rules (failing test first; full suite
after; no test edits to force green).

**Bridge (bridge repo):**
- `.bridge.yaml` parse: valid, missing, malformed, dup-alias.
- Alias resolution: hit, unknown (`404`), ambiguous (`409`/`404`).
- Capture handlers: new `alias` + `body` fields; auth (`401` without bearer).

**FlowHub (this repo):**
- `Core.Tests` — classifier leading-token alias detection; confidence gate →
  `BridgeAction=Unknown` → Inbox.
- `Skills.ContractTests` — Bridge client against a stubbed bridge (issue + idea
  happy paths, error mapping).
- `Skills.IntegrationTests` — `HandleAsync` happy + fault → `Unhandled`.

---

## Rollout / sequencing

1. **Bridge PR** — `.bridge.yaml` indexing, alias resolution, `body`, auth.
   Tracked as its own issue in the bridge repo.
2. **Seed aliases** — add `.bridge.yaml` to `bridge` (`br`), `agent-pipeline`
   (`agp`), `ai-instructions` (`ainstr`), and any other frequently-captured repo.
3. **FlowHub PR** — `BridgeSkillIntegration`, `IBridgeCatalog`, classifier +
   `ClassificationResult` changes, config, tests.
4. **Deploy bridge serve** reachable from CT 136; set FlowHub `Bridge__*` config.

The FlowHub integration is inert until `Bridge__BaseUrl` is configured, so it can
merge ahead of the deployment step.
