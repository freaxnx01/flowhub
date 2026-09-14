# Sensitive Capture Parking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A capture judged sensitive is parked in a new terminal `Withheld` stage with a reason, and no integration, classifier call, or publish ever runs for it.

**Architecture:** A dedicated `ISensitivityScreen` pre-pass runs in `CaptureEnrichmentConsumer` before classification and before the Paperless attachment branch. It returns a three-way verdict; only `Safe` continues. The AI adapter is *total* — every failure mode returns `Unsure`, which parks — because a thrown exception would reach `LifecycleFaultObserver` and land an unscreened capture in `Unhandled`, the one stage the retry endpoint accepts.

**Tech Stack:** .NET 10 / C#, `Microsoft.Extensions.AI` (`IChatClient`, structured output), MassTransit, EF Core, xUnit + FluentAssertions + NSubstitute.

**Spec:** [`docs/superpowers/specs/2026-09-14-sensitive-capture-parking-design.md`](../specs/2026-09-14-sensitive-capture-parking-design.md)

## Global Constraints

- **No new NuGet packages.** Everything needed is already referenced.
- **No target-framework changes.** No `.csproj` edits beyond what a new file requires (none — the projects glob `**/*.cs`).
- **No `#nullable disable`, no `!` without a comment saying why it is safe.**
- **`sealed` by default; file-scoped namespaces; `record` for DTOs.**
- **`CancellationToken` on every async method that touches an external resource, and pass it down.**
- **Catch specific exception types** — with one deliberate exception, documented in Task 2: the screen's outermost catch is broad *by design*, because a leaked exception is the failure mode this feature exists to prevent.
- **Logging:** use a **hand-written `LoggerMessage.Define` delegate**, never the `[LoggerMessage]` source generator, for any code added to `FlowHub.Api`. The generator stops coverlet instrumenting the *entire* assembly on the CI runner (commit `5dbe439`, refs #11 / #91). `FlowHub.Web` and `FlowHub.AI` already use the generated partial pattern — match the file you are editing there, and do not convert existing code.
- **Tests:** xUnit, `FluentAssertions` for assertions, `NSubstitute` for mocks. No `[Fact]` containing logic — use `[Theory]` + `[InlineData]`.
- **TDD:** the failing test comes first, every task, no exceptions.
- **Commit after every task** using Conventional Commits with a `Refs #93` footer.

## Design note decided during planning (not in the approved spec)

The spec does not say what happens on a deployment with **no AI configured**
(`AiRegistrationOutcome.UsesAi == false`, where `KeywordClassifier` is used). There is no
way to judge sensitivity without a model, and the two candidate behaviours are:

- route everything (fail **open** — this is the exact bug #93 exists to prevent), or
- park everything (fail **closed** — safe, and loudly wrong rather than silently wrong).

**Task 4 implements the second**, via a `NotConfiguredSensitivityScreen` that returns
`Unsure` with reason `"sensitivity screen not configured"`, plus a warning at boot. A
deployment with AI off becomes a deployment that parks everything, which is visible
immediately. There is deliberately **no config flag to disable the screen** — a switch
that turns off the privacy guard is the foot-gun this issue is about.

CT 136 has AI configured, so this does not change the live deployment's behaviour.

---

### Task 1: The `Withheld` stage and the parking primitive

Establishes the terminal state and proves it is not retryable, before anything can write to it.

**Files:**
- Modify: `source/FlowHub.Core/Captures/LifecycleStage.cs`
- Modify: `source/FlowHub.Core/Captures/ICaptureService.cs:41`
- Modify: `source/FlowHub.Persistence/EfCaptureService.cs:163`
- Test: `tests/FlowHub.Api.IntegrationTests/Captures/CaptureRetryWithheldTests.cs` (create)
- Test: `tests/FlowHub.Persistence.Tests/EfCaptureServiceWithheldTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `LifecycleStage.Withheld`; `Task ICaptureService.MarkWithheldAsync(Guid id, string reason, CancellationToken cancellationToken = default)`.

**No migration is required.** `Stage` is persisted as a `string` with `HasMaxLength(32)`
(`source/FlowHub.Persistence/Entities/CaptureEntityTypeConfiguration.cs:15`), so a new
enum value is simply a new string value. Do not generate one.

- [ ] **Step 1: Write the failing persistence test**

Create `tests/FlowHub.Persistence.Tests/EfCaptureServiceWithheldTests.cs`. Match the
fixture style of the sibling tests already in that project (they build a
`FlowHubDbContext` over SQLite in-memory via the existing test base — open
`tests/FlowHub.Persistence.Tests/EfCaptureServiceTests.cs` and copy its setup verbatim
rather than inventing one).

```csharp
using FlowHub.Core.Captures;

namespace FlowHub.Persistence.Tests;

public sealed class EfCaptureServiceWithheldTests
{
    [Fact]
    public async Task MarkWithheldAsync_SetsWithheldStageAndReason()
    {
        // Arrange — use the same fixture construction as EfCaptureServiceTests.
        await using var fixture = new CaptureServiceFixture();
        var capture = await fixture.Service.SubmitAsync("anything", ChannelKind.Web, default);

        // Act
        await fixture.Service.MarkWithheldAsync(capture.Id, "sensitive — care journal", default);

        // Assert
        var stored = await fixture.Service.GetByIdAsync(capture.Id, default);
        stored!.Stage.Should().Be(LifecycleStage.Withheld);
        stored.FailureReason.Should().Be("sensitive — care journal");
    }
}
```

> If `CaptureServiceFixture` does not exist under that name, use whatever construction
> `EfCaptureServiceTests` uses — the point of this test is the two assertions, not the
> fixture.

- [ ] **Step 2: Run it and verify it fails**

Run: `dotnet test tests/FlowHub.Persistence.Tests -v minimal`
Expected: FAIL to compile — `LifecycleStage.Withheld` and `MarkWithheldAsync` do not exist.

- [ ] **Step 3: Add the enum value**

In `source/FlowHub.Core/Captures/LifecycleStage.cs`, append **after** `Unhandled` (append,
never reorder — the value is persisted by name, but reordering invites an int-mapped
reader to break):

```csharp
    /// <summary>
    /// Judged sensitive by the pre-pass — withheld from every Integration and never
    /// classified. Terminal and deliberately absent from CaptureRetryEndpoint's
    /// RetryableStages: a retry out of this stage is the irreversible disclosure
    /// this stage exists to prevent (issue #93).
    /// </summary>
    Withheld,
```

- [ ] **Step 4: Add the service method**

In `source/FlowHub.Core/Captures/ICaptureService.cs`, directly after `MarkUnhandledAsync`:

```csharp
    /// <summary>
    /// Parks a capture judged sensitive. Terminal — unlike <see cref="MarkUnhandledAsync"/>
    /// this stage is not retryable.
    /// </summary>
    Task MarkWithheldAsync(Guid id, string reason, CancellationToken cancellationToken = default);
```

In `source/FlowHub.Persistence/EfCaptureService.cs`, directly after `MarkUnhandledAsync`
(mirroring it exactly):

```csharp
    public async Task MarkWithheldAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        var capture = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Capture {id} not found.");
        await _repository.UpdateAsync(
            capture with { Stage = LifecycleStage.Withheld, FailureReason = reason },
            cancellationToken);
    }
```

- [ ] **Step 5: Run the persistence test and verify it passes**

Run: `dotnet test tests/FlowHub.Persistence.Tests -v minimal`
Expected: PASS.

> Other `ICaptureService` implementations (test doubles, in-memory fakes) will now fail to
> compile. Add the method to each, mirroring their `MarkUnhandledAsync`. Find them with:
> `grep -rln "MarkUnhandledAsync" --include=*.cs source/ tests/ | grep -v obj`

- [ ] **Step 6: Write the failing retry test**

Create `tests/FlowHub.Api.IntegrationTests/Captures/CaptureRetryWithheldTests.cs`. Copy
the fixture/WebApplicationFactory setup from the existing retry tests in that folder.

```csharp
using System.Net;
using FlowHub.Core.Captures;

namespace FlowHub.Api.IntegrationTests.Captures;

public sealed class CaptureRetryWithheldTests
{
    [Fact]
    public async Task Retry_WithheldCapture_IsRejectedAsNotRetryable()
    {
        // Arrange
        await using var app = new FlowHubApiFactory();
        var client = app.CreateClient();
        var service = app.Services.GetRequiredService<ICaptureService>();
        var capture = await service.SubmitAsync("anything", ChannelKind.Web, default);
        await service.MarkWithheldAsync(capture.Id, "sensitive — test fixture", default);

        // Act
        var response = await client.PostAsync($"/api/v1/captures/{capture.Id}/retry", null);

        // Assert — this is the assertion that keeps Withheld out of RetryableStages
        // if someone extends that array later.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
```

- [ ] **Step 7: Run it and verify it passes without any production change**

Run: `dotnet test tests/FlowHub.Api.IntegrationTests --filter CaptureRetryWithheldTests -v minimal`
Expected: PASS — `RetryableStages` (`source/FlowHub.Api/Endpoints/CaptureRetryEndpoint.cs:14`)
already excludes the new stage. **Do not add `Withheld` to that array.** This test exists
to fail loudly if a future change does.

- [ ] **Step 8: Commit**

```bash
git add source/FlowHub.Core/Captures/LifecycleStage.cs \
        source/FlowHub.Core/Captures/ICaptureService.cs \
        source/FlowHub.Persistence/EfCaptureService.cs \
        tests/FlowHub.Persistence.Tests/EfCaptureServiceWithheldTests.cs \
        tests/FlowHub.Api.IntegrationTests/Captures/CaptureRetryWithheldTests.cs
git commit -m "feat(core): add terminal Withheld lifecycle stage

Not added to RetryableStages — a retry out of this stage is the
irreversible disclosure it exists to prevent. Asserted by a test.

Refs #93"
```

---

### Task 2: The sensitivity screen port and its AI adapter

**Files:**
- Create: `source/FlowHub.Core/Classification/ISensitivityScreen.cs`
- Create: `source/FlowHub.AI/AiSensitivityResponse.cs`
- Create: `source/FlowHub.AI/AiSensitivityScreen.cs`
- Modify: `source/FlowHub.AI/AiPrompts.cs` (append a prompt + a `BuildMessages` overload)
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiSensitivityScreenTests.cs` (create)

**Interfaces:**
- Consumes: `ClassifierTrace` (`FlowHub.Core.Classification`), `IChatClient`, `AiModelInfo`.
- Produces:
  - `enum Sensitivity { Sensitive, Unsure, Safe }`
  - `sealed record SensitivityVerdict(Sensitivity Verdict, string Reason, ClassifierTrace? Trace = null)`
  - `interface ISensitivityScreen { Task<SensitivityVerdict> ScreenAsync(string content, CancellationToken cancellationToken); }`
  - `internal sealed partial class AiSensitivityScreen : ISensitivityScreen` with constructor
    `(IChatClient chat, ILogger<AiSensitivityScreen> log, ChatOptions options, AiModelInfo modelInfo)`

- [ ] **Step 1: Write the failing adapter tests**

Create `tests/FlowHub.Web.ComponentTests/Ai/AiSensitivityScreenTests.cs`. The fake
`IChatClient` pattern is copied from `AiClassifierTests.cs:27-29`.

```csharp
using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using Microsoft.Extensions.AI;
using NSubstitute.ExceptionExtensions;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class AiSensitivityScreenTests
{
    private readonly IChatClient _chat = Substitute.For<IChatClient>();
    private readonly FakeLogger<AiSensitivityScreen> _log = new();
    private readonly ChatOptions _opts = new() { MaxOutputTokens = 300, Temperature = 0.2f };

    private AiSensitivityScreen Sut() =>
        new(_chat, _log, _opts, new AiModelInfo("OpenRouter", "test-model"));

    private static ChatResponse JsonResponse(object payload) =>
        new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload)));

    private void Responds(object payload) =>
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(JsonResponse(payload));

    [Theory]
    [InlineData("sensitive", Sensitivity.Sensitive)]
    [InlineData("unsure", Sensitivity.Unsure)]
    [InlineData("safe", Sensitivity.Safe)]
    public async Task ScreenAsync_WellFormedResponse_MapsTheVerdict(string answer, Sensitivity expected)
    {
        Responds(new { verdict = answer, reason = "because" });

        var result = await Sut().ScreenAsync("anything", default);

        result.Verdict.Should().Be(expected);
    }

    [Fact]
    public async Task ScreenAsync_SafeVerdict_HasEmptyReason()
    {
        Responds(new { verdict = "safe", reason = "because" });

        var result = await Sut().ScreenAsync("anything", default);

        result.Reason.Should().BeEmpty();
    }

    [Fact]
    public async Task ScreenAsync_MalformedJson_ReturnsUnsureAndDoesNotThrow()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json at all")));

        var result = await Sut().ScreenAsync("anything", default);

        result.Verdict.Should().Be(Sensitivity.Unsure);
        result.Reason.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ScreenAsync_ProviderThrows_ReturnsUnsureAndDoesNotThrow()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new HttpRequestException("provider down"));

        var result = await Sut().ScreenAsync("anything", default);

        result.Verdict.Should().Be(Sensitivity.Unsure);
        result.Reason.Should().Contain("HttpRequestException");
    }

    [Fact]
    public async Task ScreenAsync_UnrecognisedVerdictString_ReturnsUnsure()
    {
        Responds(new { verdict = "definitely-fine", reason = "because" });

        var result = await Sut().ScreenAsync("anything", default);

        result.Verdict.Should().Be(Sensitivity.Unsure);
    }

    [Fact]
    public async Task ScreenAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
             .ThrowsAsync(new OperationCanceledException(cts.Token));

        // Shutdown is not a screen failure — the capture is simply not processed yet.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Sut().ScreenAsync("anything", cts.Token));
    }

    [Fact]
    public async Task ScreenAsync_ForwardsCancellationTokenToChatClient()
    {
        using var cts = new CancellationTokenSource();
        Responds(new { verdict = "safe", reason = "" });

        await Sut().ScreenAsync("anything", cts.Token);

        await _chat.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), cts.Token);
    }
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter AiSensitivityScreenTests -v minimal`
Expected: FAIL to compile — none of the types exist.

- [ ] **Step 3: Create the port**

Create `source/FlowHub.Core/Classification/ISensitivityScreen.cs`:

```csharp
namespace FlowHub.Core.Classification;

/// <summary>Three-way sensitivity verdict. Only <see cref="Safe"/> may be routed.</summary>
public enum Sensitivity
{
    /// <summary>Must not leave the machine.</summary>
    Sensitive,

    /// <summary>Could not be decided — parks, per the asymmetry in issue #93.</summary>
    Unsure,

    /// <summary>Safe to classify and route.</summary>
    Safe,
}

/// <summary>
/// Outcome of <see cref="ISensitivityScreen.ScreenAsync"/>. <paramref name="Reason"/> is
/// empty for <see cref="Sensitivity.Safe"/> and otherwise names why the capture parked —
/// it is written to the capture's FailureReason so a park can be audited without reading
/// the content back out.
/// </summary>
public sealed record SensitivityVerdict(
    Sensitivity Verdict,
    string Reason,
    ClassifierTrace? Trace = null);

/// <summary>
/// Driving port for the sensitivity pre-pass (issue #93). Runs before classification.
/// </summary>
/// <remarks>
/// Implementations MUST NOT throw for any provider, parsing, or timeout failure — they
/// return <see cref="Sensitivity.Unsure"/> instead. A leaked exception reaches
/// LifecycleFaultObserver, which marks the capture Unhandled — a stage the retry endpoint
/// accepts — putting an unscreened capture one button press from being routed.
/// Cancellation originating from the caller's own token is the sole exception and
/// propagates normally: that is shutdown, not a screen failure.
/// </remarks>
public interface ISensitivityScreen
{
    Task<SensitivityVerdict> ScreenAsync(string content, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Create the response DTO**

Create `source/FlowHub.AI/AiSensitivityResponse.cs`. The `AllowedValues` attribute is what
actually constrains the model — `Microsoft.Extensions.AI` generates the structured-output
schema from it (see the comment in `AiClassificationResponse.cs`).

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FlowHub.AI;

internal sealed record AiSensitivityResponse(
    [property: Description("sensitive, unsure, or safe")]
    [property: AllowedValues("sensitive", "unsure", "safe")]
    [property: JsonPropertyName("verdict")]
    string Verdict,

    [property: Description("Short reason naming the CATEGORY of sensitivity, never quoting the content")]
    [property: JsonPropertyName("reason")]
    string? Reason);
```

- [ ] **Step 5: Add the prompt**

Append to `source/FlowHub.AI/AiPrompts.cs`, before the closing brace, following the
`BridgeSystemPrompt` / `BuildBridgeMessages` pattern already in that file:

```csharp
    private const string SensitivitySystemPrompt = """
        You screen a personal note before it is filed into shared, networked tools
        (a task manager, a code forge, a read-later service). You decide ONE thing:
        must this note stay on the operator's own machine?

        Answer "sensitive" when the note concerns any of:
          - a named person's health, medication, therapy, diagnosis or treatment
          - the care, schooling, custody or wellbeing of a child
          - another person's private circumstances, finances or personal profile
          - intimate, legal or otherwise confidential personal matters

        Answer "safe" ONLY when the note is plainly ordinary: a link to save, a film
        to watch, an errand, a shopping item, a work or code task, a quote.

        Answer "unsure" whenever you cannot confidently place it in either group.
        Sensitive material is often written plainly and without any medical or
        emotional vocabulary — a note that reads as a mundane observation may be
        therapy or care material. If the note hints at a person's private situation
        and you cannot tell, answer "unsure".

        Filing a harmless note as sensitive costs the operator an inconvenience.
        Filing a sensitive note as safe discloses it irreversibly. When the two are
        in tension, prefer "unsure".

        For "reason", name only the CATEGORY (e.g. "health detail about a named
        person", "child care material"). Never quote or restate the note itself.

        Reply ONLY via the structured response schema. Never include explanations.
        """;

    internal static IList<ChatMessage> BuildSensitivityMessages(string content) =>
    [
        new ChatMessage(ChatRole.System, SensitivitySystemPrompt),
        new ChatMessage(ChatRole.User, content),
    ];
```

- [ ] **Step 6: Create the adapter**

Create `source/FlowHub.AI/AiSensitivityScreen.cs`:

```csharp
using System.Diagnostics;
using FlowHub.Core.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FlowHub.AI;

/// <summary>
/// LLM-backed <see cref="ISensitivityScreen"/> (issue #93). Total by construction: every
/// provider, schema, or parse failure degrades to <see cref="Sensitivity.Unsure"/>, which
/// parks the capture. See the remarks on <see cref="ISensitivityScreen"/> for why a throw
/// here would be a disclosure bug rather than an availability one.
/// </summary>
internal sealed partial class AiSensitivityScreen : ISensitivityScreen
{
    private readonly IChatClient _chat;
    private readonly ILogger<AiSensitivityScreen> _log;
    private readonly ChatOptions _options;
    private readonly AiModelInfo _modelInfo;

    public AiSensitivityScreen(
        IChatClient chat,
        ILogger<AiSensitivityScreen> log,
        ChatOptions options,
        AiModelInfo modelInfo)
    {
        _chat = chat;
        _log = log;
        _options = options;
        _modelInfo = modelInfo;
    }

    public async Task<SensitivityVerdict> ScreenAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var sw = Stopwatch.StartNew();

        try
        {
            var response = await _chat.GetResponseAsync<AiSensitivityResponse>(
                AiPrompts.BuildSensitivityMessages(content),
                _options,
                cancellationToken: cancellationToken);

            sw.Stop();

            if (!response.TryGetResult(out var payload))
            {
                return Park("schema_violation", sw);
            }

            var trace = new ClassifierTrace(
                ClassifierKind.Ai,
                (int)sw.ElapsedMilliseconds,
                _modelInfo.Provider,
                _modelInfo.Model,
                (int?)response.Usage?.InputTokenCount,
                (int?)response.Usage?.OutputTokenCount);

            return payload.Verdict switch
            {
                "safe" => new SensitivityVerdict(Sensitivity.Safe, string.Empty, trace),
                "sensitive" => new SensitivityVerdict(
                    Sensitivity.Sensitive, Reason(payload, "sensitive"), trace),
                "unsure" => new SensitivityVerdict(
                    Sensitivity.Unsure, Reason(payload, "unsure"), trace),
                // AllowedValues constrains the schema, but a provider that ignores it
                // must not be able to produce a routable verdict by accident.
                _ => Park($"unrecognised verdict '{payload.Verdict}'", sw),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a screen failure — the capture is simply not processed yet.
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad, against the repo's catch-specific rule. Every escape
            // from this method is a capture that bypasses the privacy guard, so the
            // catch-all IS the feature. Narrowing it would reintroduce issue #93.
            sw.Stop();
            return Park(ex.GetType().Name, sw);
        }
    }

    private static string Reason(AiSensitivityResponse payload, string fallback) =>
        string.IsNullOrWhiteSpace(payload.Reason) ? fallback : payload.Reason;

    private SensitivityVerdict Park(string reason, Stopwatch sw)
    {
        LogScreenDegraded(reason, sw.ElapsedMilliseconds);
        return new SensitivityVerdict(
            Sensitivity.Unsure,
            $"sensitivity screen unavailable — {reason}",
            new ClassifierTrace(
                ClassifierKind.Ai,
                (int)sw.ElapsedMilliseconds,
                _modelInfo.Provider,
                _modelInfo.Model));
    }

    [LoggerMessage(
        EventId = 3020,
        Level = LogLevel.Warning,
        Message = "AiSensitivityScreen degraded to Unsure (reason={Reason}, duration_ms={DurationMs}) — capture parked")]
    private partial void LogScreenDegraded(string reason, long durationMs);
}
```

- [ ] **Step 7: Run the adapter tests and verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter AiSensitivityScreenTests -v minimal`
Expected: PASS, all eight.

- [ ] **Step 8: Commit**

```bash
git add source/FlowHub.Core/Classification/ISensitivityScreen.cs \
        source/FlowHub.AI/AiSensitivityResponse.cs \
        source/FlowHub.AI/AiSensitivityScreen.cs \
        source/FlowHub.AI/AiPrompts.cs \
        tests/FlowHub.Web.ComponentTests/Ai/AiSensitivityScreenTests.cs
git commit -m "feat(ai): add three-way sensitivity screen with a total adapter

Every provider, schema and parse failure degrades to Unsure rather than
throwing — a leaked exception would land an unscreened capture in
Unhandled, which the retry endpoint accepts.

Refs #93"
```

---

### Task 3: Run the screen in the enrichment consumer

**Files:**
- Modify: `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs:18-57,163`
- Modify: `tests/FlowHub.Web.ComponentTests/Pipeline/PipelineTestBase.cs` (default registration)
- Test: `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerSensitivityTests.cs` (create)

**Interfaces:**
- Consumes: `ISensitivityScreen`, `SensitivityVerdict`, `Sensitivity` (Task 2); `ICaptureService.MarkWithheldAsync` (Task 1).
- Produces: nothing new.

- [ ] **Step 1: Give the test base a default screen**

Existing pipeline tests must keep passing unchanged. In
`tests/FlowHub.Web.ComponentTests/Pipeline/PipelineTestBase.cs`, inside `Build`, register a
default **before** the caller's `configure` delegate runs so a test can override it:

```csharp
// Default: every capture is Safe, so tests written before the sensitivity screen
// existed are unaffected. Tests that care register their own ISensitivityScreen.
var safeScreen = Substitute.For<ISensitivityScreen>();
safeScreen.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(new SensitivityVerdict(Sensitivity.Safe, string.Empty));
services.AddSingleton(safeScreen);
```

> Place this next to the other default registrations in that method. If `configure` runs
> before the defaults in this file, use `TryAddSingleton` for the default instead so the
> caller's registration wins.

- [ ] **Step 2: Write the failing consumer tests**

Create `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerSensitivityTests.cs`:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;
using FlowHub.Core.Events;
using FlowHub.Web.Pipeline;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class CaptureEnrichmentConsumerSensitivityTests
{
    private static ISensitivityScreen ScreenReturning(Sensitivity verdict, string reason)
    {
        var screen = Substitute.For<ISensitivityScreen>();
        screen.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(new SensitivityVerdict(verdict, reason));
        return screen;
    }

    private static IClassifier TrackingClassifier()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(new ClassificationResult(["t"], "Vikunja", VikunjaProject: "Inbox"));
        return classifier;
    }

    [Theory]
    [InlineData(Sensitivity.Sensitive)]
    [InlineData(Sensitivity.Unsure)]
    public async Task Consume_NonSafeVerdict_WithholdsAndNeverClassifies(Sensitivity verdict)
    {
        var classifier = TrackingClassifier();
        var screen = ScreenReturning(verdict, "child care material");

        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(classifier); s.AddSingleton(screen); },
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("anything at all", ChannelKind.Web, default);

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        var stored = await captureService.GetByIdAsync(capture.Id, default);
        stored!.Stage.Should().Be(LifecycleStage.Withheld);
        stored.FailureReason.Should().Contain("child care material");

        // The two assertions that matter: no classification, and nothing published
        // for the routing stage to pick up and hand to an integration.
        await classifier.DidNotReceive().ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Consume_SafeVerdict_ClassifiesAsBefore()
    {
        var classifier = TrackingClassifier();
        var screen = ScreenReturning(Sensitivity.Safe, string.Empty);

        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(classifier); s.AddSingleton(screen); },
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("an ordinary errand", ChannelKind.Web, default);

        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();
        await classifier.Received(1).ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_CaptureAwaitingTranscription_IsNotScreenedOnItsPlaceholder()
    {
        var screen = ScreenReturning(Sensitivity.Safe, string.Empty);

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(screen),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Publish directly so NeedsTranscription can be set — a voice capture holds
        // placeholder text until the transcription consumer republishes it.
        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("[voice message]", ChannelKind.Telegram, default);
        await harness.Bus.Publish(new CaptureCreated(
            capture.Id, "[voice message]", DateTimeOffset.UtcNow, NeedsTranscription: true));

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.NeedsTranscription))
            .Should().BeTrue();

        // Screening the placeholder would be meaningless; the transcript gets screened
        // when the transcription consumer republishes without the flag.
        await screen.DidNotReceive().ScreenAsync("[voice message]", Arg.Any<CancellationToken>());
    }
}
```

> Match the `CaptureCreated` constructor to the real record — open
> `source/FlowHub.Core/Events/CaptureCreated.cs` and supply every required member. The
> three assertions are what matter, not the exact construction.

- [ ] **Step 3: Run them and verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter CaptureEnrichmentConsumerSensitivityTests -v minimal`
Expected: FAIL — captures reach `Classified`, not `Withheld`.

- [ ] **Step 4: Inject the screen**

In `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs`, add the field and
constructor parameter alongside the existing four:

```csharp
    private readonly ISensitivityScreen _sensitivity;
```

```csharp
    public CaptureEnrichmentConsumer(
        IClassifier classifier,
        EnricherDispatcher enricher,
        ICaptureService captureService,
        ISensitivityScreen sensitivity,
        ILogger<CaptureEnrichmentConsumer> logger)
    {
        _classifier = classifier;
        _enricher = enricher;
        _captureService = captureService;
        _sensitivity = sensitivity;
        _logger = logger;
    }
```

- [ ] **Step 5: Run the screen before classification and before the attachment branch**

In `Consume`, insert between the `NeedsTranscription` early-return and the
`HasAttachment` branch:

```csharp
        // Issue #93. Runs before classification AND before the Paperless attachment
        // branch, because both end in an Integration write. Only Safe continues —
        // Unsure parks too, since routing a sensitive capture is irreversible while
        // parking a benign one is an inconvenience.
        var verdict = await _sensitivity.ScreenAsync(msg.Content, ct);
        if (verdict.Verdict != Sensitivity.Safe)
        {
            await _captureService.MarkWithheldAsync(msg.CaptureId, verdict.Reason, ct);
            LogWithheld(msg.CaptureId, verdict.Verdict.ToString(), verdict.Reason);
            return;
        }
```

Add the log declaration next to the other `[LoggerMessage]` partials at the bottom of the
file (this is `FlowHub.Web`, where the generated pattern is already in use):

```csharp
    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Information,
        Message = "Capture {CaptureId} withheld ({Verdict}) — {Reason}")]
    private partial void LogWithheld(Guid captureId, string verdict, string reason);
```

> The message deliberately logs the *reason category*, never the capture content.

- [ ] **Step 6: Run the new tests and verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter CaptureEnrichmentConsumerSensitivityTests -v minimal`
Expected: PASS, all four cases.

- [ ] **Step 7: Run the whole component suite for regressions**

Run: `dotnet test tests/FlowHub.Web.ComponentTests -v minimal`
Expected: PASS. If pre-existing pipeline tests fail with a DI resolution error for
`ISensitivityScreen`, the default registration from Step 1 is not being applied — fix
Step 1, do not weaken the consumer.

- [ ] **Step 8: Commit**

```bash
git add source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs \
        tests/FlowHub.Web.ComponentTests/Pipeline/PipelineTestBase.cs \
        tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerSensitivityTests.cs
git commit -m "feat(pipeline): withhold sensitive captures before classification

Runs before the Paperless attachment branch too — both paths end in an
Integration write. Unsure parks alongside Sensitive.

Refs #93"
```

---

### Task 4: Registration, and fail-closed when AI is not configured

**Files:**
- Modify: `source/FlowHub.AI/AiServiceCollectionExtensions.cs:109-113,155-165`
- Create: `source/FlowHub.AI/NotConfiguredSensitivityScreen.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/NotConfiguredSensitivityScreenTests.cs` (create)

**Interfaces:**
- Consumes: `ISensitivityScreen`, `Sensitivity`, `SensitivityVerdict` (Task 2).
- Produces: `internal sealed class NotConfiguredSensitivityScreen : ISensitivityScreen` (parameterless).

See *Design note decided during planning* above for why the unconfigured case parks rather
than routes. There is deliberately no flag to disable the screen.

- [ ] **Step 1: Write the failing test**

Create `tests/FlowHub.Web.ComponentTests/Ai/NotConfiguredSensitivityScreenTests.cs`:

```csharp
using FlowHub.AI;
using FlowHub.Core.Classification;

namespace FlowHub.Web.ComponentTests.Ai;

public sealed class NotConfiguredSensitivityScreenTests
{
    [Fact]
    public async Task ScreenAsync_AlwaysParks()
    {
        var result = await new NotConfiguredSensitivityScreen()
            .ScreenAsync("a completely ordinary errand", default);

        // With no model there is no way to judge sensitivity. Parking everything is
        // loudly wrong; routing everything is silently wrong — and is issue #93.
        result.Verdict.Should().Be(Sensitivity.Unsure);
        result.Reason.Should().Contain("not configured");
    }
}
```

- [ ] **Step 2: Run it and verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter NotConfiguredSensitivityScreenTests -v minimal`
Expected: FAIL to compile — the type does not exist.

- [ ] **Step 3: Create the fallback screen**

Create `source/FlowHub.AI/NotConfiguredSensitivityScreen.cs`:

```csharp
using FlowHub.Core.Classification;

namespace FlowHub.AI;

/// <summary>
/// Used when no AI provider is configured. Without a model there is no way to judge
/// sensitivity, so every capture parks (issue #93). This makes a misconfigured
/// deployment obviously broken rather than quietly unsafe. There is intentionally no
/// switch to disable the screen — that switch would be the vulnerability.
/// </summary>
internal sealed class NotConfiguredSensitivityScreen : ISensitivityScreen
{
    public Task<SensitivityVerdict> ScreenAsync(string content, CancellationToken cancellationToken) =>
        Task.FromResult(new SensitivityVerdict(
            Sensitivity.Unsure,
            "sensitivity screen not configured — no AI provider"));
}
```

- [ ] **Step 4: Register both paths**

In `source/FlowHub.AI/AiServiceCollectionExtensions.cs`, inside the `if (!outcome.UsesAi)`
block (currently lines 109-113), add before the `return`:

```csharp
            services.AddSingleton<ISensitivityScreen>(_ => new NotConfiguredSensitivityScreen());
```

And in the AI-configured path, next to the `AiClassifier` registration (after line 165):

```csharp
        // Its own ChatOptions instance: the screen answers one yes/no question and does
        // not need the classifier's output budget.
        services.AddSingleton<ISensitivityScreen>(sp => new AiSensitivityScreen(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILogger<AiSensitivityScreen>>(),
            new ChatOptions { MaxOutputTokens = 200, Temperature = 0.2f },
            sp.GetRequiredService<AiModelInfo>()));
```

- [ ] **Step 5: Run the test and verify it passes**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter NotConfiguredSensitivityScreenTests -v minimal`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add source/FlowHub.AI/NotConfiguredSensitivityScreen.cs \
        source/FlowHub.AI/AiServiceCollectionExtensions.cs \
        tests/FlowHub.Web.ComponentTests/Ai/NotConfiguredSensitivityScreenTests.cs
git commit -m "feat(ai): register the sensitivity screen, fail closed without AI

No model means no way to judge sensitivity, so every capture parks. A
deployment with AI off becomes obviously broken rather than quietly unsafe.

Refs #93"
```

---

### Task 5: Surface the verdict in the preview endpoint

**Files:**
- Modify: `source/FlowHub.Api/Requests/CapturePreviewResponse.cs`
- Modify: `source/FlowHub.Api/Endpoints/CapturePreviewEndpoint.cs:23-60`
- Test: `tests/FlowHub.Api.IntegrationTests/Captures/CapturePreviewSensitivityTests.cs` (create)

**Interfaces:**
- Consumes: `ISensitivityScreen`, `Sensitivity`, `SensitivityVerdict` (Task 2).
- Produces: `CapturePreviewResponse` gains `Sensitivity Sensitivity` and `string? SensitivityReason` as the **last two** positional members.

- [ ] **Step 1: Write the failing endpoint tests**

Create `tests/FlowHub.Api.IntegrationTests/Captures/CapturePreviewSensitivityTests.cs`,
copying the factory/client setup from the existing `CapturePreviewTests.cs` in that folder.

```csharp
using System.Net.Http.Json;
using FlowHub.Core.Classification;

namespace FlowHub.Api.IntegrationTests.Captures;

public sealed class CapturePreviewSensitivityTests
{
    [Fact]
    public async Task Preview_SensitiveVerdict_ReturnsVerdictAndProposesNoTarget()
    {
        var screen = Substitute.For<ISensitivityScreen>();
        screen.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(new SensitivityVerdict(Sensitivity.Sensitive, "health detail about a named person"));

        await using var app = new FlowHubApiFactory(s => s.AddSingleton(screen));
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/captures/preview",
            new { Content = "anything", Source = "Telegram" });

        var body = await response.Content.ReadFromJsonAsync<CapturePreviewResponse>();

        body!.Sensitivity.Should().Be(Sensitivity.Sensitive);
        body.SensitivityReason.Should().Be("health detail about a named person");

        // No target is proposed — preview mirrors the production path, and a proposed
        // target for a capture that will never be routed invites someone to act on it.
        body.MatchedSkill.Should().BeEmpty();
        body.VikunjaProject.Should().BeNull();
        body.VikunjaProjectId.Should().BeNull();
    }

    [Fact]
    public async Task Preview_SafeVerdict_ReturnsTheUsualShape()
    {
        var screen = Substitute.For<ISensitivityScreen>();
        screen.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(new SensitivityVerdict(Sensitivity.Safe, string.Empty));

        await using var app = new FlowHubApiFactory(s => s.AddSingleton(screen));
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/captures/preview",
            new { Content = "https://example.com/article", Source = "Telegram" });

        var body = await response.Content.ReadFromJsonAsync<CapturePreviewResponse>();

        body!.Sensitivity.Should().Be(Sensitivity.Safe);
        body.SensitivityReason.Should().BeNull();
    }
}
```

> If `FlowHubApiFactory` has no service-override constructor, add one following the
> pattern the existing integration tests use for substituting services.

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/FlowHub.Api.IntegrationTests --filter CapturePreviewSensitivityTests -v minimal`
Expected: FAIL to compile — `CapturePreviewResponse` has no `Sensitivity` member.

- [ ] **Step 3: Extend the response record**

In `source/FlowHub.Api/Requests/CapturePreviewResponse.cs`, append two members **after**
`Trace` (appending keeps every existing positional construction valid):

```csharp
    ClassifierTrace? Trace,

    /// <summary>
    /// The privacy pre-pass verdict (issue #93). Anything other than
    /// <see cref="Sensitivity.Safe"/> means the capture would be withheld and every
    /// target field above is null — the capture is not classified at all.
    /// </summary>
    Sensitivity Sensitivity,

    /// <summary>Why it would be withheld. Null when the verdict is Safe.</summary>
    string? SensitivityReason);
```

> A positional member whose name matches its type (`Sensitivity Sensitivity`) is the
> "Color Color" pattern. It is legal C# and compiles — do not rename it to work around a
> perceived conflict; the JSON field name is part of the survey tooling's contract.

- [ ] **Step 4: Screen first in the endpoint**

In `source/FlowHub.Api/Endpoints/CapturePreviewEndpoint.cs`, add `ISensitivityScreen screen`
to the `PreviewAsync` parameter list, then insert immediately after the validation block
and before `classifier.ClassifyAsync`:

```csharp
        // Mirror the production path exactly (spec D5): a non-Safe verdict short-circuits
        // with no classification and no proposed target. This also makes a whole-corpus
        // calibration run cost one screen call per capture rather than two calls.
        var verdict = await screen.ScreenAsync(request.Content, ct);
        if (verdict.Verdict != Sensitivity.Safe)
        {
            return TypedResults.Ok(new CapturePreviewResponse(
                MatchedSkill: string.Empty,
                Title: null,
                Tags: [],
                Entities: null,
                VikunjaProject: null,
                VikunjaProjectId: null,
                VikunjaProjectResolved: false,
                BridgeAlias: null,
                BridgeTarget: null,
                BridgeAction: BridgeAction.Unknown,
                BridgeBody: null,
                UnknownShorthand: null,
                Trace: verdict.Trace,
                Sensitivity: verdict.Verdict,
                SensitivityReason: verdict.Reason));
        }
```

Then extend the existing success return at the end of the method with the two new
arguments:

```csharp
            result.Trace,
            Sensitivity.Safe,
            null));
```

- [ ] **Step 5: Run the endpoint tests and verify they pass**

Run: `dotnet test tests/FlowHub.Api.IntegrationTests --filter CapturePreviewSensitivityTests -v minimal`
Expected: PASS, both.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test -v minimal`
Expected: PASS.

> **Known local-environment gotcha:** `just test` and the `.slnx` solution build fail
> locally with `NU1903`. Run per-project `dotnet test` invocations as above instead. This
> is a local toolchain issue, not a code failure — do not "fix" it by changing packages.

- [ ] **Step 7: Commit**

```bash
git add source/FlowHub.Api/Requests/CapturePreviewResponse.cs \
        source/FlowHub.Api/Endpoints/CapturePreviewEndpoint.cs \
        tests/FlowHub.Api.IntegrationTests/Captures/CapturePreviewSensitivityTests.cs
git commit -m "feat(api): surface the sensitivity verdict in capture preview

Short-circuits on a non-Safe verdict and proposes no target, so preview
mirrors the production path and stays auditable without writes.

Refs #93"
```

---

### Task 6: Synthesised sensitivity fixtures

The judgement itself, not the wiring. Runs against the real prompt with a scripted chat
client, so a prompt edit that stops catching the unmarked case fails CI.

**Files:**
- Test: `tests/FlowHub.Web.ComponentTests/Ai/SensitivityFixtureTests.cs` (create)

**Interfaces:**
- Consumes: `AiSensitivityScreen`, `Sensitivity` (Task 2).
- Produces: nothing.

**All content below is invented.** No real capture text enters this repo — that is an
acceptance criterion, not a preference.

- [ ] **Step 1: Write the fixture tests**

Create `tests/FlowHub.Web.ComponentTests/Ai/SensitivityFixtureTests.cs`:

```csharp
using System.Text.Json;
using FlowHub.AI;
using FlowHub.Core.Classification;
using Microsoft.Extensions.AI;

namespace FlowHub.Web.ComponentTests.Ai;

/// <summary>
/// Fixtures reproducing the SHAPES the corpus survey found, with entirely invented
/// content. Fixture 4 is the one that matters: material that reads as a mundane note
/// and is in fact care/therapy content. A keyword rule cannot catch it, which is why
/// the screen is a model call rather than a regex.
/// </summary>
public sealed class SensitivityFixtureTests
{
    public static TheoryData<string, string> SensitiveShapes() => new()
    {
        { "care journal", "Mittwoch war ein besserer Tag, weniger Weinen beim Abholen, Erzieherin meint es wird langsam ruhiger" },
        { "medication note", "neue Dosis ab Montag, halbe Tablette morgens, Kontrolle in vier Wochen" },
        { "third-party profile", "Sandra, 41, getrennt seit letztem Jahr, zwei Kinder, arbeitet Teilzeit, sucht gerade eine neue Wohnung" },
        { "unmarked care material", "beim Autofahren redet sie viel mehr, ohne Blickkontakt geht es ihr leichter — das nächste Mal wieder so versuchen" },
    };

    public static TheoryData<string, string> SafeShapes() => new()
    {
        { "link", "https://example.com/an-article-worth-reading" },
        { "errand", "Batterien kaufen" },
        { "dev task", "bridge mcp from outside LAN?" },
        { "media", "Mr Bean Filme" },
    };

    private static AiSensitivityScreen ScreenRespondingWith(string verdict)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                JsonSerializer.Serialize(new { verdict, reason = "category" }))));

        return new AiSensitivityScreen(
            chat,
            new FakeLogger<AiSensitivityScreen>(),
            new ChatOptions { MaxOutputTokens = 200, Temperature = 0.2f },
            new AiModelInfo("OpenRouter", "test-model"));
    }

    [Theory]
    [MemberData(nameof(SensitiveShapes))]
    public async Task SensitiveShape_WhenModelSaysSensitive_IsWithheldWithACategoryReason(string shape, string content)
    {
        var result = await ScreenRespondingWith("sensitive").ScreenAsync(content, default);

        result.Verdict.Should().Be(Sensitivity.Sensitive, because: $"the {shape} shape must never route");
        result.Reason.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(SensitiveShapes))]
    public async Task SensitiveShape_WhenModelIsUnsure_StillParks(string shape, string content)
    {
        var result = await ScreenRespondingWith("unsure").ScreenAsync(content, default);

        result.Verdict.Should().NotBe(Sensitivity.Safe, because: $"doubt about the {shape} shape parks");
    }

    [Theory]
    [MemberData(nameof(SafeShapes))]
    public async Task SafeShape_WhenModelSaysSafe_Routes(string shape, string content)
    {
        var result = await ScreenRespondingWith("safe").ScreenAsync(content, default);

        result.Verdict.Should().Be(Sensitivity.Safe, because: $"the {shape} shape is ordinary");
        result.Reason.Should().BeEmpty();
    }

    [Fact]
    public void SensitivityPrompt_NamesTheUnmarkedCase()
    {
        // The single most important line in the prompt: the corpus's most dangerous
        // capture had no medical vocabulary at all. If this instruction is ever edited
        // away, the screen silently stops catching that class.
        var messages = AiPrompts.BuildSensitivityMessages("x");
        var system = messages[0].Text;

        system.Should().Contain("without any medical or");
        system.Should().Contain("prefer \"unsure\"");
    }
}
```

> These fixtures assert the **contract and the prompt**, not the live model's judgement —
> a scripted chat client keeps CI hermetic and free. The real-model check is the corpus
> calibration run described in the spec, which is an operator step, not a CI step.

- [ ] **Step 2: Run them and verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter SensitivityFixtureTests -v minimal`
Expected: PASS.

- [ ] **Step 3: Run the full suite one last time**

Run: `dotnet test -v minimal`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/FlowHub.Web.ComponentTests/Ai/SensitivityFixtureTests.cs
git commit -m "test(ai): synthesised sensitivity fixtures incl. the unmarked case

All content invented. Also pins the prompt line covering material that
carries no medical vocabulary — the class a keyword rule cannot catch.

Refs #93"
```

---

## Acceptance criteria mapping

| Spec AC | Task |
|---|---|
| Sensitive/Unsure → `Withheld` with a reason, no `HandleAsync` | 1, 3 |
| `ClassifyAsync` not called for a withheld capture | 3 |
| The screen never throws; every failure yields `Unsure` | 2 |
| `Withheld` not in `RetryableStages`, asserted by a test | 1 |
| Verdict and reason visible in preview, no target proposed | 5 |
| Transcription: screened on the transcript, not the placeholder | 3 |
| Synthesised fixtures incl. the unmarked case | 6 |
| Full suite green | 5, 6 |
