# Classifier Entities Schema Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The classifier's structured-output schema carries no `additionalProperties` schema object, so `anthropic/*` models stop returning HTTP 400 and silently degrading every capture to the keyword classifier.

**Architecture:** `AiClassificationResponse.Entities` moves from `Dictionary<string, string>?` to an array of `AiEntity(Key, Value)` records — an array of closed objects, which Anthropic accepts. `AiClassifier` converts that array to the dictionary the rest of the system already uses, at its single call site. Nothing downstream of that conversion changes.

**Tech Stack:** .NET 10 / C#, Microsoft.Extensions.AI 10.5.1, xUnit + FluentAssertions + NSubstitute.

**Spec:** [`docs/superpowers/specs/2026-09-20-classifier-entities-schema-design.md`](../specs/2026-09-20-classifier-entities-schema-design.md)

## Global Constraints

- **No new NuGet packages.** `AIJsonUtilities` is already available via Microsoft.Extensions.AI 10.5.1, pinned in `Directory.Packages.props:73`.
- **No target-framework changes**, no `.csproj` edits — the projects glob `**/*.cs`.
- **No `#nullable disable`**, and no `!` without a comment saying why it is safe.
- **`sealed` by default; file-scoped namespaces; `record` for DTOs.**
- **Do not change** `ClassificationResult`, `ShorthandResolver`, `ZitateEnricher` or `CapturePreviewResponse`. They keep the dictionary; only the wire shape moves.
- **Do not change `MergeEntities`' signature** (`AiClassifier.cs:232`). It keeps taking `IReadOnlyDictionary<string, string>? fromModel`.
- **TDD:** the failing test comes first, every task.
- **Commit after every task**, Conventional Commits, `Refs #111` footer.
- **Local gotcha:** `just test` and the `.slnx` build fail with `NU1903`. Use per-project `dotnet test`. This is a local toolchain issue, not a code failure — do not change packages to "fix" it.
- `FlowHub.AI` already exposes internals to the test project (`source/FlowHub.AI/AiPrompts.cs:5`), so no new `InternalsVisibleTo` is needed.

---

### Task 1: The wire shape becomes an array of key/value pairs

**Files:**
- Create: `source/FlowHub.AI/AiEntity.cs`
- Modify: `source/FlowHub.AI/AiClassificationResponse.cs` (the `Entities` member)
- Modify: `source/FlowHub.AI/AiClassifier.cs:155` (the conversion at the call site)
- Modify: `source/FlowHub.AI/AiPrompts.cs` (the `- entities:` block, around line 54)
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierTests.cs`

**Interfaces:**
- Produces: `internal sealed record AiEntity(string Key, string Value)` in namespace `FlowHub.AI`. Task 2 uses it by name.
- Produces: `AiClassificationResponse.Entities` typed `AiEntity[]?`.
- Consumes: nothing from earlier tasks.

- [ ] **Step 1: Write the failing schema-guard test**

This is the load-bearing test. It is what stops a future DTO change silently reintroducing
the rejected construct, and it needs no network and no spend.

Add to `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierTests.cs`, inside the
`AiClassifierTests` class:

```csharp
    [Fact]
    public void ClassificationSchema_ContainsNoAdditionalPropertiesSchemaObject()
    {
        // Anthropic requires additionalProperties to be `false` for an object and rejects
        // a schema there with HTTP 400. A Dictionary<string,string> member generates
        // "additionalProperties": {"type":"string"}, which took every capture down to the
        // keyword classifier. See docs/superpowers/specs/2026-09-20-classifier-entities-schema-design.md
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(AiClassificationResponse));

        var offenders = new List<string>();
        Walk(schema, "$", offenders);

        offenders.Should().BeEmpty(
            "every `additionalProperties` must be the literal false, not a schema object");

        static void Walk(JsonElement node, string path, List<string> offenders)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.NameEquals("additionalProperties")
                        && prop.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.True))
                    {
                        offenders.Add($"{path}.additionalProperties");
                    }

                    Walk(prop.Value, $"{path}.{prop.Name}", offenders);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, $"{path}[{i++}]", offenders);
                }
            }
        }
    }
```

`AIJsonUtilities` lives in `Microsoft.Extensions.AI`, already imported at the top of this
file. `JsonElement` / `JsonValueKind` need `using System.Text.Json;`, also already imported.

- [ ] **Step 2: Run it and verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~ClassificationSchema_ContainsNoAdditionalPropertiesSchemaObject" -v minimal`

Expected: FAIL, reporting one offender at the `entities` property — the dictionary's
`additionalProperties: {"type":"string"}`.

- [ ] **Step 3: Create the AiEntity record**

Create `source/FlowHub.AI/AiEntity.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace FlowHub.AI;

/// <summary>
/// One structured field extracted by the classifier. A closed object in an array, rather
/// than a free-form dictionary: Microsoft.Extensions.AI renders a dictionary as
/// <c>additionalProperties: {"type":"string"}</c>, which Anthropic rejects outright.
/// </summary>
internal sealed record AiEntity(
    [property: Description("The field name, e.g. quote, author, title, year")]
    [property: JsonPropertyName("key")]
    string Key,

    [property: Description("The field value")]
    [property: JsonPropertyName("value")]
    string Value);
```

- [ ] **Step 4: Change the DTO member**

In `source/FlowHub.AI/AiClassificationResponse.cs`, replace the final member:

```csharp
    [property: Description("Optional structured entities the bucket may consume (e.g. quote, author)")]
    [property: JsonPropertyName("entities")]
    Dictionary<string, string>? Entities);
```

with:

```csharp
    [property: Description("Optional structured entities as key/value pairs the bucket may consume (e.g. key=quote, key=author)")]
    [property: JsonPropertyName("entities")]
    AiEntity[]? Entities);
```

- [ ] **Step 5: Convert at the call site**

In `source/FlowHub.AI/AiClassifier.cs`, line 155 currently reads:

```csharp
        var entities = MergeEntities(shorthand.Entities, payload.Entities);
```

Replace it with:

```csharp
        var entities = MergeEntities(shorthand.Entities, ToDictionary(payload.Entities));
```

and add this private helper next to `MergeEntities` (which is at line 232 and does **not**
change):

```csharp
    private static Dictionary<string, string>? ToDictionary(AiEntity[]? entities)
    {
        if (entities is not { Length: > 0 })
        {
            return null;
        }

        return entities.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
    }
```

> **Leave `ToDictionary` exactly as written, including its duplicate-key behaviour.**
> Task 2 drives out the last-wins rule from a failing test. Writing it defensively here
> makes that task's test green on arrival and removes the only evidence that the rule is
> load-bearing.

- [ ] **Step 6: Update the prompt to match the schema**

In `source/FlowHub.AI/AiPrompts.cs`, the `- entities:` block currently reads:

```text
            - entities: optional structured fields the project may use, e.g.
                Zitate → {"quote": "...", "author": "..."}
                Movies → {"title": "...", "year": "..."}
              Omit if nothing applies.
```

Replace it with:

```text
            - entities: optional structured fields the project may use, as a list of
              {"key": "...", "value": "..."} pairs, e.g.
                Zitate → [{"key": "quote", "value": "..."}, {"key": "author", "value": "..."}]
                Movies → [{"key": "title", "value": "..."}, {"key": "year", "value": "..."}]
              Omit if nothing applies.
```

A prompt describing an object while the schema demands an array is the failure mode this
step exists to prevent.

- [ ] **Step 7: Update the existing entities test to the new payload shape**

In the same test file, `ClassifyAsync_PropagatesProjectAndEntitiesFromModel` builds the
model payload with a dictionary. Replace that `entities = new Dictionary<string, string> { … }`
initialiser with the array shape, leaving every assertion in the test untouched:

```csharp
                 entities = new[]
                 {
                     new { key = "quote", value = "Unix and C are the ultimate computer viruses." },
                     new { key = "author", value = "Richard Gabriel" },
                 },
```

The assertions still read `result.Entities!["author"]`, which is the point: the dictionary
survives the conversion, so nothing downstream had to change.

- [ ] **Step 8: Run the tests and verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests -v minimal`

Expected: PASS, including the schema guard from Step 1 and the updated entities test.

- [ ] **Step 9: Commit**

```bash
git add source/FlowHub.AI/AiEntity.cs source/FlowHub.AI/AiClassificationResponse.cs \
        source/FlowHub.AI/AiClassifier.cs source/FlowHub.AI/AiPrompts.cs \
        tests/FlowHub.Web.ComponentTests/Ai/AiClassifierTests.cs
git commit -m "fix(ai): emit classifier entities as key/value pairs

A Dictionary<string,string> member makes Microsoft.Extensions.AI render
additionalProperties as a schema object, which Anthropic rejects with 400 —
taking every capture down to the keyword classifier. An array of closed
objects is accepted, and converts back to the same dictionary at the
boundary so nothing downstream changes.

Refs #111"
```

---

### Task 2: Duplicate keys resolve last-wins

**Files:**
- Modify: `source/FlowHub.AI/AiClassifier.cs` (the `ToDictionary` helper added in Task 1)
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierTests.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierGlossaryTests.cs`

**Interfaces:**
- Consumes: `AiEntity` and the `ToDictionary` helper from Task 1.
- Produces: nothing new.

An array can express a duplicate key where a dictionary could not. `ToDictionary` throws
`ArgumentException` on one, which would fail a whole capture over model noise.

- [ ] **Step 1: Write the failing duplicate-key test**

Add to `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierTests.cs`:

```csharp
    [Fact]
    public async Task ClassifyAsync_DuplicateEntityKeys_LastOneWins()
    {
        // The array shape can carry a key twice; the dictionary it converts into cannot.
        // A duplicate is model noise, not a reason to fail the capture — and last-wins
        // matches MergeEntities' own `merged[key] = value` semantics.
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(new
             {
                 tags = new[] { "quote" },
                 matched_skill = "Vikunja",
                 title = "A quote with a repeated key",
                 project = "Zitate",
                 entities = new[]
                 {
                     new { key = "author", value = "First" },
                     new { key = "author", value = "Second" },
                 },
             }));

        var result = await Sut().ClassifyAsync("a quote", default);

        result.Entities.Should().NotBeNull();
        result.Entities!["author"].Should().Be("Second");
    }
```

- [ ] **Step 2: Run it and verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~ClassifyAsync_DuplicateEntityKeys_LastOneWins" -v minimal`

Expected: FAIL. `ToDictionary` throws `ArgumentException: An item with the same key has
already been added. Key: author`, which `AiClassifier` does not catch, so the test fails
rather than falling back.

- [ ] **Step 3: Make the conversion last-wins**

In `source/FlowHub.AI/AiClassifier.cs`, replace the body of the `ToDictionary` helper:

```csharp
        return entities.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
```

with an explicit loop:

```csharp
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            result[entity.Key] = entity.Value;
        }

        return result;
```

- [ ] **Step 4: Run it and verify it passes**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~ClassifyAsync_DuplicateEntityKeys_LastOneWins" -v minimal`

Expected: PASS.

- [ ] **Step 5: Update the glossary merge tests to the array shape**

`tests/FlowHub.Web.ComponentTests/Ai/AiClassifierGlossaryTests.cs` has three `ModelReturns(...)`
calls passing `entities = (object?)null` (around lines 68 and 80) — those keep working
untouched, because null is null in both shapes.

`ClassifyAsync_ModelEntities_WinOverResolvedOnes` (around line 94) supplies model entities.
Change only its `entities` initialiser to the array shape, matching Task 1 Step 7, and leave
its assertions alone. This test is the precedence guarantee — model-supplied values beat
glossary-resolved ones for the same key — and it must still pass through the new conversion.

- [ ] **Step 6: Run the full suite**

Run, per-project (the `.slnx` build fails locally with `NU1903`):

```bash
dotnet test tests/FlowHub.Core.Tests -v minimal
dotnet test tests/FlowHub.Web.ComponentTests -v minimal
dotnet test tests/FlowHub.Api.IntegrationTests -v minimal
dotnet test tests/FlowHub.Persistence.Tests -v minimal
dotnet test tests/FlowHub.Telegram.Tests -v minimal
dotnet test tests/FlowHub.Skills.Tests -v minimal
```

Expected: PASS. `tests/FlowHub.Web.E2ETests` needs Playwright browsers and the docker stack;
CI runs it, and it is expected to fail locally with
`Executable doesn't exist at …/chromium_headless_shell-…`.

- [ ] **Step 7: Add the CHANGELOG entry**

Under `## [Unreleased]` in `CHANGELOG.md`, add a `### Fixed` entry (create the heading if
the section does not have one):

```markdown
- **ai:** the classifier emits entities as key/value pairs rather than a free-form map.
  A dictionary member made Microsoft.Extensions.AI render `additionalProperties` as a
  schema object, which Anthropic rejects with HTTP 400 — silently degrading every capture
  to the keyword classifier and blocking `anthropic/*` models entirely.
```

- [ ] **Step 8: Commit**

```bash
git add source/FlowHub.AI/AiClassifier.cs \
        tests/FlowHub.Web.ComponentTests/Ai/AiClassifierTests.cs \
        tests/FlowHub.Web.ComponentTests/Ai/AiClassifierGlossaryTests.cs \
        CHANGELOG.md
git commit -m "fix(ai): resolve duplicate entity keys last-wins

An array can carry a key twice where a dictionary cannot. ToDictionary threw
on it, failing a whole capture over model noise; last-wins matches
MergeEntities' own semantics.

Refs #111"
```
