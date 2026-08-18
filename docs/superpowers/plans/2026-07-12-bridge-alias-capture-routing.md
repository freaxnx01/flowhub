# Bridge Alias Capture Routing — Implementation Plan (FlowHub side)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route a capture whose leading token is a known repo alias (`br`, `agp`, …) to the `bridge` service as either a new **issue** or a new **`ideas.md` entry**, with the AI deciding issue-vs-idea and bridge doing the forge work.

**Architecture:** A new `BridgeSkillIntegration : ISkillIntegration` (`Name = "Bridge"`) mirrors Wallabag/Vikunja and POSTs to bridge's REST API (`/api/capture/issue|idea`) with a Bearer token; bridge resolves the alias server-side. An `IBridgeCatalog` (thin, TTL-cached client over `GET /api/repos`) exposes the known alias **set** to the classifiers. `KeywordClassifier` does the cheap leading-token alias match (short-circuit `MatchedSkill=Bridge`); `AiClassifier` decides `Issue`/`Idea`/`Unknown` + a title via a dedicated structured schema. `ClassificationResult` and the `CaptureClassified` event gain `BridgeAlias`/`BridgeAction`, carried transiently onto the in-memory `Capture` in `SkillRoutingConsumer` (no persistence, no EF migration). `BridgeAction=Unknown` (low confidence) is gated at routing time to `Unhandled` so the capture stays in the Inbox for `/flowhub-triage`.

**Tech Stack:** .NET 10, C#, MassTransit, `Microsoft.Extensions.AI`, `IHttpClientFactory`/typed clients, `IOptions<T>`; tests with xUnit + FluentAssertions + NSubstitute, WireMock.Net (contract), RichardSzalay.MockHttp (unit).

**Scope:** This plan is the **FlowHub half only**. The bridge-repo changes (`.bridge.yaml` indexing, alias resolution, issue `body` field, `BRIDGE_API_TOKEN` auth) are a separate tracked issue/PR in `~/repos/github/freaxnx01/public/bridge` (spec §B, §Rollout step 1). FlowHub tests stub bridge; nothing here touches the bridge repo. The FlowHub integration is **inert until `Skills:Bridge:BaseUrl` is configured**, so it can merge ahead of the bridge deploy.

## Global Constraints

- **.NET 10; nullable enabled; warnings-as-errors** (`Directory.Build.props`) — all new code must compile with zero warnings. No new NuGet packages (guardrail); all needed test packages already exist in `Directory.Packages.props` (WireMock.Net 1.7.4, RichardSzalay.MockHttp 7.0.0, NSubstitute 5.3.0, FluentAssertions 6.12.2, xUnit 2.9.3, Xunit.SkippableFact 1.4.13).
- **TDD, non-negotiable** (CLAUDE.md): write the failing test first; run it and confirm it fails for the right reason; implement minimal code; run to green; **run the full `dotnet test FlowHub.slnx` before considering a task done**. Never edit a test to force green; never hardcode/stub logic to satisfy a test. Stop after 3 failed attempts and explain.
- **Test naming:** `MethodName_StateUnderTest_ExpectedBehavior`.
- **Config namespace:** use `Skills:Bridge` (env `Skills__Bridge__BaseUrl`, `Skills__Bridge__ApiToken`, `Skills__Bridge__CatalogTtl`). **This deviates from the spec prose** (`Bridge__*`) — chosen deliberately to match the three existing skills (`Skills:Wallabag`, `Skills:Vikunja`, `Skills:Paperless`) and their `SectionName = "Skills:<X>"` convention. The config-key names are not in the spec's locked-Decisions list, so this is a convention alignment, not a decision reversal.
- **Fail-closed registration:** `AddBridge` registers nothing (emits `SkillsRegistrationOutcome("Bridge", Registered:false, reason)`) unless both `BaseUrl` and `ApiToken` are non-empty — exactly like `AddVikunja`.
- **HTTP error convention (mirror Wallabag/Vikunja):** integrations call `response.EnsureSuccessStatusCode()` and let exceptions propagate. Do **not** catch-and-return `SkillResult(Success:false)`. The throw engages MassTransit retry (`r.Intervals(500, 2000, 5000)`), then `LifecycleFaultObserver` marks the capture `Unhandled`.
- **No EF migration.** `BridgeAlias`/`BridgeAction` live on `CaptureClassified` and are injected transiently onto the in-memory `Capture` in `SkillRoutingConsumer` (same mechanism already used for `EnrichmentDescription`). Do not add columns to `CaptureEntity`/`CaptureEntityTypeConfiguration`.
- **`MatchedSkill` value is the literal string `"Bridge"`** — the classifier emits it, `BridgeSkillIntegration.Name` returns it, `SkillRoutingConsumer` matches it ordinal.
- **`ExternalRef` mapping:** the spec's `SkillResult { success, url }` maps `url` → `SkillResult.ExternalRef` (the click-through ref persisted by `MarkCompletedAsync`).

## File Structure

New files:
- `source/FlowHub.Core/Skills/IBridgeCatalog.cs` — driven port: known alias set.
- `source/FlowHub.Core/Skills/EmptyBridgeCatalog.cs` — null-object (empty set); DI fallback so classifiers always resolve a catalog.
- `source/FlowHub.Core/Classification/BridgeAction.cs` — `enum BridgeAction { Unknown, Issue, Idea }`.
- `source/FlowHub.Core/Classification/BridgeAliasMatcher.cs` — pure leading-token matcher.
- `source/FlowHub.Skills/Bridge/BridgeOptions.cs` — `SectionName = "Skills:Bridge"`, `BaseUrl`/`ApiToken`/`CatalogTtl`.
- `source/FlowHub.Skills/Bridge/BridgeCatalog.cs` — `IBridgeCatalog` impl over `GET /api/repos`, TTL cache, graceful degradation.
- `source/FlowHub.Skills/Bridge/BridgeSkillIntegration.cs` — `ISkillIntegration` (`Name="Bridge"`), POSTs issue/idea.
- `source/FlowHub.AI/BridgeDecisionResponse.cs` — structured-output schema for the issue-vs-idea decision.
- Test files listed per task.

Modified files:
- `source/FlowHub.Core/Classification/ClassificationResult.cs` — add `BridgeAlias`, `BridgeAction`.
- `source/FlowHub.Core/Classification/KeywordClassifier.cs` — inject `IBridgeCatalog?`, async alias short-circuit.
- `source/FlowHub.Core/Captures/Capture.cs` — add transient `BridgeAlias`, `BridgeAction`.
- `source/FlowHub.Core/Events/CaptureClassified.cs` — add `BridgeAlias`, `BridgeAction`.
- `source/FlowHub.AI/AiPrompts.cs` — add `BuildBridgeMessages`.
- `source/FlowHub.AI/AiClassifier.cs` — inject `IBridgeCatalog`, add Bridge decision path + confidence gate.
- `source/FlowHub.AI/AiServiceCollectionExtensions.cs` — register `EmptyBridgeCatalog` fallback; pass `IBridgeCatalog` into `AiClassifier`.
- `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs` — thread `BridgeAlias`/`BridgeAction` into published event.
- `source/FlowHub.Web/Pipeline/SkillRoutingConsumer.cs` — low-confidence gate + transient field injection.
- `source/FlowHub.Skills/SkillsServiceCollectionExtensions.cs` — `AddBridge`.
- `source/FlowHub.Web/appsettings.json` — `Skills:Bridge` sentinel block.
- `.env.example` — `Skills__Bridge__*` keys.

**Dependency order / parallelism:** 1 → 2; 3 → {4, 5, 6}; 6 → 7; 3 → 8; {2, 8} → 9. Tasks 4/5/6 depend only on 3 (and 4 also on 1); 8 depends only on 3 (+1). Execute in the numbered order; a subagent runner does one task at a time with review between.

---

### Task 1: `IBridgeCatalog` port + `EmptyBridgeCatalog` null-object + `BridgeOptions`

**Files:**
- Create: `source/FlowHub.Core/Skills/IBridgeCatalog.cs`
- Create: `source/FlowHub.Core/Skills/EmptyBridgeCatalog.cs`
- Create: `source/FlowHub.Skills/Bridge/BridgeOptions.cs`
- Test: `tests/FlowHub.Skills.Tests/Bridge/EmptyBridgeCatalogTests.cs`

**Interfaces:**
- Produces: `FlowHub.Core.Skills.IBridgeCatalog` — `Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken cancellationToken)` (returns the set of known, lowercased repo aliases; never throws — degrades to the last-known/empty set). `FlowHub.Core.Skills.EmptyBridgeCatalog : IBridgeCatalog` (empty set). `FlowHub.Skills.Bridge.BridgeOptions` — `const string SectionName = "Skills:Bridge"`, `string? BaseUrl`, `string? ApiToken`, `TimeSpan CatalogTtl = TimeSpan.FromMinutes(5)`.

- [ ] **Step 1: Write the failing test**

Create `tests/FlowHub.Skills.Tests/Bridge/EmptyBridgeCatalogTests.cs`:
```csharp
using FlowHub.Core.Skills;

namespace FlowHub.Skills.Tests.Bridge;

public sealed class EmptyBridgeCatalogTests
{
    [Fact]
    public async Task GetAliasesAsync_Always_ReturnsEmptySet()
    {
        var sut = new EmptyBridgeCatalog();

        var aliases = await sut.GetAliasesAsync(TestContext.Current.CancellationToken);

        aliases.Should().BeEmpty();
    }
}
```
(`Usings.cs` in `FlowHub.Skills.Tests` already globally imports `Xunit`, `FluentAssertions`, `NSubstitute`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter FullyQualifiedName~EmptyBridgeCatalogTests`
Expected: **build failure** — `IBridgeCatalog`/`EmptyBridgeCatalog` do not exist.

- [ ] **Step 3: Create the port**

`source/FlowHub.Core/Skills/IBridgeCatalog.cs`:
```csharp
namespace FlowHub.Core.Skills;

/// <summary>
/// Driven port exposing the set of known repo aliases indexed by the bridge
/// service (from its <c>GET /api/repos</c> catalog). Used by the classifiers to
/// recognise a capture whose leading token is a repo alias. Implementations must
/// never throw — degrade to the last-known or empty set so classification is
/// always able to proceed.
/// </summary>
public interface IBridgeCatalog
{
    Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Create the null-object**

`source/FlowHub.Core/Skills/EmptyBridgeCatalog.cs`:
```csharp
namespace FlowHub.Core.Skills;

/// <summary>
/// No-op <see cref="IBridgeCatalog"/> registered as the DI fallback so the
/// classifiers resolve a catalog even when the Bridge skill is not configured.
/// Returns no aliases, so no capture is ever routed to Bridge.
/// </summary>
public sealed class EmptyBridgeCatalog : IBridgeCatalog
{
    private static readonly IReadOnlySet<string> Empty =
        new HashSet<string>(StringComparer.Ordinal);

    public Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Empty);
}
```

- [ ] **Step 5: Create the options**

`source/FlowHub.Skills/Bridge/BridgeOptions.cs`:
```csharp
namespace FlowHub.Skills.Bridge;

public sealed class BridgeOptions
{
    public const string SectionName = "Skills:Bridge";

    public string? BaseUrl { get; set; }
    public string? ApiToken { get; set; }

    /// <summary>How long the alias catalog is cached before a refresh from bridge.</summary>
    public TimeSpan CatalogTtl { get; set; } = TimeSpan.FromMinutes(5);
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter FullyQualifiedName~EmptyBridgeCatalogTests`
Expected: PASS.

- [ ] **Step 7: Full suite + commit**

Run: `dotnet test FlowHub.slnx`
Expected: PASS (no regressions).
```bash
git add source/FlowHub.Core/Skills/IBridgeCatalog.cs \
        source/FlowHub.Core/Skills/EmptyBridgeCatalog.cs \
        source/FlowHub.Skills/Bridge/BridgeOptions.cs \
        tests/FlowHub.Skills.Tests/Bridge/EmptyBridgeCatalogTests.cs
git commit -m "feat(skills): add IBridgeCatalog port, empty fallback, and BridgeOptions"
```

---

### Task 2: `BridgeCatalog` — TTL-cached alias-set client over `GET /api/repos`

**Files:**
- Create: `source/FlowHub.Skills/Bridge/BridgeCatalog.cs`
- Test: `tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogTests.cs` (unit, MockHttp)
- Test: `tests/FlowHub.Skills.ContractTests/Bridge/BridgeCatalogContractTests.cs` (WireMock)

**Interfaces:**
- Consumes: `IBridgeCatalog`, `BridgeOptions` (Task 1); `HttpClient` (typed client, `BaseAddress` set by DI in Task 9); `TimeProvider`; `ILogger<BridgeCatalog>`.
- Produces: `FlowHub.Skills.Bridge.BridgeCatalog : IBridgeCatalog`. Reads `GET /api/repos` → JSON array; each element may carry `"alias"`. Collects non-empty, lowercased aliases into an `IReadOnlySet<string>`. Caches for `BridgeOptions.CatalogTtl` behind a `SemaphoreSlim(1,1)` single-flight. On refresh failure keeps the last-known set; on first-fetch failure returns empty. **Never throws.**

Contract of the bridge response FlowHub relies on (produced by the separate bridge PR, spec §B.1): `GET /api/repos → [ { "alias": "br", ... }, { "alias": "agp", ... }, { ... no alias ... } ]`. FlowHub reads only `alias`; unknown fields are ignored.

- [ ] **Step 1: Write the failing unit tests**

Create `tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogTests.cs`:
```csharp
using System.Net;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;

namespace FlowHub.Skills.Tests.Bridge;

public sealed class BridgeCatalogTests
{
    private const string BaseUrl = "https://bridge.example.com";

    private static BridgeCatalog CreateSut(MockHttpMessageHandler mock, TimeProvider clock, TimeSpan? ttl = null)
    {
        var http = mock.ToHttpClient();
        http.BaseAddress = new Uri(BaseUrl);
        var options = Options.Create(new BridgeOptions
        {
            BaseUrl = BaseUrl,
            ApiToken = "tok",
            CatalogTtl = ttl ?? TimeSpan.FromMinutes(5),
        });
        return new BridgeCatalog(http, options, clock, NullLogger<BridgeCatalog>.Instance);
    }

    [Fact]
    public async Task GetAliasesAsync_WithAliasField_ReturnsLowercasedNonEmptyAliases()
    {
        var mock = new MockHttpMessageHandler();
        mock.Expect(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .WithHeaders("Authorization", "Bearer tok")
            .Respond("application/json",
                """[{"alias":"BR"},{"alias":"agp"},{"name":"no-alias-repo"},{"alias":""}]""");
        var sut = CreateSut(mock, TimeProvider.System);

        var aliases = await sut.GetAliasesAsync(TestContext.Current.CancellationToken);

        aliases.Should().BeEquivalentTo("br", "agp");
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task GetAliasesAsync_WithinTtl_HitsBridgeOnceAndServesCache()
    {
        var mock = new MockHttpMessageHandler();
        mock.Expect(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond("application/json", """[{"alias":"br"}]""");
        var clock = new FakeTimeProvider();
        var sut = CreateSut(mock, clock, TimeSpan.FromMinutes(5));

        var first = await sut.GetAliasesAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await sut.GetAliasesAsync(TestContext.Current.CancellationToken);

        first.Should().BeEquivalentTo("br");
        second.Should().BeEquivalentTo("br");
        mock.VerifyNoOutstandingExpectation(); // only ONE expectation was set → only one call made
    }

    [Fact]
    public async Task GetAliasesAsync_WhenBridgeFailsOnFirstFetch_ReturnsEmptyAndDoesNotThrow()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Get, $"{BaseUrl}/api/repos").Respond(HttpStatusCode.ServiceUnavailable);
        var sut = CreateSut(mock, TimeProvider.System);

        var aliases = await sut.GetAliasesAsync(TestContext.Current.CancellationToken);

        aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAliasesAsync_WhenRefreshFails_KeepsLastKnownSet()
    {
        var mock = new MockHttpMessageHandler();
        mock.Expect(HttpMethod.Get, $"{BaseUrl}/api/repos")
            .Respond("application/json", """[{"alias":"br"}]""");
        mock.When(HttpMethod.Get, $"{BaseUrl}/api/repos").Respond(HttpStatusCode.ServiceUnavailable);
        var clock = new FakeTimeProvider();
        var sut = CreateSut(mock, clock, TimeSpan.FromMinutes(5));

        var first = await sut.GetAliasesAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(10)); // force refresh, which now fails
        var second = await sut.GetAliasesAsync(TestContext.Current.CancellationToken);

        first.Should().BeEquivalentTo("br");
        second.Should().BeEquivalentTo("br"); // last-known retained
    }
}
```

`FakeTimeProvider` helper — if the repo has no shared one, add `tests/FlowHub.Skills.Tests/Bridge/FakeTimeProvider.cs`:
```csharp
namespace FlowHub.Skills.Tests.Bridge;

/// <summary>Minimal controllable TimeProvider for cache-TTL tests.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
```
(Check first: `grep -rl "class FakeTimeProvider\|Microsoft.Extensions.TimeProvider.Testing" tests/`. If a `FakeTimeProvider` or `Microsoft.Extensions.Time.Testing` already exists in the test projects, use that instead of adding this file.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter FullyQualifiedName~BridgeCatalogTests`
Expected: **build failure** — `BridgeCatalog` does not exist.

- [ ] **Step 3: Implement `BridgeCatalog`**

`source/FlowHub.Skills/Bridge/BridgeCatalog.cs`:
```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowHub.Core.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowHub.Skills.Bridge;

/// <summary>
/// <see cref="IBridgeCatalog"/> over bridge's <c>GET /api/repos</c>. Caches the
/// alias set for <see cref="BridgeOptions.CatalogTtl"/> behind a single-flight
/// gate. Degrades gracefully: keeps the last-known set on a refresh failure and
/// returns an empty set if the very first fetch fails — never throws, so a
/// classifier can always proceed (a capture simply won't route to Bridge).
/// </summary>
public sealed partial class BridgeCatalog : IBridgeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly BridgeOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<BridgeCatalog> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlySet<string> _cache = new HashSet<string>(StringComparer.Ordinal);
    private DateTimeOffset? _fetchedAt;

    public BridgeCatalog(
        HttpClient http,
        IOptions<BridgeOptions> options,
        TimeProvider clock,
        ILogger<BridgeCatalog> log)
    {
        _http = http;
        _options = options.Value;
        _clock = clock;
        _log = log;
    }

    public async Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (_fetchedAt is { } at && now - at < _options.CatalogTtl)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = _clock.GetUtcNow();
            if (_fetchedAt is { } at2 && now - at2 < _options.CatalogTtl)
            {
                return _cache; // another caller refreshed while we waited
            }

            var fresh = await FetchAsync(cancellationToken);
            if (fresh is not null)
            {
                _cache = fresh;
                _fetchedAt = now;
            }
            else if (_fetchedAt is null)
            {
                // First fetch failed: mark as fetched so we serve the empty set and
                // back off until the next TTL window rather than hammering bridge.
                _fetchedAt = now;
            }

            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlySet<string>?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/repos");
            if (!string.IsNullOrWhiteSpace(_options.ApiToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
            }

            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var repos = await response.Content.ReadFromJsonAsync<BridgeRepo[]>(JsonOptions, cancellationToken)
                        ?? [];

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var repo in repos)
            {
                if (!string.IsNullOrWhiteSpace(repo.Alias))
                {
                    set.Add(repo.Alias.Trim().ToLowerInvariant());
                }
            }

            return set;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRefreshFailed(ex.GetType().Name, ex.Message);
            return null;
        }
    }

    private sealed record BridgeRepo(string? Alias);

    [LoggerMessage(
        EventId = 3030,
        Level = LogLevel.Warning,
        Message = "BridgeCatalog refresh failed ({ExceptionType}: {Reason}); serving last-known alias set")]
    private partial void LogRefreshFailed(string exceptionType, string reason);
}
```

- [ ] **Step 4: Run unit tests to verify they pass**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter FullyQualifiedName~BridgeCatalogTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Write the WireMock contract test**

Create `tests/FlowHub.Skills.ContractTests/Bridge/BridgeCatalogContractTests.cs`:
```csharp
using System.Net.Http.Headers;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace FlowHub.Skills.ContractTests.Bridge;

public sealed class BridgeCatalogContractTests : IClassFixture<WireMockServerFixture>
{
    private readonly WireMockServerFixture _wire;

    public BridgeCatalogContractTests(WireMockServerFixture wire)
    {
        _wire = wire;
        _wire.Server.Reset();
    }

    private BridgeCatalog CreateSut()
    {
        var http = new HttpClient { BaseAddress = new Uri(_wire.BaseUrl) };
        var options = Options.Create(new BridgeOptions
        {
            BaseUrl = _wire.BaseUrl,
            ApiToken = "contract-tok",
            CatalogTtl = TimeSpan.FromMinutes(5),
        });
        return new BridgeCatalog(http, options, TimeProvider.System, NullLogger<BridgeCatalog>.Instance);
    }

    [Fact]
    public async Task GetAliasesAsync_AgainstStubbedBridge_ReturnsAliasSetWithBearer()
    {
        _wire.Server
            .Given(Request.Create().WithPath("/api/repos").UsingGet()
                .WithHeader("Authorization", "Bearer contract-tok"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""[{"alias":"br"},{"alias":"agp"}]"""));

        var aliases = await CreateSut().GetAliasesAsync(TestContext.Current.CancellationToken);

        aliases.Should().BeEquivalentTo("br", "agp");
    }
}
```
(`FlowHub.Skills.ContractTests/Usings.cs` globally imports `Xunit` + `FluentAssertions`; `WireMockServerFixture` already exists with `.Server`, `.BaseUrl`, `.Reset()`.)

- [ ] **Step 6: Run the contract test**

Run: `dotnet test tests/FlowHub.Skills.ContractTests --filter FullyQualifiedName~BridgeCatalogContractTests`
Expected: PASS.

- [ ] **Step 7: Full suite + commit**

Run: `dotnet test FlowHub.slnx`
Expected: PASS.
```bash
git add source/FlowHub.Skills/Bridge/BridgeCatalog.cs \
        tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogTests.cs \
        tests/FlowHub.Skills.Tests/Bridge/FakeTimeProvider.cs \
        tests/FlowHub.Skills.ContractTests/Bridge/BridgeCatalogContractTests.cs
git commit -m "feat(skills): add BridgeCatalog alias-set client with TTL cache and graceful degradation"
```
(Omit `FakeTimeProvider.cs` from `git add` if you reused an existing one.)

---

### Task 3: `BridgeAction` enum + `ClassificationResult` extension + `BridgeAliasMatcher`

**Files:**
- Create: `source/FlowHub.Core/Classification/BridgeAction.cs`
- Create: `source/FlowHub.Core/Classification/BridgeAliasMatcher.cs`
- Modify: `source/FlowHub.Core/Classification/ClassificationResult.cs`
- Test: `tests/FlowHub.Core.Tests/Classification/BridgeAliasMatcherTests.cs`

**Interfaces:**
- Produces: `enum FlowHub.Core.Classification.BridgeAction { Unknown = 0, Issue, Idea }`. `static class BridgeAliasMatcher` with `bool TryMatch(string content, IReadOnlySet<string> aliases, out string alias, out string remainder)` — lowercases the leading whitespace-delimited token; if it's in `aliases`, returns `true` with `alias` = that token and `remainder` = the rest of the content trimmed (possibly empty). `ClassificationResult` gains trailing optional params `string? BridgeAlias = null`, `BridgeAction BridgeAction = BridgeAction.Unknown`.

- [ ] **Step 1: Write the failing tests**

Create `tests/FlowHub.Core.Tests/Classification/BridgeAliasMatcherTests.cs`:
```csharp
using FlowHub.Core.Classification;

namespace FlowHub.Core.Tests.Classification;

public sealed class BridgeAliasMatcherTests
{
    private static readonly IReadOnlySet<string> Aliases =
        new HashSet<string>(StringComparer.Ordinal) { "br", "agp" };

    [Fact]
    public void TryMatch_LeadingTokenIsAlias_ReturnsTrueWithAliasAndRemainder()
    {
        var ok = BridgeAliasMatcher.TryMatch("br the login 500s on Safari", Aliases, out var alias, out var remainder);

        ok.Should().BeTrue();
        alias.Should().Be("br");
        remainder.Should().Be("the login 500s on Safari");
    }

    [Fact]
    public void TryMatch_AliasCasingDiffers_MatchesCaseInsensitively()
    {
        var ok = BridgeAliasMatcher.TryMatch("BR what if repos had a health score", Aliases, out var alias, out var remainder);

        ok.Should().BeTrue();
        alias.Should().Be("br");
        remainder.Should().Be("what if repos had a health score");
    }

    [Fact]
    public void TryMatch_LeadingTokenNotAlias_ReturnsFalse()
    {
        var ok = BridgeAliasMatcher.TryMatch("brew install foo", Aliases, out _, out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryMatch_AliasAloneNoBody_ReturnsTrueWithEmptyRemainder()
    {
        var ok = BridgeAliasMatcher.TryMatch("  br  ", Aliases, out var alias, out var remainder);

        ok.Should().BeTrue();
        alias.Should().Be("br");
        remainder.Should().BeEmpty();
    }

    [Fact]
    public void TryMatch_EmptyAliasSet_ReturnsFalse()
    {
        var ok = BridgeAliasMatcher.TryMatch("br anything", new HashSet<string>(), out _, out _);

        ok.Should().BeFalse();
    }
}
```
(`FlowHub.Core.Tests` uses xUnit; add `using FluentAssertions;` — the exploration confirmed FluentAssertions is available in this project. If there is no global using, the explicit `using FluentAssertions;` at file top covers it; match the style of the neighbouring `KeywordClassifierTests.cs`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FlowHub.Core.Tests --filter FullyQualifiedName~BridgeAliasMatcherTests`
Expected: **build failure** — `BridgeAliasMatcher` does not exist.

- [ ] **Step 3: Create the enum**

`source/FlowHub.Core/Classification/BridgeAction.cs`:
```csharp
namespace FlowHub.Core.Classification;

/// <summary>
/// The action the classifier inferred for a Bridge-routed capture. <see cref="Unknown"/>
/// is the low-confidence fallback: the routing stage leaves such a capture in the Inbox
/// (Unhandled) rather than guessing between issue and idea.
/// </summary>
public enum BridgeAction
{
    Unknown = 0,
    Issue,
    Idea,
}
```

- [ ] **Step 4: Create the matcher**

`source/FlowHub.Core/Classification/BridgeAliasMatcher.cs`:
```csharp
namespace FlowHub.Core.Classification;

/// <summary>
/// Pure, dependency-free leading-token matcher: if the first whitespace-delimited
/// token of a capture (lowercased) is a known repo alias, extracts the alias and the
/// remaining body text. Used by both classifiers to recognise Bridge-routed captures.
/// </summary>
public static class BridgeAliasMatcher
{
    private static readonly char[] Whitespace = [' ', '\t', '\n', '\r'];

    public static bool TryMatch(
        string content,
        IReadOnlySet<string> aliases,
        out string alias,
        out string remainder)
    {
        alias = string.Empty;
        remainder = string.Empty;

        if (string.IsNullOrWhiteSpace(content) || aliases.Count == 0)
        {
            return false;
        }

        var trimmed = content.TrimStart();
        var spaceIndex = trimmed.IndexOfAny(Whitespace);
        var firstToken = (spaceIndex < 0 ? trimmed : trimmed[..spaceIndex]).ToLowerInvariant();

        if (!aliases.Contains(firstToken))
        {
            return false;
        }

        alias = firstToken;
        remainder = spaceIndex < 0 ? string.Empty : trimmed[(spaceIndex + 1)..].Trim();
        return true;
    }
}
```

- [ ] **Step 5: Extend `ClassificationResult`**

Modify `source/FlowHub.Core/Classification/ClassificationResult.cs` — add the two trailing optional params:
```csharp
namespace FlowHub.Core.Classification;

public sealed record ClassificationResult(
    IReadOnlyList<string> Tags,
    string MatchedSkill,
    string? Title = null,
    string? VikunjaProject = null,
    IReadOnlyDictionary<string, string>? Entities = null,
    ClassifierTrace? Trace = null,
    string? BridgeAlias = null,
    BridgeAction BridgeAction = BridgeAction.Unknown);
```
(Trailing optional params keep every existing positional call site — e.g. `new ClassificationResult(["link"], "Wallabag")` — compiling unchanged.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/FlowHub.Core.Tests --filter FullyQualifiedName~BridgeAliasMatcherTests`
Expected: PASS (5 tests).

- [ ] **Step 7: Full suite + commit**

Run: `dotnet test FlowHub.slnx`
Expected: PASS.
```bash
git add source/FlowHub.Core/Classification/BridgeAction.cs \
        source/FlowHub.Core/Classification/BridgeAliasMatcher.cs \
        source/FlowHub.Core/Classification/ClassificationResult.cs \
        tests/FlowHub.Core.Tests/Classification/BridgeAliasMatcherTests.cs
git commit -m "feat(classification): add BridgeAction, BridgeAliasMatcher, and ClassificationResult bridge fields"
```

---

### Task 4: `KeywordClassifier` alias short-circuit + DI fallback registration

**Files:**
- Modify: `source/FlowHub.Core/Classification/KeywordClassifier.cs`
- Modify: `source/FlowHub.AI/AiServiceCollectionExtensions.cs` (register `EmptyBridgeCatalog` fallback)
- Test: `tests/FlowHub.Core.Tests/Classification/KeywordClassifierBridgeTests.cs`

**Interfaces:**
- Consumes: `IBridgeCatalog` (Task 1), `BridgeAliasMatcher` + `BridgeAction` (Task 3).
- Produces: `KeywordClassifier` gains an optional ctor param `IBridgeCatalog? bridgeCatalog = null`. When non-null, `ClassifyAsync` first awaits the alias set and, on a leading-token match, short-circuits to `new ClassificationResult(["bridge"], "Bridge", BridgeAlias: alias, BridgeAction: BridgeAction.Unknown, Trace: …)` — keyword classification cannot decide issue-vs-idea, so it emits `Unknown` (routing then parks it in the Inbox until the AI path runs). When null (e.g. `new KeywordClassifier()` in existing tests), behaviour is unchanged.

- [ ] **Step 1: Write the failing tests**

Create `tests/FlowHub.Core.Tests/Classification/KeywordClassifierBridgeTests.cs`:
```csharp
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;

namespace FlowHub.Core.Tests.Classification;

public sealed class KeywordClassifierBridgeTests
{
    private sealed class FakeBridgeCatalog(params string[] aliases) : IBridgeCatalog
    {
        private readonly IReadOnlySet<string> _set = new HashSet<string>(aliases, StringComparer.Ordinal);
        public Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken ct) => Task.FromResult(_set);
    }

    [Fact]
    public async Task ClassifyAsync_LeadingTokenIsAlias_ShortCircuitsToBridgeWithUnknownAction()
    {
        var sut = new KeywordClassifier(new FakeBridgeCatalog("br"));

        var result = await sut.ClassifyAsync("br the login 500s on Safari", TestContext.Current.CancellationToken);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().Be("br");
        result.BridgeAction.Should().Be(BridgeAction.Unknown);
        result.Trace!.Kind.Should().Be(ClassifierKind.Keyword);
    }

    [Fact]
    public async Task ClassifyAsync_AliasWinsOverUrlAndTodoRules()
    {
        var sut = new KeywordClassifier(new FakeBridgeCatalog("br"));

        // Contains a URL and 'todo' but leads with an alias → Bridge short-circuit first.
        var result = await sut.ClassifyAsync("br todo read https://example.com", TestContext.Current.CancellationToken);

        result.MatchedSkill.Should().Be("Bridge");
    }

    [Fact]
    public async Task ClassifyAsync_NoBridgeCatalog_KeepsExistingBehaviour()
    {
        var sut = new KeywordClassifier(); // null catalog

        var result = await sut.ClassifyAsync("br todo something", TestContext.Current.CancellationToken);

        result.MatchedSkill.Should().Be("Vikunja"); // 'todo' keyword rule, no alias awareness
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FlowHub.Core.Tests --filter FullyQualifiedName~KeywordClassifierBridgeTests`
Expected: **build failure** — `KeywordClassifier` has no `IBridgeCatalog` ctor.

- [ ] **Step 3: Modify `KeywordClassifier`**

Rewrite `source/FlowHub.Core/Classification/KeywordClassifier.cs`:
```csharp
using FlowHub.Core.Skills;

namespace FlowHub.Core.Classification;

public sealed class KeywordClassifier : IClassifier
{
    private readonly IBridgeCatalog? _bridgeCatalog;

    public KeywordClassifier(IBridgeCatalog? bridgeCatalog = null) => _bridgeCatalog = bridgeCatalog;

    public async Task<ClassificationResult> ClassifyAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (_bridgeCatalog is not null)
        {
            var aliases = await _bridgeCatalog.GetAliasesAsync(cancellationToken);
            if (BridgeAliasMatcher.TryMatch(content, aliases, out var alias, out _))
            {
                sw.Stop();
                return new ClassificationResult(
                    ["bridge"],
                    "Bridge",
                    Trace: new ClassifierTrace(ClassifierKind.Keyword, (int)sw.ElapsedMilliseconds),
                    BridgeAlias: alias,
                    BridgeAction: BridgeAction.Unknown);
            }
        }

        var result =
            LooksLikeUrl(content) ? new ClassificationResult(["link"], "Wallabag")
            : ContainsTodoKeyword(content) ? new ClassificationResult(["task"], "Vikunja")
            : new ClassificationResult(["unsorted"], string.Empty);

        sw.Stop();
        return result with { Trace = new ClassifierTrace(ClassifierKind.Keyword, (int)sw.ElapsedMilliseconds) };
    }

    private static bool LooksLikeUrl(string content) =>
        Uri.TryCreate(content.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool ContainsTodoKeyword(string content) =>
        content.Contains("todo", StringComparison.OrdinalIgnoreCase)
        || content.Contains("task", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Register the DI fallback catalog**

In `source/FlowHub.AI/AiServiceCollectionExtensions.cs`, inside `AddFlowHubAi`, right after `services.AddSingleton<KeywordClassifier>();` (line 48), add the fallback so `KeywordClassifier`'s optional `IBridgeCatalog` always resolves (the real `BridgeCatalog` in `AddBridge` overrides it — last `AddSingleton` wins, and `AddFlowHubSkills` runs after `AddFlowHubAi` in `Program.cs`):
```csharp
        services.AddSingleton<KeywordClassifier>();

        // Fallback alias catalog so the classifiers resolve even when the Bridge
        // skill isn't configured. AddBridge (in AddFlowHubSkills, which runs after
        // this) registers the real BridgeCatalog and wins — last AddSingleton wins.
        services.TryAddSingleton<IBridgeCatalog>(new EmptyBridgeCatalog());
```
Add `using FlowHub.Core.Skills;` if not already present (it is — line 5). `TryAddSingleton` requires `Microsoft.Extensions.DependencyInjection.Extensions` (already imported, line 9).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/FlowHub.Core.Tests --filter FullyQualifiedName~KeywordClassifierBridgeTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Full suite + commit**

Run: `dotnet test FlowHub.slnx`
Expected: PASS — existing `KeywordClassifierTests`/`KeywordClassifierTraceTests` still green (null-catalog path unchanged).
```bash
git add source/FlowHub.Core/Classification/KeywordClassifier.cs \
        source/FlowHub.AI/AiServiceCollectionExtensions.cs \
        tests/FlowHub.Core.Tests/Classification/KeywordClassifierBridgeTests.cs
git commit -m "feat(classification): keyword alias short-circuit to Bridge + DI fallback catalog"
```

---

### Task 5: `AiClassifier` Bridge decision path (issue/idea/unknown + confidence gate)

**Files:**
- Create: `source/FlowHub.AI/BridgeDecisionResponse.cs`
- Modify: `source/FlowHub.AI/AiPrompts.cs` (add `BuildBridgeMessages`)
- Modify: `source/FlowHub.AI/AiClassifier.cs` (inject `IBridgeCatalog`, add Bridge path)
- Modify: `source/FlowHub.AI/AiServiceCollectionExtensions.cs` (pass `IBridgeCatalog` into `AiClassifier`)
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierBridgeTests.cs`

**Interfaces:**
- Consumes: `IBridgeCatalog` (Task 1), `BridgeAliasMatcher`/`BridgeAction`/`ClassificationResult` (Task 3), existing `IChatClient`/`ChatOptions`/`AiModelInfo`.
- Produces: `AiClassifier` gains a leading-token Bridge pre-check before the normal skill classification. On a match it calls the model with a dedicated `BridgeDecisionResponse` schema and maps to `ClassificationResult(["bridge"], "Bridge", Title, BridgeAlias, BridgeAction)`. Confidence below `BridgeConfidenceThreshold = 0.6` **or** a non-issue/idea action → `BridgeAction.Unknown`. Any model error on the Bridge path falls back to `_keyword.ClassifyAsync(content)` (which re-detects the alias and returns `Unknown` → Inbox). `AiClassifier`'s constructor gains a trailing `IBridgeCatalog bridgeCatalog` param.

- [ ] **Step 1: Write the failing tests**

Create `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierBridgeTests.cs`. Mirror the existing `AiClassifierTests` harness (`Substitute.For<IChatClient>()`, a `JsonResponse` helper, `KeywordClassifier` as the fallback, an `EnricherBucketCatalog`/stub `IVikunjaProjectCatalog`). First **inspect `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierTests.cs`** and reuse its exact `JsonResponse(object)` helper and chat-client stubbing so the two files are consistent.
```csharp
using FlowHub.AI;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiClassifierBridgeTests
{
    private sealed class FakeBridgeCatalog(params string[] aliases) : IBridgeCatalog
    {
        private readonly IReadOnlySet<string> _set = new HashSet<string>(aliases, StringComparer.Ordinal);
        public Task<IReadOnlySet<string>> GetAliasesAsync(CancellationToken ct) => Task.FromResult(_set);
    }

    private sealed class StubVikunjaCatalog : IVikunjaProjectCatalog
    {
        public Task<IReadOnlyDictionary<string, int>> GetAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int> { ["Inbox"] = 1 });
    }

    // Reuse the JsonResponse<T> helper shape from AiClassifierTests: builds a ChatResponse
    // whose message content deserializes to the given anonymous object via GetResponseAsync<T>.
    private static AiClassifier CreateSut(IChatClient chat, IBridgeCatalog bridgeCatalog)
    {
        var keyword = new KeywordClassifier(bridgeCatalog);
        return new AiClassifier(
            chat,
            keyword,
            NullLogger<AiClassifier>.Instance,
            new ChatOptions { MaxOutputTokens = 300, Temperature = 0.2f },
            new StubVikunjaCatalog(),
            new AiModelInfo("Test", "test-model"),
            bridgeCatalog);
    }

    [Fact]
    public async Task ClassifyAsync_BridgeAliasBugWording_ReturnsIssueWithTitle()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(TestChat.JsonResponse(new { action = "issue", title = "Login returns 500 on Safari", confidence = 0.92 }));
        var sut = CreateSut(chat, new FakeBridgeCatalog("br"));

        var result = await sut.ClassifyAsync("br the login 500s on Safari sometimes", TestContext.Current.CancellationToken);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().Be("br");
        result.BridgeAction.Should().Be(BridgeAction.Issue);
        result.Title.Should().Be("Login returns 500 on Safari");
    }

    [Fact]
    public async Task ClassifyAsync_BridgeAliasExploratoryWording_ReturnsIdea()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(TestChat.JsonResponse(new { action = "idea", title = (string?)null, confidence = 0.88 }));
        var sut = CreateSut(chat, new FakeBridgeCatalog("br"));

        var result = await sut.ClassifyAsync("br what if repos had a health score", TestContext.Current.CancellationToken);

        result.BridgeAction.Should().Be(BridgeAction.Idea);
    }

    [Fact]
    public async Task ClassifyAsync_LowConfidence_ReturnsUnknown()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(TestChat.JsonResponse(new { action = "issue", title = "Maybe a bug", confidence = 0.3 }));
        var sut = CreateSut(chat, new FakeBridgeCatalog("br"));

        var result = await sut.ClassifyAsync("br hmm not sure about this one", TestContext.Current.CancellationToken);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAction.Should().Be(BridgeAction.Unknown);
    }

    [Fact]
    public async Task ClassifyAsync_NoAliasMatch_UsesNormalSkillClassification()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(TestChat.JsonResponse(new { tags = new[] { "link" }, matched_skill = "Wallabag", title = "An article" }));
        var sut = CreateSut(chat, new FakeBridgeCatalog("br"));

        var result = await sut.ClassifyAsync("https://example.com/read-this", TestContext.Current.CancellationToken);

        result.MatchedSkill.Should().Be("Wallabag");
        result.BridgeAction.Should().Be(BridgeAction.Unknown); // default; not a bridge capture
    }
}
```
**Note:** `TestChat.JsonResponse` is a placeholder for whatever helper `AiClassifierTests` already uses to fake `GetResponseAsync<T>`. Reuse the existing helper verbatim (it may be a local static method or a shared test util) rather than introducing a new one. The two Bridge-path tests need the fake to satisfy `GetResponseAsync<BridgeDecisionResponse>`; the last test needs `GetResponseAsync<AiClassificationResponse>` — the existing helper already covers the generic-response shape.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter FullyQualifiedName~AiClassifierBridgeTests`
Expected: **build failure** — `AiClassifier` has no `IBridgeCatalog` ctor param; `BridgeDecisionResponse` missing.

- [ ] **Step 3: Create the decision schema**

`source/FlowHub.AI/BridgeDecisionResponse.cs`:
```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace FlowHub.AI;

/// <summary>
/// Structured-output schema for the Bridge issue-vs-idea decision. Kept separate
/// from <see cref="AiClassificationResponse"/> so the skill-bucket classification
/// schema stays focused. The classifier maps <c>action</c>+<c>confidence</c> to
/// <see cref="FlowHub.Core.Classification.BridgeAction"/> (low confidence → Unknown).
/// </summary>
internal sealed record BridgeDecisionResponse(
    [property: Description("issue for a concrete bug/task/actionable item; idea for a fuzzy or exploratory thought; unknown if genuinely unsure")]
    [property: AllowedValues("issue", "idea", "unknown")]
    [property: JsonPropertyName("action")] string Action,

    [property: Description("A concise imperative issue title (<= 80 chars) when action=issue; may be null for idea")]
    [property: JsonPropertyName("title")] string? Title,

    [property: Description("Confidence 0.0-1.0 that the action is correct")]
    [property: JsonPropertyName("confidence")] double Confidence);
```
(`AllowedValues` is `System.ComponentModel.DataAnnotations` — confirm the using matches `AiClassificationResponse.cs`, which already uses `[AllowedValues(...)]`; copy its exact using directives.)

- [ ] **Step 4: Add the Bridge prompt builder**

In `source/FlowHub.AI/AiPrompts.cs`, add two members alongside the existing ones:
```csharp
    internal static string BuildBridgeSystemPrompt() =>
        """
        You triage a personal capture that is destined for a specific code repository.
        The user has already chosen the repo (via a short alias); your ONLY job is to
        decide what KIND of entry it should become and to draft a title.

        Return:
        - action: choose exactly ONE
            "issue"   – a concrete bug, task, or actionable request (something to DO/FIX)
            "idea"    – a fuzzy, exploratory, or "what if" thought worth remembering
            "unknown" – you genuinely cannot tell which of the above it is
        - title: for action="issue", a concise imperative title (<= 80 chars). For
                  "idea" or "unknown", leave it null.
        - confidence: 0.0-1.0, how sure you are of the action.

        Reply ONLY via the structured response schema. Never include explanations.
        """;

    internal static IList<ChatMessage> BuildBridgeMessages(string body) =>
    [
        new ChatMessage(ChatRole.System, BuildBridgeSystemPrompt()),
        new ChatMessage(ChatRole.User, body),
    ];
```

- [ ] **Step 5: Add the Bridge path to `AiClassifier`**

Modify `source/FlowHub.AI/AiClassifier.cs`:

(a) Add the field, ctor param, and threshold const:
```csharp
    private const double BridgeConfidenceThreshold = 0.6;

    private static readonly string[] AllowedSkills = ["Wallabag", "Vikunja", ""];

    private readonly IChatClient _chat;
    private readonly IClassifier _keyword;
    private readonly ILogger<AiClassifier> _log;
    private readonly ChatOptions _options;
    private readonly IVikunjaProjectCatalog _catalog;
    private readonly AiModelInfo _modelInfo;
    private readonly IBridgeCatalog _bridgeCatalog;

    public AiClassifier(
        IChatClient chat,
        IClassifier keyword,
        ILogger<AiClassifier> log,
        ChatOptions options,
        IVikunjaProjectCatalog catalog,
        AiModelInfo modelInfo,
        IBridgeCatalog bridgeCatalog)
    {
        _chat = chat;
        _keyword = keyword;
        _log = log;
        _options = options;
        _catalog = catalog;
        _modelInfo = modelInfo;
        _bridgeCatalog = bridgeCatalog;
    }
```

(b) At the top of `ClassifyAsync`, after `ArgumentNullException.ThrowIfNull(content);` and `var sw = Stopwatch.StartNew();`, add the Bridge pre-check before the existing `try`:
```csharp
        var aliases = await _bridgeCatalog.GetAliasesAsync(cancellationToken);
        if (BridgeAliasMatcher.TryMatch(content, aliases, out var alias, out var remainder))
        {
            return await ClassifyBridgeAsync(alias, remainder, content, sw, cancellationToken);
        }
```
(`_bridgeCatalog.GetAliasesAsync` never throws — Task 2 guarantees graceful degradation — so no guard is needed here.)

(c) Add the Bridge classification method and action mapper:
```csharp
    private async Task<ClassificationResult> ClassifyBridgeAsync(
        string alias,
        string remainder,
        string originalContent,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _chat.GetResponseAsync<BridgeDecisionResponse>(
                AiPrompts.BuildBridgeMessages(remainder),
                _options,
                cancellationToken: cancellationToken);

            if (!response.TryGetResult(out var payload))
            {
                throw new InvalidOperationException("schema_violation");
            }

            var action = MapBridgeAction(payload.Action, payload.Confidence);
            var title = string.IsNullOrWhiteSpace(payload.Title) ? null : payload.Title.Trim();

            sw.Stop();
            var trace = new ClassifierTrace(
                ClassifierKind.Ai,
                (int)sw.ElapsedMilliseconds,
                _modelInfo.Provider,
                _modelInfo.Model,
                (int?)response.Usage?.InputTokenCount,
                (int?)response.Usage?.OutputTokenCount);

            return new ClassificationResult(
                ["bridge"],
                "Bridge",
                title,
                Trace: trace,
                BridgeAlias: alias,
                BridgeAction: action);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var reason = ex is InvalidOperationException && ex.Message == "schema_violation"
                ? "schema_violation"
                : ex.GetType().Name;
            LogFellBack(reason, sw.ElapsedMilliseconds);
            // Keyword fallback re-detects the alias and yields BridgeAction=Unknown → Inbox.
            return await _keyword.ClassifyAsync(originalContent, cancellationToken);
        }
    }

    private static BridgeAction MapBridgeAction(string action, double confidence)
    {
        if (confidence < BridgeConfidenceThreshold)
        {
            return BridgeAction.Unknown;
        }

        return action switch
        {
            "issue" => BridgeAction.Issue,
            "idea" => BridgeAction.Idea,
            _ => BridgeAction.Unknown,
        };
    }
```
Ensure `using FlowHub.Core.Skills;` is present (line 3 already has it) and `using FlowHub.Core.Classification;` (line 2) for `BridgeAction`/`BridgeAliasMatcher`.

- [ ] **Step 6: Update the DI construction**

In `source/FlowHub.AI/AiServiceCollectionExtensions.cs`, add `IBridgeCatalog` to the hand-built `AiClassifier` (lines 109–115):
```csharp
        services.AddSingleton(sp => new AiClassifier(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<KeywordClassifier>(),
            sp.GetRequiredService<ILogger<AiClassifier>>(),
            new ChatOptions { MaxOutputTokens = maxTokens, Temperature = 0.2f },
            sp.GetRequiredService<IVikunjaProjectCatalog>(),
            sp.GetRequiredService<AiModelInfo>(),
            sp.GetRequiredService<IBridgeCatalog>()));
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter FullyQualifiedName~AiClassifierBridgeTests`
Expected: PASS (4 tests). If the `TestChat.JsonResponse` helper name differs, fix the test to use the real one; do **not** change production code to match a wrong helper.

- [ ] **Step 8: Full suite + commit**

Run: `dotnet test FlowHub.slnx`
Expected: PASS — existing `AiClassifierTests` still green (non-alias captures unaffected; empty alias set in those tests means the pre-check is a no-op).
```bash
git add source/FlowHub.AI/BridgeDecisionResponse.cs \
        source/FlowHub.AI/AiPrompts.cs \
        source/FlowHub.AI/AiClassifier.cs \
        source/FlowHub.AI/AiServiceCollectionExtensions.cs \
        tests/FlowHub.Web.ComponentTests/Ai/AiClassifierBridgeTests.cs
git commit -m "feat(ai): AiClassifier decides Bridge issue/idea with confidence gate"
```

---

### Task 6: Carry `BridgeAlias`/`BridgeAction` on the event, record, and classify stage

**Files:**
- Modify: `source/FlowHub.Core/Events/CaptureClassified.cs`
- Modify: `source/FlowHub.Core/Captures/Capture.cs`
- Modify: `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerBridgeTests.cs`

**Interfaces:**
- Consumes: `BridgeAction` (Task 3); `ClassificationResult.BridgeAlias`/`.BridgeAction` (Task 3/5).
- Produces: `CaptureClassified` gains trailing `string? BridgeAlias = null`, `BridgeAction BridgeAction = BridgeAction.Unknown`. `Capture` gains the same two trailing optional (transient, non-persisted) props. `CaptureEnrichmentConsumer` threads `result.BridgeAlias`/`result.BridgeAction` into the published `CaptureClassified` on the main classification path.

- [ ] **Step 1: Write the failing test**

Create `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerBridgeTests.cs`. Model it on the existing `CaptureEnrichmentConsumer` test (find it: `grep -rl CaptureEnrichmentConsumer tests/`), reusing its `ITestHarness` setup, `IClassifier`/`ICaptureService` substitutes, and `EnricherDispatcher` construction.
```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using MassTransit;
using MassTransit.Testing;
// ...match the existing test's usings for EnricherDispatcher / ICaptureService substitutes...

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class CaptureEnrichmentConsumerBridgeTests
{
    [Fact]
    public async Task Consume_BridgeClassification_PublishesEventWithAliasAndAction()
    {
        var captureId = Guid.NewGuid();
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(
                ["bridge"], "Bridge", Title: "Fix login 500",
                BridgeAlias: "br", BridgeAction: BridgeAction.Issue));

        // ...construct harness + consumer exactly as the existing test does, wiring `classifier`
        //    and an ICaptureService substitute; publish CaptureCreated { CaptureId=captureId, ... }...

        // Assert the published CaptureClassified carries the bridge fields:
        var published = await harness.Published.SelectAsync<CaptureClassified>(
            TestContext.Current.CancellationToken).FirstOrDefault();
        published.Should().NotBeNull();
        var evt = published!.Context.Message;
        evt.MatchedSkill.Should().Be("Bridge");
        evt.BridgeAlias.Should().Be("br");
        evt.BridgeAction.Should().Be(BridgeAction.Issue);
    }
}
```
(Fill in the harness/consumer construction verbatim from the existing `CaptureEnrichmentConsumer` test so this compiles against the real MassTransit test harness.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter FullyQualifiedName~CaptureEnrichmentConsumerBridgeTests`
Expected: **build failure** — `CaptureClassified` has no `BridgeAlias`/`BridgeAction`.

- [ ] **Step 3: Extend `CaptureClassified`**

Modify `source/FlowHub.Core/Events/CaptureClassified.cs` — add `using FlowHub.Core.Classification;` and the two trailing params:
```csharp
using FlowHub.Core.Classification;

namespace FlowHub.Core.Events;

public sealed record CaptureClassified(
    Guid CaptureId,
    IReadOnlyList<string> Tags,
    string MatchedSkill,
    DateTimeOffset ClassifiedAt,
    string? VikunjaProject = null,
    string? EnrichmentDescription = null,
    string? BridgeAlias = null,
    BridgeAction BridgeAction = BridgeAction.Unknown);
```

- [ ] **Step 4: Extend `Capture`**

Modify `source/FlowHub.Core/Captures/Capture.cs` — add the two trailing optional props (transient; **not** added to `CaptureEntity` — no migration):
```csharp
public sealed record Capture(
    Guid Id,
    ChannelKind Source,
    string Content,
    DateTimeOffset CreatedAt,
    LifecycleStage Stage,
    string? MatchedSkill,
    string? FailureReason = null,
    string? Title = null,
    string? ExternalRef = null,
    string? VikunjaProject = null,
    string? EnrichmentDescription = null,
    Attachment? Attachment = null,
    FlowHub.Core.Classification.ClassifierTrace? ClassifierTrace = null,
    string? BridgeAlias = null,
    FlowHub.Core.Classification.BridgeAction BridgeAction = FlowHub.Core.Classification.BridgeAction.Unknown);
```

- [ ] **Step 5: Thread the fields through `CaptureEnrichmentConsumer`**

In `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs`, update the main-path publish (lines 82–88) to include the bridge fields:
```csharp
        await context.Publish(new CaptureClassified(
            msg.CaptureId,
            result.Tags,
            result.MatchedSkill,
            DateTimeOffset.UtcNow,
            project,
            enrichment?.Description,
            result.BridgeAlias,
            result.BridgeAction));
```
(The attachment/Paperless short-circuit at lines 44–48 is never a Bridge capture — leave it unchanged; its `BridgeAction` defaults to `Unknown`.)

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter FullyQualifiedName~CaptureEnrichmentConsumerBridgeTests`
Expected: PASS.

- [ ] **Step 7: Full suite + commit**

Run: `dotnet test FlowHub.slnx`
Expected: PASS.
```bash
git add source/FlowHub.Core/Events/CaptureClassified.cs \
        source/FlowHub.Core/Captures/Capture.cs \
        source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs \
        tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerBridgeTests.cs
git commit -m "feat(pipeline): carry BridgeAlias/BridgeAction on CaptureClassified and Capture"
```

---

### Task 7: `SkillRoutingConsumer` — low-confidence gate + transient bridge-field injection

**Files:**
- Modify: `source/FlowHub.Web/Pipeline/SkillRoutingConsumer.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Pipeline/SkillRoutingConsumerBridgeTests.cs`

**Interfaces:**
- Consumes: `CaptureClassified.BridgeAlias`/`.BridgeAction` (Task 6); `ICaptureService.MarkUnhandledAsync` (existing).
- Produces: When `MatchedSkill == "Bridge"` and `BridgeAction` is neither `Issue` nor `Idea`, the consumer calls `MarkUnhandledAsync(...)` and returns **before** integration lookup (low-confidence gate → capture stays in Inbox). Otherwise it injects `BridgeAlias`/`BridgeAction` onto the loaded `Capture` (alongside the existing `EnrichmentDescription` injection) before `HandleAsync`.

- [ ] **Step 1: Write the failing tests**

Create `tests/FlowHub.Web.ComponentTests/Pipeline/SkillRoutingConsumerBridgeTests.cs`. Model on the existing `SkillRoutingConsumerTests` (`ITestHarness`, `Substitute.For<ISkillIntegration>()` with `Name` returning "Bridge", `ICaptureService` substitute). Two cases:
```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using FlowHub.Core.Skills;
// ...match existing SkillRoutingConsumerTests usings/harness...

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class SkillRoutingConsumerBridgeTests
{
    [Fact]
    public async Task Consume_BridgeUnknownAction_MarksUnhandledWithoutCallingIntegration()
    {
        var captureId = Guid.NewGuid();
        var bridge = Substitute.For<ISkillIntegration>();
        bridge.Name.Returns("Bridge");
        var captureService = Substitute.For<ICaptureService>();
        // ...harness with [bridge] registered as ISkillIntegration and captureService...

        // publish CaptureClassified { captureId, Tags:["bridge"], MatchedSkill:"Bridge",
        //   ClassifiedAt: now, BridgeAlias:"br", BridgeAction: BridgeAction.Unknown }

        await captureService.Received(1).MarkUnhandledAsync(captureId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await bridge.DidNotReceive().HandleAsync(Arg.Any<Capture>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_BridgeIssueAction_InjectsAliasAndActionOntoCaptureBeforeHandle()
    {
        var captureId = Guid.NewGuid();
        var bridge = Substitute.For<ISkillIntegration>();
        bridge.Name.Returns("Bridge");
        bridge.HandleAsync(Arg.Any<Capture>(), Arg.Any<CancellationToken>())
            .Returns(new SkillResult(true, ExternalRef: "https://github.com/x/bridge/issues/7"));
        var captureService = Substitute.For<ICaptureService>();
        var stored = new Capture(captureId, ChannelKind.Web, "br fix the login 500", DateTimeOffset.UtcNow,
            LifecycleStage.Classified, "Bridge", Title: "Fix login 500");
        captureService.GetByIdAsync(captureId, Arg.Any<CancellationToken>()).Returns(stored);
        // ...harness...

        // publish CaptureClassified { ..., BridgeAlias:"br", BridgeAction: BridgeAction.Issue }

        await bridge.Received(1).HandleAsync(
            Arg.Is<Capture>(c => c.BridgeAlias == "br" && c.BridgeAction == BridgeAction.Issue),
            Arg.Any<CancellationToken>());
        await captureService.Received(1).MarkCompletedAsync(
            captureId, "https://github.com/x/bridge/issues/7", Arg.Any<CancellationToken>());
    }
}
```
(Fill harness/consumer construction from the existing `SkillRoutingConsumerTests`. `ChannelKind.Web` — use whatever member the existing tests use for a sample capture source.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter FullyQualifiedName~SkillRoutingConsumerBridgeTests`
Expected: FAIL — no gate exists, so the Unknown case would attempt to route (integration `HandleAsync` called / no `MarkUnhandledAsync`), and the Issue case's capture lacks the injected fields.

- [ ] **Step 3: Add the gate + injection**

In `source/FlowHub.Web/Pipeline/SkillRoutingConsumer.cs`, add `using FlowHub.Core.Classification;`. Insert the low-confidence gate immediately after `var integration = _integrations.FirstOrDefault(...)` resolves — actually gate on the message before the null-integration branch, so an Unknown Bridge action never routes even if a Bridge integration is registered:
```csharp
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (string.Equals(msg.MatchedSkill, "Bridge", StringComparison.Ordinal)
            && msg.BridgeAction is not (BridgeAction.Issue or BridgeAction.Idea))
        {
            await _captureService.MarkUnhandledAsync(msg.CaptureId,
                "bridge action could not be determined (low confidence); left in Inbox for triage", ct);
            LogUnhandled(msg.CaptureId, msg.MatchedSkill);
            return;
        }

        var integration = _integrations.FirstOrDefault(i =>
            string.Equals(i.Name, msg.MatchedSkill, StringComparison.Ordinal));
```
Then extend the existing transient injection line (currently `capture = capture with { EnrichmentDescription = msg.EnrichmentDescription };`):
```csharp
        capture = capture with
        {
            EnrichmentDescription = msg.EnrichmentDescription,
            BridgeAlias = msg.BridgeAlias,
            BridgeAction = msg.BridgeAction,
        };
```
(Reuse the existing `LogUnhandled` logger message — EventId 1002 — for the gate.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter FullyQualifiedName~SkillRoutingConsumerBridgeTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Full suite + commit**

Run: `dotnet test FlowHub.slnx`
Expected: PASS — existing `SkillRoutingConsumerTests` still green (non-Bridge skills skip the gate).
```bash
git add source/FlowHub.Web/Pipeline/SkillRoutingConsumer.cs \
        tests/FlowHub.Web.ComponentTests/Pipeline/SkillRoutingConsumerBridgeTests.cs
git commit -m "feat(pipeline): gate low-confidence Bridge captures to Unhandled and inject alias/action"
```

---

### Task 8: `BridgeSkillIntegration` — POST issue / idea to bridge

**Files:**
- Create: `source/FlowHub.Skills/Bridge/BridgeSkillIntegration.cs`
- Test: `tests/FlowHub.Skills.Tests/Bridge/BridgeSkillIntegrationTests.cs` (unit, MockHttp)
- Test: `tests/FlowHub.Skills.ContractTests/Bridge/BridgeSkillIntegrationContractTests.cs` (WireMock)

**Interfaces:**
- Consumes: `ISkillIntegration`/`SkillResult` (existing), `BridgeOptions` (Task 1), `Capture.BridgeAlias`/`.BridgeAction`/`.Title`/`.Content` (Task 6), `BridgeAction` (Task 3); `HttpClient` (typed client, `BaseAddress` set in Task 9).
- Produces: `FlowHub.Skills.Bridge.BridgeSkillIntegration : ISkillIntegration`, `Name => "Bridge"`. `HandleAsync`: builds the body/text by stripping the leading alias token from `capture.Content`; for `BridgeAction.Issue` POSTs `/api/capture/issue { alias, title, body }` (title = `capture.Title` ?? truncated body), for `BridgeAction.Idea` POSTs `/api/capture/idea { alias, text }`; Bearer `ApiToken`; `EnsureSuccessStatusCode()`; parses `{ url }` → `SkillResult(true, ExternalRef: url)`. Throws on missing url or `BridgeAction.Unknown` (defensive — routing already gated it).

- [ ] **Step 1: Write the failing unit tests**

Create `tests/FlowHub.Skills.Tests/Bridge/BridgeSkillIntegrationTests.cs`:
```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;

namespace FlowHub.Skills.Tests.Bridge;

public sealed class BridgeSkillIntegrationTests
{
    private const string BaseUrl = "https://bridge.example.com";

    private static (BridgeSkillIntegration Sut, MockHttpMessageHandler Mock) Create()
    {
        var mock = new MockHttpMessageHandler();
        var http = mock.ToHttpClient();
        http.BaseAddress = new Uri(BaseUrl);
        var options = Options.Create(new BridgeOptions { BaseUrl = BaseUrl, ApiToken = "tok" });
        var sut = new BridgeSkillIntegration(http, options, NullLogger<BridgeSkillIntegration>.Instance);
        return (sut, mock);
    }

    private static Capture BridgeCapture(BridgeAction action, string content, string? title = null) =>
        new(Guid.NewGuid(), ChannelKind.Web, content, DateTimeOffset.UtcNow, LifecycleStage.Routed,
            "Bridge", Title: title, BridgeAlias: "br", BridgeAction: action);

    [Fact]
    public void Name_Always_ReturnsBridge()
    {
        var (sut, _) = Create();
        sut.Name.Should().Be("Bridge");
    }

    [Fact]
    public async Task HandleAsync_IssueAction_PostsIssueWithAliasTitleBodyAndReturnsUrl()
    {
        var (sut, mock) = Create();
        mock.Expect(HttpMethod.Post, $"{BaseUrl}/api/capture/issue")
            .WithHeaders("Authorization", "Bearer tok")
            .WithPartialContent("\"alias\":\"br\"")
            .WithPartialContent("\"title\":\"Fix login 500\"")
            .WithPartialContent("\"body\":\"the login 500s on Safari\"")
            .Respond("application/json", """{"url":"https://github.com/x/bridge/issues/7"}""");

        var result = await sut.HandleAsync(
            BridgeCapture(BridgeAction.Issue, "br the login 500s on Safari", title: "Fix login 500"),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.ExternalRef.Should().Be("https://github.com/x/bridge/issues/7");
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task HandleAsync_IdeaAction_PostsIdeaWithAliasAndTextAndReturnsUrl()
    {
        var (sut, mock) = Create();
        mock.Expect(HttpMethod.Post, $"{BaseUrl}/api/capture/idea")
            .WithHeaders("Authorization", "Bearer tok")
            .WithPartialContent("\"alias\":\"br\"")
            .WithPartialContent("\"text\":\"what if repos had a health score\"")
            .Respond("application/json", """{"url":"https://github.com/x/bridge/blob/main/ideas.md"}""");

        var result = await sut.HandleAsync(
            BridgeCapture(BridgeAction.Idea, "br what if repos had a health score"),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.ExternalRef.Should().Be("https://github.com/x/bridge/blob/main/ideas.md");
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task HandleAsync_BridgeReturnsError_ThrowsToEngageRetry()
    {
        var (sut, mock) = Create();
        mock.When(HttpMethod.Post, $"{BaseUrl}/api/capture/issue")
            .Respond(System.Net.HttpStatusCode.NotFound); // unknown alias at bridge

        var act = async () => await sut.HandleAsync(
            BridgeCapture(BridgeAction.Issue, "br whatever", title: "x"),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter FullyQualifiedName~BridgeSkillIntegrationTests`
Expected: **build failure** — `BridgeSkillIntegration` does not exist.

- [ ] **Step 3: Implement `BridgeSkillIntegration`**

`source/FlowHub.Skills/Bridge/BridgeSkillIntegration.cs`:
```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Capture = FlowHub.Core.Captures.Capture;

namespace FlowHub.Skills.Bridge;

/// <summary>
/// Routes a Bridge-classified capture to the bridge service's REST API: creates an
/// issue or appends to the repo's ideas.md, decided by <see cref="BridgeAction"/>.
/// Bridge resolves the alias to a concrete forge/owner/repo. Follows the repo
/// convention of throwing on transport/HTTP failure so MassTransit retry and the
/// LifecycleFaultObserver take the capture to Unhandled.
/// </summary>
public sealed partial class BridgeSkillIntegration : ISkillIntegration
{
    private const int FallbackTitleMaxLength = 80;
    private static readonly char[] Whitespace = [' ', '\t', '\n', '\r'];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly BridgeOptions _options;
    private readonly ILogger<BridgeSkillIntegration> _log;

    public BridgeSkillIntegration(
        HttpClient http,
        IOptions<BridgeOptions> options,
        ILogger<BridgeSkillIntegration> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public string Name => "Bridge";

    public async Task<SkillResult> HandleAsync(Capture capture, CancellationToken cancellationToken)
    {
        var body = StripAlias(capture.Content, capture.BridgeAlias);
        var alias = capture.BridgeAlias
            ?? throw new InvalidOperationException("Bridge capture is missing an alias.");

        return capture.BridgeAction switch
        {
            BridgeAction.Issue => await PostIssueAsync(alias, capture.Title, body, cancellationToken),
            BridgeAction.Idea => await PostIdeaAsync(alias, body, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Bridge capture {capture.Id} reached the integration with unresolved action '{capture.BridgeAction}'."),
        };
    }

    private async Task<SkillResult> PostIssueAsync(string alias, string? title, string body, CancellationToken cancellationToken)
    {
        var resolvedTitle = !string.IsNullOrWhiteSpace(title) ? title.Trim() : Truncate(body, FallbackTitleMaxLength);
        var url = await PostAsync("/api/capture/issue", new { alias, title = resolvedTitle, body }, cancellationToken);
        return new SkillResult(Success: true, ExternalRef: url);
    }

    private async Task<SkillResult> PostIdeaAsync(string alias, string text, CancellationToken cancellationToken)
    {
        var url = await PostAsync("/api/capture/idea", new { alias, text }, cancellationToken);
        return new SkillResult(Success: true, ExternalRef: url);
    }

    private async Task<string> PostAsync(string path, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var parsed = await response.Content.ReadFromJsonAsync<BridgeCaptureResponse>(JsonOptions, cancellationToken)
                     ?? throw new InvalidOperationException("Bridge response body was empty.");

        if (string.IsNullOrWhiteSpace(parsed.Url))
        {
            throw new InvalidOperationException("Bridge response did not include a 'url' field.");
        }

        return parsed.Url;
    }

    private static string StripAlias(string content, string? alias)
    {
        var trimmed = content.TrimStart();
        if (string.IsNullOrEmpty(alias))
        {
            return trimmed.Trim();
        }

        var spaceIndex = trimmed.IndexOfAny(Whitespace);
        var firstToken = spaceIndex < 0 ? trimmed : trimmed[..spaceIndex];
        if (string.Equals(firstToken, alias, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = spaceIndex < 0 ? string.Empty : trimmed[(spaceIndex + 1)..];
        }

        return trimmed.Trim();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd();

    private sealed record BridgeCaptureResponse(string? Url);
}
```

- [ ] **Step 4: Run unit tests to verify they pass**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter FullyQualifiedName~BridgeSkillIntegrationTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Write the WireMock contract test**

Create `tests/FlowHub.Skills.ContractTests/Bridge/BridgeSkillIntegrationContractTests.cs`:
```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace FlowHub.Skills.ContractTests.Bridge;

public sealed class BridgeSkillIntegrationContractTests : IClassFixture<WireMockServerFixture>
{
    private readonly WireMockServerFixture _wire;

    public BridgeSkillIntegrationContractTests(WireMockServerFixture wire)
    {
        _wire = wire;
        _wire.Server.Reset();
    }

    private BridgeSkillIntegration CreateSut()
    {
        var http = new HttpClient { BaseAddress = new Uri(_wire.BaseUrl) };
        var options = Options.Create(new BridgeOptions { BaseUrl = _wire.BaseUrl, ApiToken = "tok" });
        return new BridgeSkillIntegration(http, options, NullLogger<BridgeSkillIntegration>.Instance);
    }

    private static Capture Cap(BridgeAction action, string content, string? title = null) =>
        new(Guid.NewGuid(), ChannelKind.Web, content, DateTimeOffset.UtcNow, LifecycleStage.Routed,
            "Bridge", Title: title, BridgeAlias: "br", BridgeAction: action);

    [Fact]
    public async Task HandleAsync_Issue_AgainstStubbedBridge_ReturnsIssueUrl()
    {
        _wire.Server
            .Given(Request.Create().WithPath("/api/capture/issue").UsingPost()
                .WithHeader("Authorization", "Bearer tok"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"url":"https://forgejo.example/br/issues/12"}"""));

        var result = await CreateSut().HandleAsync(
            Cap(BridgeAction.Issue, "br fix the flaky test", title: "Fix flaky test"),
            TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.ExternalRef.Should().Be("https://forgejo.example/br/issues/12");
    }

    [Fact]
    public async Task HandleAsync_Idea_AgainstStubbedBridge_ReturnsIdeaUrl()
    {
        _wire.Server
            .Given(Request.Create().WithPath("/api/capture/idea").UsingPost()
                .WithHeader("Authorization", "Bearer tok"))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"url":"https://forgejo.example/br/ideas.md"}"""));

        var result = await CreateSut().HandleAsync(
            Cap(BridgeAction.Idea, "br what if we cached the catalog"),
            TestContext.Current.CancellationToken);

        result.ExternalRef.Should().Be("https://forgejo.example/br/ideas.md");
    }

    [Fact]
    public async Task HandleAsync_UnauthorizedFromBridge_Throws()
    {
        _wire.Server
            .Given(Request.Create().WithPath("/api/capture/issue").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        var act = async () => await CreateSut().HandleAsync(
            Cap(BridgeAction.Issue, "br x", title: "x"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
```

- [ ] **Step 6: Run the contract tests**

Run: `dotnet test tests/FlowHub.Skills.ContractTests --filter FullyQualifiedName~BridgeSkillIntegrationContractTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Full suite + commit**

Run: `dotnet test FlowHub.slnx`
Expected: PASS.
```bash
git add source/FlowHub.Skills/Bridge/BridgeSkillIntegration.cs \
        tests/FlowHub.Skills.Tests/Bridge/BridgeSkillIntegrationTests.cs \
        tests/FlowHub.Skills.ContractTests/Bridge/BridgeSkillIntegrationContractTests.cs
git commit -m "feat(skills): add BridgeSkillIntegration for issue/idea capture routing"
```

---

### Task 9: Wire it up — `AddBridge` DI registration + config

**Files:**
- Modify: `source/FlowHub.Skills/SkillsServiceCollectionExtensions.cs` (`AddBridge`)
- Modify: `source/FlowHub.Web/appsettings.json` (`Skills:Bridge` sentinel block)
- Modify: `.env.example` (`Skills__Bridge__*` keys)
- Test: `tests/FlowHub.Skills.Tests/SkillsServiceCollectionExtensionsTests.cs` (extend for Bridge outcomes)
- Test: `tests/FlowHub.Skills.IntegrationTests/BridgeLiveTests.cs` (skippable live smoke)

**Interfaces:**
- Consumes: `BridgeOptions`/`BridgeCatalog`/`IBridgeCatalog` (Tasks 1–2), `BridgeSkillIntegration` (Task 8), `SkillsRegistrationOutcome` (existing).
- Produces: `AddBridge(IServiceCollection, IConfiguration)` — fail-closed (needs `BaseUrl` + `ApiToken`), registers typed `HttpClient`s for `BridgeCatalog` and `BridgeSkillIntegration`, `AddSingleton<IBridgeCatalog>(sp => sp.GetRequiredService<BridgeCatalog>())` (overrides the `AddFlowHubAi` fallback — last wins), `AddSingleton<ISkillIntegration>(sp => sp.GetRequiredService<BridgeSkillIntegration>())`, and the matching `SkillsRegistrationOutcome`. Called from `AddFlowHubSkills`.

- [ ] **Step 1: Write the failing registration tests**

Extend `tests/FlowHub.Skills.Tests/SkillsServiceCollectionExtensionsTests.cs` (add these cases, mirroring the existing Vikunja/Wallabag registration-outcome tests). First read that file to reuse its config-building + `AddFlowHubSkills` invocation helper.
```csharp
    [Fact]
    public void AddFlowHubSkills_BridgeConfigured_RegistersBridgeIntegrationAndCatalog()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Skills:Bridge:BaseUrl"] = "https://bridge.example.com",
            ["Skills:Bridge:ApiToken"] = "tok",
        });
        var services = new ServiceCollection();
        services.AddFlowHubSkills(config);
        var provider = services.BuildServiceProvider();

        provider.GetServices<ISkillIntegration>().Should().Contain(i => i.Name == "Bridge");
        provider.GetServices<SkillsRegistrationOutcome>()
            .Should().Contain(o => o.Skill == "Bridge" && o.Registered);
        provider.GetRequiredService<IBridgeCatalog>().Should().BeOfType<BridgeCatalog>();
    }

    [Fact]
    public void AddFlowHubSkills_BridgeMissingBaseUrl_DoesNotRegisterAndReasonsMissingBaseUrl()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Skills:Bridge:ApiToken"] = "tok",
        });
        var services = new ServiceCollection();
        services.AddFlowHubSkills(config);
        var provider = services.BuildServiceProvider();

        provider.GetServices<ISkillIntegration>().Should().NotContain(i => i.Name == "Bridge");
        provider.GetServices<SkillsRegistrationOutcome>()
            .Should().Contain(o => o.Skill == "Bridge" && !o.Registered && o.Reason == "missing-base-url");
    }

    [Fact]
    public void AddFlowHubSkills_BridgeMissingApiToken_ReasonsMissingApiToken()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Skills:Bridge:BaseUrl"] = "https://bridge.example.com",
        });
        var services = new ServiceCollection();
        services.AddFlowHubSkills(config);
        var provider = services.BuildServiceProvider();

        provider.GetServices<SkillsRegistrationOutcome>()
            .Should().Contain(o => o.Skill == "Bridge" && !o.Registered && o.Reason == "missing-api-token");
    }
```
Add `using FlowHub.Skills.Bridge;` and `using FlowHub.Core.Skills;` to the test file if not present. **Note:** `GetRequiredService<IBridgeCatalog>()` here resolves against only `AddFlowHubSkills` (the test doesn't call `AddFlowHubAi`), so `AddBridge` must register the real `BridgeCatalog` itself for the "configured" assertion to hold — which it does.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter FullyQualifiedName~SkillsServiceCollectionExtensionsTests`
Expected: FAIL — no Bridge registration; `Skill=="Bridge"` outcomes absent.

- [ ] **Step 3: Implement `AddBridge`**

In `source/FlowHub.Skills/SkillsServiceCollectionExtensions.cs`, add `using FlowHub.Skills.Bridge;`, call `AddBridge` from `AddFlowHubSkills`, and add the method (mirrors `AddVikunja`):
```csharp
    public static IServiceCollection AddFlowHubSkills(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<SkillsBootLogger>();
        AddWallabag(services, configuration);
        AddVikunja(services, configuration);
        AddPaperless(services, configuration);
        AddBridge(services, configuration);
        return services;
    }

    private static void AddBridge(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(BridgeOptions.SectionName);
        var options = section.Get<BridgeOptions>() ?? new BridgeOptions();

        string? reason = null;
        if (string.IsNullOrWhiteSpace(options.BaseUrl)) { reason = "missing-base-url"; }
        else if (string.IsNullOrWhiteSpace(options.ApiToken)) { reason = "missing-api-token"; }

        if (reason is not null)
        {
            services.AddSingleton(new SkillsRegistrationOutcome("Bridge", Registered: false, Reason: reason));
            return;
        }

        services.Configure<BridgeOptions>(section);
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<BridgeSkillIntegration>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl!);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient<BridgeCatalog>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl!);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddSingleton<IBridgeCatalog>(sp => sp.GetRequiredService<BridgeCatalog>());
        services.AddSingleton<ISkillIntegration>(sp => sp.GetRequiredService<BridgeSkillIntegration>());
        services.AddSingleton(new SkillsRegistrationOutcome("Bridge", Registered: true, Reason: "configured"));
    }
```
Confirm the file already has `using Microsoft.Extensions.DependencyInjection.Extensions;` for `TryAddSingleton` (it's used by `AddVikunja` per the exploration — reuse the same imports).

- [ ] **Step 4: Run registration tests to verify they pass**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter FullyQualifiedName~SkillsServiceCollectionExtensionsTests`
Expected: PASS.

- [ ] **Step 5: Add the appsettings sentinel block**

In `source/FlowHub.Web/appsettings.json`, add a `Bridge` sibling under `Skills` (fail-closed empty sentinels, matching Vikunja/Wallabag/Paperless):
```json
    "Bridge": {
      "_comment": "Fails closed at DI registration when BaseUrl/ApiToken are empty. Override via Skills__Bridge__* env vars. CatalogTtl is an ISO-8601 duration.",
      "BaseUrl": "",
      "ApiToken": "",
      "CatalogTtl": "00:05:00"
    }
```

- [ ] **Step 6: Add `.env.example` keys**

Append to `.env.example` (matching the existing `Skills__Vikunja__*` style):
```bash
# Bridge skill — routes alias-prefixed captures to the bridge service (issue/idea).
# Leave BaseUrl empty to disable (fails closed). CatalogTtl is optional (default 5m).
Skills__Bridge__BaseUrl=
Skills__Bridge__ApiToken=
Skills__Bridge__CatalogTtl=00:05:00
```

- [ ] **Step 7: Add the skippable live smoke test**

Create `tests/FlowHub.Skills.IntegrationTests/BridgeLiveTests.cs` (mirror `WallabagLiveTests` — `[Trait("Category","BetaSmoke")]`, `[SkippableFact]`, env-gated):
```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Skills.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Skills.IntegrationTests;

[Trait("Category", "BetaSmoke")]
public sealed class BridgeLiveTests
{
    [SkippableFact]
    public async Task HandleAsync_LiveBridge_CreatesIdea()
    {
        var baseUrl = Environment.GetEnvironmentVariable("Skills__Bridge__BaseUrl");
        var apiToken = Environment.GetEnvironmentVariable("Skills__Bridge__ApiToken");
        var alias = Environment.GetEnvironmentVariable("Bridge__SmokeAlias"); // e.g. "br"
        Skip.If(string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiToken)
                || string.IsNullOrWhiteSpace(alias), "Live bridge not configured.");

        var http = new HttpClient { BaseAddress = new Uri(baseUrl!) };
        var options = Options.Create(new BridgeOptions { BaseUrl = baseUrl, ApiToken = apiToken });
        var sut = new BridgeSkillIntegration(http, options, NullLogger<BridgeSkillIntegration>.Instance);

        var capture = new Capture(Guid.NewGuid(), ChannelKind.Web,
            $"{alias} flowhub live smoke test idea", DateTimeOffset.UtcNow, LifecycleStage.Routed,
            "Bridge", BridgeAlias: alias, BridgeAction: BridgeAction.Idea);

        var result = await sut.HandleAsync(capture, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.ExternalRef.Should().NotBeNullOrWhiteSpace();
    }
}
```

- [ ] **Step 8: Full suite + commit**

Run: `dotnet test FlowHub.slnx`
Expected: PASS (the live test skips without env vars).
Also verify the app builds and starts inert (no Bridge config → registration logs "not-configured"):
Run: `dotnet build FlowHub.slnx`
Expected: build succeeds, zero warnings.
```bash
git add source/FlowHub.Skills/SkillsServiceCollectionExtensions.cs \
        source/FlowHub.Web/appsettings.json \
        .env.example \
        tests/FlowHub.Skills.Tests/SkillsServiceCollectionExtensionsTests.cs \
        tests/FlowHub.Skills.IntegrationTests/BridgeLiveTests.cs
git commit -m "feat(skills): register Bridge skill (fail-closed) with config and live smoke test"
```

---

## Post-implementation (out of plan scope — for the human)

These are the spec's remaining rollout steps that live **outside** this FlowHub PR:

- [ ] **1.** Bridge PR in `~/repos/github/freaxnx01/public/bridge`: `.bridge.yaml` indexing, alias resolution (`{alias}` on capture handlers, 404 unknown / 409 ambiguous), issue `body` field, `BRIDGE_API_TOKEN` bearer auth (spec §B).
- [ ] **2.** Seed `.bridge.yaml` (`alias:`) in `bridge` (`br`), `agent-pipeline` (`agp`), `ai-instructions` (`ainstr`), and other frequently-captured repos (spec §Rollout step 2).
- [ ] **3.** Deploy `bridge serve` reachable from CT 136 (e.g. `bridge-serve.home.freaxnx01.ch` via the `homelab-service-routing` skill) with `GH_TOKEN` / `FORGEJO_TOKEN` / `BRIDGE_API_TOKEN` set; then set FlowHub `Skills__Bridge__BaseUrl` + `Skills__Bridge__ApiToken` and restart (spec §Deployment).

## Self-Review notes

- **Spec coverage:** §A `.bridge.yaml` → bridge PR (out of scope, noted). §B → bridge PR (out of scope, noted). §C: `BridgeSkillIntegration` (T8), `IBridgeCatalog` (T1–2), classifier changes (T4–5), `ClassificationResult` extension (T3), low-confidence gate (T7), config (T9) — all covered. §D contracts → T8 (issue/idea request shapes, url response, error→throw). §Error handling → T2 (catalog degradation), T5 (AI fallback), T7 (Unknown gate), T8 (throw→fault). §Testing split → Core.Tests (T3–4), Skills.ContractTests (T2, T8), Skills.IntegrationTests (T9), plus AI ComponentTests (T5) and pipeline ComponentTests (T6–7).
- **Deviation flagged:** config namespace `Skills:Bridge` vs spec's `Bridge__` (Global Constraints). Lifecycle initial state is `Raw` (not spec's "Submitted") — plan uses the real enum.
- **Type consistency:** `IBridgeCatalog.GetAliasesAsync → Task<IReadOnlySet<string>>`, `BridgeAliasMatcher.TryMatch(string, IReadOnlySet<string>, out string, out string)`, `BridgeAction {Unknown,Issue,Idea}`, `SkillResult(bool, string? ExternalRef, string? FailureReason)`, `BridgeOptions.SectionName="Skills:Bridge"` — used identically across tasks.
</content>
</invoke>
