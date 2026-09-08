# Shorthand glossary — design

**Issue:** #83
**Date:** 2026-09-08
**Status:** approved

## Problem

Captures are written in the operator's private shorthand. The classifier has no
glossary, so it resolves shorthand from the words alone — and produces confident wrong
routes, which is worse than parking because a wrong route looks handled.

Three kinds of shorthand appear in the historical corpus:

- **Person tokens** — one to three characters, usually trailing, sometimes leading or
  possessive. They select between parallel Vikunja projects (a general project and a
  person-scoped variant hold genuinely different content).
- **Domain acronyms** — an abbreviation for a podcast, product or concept. One capture
  reads as a finance entry (a wage-like token and an amount) and is actually a podcast
  episode plus a recommendation marker.
- **Operators** — `>` is overloaded: *recommend to `<person>`* when followed by a person
  token, *in the style of* when followed by a thing. `->` means *produces / leads to*.

None of it is inferable from the text.

## Non-goals

**The parking rule catches unknown _shorthand_, not unknown _meaning_.** A capture can
be entirely composed of ordinary words and still mean something private — the corpus
contains a note that reads as a remark about driving and is in fact therapy material. It
carries no short token and will pass through this feature untouched. Detecting that
class is out of scope here and must not be implied by the acceptance criteria.

Also out of scope: authoring or storing the glossary *content* (personal, stays out of
this repo), and any UI for editing it.

## Decisions

### D1 — Storage: a mounted file now, the vault later, behind an interface

The glossary contains real names and cannot live in this public repo. A JSON file
mounted into the container, path from configuration, is the quickest thing that works.
The eventual home is the `obsidian-me` vault (#84), so the file reader sits behind
`IGlossary` and is replaced later without touching the classifier.

```
Ai__Glossary__Path=/config/glossary.json
Ai__Glossary__RefreshInterval=00:05:00
```

```json
{
  "people":    { "<token>": "<display name>" },
  "acronyms":  { "<TOKEN>": "<expansion>" },
  "operators": { ">": "recommend-to|in-style-of", "->": "produces" }
}
```

### D2 — Application: deterministic resolve, then prompt

Rejected *prompt-injection only*: it makes the parking rule a model judgement, so the
acceptance criterion cannot be enforced or tested. Rejected *deterministic rewrite only*:
the model loses the original wording, and tokens that are also ordinary words get mangled.

The hybrid resolves known tokens deterministically into `Entities`, hands the model the
expanded meanings as context, and leaves the original text intact.

```
"Gag 1800 > nat"
  ↓ deterministic resolve
  entities: { acronym: "<expansion>", person: "<name>", operator: "recommend-to" }
  glossary context appended to the system prompt
  ↓ LLM classifies on the original text plus that context
  project: Podcasts
```

### D3 — Empty glossary must be byte-identical to today

`AiPromptsTests` asserts the system prompt is byte-identical when Bridge is disabled,
because drift there reclassifies every capture. The glossary section extends that guard:
when the glossary is empty, the prompt is unchanged from the current output. An
unconfigured deployment therefore behaves exactly as it does now.

## Architecture

| Component | Project | Responsibility |
|---|---|---|
| `IGlossary`, `GlossarySnapshot` | `FlowHub.Core` | the contract and the immutable snapshot |
| `FileGlossarySource` | `FlowHub.AI` | reads + caches the JSON file |
| `EmptyGlossary` | `FlowHub.AI` | `TryAddSingleton` default when unconfigured |
| `ShorthandResolver` | `FlowHub.AI` | text + snapshot → entities, prompt context, unknown tokens |
| `AiPrompts` | `FlowHub.AI` | optional glossary section in the system prompt |
| `AiClassifier` | `FlowHub.AI` | calls the resolver, merges entities, parks on unknowns |

`FileGlossarySource` mirrors `VikunjaProjectCatalog`: single-flight fetch behind a
`SemaphoreSlim`, `TimeProvider`-driven refresh interval, and on failure it keeps the last
good snapshot rather than throwing. A missing or malformed file yields an **empty**
glossary and logs once — never an exception into the classification path.

## Resolution rules

- **Person tokens** match on a word boundary, case-insensitively, in three positions:
  trailing (`Röstifarm nat`), leading (`J reisen boombox`) and possessive
  (`Reiseliste j: boombox`).
- **Acronyms** match on a word boundary, case-insensitively.
- **`>` disambiguation:** if the next token resolves to a person, the operator is
  `recommend-to`; otherwise `in-style-of`.
- **Unknown-token detection is deliberately narrow** — a *trailing* alphabetic token of
  three characters or fewer that is not a known glossary key. Narrow on purpose: a broad
  rule would park a large fraction of the corpus on ordinary short words.

## Pipeline placement

In `AiClassifier.ClassifyAsync`, after the alias short-circuit and before `BuildMessages`.
Resolved entities merge into `ClassificationResult.Entities`; existing entity keys win
over resolved ones so the model can still override. A non-empty `UnknownTokens` parks the
capture through the existing `Unhandled` + `FailureReason` path, with a reason naming the
token.

## Error handling

| Condition | Behaviour |
|---|---|
| Path unconfigured | `EmptyGlossary`; prompt byte-identical to today |
| File missing / unreadable | empty snapshot, logged once, classification proceeds |
| Malformed JSON | last good snapshot kept if any, else empty; logged |
| Refresh fails | last good snapshot kept |
| Unknown trailing token | capture parked with a reason naming the token |

The glossary is never allowed to fail a classification. The worst case is that
classification degrades to today's behaviour.

## Testing

- `ShorthandResolverTests` — token positions, case-insensitivity, `>` disambiguation
  both ways, narrow unknown detection, no LLM involved.
- `FileGlossarySourceTests` — parse, cache hit, refresh after interval, missing file,
  malformed JSON, last-good retention.
- `AiPromptsTests` — prompt byte-identical when the glossary is empty; contains entries
  when it is not.
- `AiClassifierGlossaryTests` — entities merged, model entities win, unknown token parks.

No test contains real glossary content; fixtures use placeholder tokens.
