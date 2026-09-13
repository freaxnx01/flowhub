# Classifier preview (dry run) — design

**Issue:** #11
**Date:** 2026-09-13
**Status:** approved

## Problem

There is no way to ask FlowHub *where would this capture go* without it going there.
Every classification today ends in a write to a real downstream service.

That is not hypothetical friction. Retrying one historical capture,
`Game: Platformer Giana Sisters Clone (Browser)`, produced two findings in a single call:

- It classified as **Vikunja** on 2026-09-03 and as **Bridge** on 2026-09-13. Same text,
  same prompt. A routing decision is a sample from a distribution, not a property of a
  capture.
- It created an issue on `freaxnx01/game-n-s-clone` — the *North & South* clone — for a
  capture asking for a **new** Giana Sisters game. The model rationalised it in the body
  ("similar to the existing North & South clone"), which is worse than an obviously wrong
  answer because it reads as deliberate.

A backlog of 223 historical captures is waiting to be replayed. Without a preview, the
only way to learn the routing distribution is to perform it — creating a few hundred
issues and tasks, an unknown fraction of them wrong, in real repositories.

## Decisions

### D1 — A separate endpoint, not a flag on submit

`POST /api/v1/captures/preview`, taking the existing `CreateCaptureRequest`
(`Content`, `Source`). A flag on `POST /api/v1/captures` would make the success shape of
the main endpoint conditional — sometimes a persisted `Capture`, sometimes a decision
object — which is worse for both callers and OpenAPI.

### D2 — Classify directly; do not run the bus

The preview calls `IClassifier.ClassifyAsync` and returns. No `Capture` row, no
`CaptureCreated` event, no consumers, no persistence of any kind.

Everything that decides routing already lives inside `AiClassifier`: skill choice, the
Vikunja project, glossary resolution, and repo inference via `RepoResolver`. Threading a
dry-run flag through `CaptureEnrichmentConsumer` and `SkillRoutingConsumer` would mean
persisting a capture and publishing events to arrive at the same answer — more machinery,
and it pollutes the very table the survey is trying to characterise.

**Nothing is persisted.** A 223-capture survey must not leave 223 preview rows behind.

### D3 — Resolve the Vikunja project id, and say when it did not resolve

`VikunjaSkillIntegration.ResolveProjectIdAsync` matches the classifier's project name
against the live catalogue **by exact string** and silently falls back to `Inbox` when it
misses. A preview that reported only the name would hide that.

So the response carries `vikunjaProjectId` and `vikunjaProjectResolved`. A `false` means
the classifier named a project that does not exist and the task would land in Inbox
instead — a real failure class, currently invisible.

This is a read-only catalogue lookup (`IVikunjaProjectCatalog.GetAsync`), so it adds no
side effects.

### D4 — Duplicate-title projects are reported, not hidden

Two projects share the title `Kaufen` (ids 67 and 93). `VikunjaProjectCatalog` builds its
map with `GroupBy(Title).ToDictionary(g => g.Key, g => g.First().Id)`, so one silently
wins and the other is unreachable. The preview reports the id it would actually use; it
does not attempt to resolve the ambiguity, which is a separate bug.

## Non-goals

- **This previews classification, not the pipeline.** It does not exercise
  `EnricherDispatcher`, nor the integration's behaviour at write time. It answers
  "where would this go?", not "would the write succeed?".
- No batch endpoint. A survey issues one request per capture; at roughly 4s of LLM
  latency each, 223 captures is about 15 minutes, which is acceptable and keeps the
  surface small.
- No persistence of previews, and therefore no run-over-run diffing. If that is wanted
  later it is a different design, and the non-determinism above is the argument for it.

## Response shape

```json
{
  "matchedSkill": "Bridge",
  "title": "Add Giana Sisters Clone",
  "tags": ["game", "idea"],
  "entities": { "prefix": "a game concept" },
  "vikunjaProject": "Games Ideen",
  "vikunjaProjectId": 92,
  "vikunjaProjectResolved": true,
  "bridgeAlias": null,
  "bridgeTarget": "freaxnx01/game-n-s-clone",
  "bridgeAction": "Issue",
  "bridgeBody": "...",
  "unknownShorthand": null,
  "trace": { "kind": "Ai", "latencyMs": 4557, "provider": "OpenRouter", "model": "..." }
}
```

Every field comes from `ClassificationResult` except `vikunjaProjectId` and
`vikunjaProjectResolved` (D3). Fields are emitted even when null, so a survey can diff
rows without special-casing absence.

## Error handling

| Condition | Behaviour |
|---|---|
| Invalid request body | 400 `ValidationProblem`, same validator as submit |
| Classifier throws | `AiClassifier` already falls back to `KeywordClassifier`; the preview reports whatever comes back, with the trace showing which ran |
| Vikunja catalogue unreachable (`HttpRequestException`) | `vikunjaProjectId: null`, `vikunjaProjectResolved: false`, logged at warning — the preview still returns |
| Any other resolution failure | propagates → 500. **Amended after review:** the first draft caught every exception here. That violates the repo's "catch specific exception types" rule, and — since `VikunjaProjectCatalog` already handles unreachability internally with its own logging and fallback — a broad catch would mostly have hidden genuine resolution bugs behind a silent "unresolved" with no diagnostic trail. |
| No Vikunja configured | same as above; the preview is not an error |

A preview never fails because a downstream service is unavailable. It is a read-only
question about routing.

## Testing

- Endpoint returns the classifier's decision unchanged for each skill (Bridge, Vikunja,
  Wallabag, empty/Orphan).
- **Nothing is persisted:** after a preview, `ICaptureService` has no new capture and the
  test harness observes no published events. This is the acceptance criterion that matters
  most — it is what makes a 223-capture survey safe.
- A project name in the catalogue resolves to its id with `resolved: true`.
- A project name absent from the catalogue returns `resolved: false` and a null id.
- An unreachable catalogue does not fail the request.
- Invalid body → 400, matching the submit endpoint.
