# Send an inferred repo as owner/repo, not as a bridge alias — design

**Date:** 2026-09-03
**Issue:** #66
**Status:** Design, approved

---

## 1. Problem

Repo inference (#38) resolves a target and puts the repo **name** into
`ClassificationResult.BridgeAlias`. `BridgeSkillIntegration.IssueBody` / `IdeaBody` send that as
`alias`. Bridge resolves `alias` via `core.ResolveAlias` — a per-repo `.bridge-alias` lookup.
**No repo in the catalogue has one**, so every inferred route 404s.

Observed on the live deployment (FlowHub `0.4.1`, CT 136, Bridge configured):

```text
capture       : "bridge mcp from outside LAN?"
stage         : Unhandled
matchedSkill  : Bridge            ← classification works
title         : Expose MCP to WAN
failureReason : exhausted retries: HttpRequestException:
                Response status code does not indicate success: 404 (Not Found)
```

Reproduced directly against bridge — the alias form fails, the target form succeeds:

```console
$ curl -XPOST .../api/capture/issue -d '{"alias":"bridge","title":"probe","body":"probe"}'
404 {"error":"unknown alias"}

$ curl -XPOST .../api/capture/idea -d '{"target":"freaxnx01/ideas-lab","text":"probe"}'
200 {"url":"https://github.com/freaxnx01/ideas-lab/blob/main/ideas.md"}
```

So bridge is fine. FlowHub sends the wrong field.

## 2. What bridge accepts

From `bridge/internal/api/capture.go:46-47,73-75`:

| Endpoint | Form A | Form B |
|---|---|---|
| `POST /api/capture/issue` | `{"alias","title","body"}` | `{"owner","repo","title","body"}` |
| `POST /api/capture/idea` | `{"alias","text"}` | `{"target":"owner/repo","text"}` |

`alias` is a human-typed shorthand resolved from disk. An inferred repo is a **name**, and needs
the owner-qualified form. Note the two endpoints spell it differently — `owner`+`repo` for an
issue, a single `target` string for an idea.

## 3. Approach — a distinct `BridgeTarget`

Considered and rejected:

- **Owner-qualify `BridgeAlias`** and teach bridge's `ResolveAlias` to accept `"owner/repo"`.
  Fewer FlowHub changes, but it needs a coordinated bridge change and release, and leaves a
  field named `alias` carrying something that is not an alias — the precise conflation that
  caused this bug.
- **Populate `.bridge-alias` files** so the existing path resolves. No code change, but it is
  manual curation across 69 repos, and inference returns names for all of them, so most would
  still miss.

**Chosen: carry the inferred repo in its own field.** FlowHub-only, so no cross-repo release
coupling, and "alias the operator typed" stays distinct from "repo we inferred" — which is what
the type system should have been expressing all along.

## 4. Design

### 4.1 The two concepts

| Field | Set by | Meaning | Sent to bridge as |
|---|---|---|---|
| `BridgeAlias` | `BridgeAliasMatcher` short-circuit (`AiClassifier.cs:53-56`) | shorthand the operator typed | `alias` |
| `BridgeTarget` | `RepoResolver` (#38) | owner-qualified repo, e.g. `freaxnx01/bridge` | `owner`+`repo` or `target` |

Exactly one is set. Both null with `MatchedSkill == "Bridge"` is the parking case that
`CaptureEnrichmentConsumer` already handles.

### 4.2 Changes

- **`BridgeRepo`** (`IBridgeCatalog.cs:22`) gains `Owner`. `/api/repos` already returns it and
  `BridgeCatalog` currently drops it — the same class of omission as the `Desc` bug fixed in
  bridge#252.
- **`ClassificationResult`** and **`Capture`** gain `string? BridgeTarget`, alongside the existing
  `BridgeAlias`. **No migration** — the `Bridge*` fields are transient event-only, carried into the
  skill call by `SkillRoutingConsumer` and never persisted.
- **`CaptureClassified`** carries it, so the routing consumer can hand it on.
- **`RepoResolution.Repo`** becomes owner-qualified. `RepoResolver` builds it from the catalogue
  entry rather than returning a bare name, and `IdeaFallbackRepo` becomes `freaxnx01/ideas-lab`.
- **`BridgeSkillIntegration`** picks the payload shape:
  - `BridgeTarget` set → issue: `{"owner","repo","title","body"}`; idea: `{"target","text"}`
  - else `BridgeAlias` set → today's `alias` payloads, unchanged
  - neither → the existing `InvalidOperationException`, with its message widened to say
    "without an alias or target"

### 4.3 Splitting owner/repo

The issue endpoint wants them separate, the idea endpoint joined. `BridgeTarget` is stored joined
(`owner/repo`) because that is what the catalogue naturally yields and what the idea endpoint
takes verbatim; the issue path splits on the **first** `/`. A target with no `/` is a
programming error — the resolver always owner-qualifies — and throws the same
`InvalidOperationException` rather than posting a half-formed payload.

### 4.4 Why this was not caught

Every existing test substitutes `IBridgeCatalog` and asserts on `ClassificationResult`, so the
JSON actually posted to bridge was never exercised. The #38 spec chose the field and no test
disagreed. The fix therefore includes a **contract test** pinning the serialized body for both
shapes — that is the gap, not the field choice.

## 5. Acceptance criteria

- [ ] `BridgeRepo` carries `Owner`, populated from `/api/repos`.
- [ ] `ClassificationResult` and `Capture` carry `BridgeTarget` alongside `BridgeAlias`; no migration is added.
- [ ] `RepoResolver` returns an owner-qualified target; the abstain target is `freaxnx01/ideas-lab`.
- [ ] An inferred **issue** posts `{"owner","repo","title","body"}` — asserted on the serialized JSON.
- [ ] An inferred **idea** posts `{"target","text"}` — asserted on the serialized JSON.
- [ ] A matched **alias** still posts `{"alias",…}`, unchanged, for both endpoints.
- [ ] Neither set → `InvalidOperationException` naming both possibilities; nothing is posted.
- [ ] A target without `/` throws rather than posting a half-formed payload.
- [ ] `AiClassifier`'s alias short-circuit is untouched.

## 6. Testing

Unit (xUnit + NSubstitute + FluentAssertions):

- `BridgeCatalog` maps `owner` into `BridgeRepo`.
- `RepoResolver` returns `owner/repo` for a resolved repo and `freaxnx01/ideas-lab` on abstain.
- **Contract tests on `BridgeSkillIntegration`** — capture the outgoing `HttpRequestMessage` via a
  stub handler and assert the deserialized JSON for all four combinations
  (issue/idea × target/alias). These are the tests whose absence let this ship.
- Neither field set → throws, and no request is made.

No integration test: no new I/O boundary, and the real bridge call is covered by the manual probe
recorded in §1.

## 7. Out of scope

- Vision (#39), the `ClassifierTrace` token undercount and the other #57 follow-ups.
- Populating `.bridge-alias` across the catalogue — the alias short-circuit has never fired in
  production for that reason, but it is a separate decision.
- Anything in bridge itself; bridge#252 is unrelated and already open.

## 8. Verification

With this merged and deployed, `"bridge mcp from outside LAN?"` should classify as `Bridge`, infer
`freaxnx01/bridge`, and produce a real issue — the end-to-end case that has failed at the last
step since the chain was assembled.
