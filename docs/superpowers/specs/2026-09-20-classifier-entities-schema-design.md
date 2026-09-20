# Classifier entities: an Anthropic-safe structured-output schema

**Issue:** [#111](https://github.com/freaxnx01/flowhub/issues/111)
**Date:** 2026-09-20
**Status:** approved

## Problem

`AiClassificationResponse.Entities` is a `Dictionary<string, string>?`.
Microsoft.Extensions.AI generates the structured-output schema from the record, and a
dictionary becomes:

```json
"entities": { "type": ["object", "null"], "additionalProperties": { "type": "string" } }
```

Anthropic's schema validator requires `additionalProperties` to be `false` for an
`object` and rejects a schema there, returning **HTTP 400**
(`output_config.format.schema: For 'object' type, 'additionalProperties'…`).

`AiClassifier` catches the resulting `ClientResultException` and falls back to the keyword
classifier — by design, and correct for a genuine provider failure. The effect here is
that **every capture** silently degrades: `matchedSkill: ""`, `title: null`,
`tags: ["unsorted"]`, `trace.kind: "Keyword"`. The sensitivity screen keeps working, because
its DTO has no dictionary, so the symptom reads as a bad classifier rather than a rejected
request.

OpenAI-compatible providers accept the same schema, which is why this never surfaced while
the deployment ran `meta-llama/*`.

## Evidence

Probed directly against OpenRouter with the production key, isolating each construct:

| Construct | `anthropic/claude-sonnet-5` |
|---|---|
| plain object schema, `strict: true` | 200 |
| `enum` including the empty-string member (`[AllowedValues]`) | 200 |
| `additionalProperties: {"type":"string"}` (the dictionary) | **400** |
| array of `{key, value}` objects, `additionalProperties: false` | **200** |

The array probe returned, unprompted:

```json
{"entities":[{"key":"person","value":"Goethe"},{"key":"quote","value":"Es ist nicht genug zu wissen."}]}
```

`quote` is exactly the key `ZitateEnricher` consumes, so the shape survives the round trip
in practice and not only in schema validation.

## Decision

Represent entities on the wire as an **array of key/value pairs**, and convert to the
dictionary the rest of the system already uses at the boundary.

Rejected alternatives:

- **Fixed named properties** (`Quote`, `Author`, …). Stricter and better typed downstream,
  but the model could no longer volunteer a key nobody anticipated, and the glossary
  shorthand merge legitimately produces arbitrary keys today.
- **Per-provider schema post-processing.** Keeps the dictionary, but hides the
  incompatibility in a transform nobody will think to look at, and has to track
  Microsoft.Extensions.AI's schema generation across upgrades.

## Design

### Wire shape

```csharp
internal sealed record AiEntity(
    [property: JsonPropertyName("key")]   string Key,
    [property: JsonPropertyName("value")] string Value);
```

`AiClassificationResponse.Entities` becomes `AiEntity[]?`. Its `[Description]` changes to
describe a list of key/value pairs.

### Conversion, and the blast radius

`AiClassifier` converts the array to a `Dictionary<string, string>` at the single call site
and passes it to `MergeEntities`, whose signature
(`IReadOnlyDictionary<string, string>? fromModel`) **does not change**.

Unchanged, deliberately: `ClassificationResult.Entities`, `ShorthandResolver`,
`ZitateEnricher`, `CapturePreviewResponse.Entities`. The internal dictionary never reaches a
provider schema, so only the DTO needs to move.

### Duplicate keys

An array can express a duplicate key where a dictionary could not, so the conversion needs
an explicit rule rather than a `ToDictionary` that throws at runtime on model output.

**Last occurrence wins**, matching `MergeEntities`' existing `merged[key] = value`
semantics. A duplicate is model noise, not an error worth failing a capture over.

### The prompt moves with the schema

`AiPrompts.cs:54` describes entities as an object. It must describe the array shape, or the
model receives a schema and a prompt that disagree.

## Testing

TDD, failing test first, per the repo's rules.

The load-bearing test asserts that **no schema the classifier sends contains
`additionalProperties` as a schema object**. It is what stops a future DTO change silently
reintroducing this, and it is a pure schema assertion — no network, no spend.

Also covered: the array converts to the expected dictionary; a duplicate key resolves
last-wins; glossary shorthand still merges and still loses to model-supplied values for the
same key.

### On #111's first acceptance criterion

It asks for "a real call against an `anthropic/*` model without a 400". That cannot be a
unit test — it needs network access and spends money, so it would be skipped in CI and
provide no regression cover.

It is satisfied instead by the schema assertion above, with the live probe in **Evidence**
standing as the one-off empirical confirmation. This is a deliberate narrowing of that AC,
recorded here so it is not read as an oversight.

## Out of scope

- **`AiBridgeResponse` needs no change.** It carries only `string` and `string[]` members —
  no dictionary — so it is already Anthropic-safe. #111 listed this as an open question;
  this is the answer, recorded so nobody re-checks it.
- **Switching the deployment to Claude.** That is a one-line `Ai__OpenRouter__Model` change
  once this ships, and it should follow a re-measurement against the ten scored fixtures
  rather than riding along with this fix.
- **The keyword fallback itself.** It behaved correctly; the request was malformed.
