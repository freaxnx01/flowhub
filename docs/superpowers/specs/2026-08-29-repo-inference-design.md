# Infer the target repo when a capture carries no alias — design

**Date:** 2026-08-29
**Issue:** #38 (blocker 3 of 4 for #35)
**Status:** Design, approved

---

## 1. Problem

`BridgeAliasMatcher.TryMatch` requires an explicit repo alias in the capture text. Measured against the
57 forge-issue candidates in the corpus: **22 carry something alias-shaped, 35 do not (61%)**. Captures
like `"Auto dispatcher Issues cross repo and milestone aligned"` are unambiguously dev tasks with no repo
named — the operator omits it because the context is obvious to them.

Since #37, the LLM may answer `"Bridge"` (behind `Ai:EnableBridgeClassification`), but such a result
carries `BridgeAlias = null` and parks as `Unhandled` with reason `bridge candidate — repo undetermined`.
This design supplies the missing repo.

## 2. What the data supports

`GET /api/repos` already serializes `core.Repo` (`bridge/internal/core/repo.go:12-22`) with `Name`,
`Desc`, `Topics`, `Alias`, `LastUsed`. FlowHub discards all but `Alias` —
`BridgeCatalog.cs:100` declares `record BridgeRepoDto(string? Alias)`. **No bridge-side change is
needed.**

Measured against the live catalog (94 repos, 2026-08-25):

| Field | Usefulness | Evidence |
|---|---|---|
| `Desc` | High | ~43 `game-*` repos carry a real one-liner |
| `Name` | High | the `game-` prefix alone partitions the catalog |
| `LastUsed` | Medium | recency tie-breaker |
| `Topics` | **Unusable** | populated on 1 of 94 repos |

**Lexical matching is not viable on its own.** A throwaway token-overlap prototype scored ~**10 right /
8 wrong / 9 no-match** over 53 captures (report §10.1). The 38-repo `game-*` family acts as a magnet,
absorbing any capture containing a common word: `"Auto dispatcher Issues cross repo…"` → `game-criss-cross`
(matched *cross*), `"Immich - Up and running?"` → `game-esel-running` (matched *running*).

## 3. Approach — embedding shortlist, LLM confirms

Considered and rejected:

- **Lexical shortlist → LLM.** The pre-filter decides what the LLM ever sees; the prototype's 9
  no-match cases would never reach it. Its ceiling is the measured weak link.
- **Embeddings alone, cosine top-1 above a threshold.** A similarity score cannot abstain with
  conviction, and the threshold would be guesswork with no labelled data. The report's own conclusion
  was that the catalog should be *a shortlist offered to the LLM, not a scorer that decides alone.*

**Chosen: embed → top-5 → LLM picks one or abstains.** The shortlist becomes semantic rather than
lexical, so the right repo actually reaches the model (`"strand buggy"` → `game-beach-buggy-racer`
without a shared token), while the catalog stays authoritative — the model can only choose from the five
it is shown, so a hallucinated repo is structurally impossible.

## 4. Design

### 4.1 Where it runs

Inside `AiClassifier.ClassifyAsync`, after the LLM returns `MatchedSkill == "Bridge"` and the alias
short-circuit did **not** fire. `ClassificationResult` then returns complete, with `BridgeAlias`,
`BridgeAction` and `BridgeBody` populated.

`CaptureEnrichmentConsumer` is **unchanged**. Its Bridge/`Unknown` guard remains for the alias path
(alias matched, issue-vs-idea undecided) and for the failure ladder in §4.6.

### 4.2 Catalog widening

`BridgeRepoDto(string? Alias)` becomes:

```csharp
internal sealed record BridgeRepoDto(
    string? Name, string? Alias, string? Desc, string[]? Topics, DateTimeOffset? LastUsed);
```

`IBridgeCatalog` keeps `GetAliasesAsync` — the short-circuit is untouched — and gains
`GetReposAsync` returning the widened records. Both share one fetch and the existing
`BridgeOptions.CatalogTtl` cache, so no extra HTTP traffic.

`Topics` is carried but **not used for matching** (1 of 94 populated). It is deserialized so that
populating topics later becomes a matching change rather than a plumbing change.

### 4.3 Repo embeddings — persisted, content-hashed

A new table stores one embedding per repo:

| Column | Type | Note |
|---|---|---|
| `RepoName` | text, PK | catalog identity |
| `ContentHash` | text | SHA-256 of `name + "\n" + desc` |
| `Embedding` | `vector(384)` | matches the Captures column; ADR 0006 applies |
| `UpdatedAt` | timestamptz | |

On each catalog refresh, re-embed **only** repos whose `ContentHash` changed, and delete rows for repos
no longer in the catalog. Recomputing 94 embeddings every 5-minute TTL would be pure waste; the hash
makes a refresh normally zero embedding calls.

Embedding text is `"{Name}\n{Desc}"`. Name matters — the `game-` prefix is real signal — and repos with
no description still get a usable vector from the name alone.

### 4.4 Shortlist

Embed the capture content via the existing `IEmbeddingService`, then cosine top-5 over the repo table
using pgvector, mirroring `EfCaptureRepository`'s existing `Pgvector.Vector` usage.

Five, not three: the prototype's failures were near-misses in a dense cluster, and the marginal prompt
cost of two more one-line candidates is negligible against the cost of the right repo never being shown.

`LastUsed` breaks ties among equal-distance candidates, preferring recently touched repos.

### 4.5 The confirm call

One LLM call — repo choice and issue-vs-idea are decided **together**, not in two round-trips:

- **Input:** the capture text plus the five candidates as `name — description` lines.
- **Output schema:** `repo` (one of the five, or null), `action` (`issue` | `idea`), `title`, `body`.
- **Validation:** a returned `repo` not in the five presented is a `schema_violation`, handled by the
  existing fallback. This is what makes hallucination structurally impossible rather than merely unlikely.

The prompt must explicitly permit `null` — a model pushed to always choose will file on the wrong repo,
which is worse than not filing.

### 4.6 Abstain → `ideas-lab`

`repo = null` means "a dev idea with no home yet". Several captures are exactly this — requests to
*create* a project: `"Game: Platformer Giana Sisters Clone (Browser)"`, `"Game browser Minigolf"`,
`"Game idea: Klubb, Wikingerschach"` (roughly 4–5 of the 12 game ideas). "Pick an existing repo" is the
wrong frame for them.

These route as `BridgeAction.Idea` to **`ideas-lab`**, appending to its `ideas.md` through machinery that
already exists. Nothing is parked, nothing is lost, and new-project ideas accumulate in one place.

**Failure ladder — every rung degrades, none throws:**

| Condition | Behaviour |
|---|---|
| `IEmbeddingService` returns null (unconfigured) | lexical shortlist instead of top-5; flow continues |
| Repo table empty (first run, refresh pending) | lexical shortlist |
| Confirm call fails or violates schema | park as `Unhandled`, reason `bridge candidate — repo undetermined` (today's behaviour) |
| Catalog fetch fails | existing `BridgeCatalog` resilience — last-known set, or empty |

### 4.7 Gating

Reuses **`Ai:EnableBridgeClassification`** from #37. That flag already means "the LLM may route a capture
to a forge"; this is what makes it useful. A second flag would only create a state where Bridge
classification is on but always abstains.

## 5. Acceptance criteria

- [ ] `IBridgeCatalog.GetReposAsync` returns name, alias, description, topics and last-used, from the same cached fetch as `GetAliasesAsync`.
- [ ] The alias short-circuit path is unchanged.
- [ ] Repo embeddings persist with a content hash; a refresh where nothing changed makes **zero** embedding calls.
- [ ] Repos absent from the catalog have their embedding rows removed on refresh.
- [ ] A Bridge-classified capture with no alias produces a cosine top-5 shortlist.
- [ ] The confirm call returns repo/action/title/body; a repo outside the five is rejected as `schema_violation`.
- [ ] A chosen repo yields a `ClassificationResult` with that repo and the returned action.
- [ ] `repo = null` yields `BridgeAction.Idea` targeting `ideas-lab`.
- [ ] Unconfigured embeddings fall back to the lexical shortlist rather than failing.
- [ ] A failed confirm call parks as `Unhandled` with the existing reason.
- [ ] With `Ai:EnableBridgeClassification` off, none of this runs and behaviour is unchanged.

## 6. Testing

Unit (xUnit + NSubstitute + FluentAssertions), substituting `IChatClient`, `IEmbeddingService` and
`IBridgeCatalog`:

- Widened DTO deserializes a real `/api/repos` payload; missing `desc`/`topics` tolerated.
- One fetch serves both `GetAliasesAsync` and `GetReposAsync` within the TTL.
- Content hash unchanged → no embedding call; changed → exactly one; removed repo → row deleted.
- Shortlist returns 5 ordered by distance, `LastUsed` breaking ties.
- Confirm returns a valid repo → result carries it; returns an unlisted repo → `schema_violation` → park.
- Confirm returns null → `BridgeAction.Idea`, repo `ideas-lab`.
- `IEmbeddingService` returns null → lexical shortlist path taken, no throw.
- Flag off → no embedding call, no confirm call.

Integration (Testcontainers, per the stack overlay): repo-embedding round-trip and cosine ordering
against real pgvector.

**Corpus check, not a test:** re-run the 53 replayed candidates through the finished path and compare
against the hand labels in the taxonomy report. That converts §2.1's caveat into a number for this
specific capability.

## 7. Risks

- **Dense `game-*` cluster.** 38 near-identical descriptions may still crowd the top-5 for non-game dev
  captures. Mitigated by the LLM's ability to abstain; measurable via the corpus check.
- **`all-minilm` on telegraphic German.** 384-dim and small; `"Gag 1800 > nat"`-style captures may embed
  poorly. They are unlikely to be Bridge-classified in the first place, but worth watching.
- **Wrong-repo filing is the expensive error.** Every design choice here — authoritative shortlist,
  explicit null, abstain-to-`ideas-lab` — prefers abstaining over guessing.

## 8. Out of scope

- Vision (#39); deployment configuration (#36).
- Creating repositories. An abstain routes to `ideas-lab`; it never calls a create API.
- Splitting one capture into several issues (message 70 in the corpus targets at least two repos).
- Populating `Topics` across the catalog — a possible later precision win, tracked in the report.

## 9. Dependency note

End-to-end verification needs **#36** (Skills configured, `bridge serve` running). Until then this is
testable only against substituted catalogs and a local pgvector — the code can land and be unit-tested,
but nothing reaches a real forge.
