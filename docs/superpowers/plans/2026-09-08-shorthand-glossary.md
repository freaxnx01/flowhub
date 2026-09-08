# Shorthand Glossary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the operator's private shorthand — people, acronyms, prefix markers and operators — deterministically before classification, so the classifier stops guessing at tokens it cannot know.

**Architecture:** A `GlossarySnapshot` is loaded from a mounted JSON file behind an `IGlossary` seam (`obsidian-me` replaces the file later, #84). `ShorthandResolver` turns capture text plus that snapshot into resolved entities, a prompt-context string, and a list of unknown tokens. `AiClassifier` merges the entities, passes the context to the prompt, and surfaces unknown tokens so `CaptureEnrichmentConsumer` parks the capture instead of guessing.

**Tech Stack:** .NET 10 / C#, Microsoft.Extensions.AI, MassTransit, xUnit + FluentAssertions + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-09-08-shorthand-glossary-design.md`

## Global Constraints

- **An empty glossary must leave the system prompt byte-identical to today.** `AiPromptsTests.BuildSystemPrompt_BridgeDisabled_MatchesThePreBridgeWording` exists because prompt drift reclassifies every capture. Extend that guard; never weaken it.
- **The glossary may never fail a classification.** Missing file, malformed JSON, unreadable path → empty snapshot, logged, classification proceeds exactly as today.
- **No real glossary content in this repo.** Every fixture uses placeholder tokens (`aa`, `bb`, `ZZZ`). The glossary is personal data; it lives only on the deployment.
- New optional record parameters go **last** so existing positional construction keeps compiling — the constraint that applied in #37, #38 and #66.
- No `#nullable disable`, no warning suppressions; `IDE0005` (unused using) is an error in this repo.
- Internal AI types are tested from `FlowHub.Web.ComponentTests` via the existing `[assembly: InternalsVisibleTo("FlowHub.Web.ComponentTests")]` in `AiPrompts.cs`.
- Conventional Commits; scopes `ai`, `core`, `web`.

---

## File Structure

- `source/FlowHub.Core/Classification/IGlossary.cs` — **create.** The `IGlossary` contract and the immutable `GlossarySnapshot`.
- `source/FlowHub.AI/GlossaryOptions.cs` — **create.** Bound from `Ai:Glossary`.
- `source/FlowHub.AI/EmptyGlossary.cs` — **create.** Default when unconfigured.
- `source/FlowHub.AI/FileGlossarySource.cs` — **create.** Reads and caches the JSON file.
- `source/FlowHub.AI/ShorthandResolver.cs` — **create.** Text + snapshot → entities, context, unknowns.
- `source/FlowHub.AI/AiPrompts.cs` — **modify.** Optional glossary section.
- `source/FlowHub.AI/AiClassifier.cs` — **modify.** Resolve, merge entities, surface unknowns.
- `source/FlowHub.Core/Classification/ClassificationResult.cs` — **modify.** Gains `UnknownShorthand`.
- `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs` — **modify.** Parks on unknown shorthand.
- `source/FlowHub.AI/AiServiceCollectionExtensions.cs` — **modify.** Registration.
- Tests: `tests/FlowHub.Web.ComponentTests/Ai/{ShorthandResolverTests,FileGlossarySourceTests,AiPromptsGlossaryTests,AiClassifierGlossaryTests}.cs`, and `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerGlossaryTests.cs`.

---

### Task 1: The glossary contract and an empty default

**Files:**
- Create: `source/FlowHub.Core/Classification/IGlossary.cs`
- Create: `source/FlowHub.AI/EmptyGlossary.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/EmptyGlossaryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `IGlossary.GetAsync(CancellationToken) → Task<GlossarySnapshot>`; `GlossarySnapshot(IReadOnlyDictionary<string,string> People, IReadOnlyDictionary<string,string> Acronyms, IReadOnlyDictionary<string,string> Prefixes)` with `GlossarySnapshot.Empty` and `IsEmpty`.

- [ ] **Step 1: Write the failing test**

```csharp
using FlowHub.AI;
using FlowHub.Core.Classification;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class EmptyGlossaryTests
{
    [Fact]
    public async Task GetAsync_ReturnsAnEmptySnapshot()
    {
        var snapshot = await new EmptyGlossary().GetAsync(default);

        snapshot.IsEmpty.Should().BeTrue();
        snapshot.People.Should().BeEmpty();
        snapshot.Acronyms.Should().BeEmpty();
        snapshot.Prefixes.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~EmptyGlossaryTests"`
Expected: FAIL — build error, `EmptyGlossary` and `GlossarySnapshot` do not exist.

- [ ] **Step 3: Write minimal implementation**

`source/FlowHub.Core/Classification/IGlossary.cs`:

```csharp
namespace FlowHub.Core.Classification;

/// <summary>
/// The operator's private shorthand. Personal data — supplied by the deployment,
/// never committed. An unconfigured deployment gets <see cref="GlossarySnapshot.Empty"/>,
/// which must leave classification byte-identical to having no glossary at all.
/// </summary>
public interface IGlossary
{
    Task<GlossarySnapshot> GetAsync(CancellationToken cancellationToken);
}

/// <param name="People">token → display name, e.g. a one-to-three character marker.</param>
/// <param name="Acronyms">token → expansion.</param>
/// <param name="Prefixes">prefix marker (including its punctuation) → routing hint.
/// Text after a prefix is subject matter and is not resolved — see D3 in the spec.</param>
public sealed record GlossarySnapshot(
    IReadOnlyDictionary<string, string> People,
    IReadOnlyDictionary<string, string> Acronyms,
    IReadOnlyDictionary<string, string> Prefixes)
{
    public static GlossarySnapshot Empty { get; } = new(
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>());

    public bool IsEmpty => People.Count == 0 && Acronyms.Count == 0 && Prefixes.Count == 0;
}
```

`source/FlowHub.AI/EmptyGlossary.cs`:

```csharp
using FlowHub.Core.Classification;

namespace FlowHub.AI;

/// <summary>Registered via TryAddSingleton so an unconfigured deployment behaves as today.</summary>
internal sealed class EmptyGlossary : IGlossary
{
    public Task<GlossarySnapshot> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(GlossarySnapshot.Empty);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~EmptyGlossaryTests"`
Expected: PASS

- [ ] **Step 5: Commit and push**

```bash
git add source/FlowHub.Core/Classification/IGlossary.cs source/FlowHub.AI/EmptyGlossary.cs tests/FlowHub.Web.ComponentTests/Ai/EmptyGlossaryTests.cs
git commit -m "feat(core): add IGlossary contract and an empty default (#83)"
git push
```

---

### Task 2: Load the glossary from a mounted JSON file

**Files:**
- Create: `source/FlowHub.AI/GlossaryOptions.cs`
- Create: `source/FlowHub.AI/FileGlossarySource.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/FileGlossarySourceTests.cs`

**Interfaces:**
- Consumes: `IGlossary`, `GlossarySnapshot` from Task 1.
- Produces: `FileGlossarySource(IOptions<GlossaryOptions>, ILogger<FileGlossarySource>, TimeProvider)` implementing `IGlossary`; `GlossaryOptions { string? Path; TimeSpan RefreshInterval }` with `SectionName = "Ai:Glossary"`.

Mirrors `VikunjaProjectCatalog` (`source/FlowHub.Skills/Vikunja/VikunjaProjectCatalog.cs:32-90`): single-flight behind a `SemaphoreSlim`, `TimeProvider`-driven interval, re-read the clock **inside** the lock, and keep the last good snapshot on failure.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class FileGlossarySourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("glossary").FullName;

    private FileGlossarySource Build(string? path, FakeTimeProvider time) =>
        new(Options.Create(new GlossaryOptions { Path = path, RefreshInterval = TimeSpan.FromMinutes(5) }),
            NullLogger<FileGlossarySource>.Instance,
            time);

    private string WriteFile(string json)
    {
        var path = Path.Combine(_dir, "glossary.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task GetAsync_ValidFile_ParsesAllSections()
    {
        var path = WriteFile("""
            {"people":{"aa":"Person A"},"acronyms":{"ZZZ":"Zed Zed Zed"},"prefixes":{"Thing:":"things"}}
            """);

        var snapshot = await Build(path, new FakeTimeProvider()).GetAsync(default);

        snapshot.People["aa"].Should().Be("Person A");
        snapshot.Acronyms["ZZZ"].Should().Be("Zed Zed Zed");
        snapshot.Prefixes["Thing:"].Should().Be("things");
    }

    [Fact]
    public async Task GetAsync_PathNotConfigured_ReturnsEmpty()
    {
        var snapshot = await Build(null, new FakeTimeProvider()).GetAsync(default);
        snapshot.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_FileMissing_ReturnsEmptyAndDoesNotThrow()
    {
        var snapshot = await Build(Path.Combine(_dir, "nope.json"), new FakeTimeProvider()).GetAsync(default);
        snapshot.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_MalformedJson_ReturnsEmptyAndDoesNotThrow()
    {
        var snapshot = await Build(WriteFile("{ not json"), new FakeTimeProvider()).GetAsync(default);
        snapshot.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_WithinRefreshInterval_DoesNotRereadTheFile()
    {
        var path = WriteFile("""{"people":{"aa":"Person A"}}""");
        var time = new FakeTimeProvider();
        var sut = Build(path, time);

        await sut.GetAsync(default);
        File.WriteAllText(path, """{"people":{"bb":"Person B"}}""");
        var second = await sut.GetAsync(default);

        second.People.Should().ContainKey("aa").And.NotContainKey("bb");
    }

    [Fact]
    public async Task GetAsync_AfterRefreshInterval_RereadsTheFile()
    {
        var path = WriteFile("""{"people":{"aa":"Person A"}}""");
        var time = new FakeTimeProvider();
        var sut = Build(path, time);

        await sut.GetAsync(default);
        File.WriteAllText(path, """{"people":{"bb":"Person B"}}""");
        time.Advance(TimeSpan.FromMinutes(6));
        var second = await sut.GetAsync(default);

        second.People.Should().ContainKey("bb");
    }

    [Fact]
    public async Task GetAsync_RefreshFails_KeepsTheLastGoodSnapshot()
    {
        var path = WriteFile("""{"people":{"aa":"Person A"}}""");
        var time = new FakeTimeProvider();
        var sut = Build(path, time);

        await sut.GetAsync(default);
        File.WriteAllText(path, "{ not json");
        time.Advance(TimeSpan.FromMinutes(6));
        var second = await sut.GetAsync(default);

        second.People.Should().ContainKey("aa");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~FileGlossarySourceTests"`
Expected: FAIL — build error, `FileGlossarySource` and `GlossaryOptions` do not exist.

If `Microsoft.Extensions.TimeProvider.Testing` is not already referenced by the test project, add it — check first with `grep -r "FakeTimeProvider" tests/ | head -1`; the repo uses Central Package Management, so the version goes in `Directory.Packages.props`.

- [ ] **Step 3: Write minimal implementation**

`source/FlowHub.AI/GlossaryOptions.cs`:

```csharp
namespace FlowHub.AI;

/// <summary>Bound from configuration section <c>Ai:Glossary</c>.</summary>
public sealed class GlossaryOptions
{
    public const string SectionName = "Ai:Glossary";

    /// <summary>Absolute path to the glossary JSON. Null or empty disables the glossary.</summary>
    public string? Path { get; set; }

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
}
```

`source/FlowHub.AI/FileGlossarySource.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowHub.Core.Classification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowHub.AI;

/// <summary>
/// Reads the glossary from a file mounted into the container. The vault-backed
/// implementation (#84) replaces this without touching the classifier.
/// </summary>
internal sealed partial class FileGlossarySource : IGlossary, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlossaryOptions _options;
    private readonly ILogger<FileGlossarySource> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GlossarySnapshot? _cache;
    private DateTimeOffset _readAt;

    public FileGlossarySource(
        IOptions<GlossaryOptions> options,
        ILogger<FileGlossarySource> log,
        TimeProvider time)
    {
        _options = options.Value;
        _log = log;
        _time = time;
    }

    public async Task<GlossarySnapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Path))
        {
            return GlossarySnapshot.Empty;
        }

        var now = _time.GetUtcNow();
        if (_cache is not null && now - _readAt < _options.RefreshInterval)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-read the clock inside the lock: a waiter that blocked through
            // another thread's successful read must see the fresh _readAt.
            now = _time.GetUtcNow();
            if (_cache is not null && now - _readAt < _options.RefreshInterval)
            {
                return _cache;
            }

            try
            {
                var json = await File.ReadAllTextAsync(_options.Path, cancellationToken);
                var dto = JsonSerializer.Deserialize<GlossaryDto>(json, JsonOptions)
                    ?? throw new JsonException("glossary document was null");

                _cache = new GlossarySnapshot(
                    dto.People ?? new Dictionary<string, string>(),
                    dto.Acronyms ?? new Dictionary<string, string>(),
                    dto.Prefixes ?? new Dictionary<string, string>());
                _readAt = now;
                return _cache;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (_cache is not null)
                {
                    LogReloadFailedKeepingCache(ex.GetType().Name);
                    _readAt = now;
                    return _cache;
                }

                LogFirstReadFailed(ex.GetType().Name);
                _cache = GlossarySnapshot.Empty;
                _readAt = now;
                return _cache;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record GlossaryDto(
        [property: JsonPropertyName("people")] Dictionary<string, string>? People,
        [property: JsonPropertyName("acronyms")] Dictionary<string, string>? Acronyms,
        [property: JsonPropertyName("prefixes")] Dictionary<string, string>? Prefixes);

    [LoggerMessage(EventId = 1200, Level = LogLevel.Warning,
        Message = "Glossary reload failed ({Reason}); keeping the previous snapshot")]
    private partial void LogReloadFailedKeepingCache(string reason);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning,
        Message = "Glossary could not be read ({Reason}); continuing without a glossary")]
    private partial void LogFirstReadFailed(string reason);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~FileGlossarySourceTests"`
Expected: PASS — 7 tests.

- [ ] **Step 5: Commit and push**

```bash
git add source/FlowHub.AI/GlossaryOptions.cs source/FlowHub.AI/FileGlossarySource.cs tests/FlowHub.Web.ComponentTests/Ai/FileGlossarySourceTests.cs
git commit -m "feat(ai): load the shorthand glossary from a mounted file (#83)"
git push
```

---

### Task 3: Resolve shorthand in capture text

**Files:**
- Create: `source/FlowHub.AI/ShorthandResolver.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/ShorthandResolverTests.cs`

**Interfaces:**
- Consumes: `GlossarySnapshot` from Task 1.
- Produces: `static ResolvedShorthand ShorthandResolver.Resolve(string content, GlossarySnapshot glossary)`; `ResolvedShorthand(IReadOnlyDictionary<string,string> Entities, string PromptContext, IReadOnlyList<string> UnknownTokens, string? BridgeTarget = null)` with `ResolvedShorthand.None`.

This is the heart of the feature and the only part with real logic. Resolution rules are D3 and the *Resolution rules* section of the spec.

- [ ] **Step 1: Write the failing test**

```csharp
using FlowHub.AI;
using FlowHub.Core.Classification;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class ShorthandResolverTests
{
    private static readonly GlossarySnapshot Glossary = new(
        People: new Dictionary<string, string>
        {
            ["aa"] = "Person A",
            ["a"] = "Person A",     // alias: two tokens, one person
            ["b"] = "Person B",
        },
        Acronyms: new Dictionary<string, string> { ["ZZZ"] = "Zed Zed Zed" },
        Prefixes: new Dictionary<string, string>
        {
            ["Thing:"] = "things",
            ["Repo Thing:"] = "someone/some-repo",   // owner-qualified → deterministic route
        });

    [Theory]
    [InlineData("Farmshop aa")]          // trailing
    [InlineData("aa reisen radio")]      // leading
    [InlineData("Packlist aa: radio")]   // possessive
    public void Resolve_PersonTokenInAnyPosition_IsResolved(string content)
    {
        var result = ShorthandResolver.Resolve(content, Glossary);

        result.Entities.Should().ContainKey("person").WhoseValue.Should().Be("Person A");
    }

    [Fact]
    public void Resolve_PersonTokenAfterAMarkerPreposition_IsResolved()
    {
        ShorthandResolver.Resolve("Coffee flavoured für aa", Glossary)
            .Entities.Should().ContainKey("person").WhoseValue.Should().Be("Person A");
    }

    [Fact]
    public void Resolve_AliasTokens_ResolveToTheSamePerson()
    {
        ShorthandResolver.Resolve("Coffee für a", Glossary)
            .Entities["person"].Should().Be("Person A");
        ShorthandResolver.Resolve("Coffee für aa", Glossary)
            .Entities["person"].Should().Be("Person A");
    }

    [Fact]
    public void Resolve_OwnerQualifiedPrefix_YieldsABridgeTarget()
    {
        var result = ShorthandResolver.Resolve("Repo Thing: SPQR", Glossary);

        result.BridgeTarget.Should().Be("someone/some-repo");
        result.Entities.Should().NotContainKey("acronym");   // subject matter, unresolved
    }

    [Fact]
    public void Resolve_UnqualifiedPrefix_YieldsNoBridgeTarget()
    {
        ShorthandResolver.Resolve("Thing: ZZZ", Glossary).BridgeTarget.Should().BeNull();
    }

    [Fact]
    public void Resolve_PersonTokenIsCaseInsensitive()
    {
        ShorthandResolver.Resolve("Packlist B: radio", Glossary)
            .Entities.Should().ContainKey("person").WhoseValue.Should().Be("Person B");
    }

    [Fact]
    public void Resolve_TokenInsideAWord_IsNotResolved()
    {
        // "aa" appears inside "Aabach" — a word-boundary match must not fire.
        ShorthandResolver.Resolve("Aabach wanderung", Glossary)
            .Entities.Should().NotContainKey("person");
    }

    [Fact]
    public void Resolve_KnownAcronym_IsExpanded()
    {
        ShorthandResolver.Resolve("ZZZ 1800", Glossary)
            .Entities.Should().ContainKey("acronym").WhoseValue.Should().Be("Zed Zed Zed");
    }

    [Fact]
    public void Resolve_GreaterThanBeforeAPerson_MeansRecommendTo()
    {
        ShorthandResolver.Resolve("ZZZ 1800 > aa", Glossary)
            .Entities.Should().ContainKey("operator").WhoseValue.Should().Be("recommend-to");
    }

    [Fact]
    public void Resolve_GreaterThanBeforeAThing_MeansInStyleOf()
    {
        ShorthandResolver.Resolve("Game: buggy > micro machines", Glossary)
            .Entities.Should().ContainKey("operator").WhoseValue.Should().Be("in-style-of");
    }

    [Fact]
    public void Resolve_KnownPrefix_IsResolvedAsRoutingHint()
    {
        ShorthandResolver.Resolve("Thing: ZZZ", Glossary)
            .Entities.Should().ContainKey("prefix").WhoseValue.Should().Be("things");
    }

    [Fact]
    public void Resolve_AcronymAfterAPrefix_IsSubjectMatterAndIsNotExpanded()
    {
        // D3: text after a prefix marker is content, not shorthand. Expanding it
        // would turn quiz content into a routing signal.
        var result = ShorthandResolver.Resolve("Thing: ZZZ", Glossary);

        result.Entities.Should().ContainKey("prefix");
        result.Entities.Should().NotContainKey("acronym");
    }

    [Fact]
    public void Resolve_UnknownTrailingShortToken_IsReported()
    {
        ShorthandResolver.Resolve("Farmshop qq", Glossary)
            .UnknownTokens.Should().ContainSingle().Which.Should().Be("qq");
    }

    [Fact]
    public void Resolve_TrailingWordLongerThanThreeChars_IsNotReported()
    {
        ShorthandResolver.Resolve("Acronym Quiz: SPQR", Glossary)
            .UnknownTokens.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_TrailingKnownToken_IsNotReportedAsUnknown()
    {
        ShorthandResolver.Resolve("Farmshop aa", Glossary)
            .UnknownTokens.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_EmptyGlossary_ResolvesNothingAndReportsNoUnknowns()
    {
        var result = ShorthandResolver.Resolve("Farmshop qq", GlossarySnapshot.Empty);

        result.Entities.Should().BeEmpty();
        result.PromptContext.Should().BeEmpty();
        result.UnknownTokens.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_PromptContext_NamesEveryResolvedToken()
    {
        var context = ShorthandResolver.Resolve("ZZZ 1800 > aa", Glossary).PromptContext;

        context.Should().Contain("ZZZ").And.Contain("Zed Zed Zed");
        context.Should().Contain("aa").And.Contain("Person A");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~ShorthandResolverTests"`
Expected: FAIL — build error, `ShorthandResolver` does not exist.

- [ ] **Step 3: Write minimal implementation**

`source/FlowHub.AI/ShorthandResolver.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;
using FlowHub.Core.Classification;

namespace FlowHub.AI;

/// <param name="Entities">resolved shorthand, merged into ClassificationResult.Entities.</param>
/// <param name="PromptContext">a short block appended to the system prompt; empty when nothing resolved.</param>
/// <param name="UnknownTokens">trailing short tokens that look like shorthand but are not in the glossary.</param>
/// <param name="BridgeTarget">owner/repo when a prefix named one — routes deterministically,
/// skipping repo inference entirely (D5). Null otherwise.</param>
internal sealed record ResolvedShorthand(
    IReadOnlyDictionary<string, string> Entities,
    string PromptContext,
    IReadOnlyList<string> UnknownTokens,
    string? BridgeTarget = null)
{
    public static ResolvedShorthand None { get; } =
        new(new Dictionary<string, string>(), string.Empty, []);
}

/// <summary>
/// Deterministic shorthand resolution. Runs before the model so that resolution is
/// testable without an LLM and the unknown-token rule is enforceable rather than a
/// model judgement. The original text is never rewritten — see D2 in the spec.
/// </summary>
internal static class ShorthandResolver
{
    /// <summary>A trailing alphabetic token of this length or shorter looks like a person marker.</summary>
    private const int ShortTokenMaxLength = 3;

    public static ResolvedShorthand Resolve(string content, GlossarySnapshot glossary)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(glossary);

        if (glossary.IsEmpty || string.IsNullOrWhiteSpace(content))
        {
            return ResolvedShorthand.None;
        }

        var entities = new Dictionary<string, string>(StringComparer.Ordinal);
        var context = new StringBuilder();

        // D3: a prefix marker delimits subject matter. Resolve the prefix itself, then
        // stop — everything after it is content, not shorthand.
        var prefix = glossary.Prefixes.Keys
            .Where(p => content.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();

        if (prefix is not null)
        {
            var target = glossary.Prefixes[prefix];
            entities["prefix"] = target;

            // D5: an owner-qualified value names a repo, so the capture routes there
            // deterministically and repo inference is skipped.
            var isRepo = target.Contains('/', StringComparison.Ordinal);
            context.Append(CultureInfoInvariant($"\"{prefix}\" marks this capture as: {target}."));
            return new ResolvedShorthand(
                entities, context.ToString(), [], isRepo ? target : null);
        }

        foreach (var (token, expansion) in glossary.Acronyms)
        {
            if (MatchesWholeWord(content, token))
            {
                entities["acronym"] = expansion;
                context.Append(CultureInfoInvariant($"\"{token}\" means \"{expansion}\". "));
                break;
            }
        }

        string? matchedPerson = null;
        foreach (var (token, name) in glossary.People)
        {
            if (MatchesWholeWord(content, token))
            {
                matchedPerson = token;
                entities["person"] = name;
                context.Append(CultureInfoInvariant($"\"{token}\" refers to the person {name}. "));
                break;
            }
        }

        if (content.Contains('>', StringComparison.Ordinal))
        {
            var after = content[(content.IndexOf('>', StringComparison.Ordinal) + 1)..].Trim();
            var followedByPerson = glossary.People.Keys
                .Any(t => after.StartsWith(t, StringComparison.OrdinalIgnoreCase)
                          && (after.Length == t.Length || !char.IsLetter(after[t.Length])));

            entities["operator"] = followedByPerson ? "recommend-to" : "in-style-of";
            context.Append(followedByPerson
                ? "\">\" before a person means: recommend this to that person. "
                : "\">\" before a thing means: in the style of that thing. ");
        }

        return new ResolvedShorthand(entities, context.ToString().TrimEnd(), FindUnknownTokens(content, glossary, matchedPerson));
    }

    private static IReadOnlyList<string> FindUnknownTokens(
        string content, GlossarySnapshot glossary, string? matchedPerson)
    {
        // Deliberately narrow: only a trailing short alphabetic token. A broader rule
        // parks a large fraction of ordinary captures on common short words.
        var trailing = content.Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (trailing is null
            || trailing.Length > ShortTokenMaxLength
            || !trailing.All(char.IsLetter))
        {
            return [];
        }

        if (matchedPerson is not null
            || glossary.People.ContainsKey(trailing)
            || glossary.Acronyms.ContainsKey(trailing))
        {
            return [];
        }

        return [trailing];
    }

    private static bool MatchesWholeWord(string content, string token) =>
        Regex.IsMatch(
            content,
            $@"(?<![\p{{L}}]){Regex.Escape(token)}(?![\p{{L}}])",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

    private static string CultureInfoInvariant(FormattableString s) =>
        s.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~ShorthandResolverTests"`
Expected: PASS — 20 tests. If the case-insensitive person lookup misses, note that `GlossarySnapshot` dictionaries come from JSON with default (ordinal) comparers; `ContainsKey` checks in `FindUnknownTokens` are therefore case-sensitive by design — the regex match is what handles case.

- [ ] **Step 5: Commit and push**

```bash
git add source/FlowHub.AI/ShorthandResolver.cs tests/FlowHub.Web.ComponentTests/Ai/ShorthandResolverTests.cs
git commit -m "feat(ai): resolve people, acronyms, prefixes and operators in capture text (#83)"
git push
```

---

### Task 4: Put the glossary context in the system prompt

**Files:**
- Modify: `source/FlowHub.AI/AiPrompts.cs:11-12` (signature) and the returned template
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiPromptsGlossaryTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks — takes a plain string.
- Produces: `AiPrompts.BuildSystemPrompt(IReadOnlyCollection<string> vikunjaBuckets, bool allowBridge = false, string glossaryContext = "")` and the matching third parameter on `BuildMessages`.

The new parameter goes **last with a default**, so every existing call site and test keeps compiling.

- [ ] **Step 1: Write the failing test**

```csharp
using FlowHub.AI;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiPromptsGlossaryTests
{
    private static readonly string[] DefaultBuckets = ["Inbox", "Movies"];

    [Fact]
    public void BuildSystemPrompt_NoGlossary_IsByteIdenticalToTheCurrentPrompt()
    {
        // The guard that matters: an unconfigured deployment must classify exactly as
        // it does today. Prompt drift reclassifies every capture.
        var without = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true);
        var withEmpty = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true, glossaryContext: "");

        withEmpty.Should().Be(without);
    }

    [Fact]
    public void BuildSystemPrompt_WhitespaceGlossary_IsAlsoByteIdentical()
    {
        AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true, glossaryContext: "   ")
            .Should().Be(AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true));
    }

    [Fact]
    public void BuildSystemPrompt_WithGlossary_IncludesTheContext()
    {
        var prompt = AiPrompts.BuildSystemPrompt(
            DefaultBuckets, allowBridge: true, glossaryContext: "\"ZZZ\" means \"Zed Zed Zed\".");

        prompt.Should().Contain("Zed Zed Zed");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiPromptsGlossaryTests"`
Expected: FAIL — build error, `BuildSystemPrompt` has no `glossaryContext` parameter.

- [ ] **Step 3: Write minimal implementation**

In `source/FlowHub.AI/AiPrompts.cs`, change the signature and add the section. Append the block **after** the existing template so the empty case is byte-identical:

```csharp
    internal static string BuildSystemPrompt(
        IReadOnlyCollection<string> vikunjaBuckets,
        bool allowBridge = false,
        string glossaryContext = "")
    {
```

At the end of the method, replace `return string.Create(...);` with:

```csharp
        var prompt = string.Create(CultureInfo.InvariantCulture, $$"""
            ... existing template, unchanged ...
            """);

        // Appended, never interleaved: an empty glossary must leave the prompt
        // byte-identical to the pre-glossary output.
        return string.IsNullOrWhiteSpace(glossaryContext)
            ? prompt
            : prompt + "\n\nThe operator writes in private shorthand. For this capture:\n"
                     + glossaryContext;
    }
```

And thread it through `BuildMessages`:

```csharp
    internal static IList<ChatMessage> BuildMessages(
        string content,
        IReadOnlyCollection<string> vikunjaBuckets,
        bool allowBridge = false,
        string glossaryContext = "") =>
    [
        new ChatMessage(ChatRole.System, BuildSystemPrompt(vikunjaBuckets, allowBridge, glossaryContext)),
        new ChatMessage(ChatRole.User, content),
    ];
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiPrompts"`
Expected: PASS — the new glossary tests **and** the pre-existing `AiPromptsTests`, including `BuildSystemPrompt_BridgeDisabled_MatchesThePreBridgeWording`. If that one fails, the glossary section is being interleaved rather than appended — fix the implementation, never the test.

- [ ] **Step 5: Commit and push**

```bash
git add source/FlowHub.AI/AiPrompts.cs tests/FlowHub.Web.ComponentTests/Ai/AiPromptsGlossaryTests.cs
git commit -m "feat(ai): append resolved glossary context to the system prompt (#83)"
git push
```

---

### Task 5: Wire the resolver into the classifier

**Files:**
- Modify: `source/FlowHub.Core/Classification/ClassificationResult.cs` (add `UnknownShorthand`)
- Modify: `source/FlowHub.AI/AiClassifier.cs:25-53` (field + ctor) and `:55-105` (`ClassifyAsync`)
- Modify: `source/FlowHub.AI/AiServiceCollectionExtensions.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierGlossaryTests.cs`

**Interfaces:**
- Consumes: `IGlossary` (Task 1), `FileGlossarySource` (Task 2), `ShorthandResolver.Resolve` (Task 3), `BuildMessages(..., glossaryContext)` (Task 4).
- Produces: `ClassificationResult` gains `IReadOnlyList<string>? UnknownShorthand = null` as the **last** parameter.

- [ ] **Step 1: Write the failing test**

```csharp
using FlowHub.AI;
using FlowHub.Core.Classification;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiClassifierGlossaryTests
{
    [Fact]
    public void ClassificationResult_CarriesUnknownShorthand()
    {
        var result = new ClassificationResult(
            ["t"], "Vikunja", UnknownShorthand: ["qq"]);

        result.UnknownShorthand.Should().ContainSingle().Which.Should().Be("qq");
    }

    [Fact]
    public void ClassificationResult_DefaultsUnknownShorthandToNull()
    {
        new ClassificationResult(["t"], "Vikunja").UnknownShorthand.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiClassifierGlossaryTests"`
Expected: FAIL — build error, `UnknownShorthand` does not exist.

- [ ] **Step 3: Write minimal implementation**

`ClassificationResult.cs` — append the parameter **last**:

```csharp
public sealed record ClassificationResult(
    IReadOnlyList<string> Tags,
    string MatchedSkill,
    string? Title = null,
    string? VikunjaProject = null,
    IReadOnlyDictionary<string, string>? Entities = null,
    ClassifierTrace? Trace = null,
    string? BridgeAlias = null,
    BridgeAction BridgeAction = BridgeAction.Unknown,
    string? BridgeBody = null,
    string? BridgeTarget = null,
    IReadOnlyList<string>? UnknownShorthand = null);
```

`AiClassifier.cs` — add the field, a constructor parameter `IGlossary glossary` (append it **last** so existing construction keeps compiling), and inside `ClassifyAsync`, after the bucket fetch:

```csharp
            var glossary = await _glossary.GetAsync(cancellationToken);
            var shorthand = ShorthandResolver.Resolve(content, glossary);

            var response = await _chat.GetResponseAsync<AiClassificationResponse>(
                AiPrompts.BuildMessages(content, buckets, _allowBridgeClassification, shorthand.PromptContext),
                _options,
                cancellationToken: cancellationToken);
```

and replace the entities construction so model entities win over resolved ones:

```csharp
            IReadOnlyDictionary<string, string>? entities = MergeEntities(shorthand.Entities, payload.Entities);

            sw.Stop();
            return new ClassificationResult(
                payload.Tags, payload.MatchedSkill, payload.Title, project, entities, BuildTrace(sw, response),
                // A prefix-supplied repo wins over anything inference produced: it is
                // explicit operator intent, not a guess. It also forces the skill to
                // Bridge — the operator named a repo, so Vikunja is not an option.
                BridgeTarget: shorthand.BridgeTarget,
                UnknownShorthand: shorthand.UnknownTokens.Count > 0 ? shorthand.UnknownTokens : null);
```

with:

```csharp
    private static IReadOnlyDictionary<string, string>? MergeEntities(
        IReadOnlyDictionary<string, string> resolved,
        IReadOnlyDictionary<string, string>? fromModel)
    {
        if (resolved.Count == 0 && fromModel is not { Count: > 0 })
        {
            return null;
        }

        var merged = new Dictionary<string, string>(resolved, StringComparer.Ordinal);
        if (fromModel is not null)
        {
            foreach (var (key, value) in fromModel)
            {
                merged[key] = value;   // the model may override a resolved value
            }
        }

        return merged;
    }
```

`AiServiceCollectionExtensions.cs` — register alongside the existing `TryAddSingleton<IVikunjaProjectCatalog>` block (`:71`):

```csharp
        services.Configure<GlossaryOptions>(configuration.GetSection(GlossaryOptions.SectionName));
        services.TryAddSingleton<IGlossary>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GlossaryOptions>>();
            return string.IsNullOrWhiteSpace(options.Value.Path)
                ? new EmptyGlossary()
                : new FileGlossarySource(
                    options,
                    sp.GetRequiredService<ILogger<FileGlossarySource>>(),
                    sp.GetRequiredService<TimeProvider>());
        });
```

Then pass `sp.GetRequiredService<IGlossary>()` into the `AiClassifier` construction at `:142`.

- [ ] **Step 4: Run the full AI and pipeline suites**

Run: `dotnet test tests/FlowHub.Web.ComponentTests`
Expected: PASS — all pre-existing tests plus the new ones. Existing `AiClassifier` construction sites in tests may need the extra argument; pass `new EmptyGlossary()`.

- [ ] **Step 5: Commit and push**

```bash
git add source/FlowHub.Core/Classification/ClassificationResult.cs source/FlowHub.AI/AiClassifier.cs source/FlowHub.AI/AiServiceCollectionExtensions.cs tests/FlowHub.Web.ComponentTests/Ai/AiClassifierGlossaryTests.cs
git commit -m "feat(ai): resolve shorthand before classifying and surface unknown tokens (#83)"
git push
```

---

### Task 6: Park a capture whose shorthand is unknown

**Files:**
- Modify: `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs:66-80`
- Test: `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerGlossaryTests.cs`

**Interfaces:**
- Consumes: `ClassificationResult.UnknownShorthand` from Task 5.
- Produces: nothing later tasks rely on.

The park goes **before** the Bridge and Orphan checks: an unresolvable token means we do not trust the classification at all, whatever skill it named.

- [ ] **Step 1: Write the failing test**

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using FlowHub.Web.Pipeline;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class CaptureEnrichmentConsumerGlossaryTests
{
    private static IClassifier ClassifierReturning(ClassificationResult result)
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);
        return classifier;
    }

    [Fact]
    public async Task Consume_UnknownShorthand_ParksWithAReasonNamingTheToken()
    {
        var classifier = ClassifierReturning(new ClassificationResult(
            ["t"], "Vikunja", Title: "Farmshop", VikunjaProject: "Inbox",
            UnknownShorthand: ["qq"]));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("Farmshop qq", ChannelKind.Web, default);

        await harness.Bus.Publish(new CaptureCreated(capture.Id, "Farmshop qq", ChannelKind.Web, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        var stored = (await captureService.GetByIdAsync(capture.Id, default))!;
        stored.Stage.Should().Be(LifecycleStage.Unhandled);
        stored.FailureReason.Should().Contain("qq");

        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Consume_NoUnknownShorthand_IsUnaffected()
    {
        var classifier = ClassifierReturning(new ClassificationResult(
            ["t"], "Vikunja", Title: "Farmshop", VikunjaProject: "Inbox"));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("Farmshop", ChannelKind.Web, default);

        await harness.Bus.Publish(new CaptureCreated(capture.Id, "Farmshop", ChannelKind.Web, DateTimeOffset.UtcNow));

        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~CaptureEnrichmentConsumerGlossaryTests"`
Expected: FAIL — the first test fails because the capture is classified normally instead of parked.

- [ ] **Step 3: Write minimal implementation**

In `CaptureEnrichmentConsumer.cs`, immediately **before** the existing Bridge-undetermined check at `:66`:

```csharp
        // An unresolvable shorthand token means the classification cannot be trusted,
        // whatever skill it named. A confident wrong route looks handled; a parked
        // capture does not.
        if (result.UnknownShorthand is { Count: > 0 } unknown)
        {
            await _captureService.MarkUnhandledAsync(
                msg.CaptureId,
                $"unknown shorthand — {string.Join(", ", unknown)}",
                ct);
            LogUnknownShorthand(msg.CaptureId, string.Join(", ", unknown));
            return;
        }
```

and add the logger message alongside the others at the bottom of the class:

```csharp
    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "Capture {CaptureId} parked — unknown shorthand: {Tokens}")]
    private partial void LogUnknownShorthand(Guid captureId, string tokens);
```

Check that EventId 1005 is not already taken in this file; if it is, use the next free one.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/FlowHub.Web.ComponentTests && dotnet test tests/FlowHub.Core.Tests && dotnet test tests/FlowHub.Skills.Tests`
Expected: PASS — all three projects green.

- [ ] **Step 5: Commit and push**

```bash
git add source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerGlossaryTests.cs
git commit -m "feat(web): park captures whose shorthand cannot be resolved (#83)"
git push
```

---

### Task 7: Document the configuration

**Files:**
- Modify: `CHANGELOG.md` (`[Unreleased]` → `Added`)
- Modify: `source/FlowHub.Skills/README.md` **only if** it documents per-skill configuration; otherwise skip and say so.

**Interfaces:**
- Consumes: the configuration keys from Task 2.
- Produces: nothing.

- [ ] **Step 1: Add the changelog entry**

Under `## [Unreleased]` → `### Added` (create the heading if the section is empty):

```markdown
- The classifier resolves the operator's private shorthand — person markers, domain
  acronyms, prefix markers and the overloaded `>` operator — from a glossary file
  supplied by the deployment (`Ai__Glossary__Path`). Resolved meanings reach the model
  as context and the capture as entities. A capture whose trailing shorthand is not in
  the glossary is parked rather than guessed at. With no glossary configured the system
  prompt is byte-identical to before, so behaviour is unchanged. (#83)
```

- [ ] **Step 2: Verify the docs build nothing and commit**

```bash
git add CHANGELOG.md
git commit -m "docs(ai): document the shorthand glossary configuration (#83)"
git push
```

---

## Self-review

**Spec coverage:** D1 storage → Tasks 1–2. D2 hybrid resolve-then-prompt → Tasks 3–5. D3 three roles + prefix suppression → Task 3 (`Resolve_AcronymAfterAPrefix_IsSubjectMatterAndIsNotExpanded`). D4 byte-identical empty prompt → Task 4. Resolution rules → Task 3. Pipeline placement → Task 5. Error-handling table → Task 2. Parking → Task 6. Non-goals need no task by definition.

**Placeholder scan:** every code step carries real code; no TBDs.

**Type consistency:** `GlossarySnapshot(People, Acronyms, Prefixes)` is used with those names in Tasks 2, 3 and 5. `ResolvedShorthand(Entities, PromptContext, UnknownTokens)` is produced in Task 3 and consumed in Task 5. `ClassificationResult.UnknownShorthand` is added in Task 5 and read in Task 6. `BuildMessages`' fourth parameter is added in Task 4 and called in Task 5.

**Known gap carried deliberately:** `ShorthandResolver` resolves at most one acronym and one person per capture (first match wins). Multi-person captures are rare in the corpus and a list-valued entity would complicate the merge; revisit if real captures need it.
