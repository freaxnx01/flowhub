# Bridge Classification Option Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the AI classifier return `"Bridge"` as a matched skill, behind a configuration flag that is off by default.

**Architecture:** Three small changes in `FlowHub.AI` plus one reason-string fix in the pipeline. `AiPrompts.BuildSystemPrompt` gains an `allowBridge` flag that adds one option to the prompt; `AiClassifier`'s `AllowedSkills` moves from `static readonly` to instance state derived from the same flag; `AddFlowHubAi` reads `Ai:EnableBridgeClassification` and passes it through. No new parking mechanism is needed — `CaptureEnrichmentConsumer` already parks a Bridge result whose `BridgeAction` is `Unknown`, before publishing `CaptureClassified`.

**Tech Stack:** .NET 10 / C#, Microsoft.Extensions.AI, MassTransit, xUnit + FluentAssertions + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-08-28-bridge-classification-option-design.md`

## Global Constraints

- The flag is `Ai:EnableBridgeClassification`, boolean, **default `false`**.
- With the flag off, the emitted system prompt must be **byte-identical** to today's, and classification behaviour must be unchanged.
- The new `AiClassifier` constructor parameter is **optional and trailing** (`bool allowBridgeClassification = false`). Existing tests construct `AiClassifier` positionally; a required parameter breaks every one of them.
- `AiPrompts` is `internal`, with `InternalsVisibleTo("FlowHub.Web.ComponentTests")` — prompt tests live in that assembly.
- No `#nullable disable`, no warning suppressions. `Directory.Build.props` treats warnings as errors.
- Do not change the alias short-circuit, `ClassifyBridgeAsync`, `BridgeAction`, `BridgeSkillIntegration`, `SkillRoutingConsumer`, or any persisted shape. No migration.
- Conventional Commits; scope `ai` for the classifier work, `pipeline` for the consumer.

---

## File Structure

- `source/FlowHub.AI/AiPrompts.cs` — add the `allowBridge` parameter to `BuildSystemPrompt` and `BuildMessages`.
- `source/FlowHub.AI/AiClassifier.cs` — `AllowedSkills` becomes an instance field; new optional ctor parameter; pass the flag to `BuildMessages`.
- `source/FlowHub.AI/AiServiceCollectionExtensions.cs` — read the config key, pass it to the `AiClassifier` factory.
- `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs` — distinguish the two park reasons.
- `tests/FlowHub.Web.ComponentTests/Ai/AiPromptsTests.cs` — prompt tests.
- `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierBridgeTests.cs` — classifier tests.
- `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerBridgeTests.cs` — consumer reason tests.

---

### Task 1: Prompt gains an optional Bridge option

**Files:**
- Modify: `source/FlowHub.AI/AiPrompts.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiPromptsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `AiPrompts.BuildSystemPrompt(IReadOnlyCollection<string> vikunjaBuckets, bool allowBridge = false)` and `AiPrompts.BuildMessages(string content, IReadOnlyCollection<string> vikunjaBuckets, bool allowBridge = false)`. Task 2 calls `BuildMessages` with the flag.

- [ ] **Step 1: Write the failing tests**

Add to `tests/FlowHub.Web.ComponentTests/Ai/AiPromptsTests.cs`:

```csharp
    [Fact]
    public void BuildSystemPrompt_BridgeDisabled_DoesNotOfferBridge()
    {
        var prompt = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: false);

        prompt.Should().NotContain("Bridge");
    }

    [Fact]
    public void BuildSystemPrompt_BridgeDisabled_IsIdenticalToDefault()
    {
        // Prompt drift silently changes classification for every capture, not just
        // dev ones. The default and the explicitly-disabled prompt must not diverge.
        AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: false)
            .Should().Be(AiPrompts.BuildSystemPrompt(DefaultBuckets));
    }

    [Fact]
    public void BuildSystemPrompt_BridgeEnabled_OffersBridgeAndKeepsBuckets()
    {
        var prompt = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true);

        prompt.Should().Contain("\"Bridge\"");
        prompt.Should().Contain("Wallabag");
        prompt.Should().Contain("Vikunja");
        prompt.Should().Contain("Inbox, Zitate");
    }

    [Fact]
    public void BuildSystemPrompt_BridgeEnabled_DoesNotAskForARepositoryName()
    {
        // Repo inference is issue #38. The model has no catalogue here and would
        // hallucinate a name, so the prompt must not invite one.
        var prompt = AiPrompts.BuildSystemPrompt(DefaultBuckets, allowBridge: true);

        prompt.Should().NotContain("repository name");
        prompt.Should().NotContain("alias");
    }

    [Fact]
    public void BuildMessages_BridgeEnabled_SystemMessageOffersBridge()
    {
        var messages = AiPrompts.BuildMessages("fix the login bug", DefaultBuckets, allowBridge: true);

        messages[0].Text.Should().Contain("\"Bridge\"");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiPromptsTests"`
Expected: FAIL — compile error, `BuildSystemPrompt` takes 1 argument.

- [ ] **Step 3: Add the parameter and the conditional option**

In `source/FlowHub.AI/AiPrompts.cs`, replace `BuildSystemPrompt` and `BuildMessages` with:

```csharp
    internal static string BuildSystemPrompt(
        IReadOnlyCollection<string> vikunjaBuckets, bool allowBridge = false)
    {
        var bucketLine = vikunjaBuckets.Count == 0
            ? "Inbox"
            : string.Join(", ", vikunjaBuckets);

        // Inserted as a whole line so the disabled prompt stays byte-identical to the
        // pre-flag one — no trailing whitespace, no blank line, when allowBridge is false.
        var bridgeOption = allowBridge
            ? """

                    "Bridge"    – the snippet is an actionable task, bug report, or feature
                                  request about one of the operator's own software projects
            """.TrimEnd('\n')
            : "";

        return string.Create(CultureInfo.InvariantCulture, $$"""
            You classify user-captured snippets for a personal knowledge tool called FlowHub.

            For each capture, return:
            - tags: 1–5 short lowercase tags describing the snippet
            - matched_skill: which downstream skill should handle it. Choose exactly ONE:
                "Wallabag"  – the snippet is a URL or article worth saving for later reading
                "Vikunja"   – the snippet is a task, todo, OR a structured piece of content
                              that belongs in a Vikunja project (quote, movie, book, …){{bridgeOption}}
                ""          – none of the above; it will be marked as Orphan
            - project: when matched_skill="Vikunja", pick the best matching project from
              this list. If unsure, pick "Inbox".
                Available: {{bucketLine}}
              Leave empty otherwise.
            - title: a 3–8 word title summarising the snippet (omit only if the snippet
                     is itself shorter than 8 words)
            - entities: optional structured fields the project may use, e.g.
                Zitate → {"quote": "...", "author": "..."}
                Movies → {"title": "...", "year": "..."}
              Omit if nothing applies.

            Reply ONLY via the structured response schema. Never include explanations.
            """);
    }

    internal static IList<ChatMessage> BuildMessages(
        string content, IReadOnlyCollection<string> vikunjaBuckets, bool allowBridge = false) =>
    [
        new ChatMessage(ChatRole.System, BuildSystemPrompt(vikunjaBuckets, allowBridge)),
        new ChatMessage(ChatRole.User, content),
    ];
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiPromptsTests"`
Expected: PASS, including the three pre-existing tests in that class.

If `BuildSystemPrompt_BridgeDisabled_IsIdenticalToDefault` fails, the interpolation introduced whitespace when `allowBridge` is false — `bridgeOption` must be exactly `""`, not `"\n"`.

- [ ] **Step 5: Commit**

```bash
git add source/FlowHub.AI/AiPrompts.cs tests/FlowHub.Web.ComponentTests/Ai/AiPromptsTests.cs
git commit -m "feat(ai): offer Bridge in the system prompt behind a flag

The disabled prompt stays byte-identical to the pre-flag one, asserted
by a test - prompt drift would silently change classification for every
capture, not just dev ones.

Refs #37"
```

---

### Task 2: Classifier accepts Bridge when the flag is on

**Files:**
- Modify: `source/FlowHub.AI/AiClassifier.cs:19` (the `AllowedSkills` field), the constructor, and the `BuildMessages` call
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierBridgeTests.cs`

**Interfaces:**
- Consumes: `AiPrompts.BuildMessages(content, buckets, allowBridge)` from Task 1.
- Produces: `AiClassifier(IChatClient, IClassifier, ILogger<AiClassifier>, ChatOptions, IVikunjaProjectCatalog, AiModelInfo, IBridgeCatalog, bool allowBridgeClassification = false)`. Task 3 supplies the last argument from configuration.

- [ ] **Step 1: Write the failing tests**

Add to `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierBridgeTests.cs`. Note the existing `Sut()` helper and the `_bridge` stub returning alias `"br"` — these tests use content with **no** alias so the short-circuit does not fire:

```csharp
    private AiClassifier SutWithBridgeClassification() =>
        new(_chat, _keyword, NullLogger<AiClassifier>.Instance, _opts, _catalog,
            new AiModelInfo("OpenRouter", "test-model"), _bridge, allowBridgeClassification: true);

    [Fact]
    public async Task ClassifyAsync_BridgeEnabledAndModelReturnsBridge_ReturnsBridgeWithNoAlias()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(JsonResponse(new
            {
                tags = new[] { "dev" },
                matched_skill = "Bridge",
                title = "Align milestones across repos",
                project = (string?)null,
                entities = (object?)null,
            }));

        var result = await SutWithBridgeClassification()
            .ClassifyAsync("Auto dispatcher issues cross repo and milestone aligned", default);

        result.MatchedSkill.Should().Be("Bridge");
        result.BridgeAlias.Should().BeNull();
        result.BridgeAction.Should().Be(BridgeAction.Unknown);
    }

    [Fact]
    public async Task ClassifyAsync_BridgeDisabledAndModelReturnsBridge_FallsBackToKeyword()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(JsonResponse(new
            {
                tags = new[] { "dev" },
                matched_skill = "Bridge",
                title = "Align milestones across repos",
                project = (string?)null,
                entities = (object?)null,
            }));
        _keyword.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult([], ""));

        var result = await Sut()
            .ClassifyAsync("Auto dispatcher issues cross repo and milestone aligned", default);

        result.MatchedSkill.Should().Be("");
        await _keyword.Received(1).ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassifyAsync_BridgeEnabled_VikunjaResultIsUnaffected()
    {
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(JsonResponse(new
            {
                tags = new[] { "todo" },
                matched_skill = "Vikunja",
                title = "Buy milk on Saturday",
                project = "Inbox",
                entities = (object?)null,
            }));

        var result = await SutWithBridgeClassification().ClassifyAsync("todo: buy milk", default);

        result.MatchedSkill.Should().Be("Vikunja");
        result.VikunjaProject.Should().Be("Inbox");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiClassifierBridgeTests"`
Expected: FAIL — compile error, `AiClassifier` has no `allowBridgeClassification` parameter.

- [ ] **Step 3: Make the allow-list instance state**

In `source/FlowHub.AI/AiClassifier.cs`, delete the `static readonly` field at line 19 and add an instance field. Replace:

```csharp
    private static readonly string[] AllowedSkills = ["Wallabag", "Vikunja", ""];
```

with:

```csharp
    private static readonly string[] SkillsWithoutBridge = ["Wallabag", "Vikunja", ""];
    private static readonly string[] SkillsWithBridge = ["Wallabag", "Vikunja", "Bridge", ""];

    private readonly string[] _allowedSkills;
    private readonly bool _allowBridgeClassification;
```

Add the optional trailing constructor parameter and set both fields. The parameter must be **last and optional** so existing positional constructions keep compiling:

```csharp
    public AiClassifier(
        IChatClient chat,
        IClassifier keyword,
        ILogger<AiClassifier> log,
        ChatOptions options,
        IVikunjaProjectCatalog catalog,
        AiModelInfo modelInfo,
        IBridgeCatalog bridgeCatalog,
        bool allowBridgeClassification = false)
    {
        _chat = chat;
        _keyword = keyword;
        _log = log;
        _options = options;
        _catalog = catalog;
        _modelInfo = modelInfo;
        _bridgeCatalog = bridgeCatalog;
        _allowBridgeClassification = allowBridgeClassification;
        _allowedSkills = allowBridgeClassification ? SkillsWithBridge : SkillsWithoutBridge;
    }
```

In `ClassifyAsync`, pass the flag to the prompt builder:

```csharp
            var response = await _chat.GetResponseAsync<AiClassificationResponse>(
                AiPrompts.BuildMessages(content, buckets, _allowBridgeClassification),
                _options,
                cancellationToken: cancellationToken);
```

and use the instance allow-list in the re-validation:

```csharp
            if (Array.IndexOf(_allowedSkills, payload.MatchedSkill) < 0)
            {
                throw new InvalidOperationException("schema_violation");
            }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~AiClassifier"`
Expected: PASS — the new tests plus every pre-existing `AiClassifierTests`, `AiClassifierBridgeTests` and `AiClassifierTraceTests` case.

- [ ] **Step 5: Commit**

```bash
git add source/FlowHub.AI/AiClassifier.cs tests/FlowHub.Web.ComponentTests/Ai/AiClassifierBridgeTests.cs
git commit -m "feat(ai): accept Bridge as a matched skill when enabled

AllowedSkills moves from static to instance state chosen by the flag.
The constructor parameter is optional and trailing so existing
positional constructions in the test suite keep compiling.

Refs #37"
```

---

### Task 3: Wire the configuration key

**Files:**
- Modify: `source/FlowHub.AI/AiServiceCollectionExtensions.cs:116-123` (the `AiClassifier` factory)
- Test: `tests/FlowHub.Web.ComponentTests/Ai/AiPromptsTests.cs` is unaffected; no new test file

**Interfaces:**
- Consumes: the `AiClassifier` constructor from Task 2.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Read the key next to the other AI settings**

In `source/FlowHub.AI/AiServiceCollectionExtensions.cs`, immediately after the existing `maxTokens` line (~line 103):

```csharp
        var allowBridgeClassification =
            bool.TryParse(configuration["Ai:EnableBridgeClassification"], out var allowBridge) && allowBridge;
```

`bool.TryParse` returning false for an unset or malformed value gives the required default of `false`.

- [ ] **Step 2: Pass it to the classifier factory**

Replace the `AddSingleton` for `AiClassifier` with:

```csharp
        services.AddSingleton(sp => new AiClassifier(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<KeywordClassifier>(),
            sp.GetRequiredService<ILogger<AiClassifier>>(),
            new ChatOptions { MaxOutputTokens = maxTokens, Temperature = 0.2f },
            sp.GetRequiredService<IVikunjaProjectCatalog>(),
            sp.GetRequiredService<AiModelInfo>(),
            sp.GetRequiredService<IBridgeCatalog>(),
            allowBridgeClassification));
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build source/FlowHub.AI`
Expected: succeeds with no warnings (warnings are errors in this repo).

- [ ] **Step 4: Commit**

```bash
git add source/FlowHub.AI/AiServiceCollectionExtensions.cs
git commit -m "feat(ai): read Ai:EnableBridgeClassification, default off

Refs #37"
```

---

### Task 4: Distinguish the two park reasons

**Files:**
- Modify: `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs:55-63`
- Test: `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerBridgeTests.cs`

**Interfaces:**
- Consumes: a `ClassificationResult` with `MatchedSkill == "Bridge"` and `BridgeAlias == null`, produced by Task 2.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

Add to `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerBridgeTests.cs`. This file
uses a **real** capture service behind a MassTransit test harness (`PipelineTestBase.Build`), not an
NSubstitute mock — so assert on the persisted `FailureReason`, mirroring the existing
`Consume_BridgeUnknown_MarksUnhandledAndDoesNotPublish` test:

```csharp
    [Fact]
    public async Task Consume_BridgeWithoutAlias_ParksWithRepoUndeterminedReason()
    {
        var classifier = ClassifierReturning(new ClassificationResult(
            ["dev"], "Bridge", Title: "Align milestones"));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        const string content = "Auto dispatcher issues cross repo and milestone aligned";
        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync(content, ChannelKind.Web, default);

        await harness.Bus.Publish(new CaptureCreated(capture.Id, content, ChannelKind.Web, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        var stored = (await captureService.GetByIdAsync(capture.Id, default))!;
        stored.Stage.Should().Be(LifecycleStage.Unhandled);
        stored.FailureReason.Should().Be("bridge candidate — repo undetermined");
        (await harness.Published.Any<CaptureClassified>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Consume_BridgeWithAliasButUnknownAction_KeepsActionUndeterminedReason()
    {
        var classifier = ClassifierReturning(new ClassificationResult(
            ["bridge"], "Bridge", BridgeAlias: "br", BridgeAction: BridgeAction.Unknown));

        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var captureService = provider.GetRequiredService<ICaptureService>();
        var capture = await captureService.SubmitAsync("br hmm", ChannelKind.Web, default);

        await harness.Bus.Publish(new CaptureCreated(capture.Id, "br hmm", ChannelKind.Web, DateTimeOffset.UtcNow));

        (await harness.Consumed.Any<CaptureCreated>(x => x.Context.Message.CaptureId == capture.Id))
            .Should().BeTrue();

        var stored = (await captureService.GetByIdAsync(capture.Id, default))!;
        stored.FailureReason.Should().Be("bridge action undetermined — needs triage");
    }
```

**The em dash in both reason strings is U+2014**, matching the existing literal in
`CaptureEnrichmentConsumer`. Copy it exactly — a hyphen makes the assertion fail confusingly.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~CaptureEnrichmentConsumerBridgeTests"`
Expected: FAIL — the first test gets `"bridge action undetermined — needs triage"`.

- [ ] **Step 3: Split the reason on the presence of an alias**

In `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs`, replace the guard at lines 55-63 with:

```csharp
        // Bridge with no determinable target → park for triage before any publish or
        // network call (spec decision #6). Two distinct causes, two distinct reasons:
        // no alias means the LLM proposed Bridge but no repo is known (issue #38);
        // an alias with an Unknown action means issue-vs-idea could not be decided.
        if (string.Equals(result.MatchedSkill, "Bridge", StringComparison.Ordinal)
            && result.BridgeAction == BridgeAction.Unknown)
        {
            var reason = result.BridgeAlias is null
                ? "bridge candidate — repo undetermined"
                : "bridge action undetermined — needs triage";

            await _captureService.MarkUnhandledAsync(msg.CaptureId, reason, ct);
            LogBridgeUndetermined(msg.CaptureId, result.BridgeAlias ?? string.Empty);
            return;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~CaptureEnrichmentConsumer"`
Expected: PASS, including the pre-existing bridge and non-bridge consumer tests.

- [ ] **Step 5: Commit**

```bash
git add source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerBridgeTests.cs
git commit -m "fix(pipeline): distinguish bridge repo-undetermined from action-undetermined

A Bridge result with no alias is the LLM proposing a forge route with no
known repo, not an undecided issue-vs-idea. Triage sees the real cause.

Refs #37"
```

---

### Task 5: Full suite and documentation

**Files:**
- Modify: `CHANGELOG.md`
- Test: the whole suite

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: nothing.

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test tests/FlowHub.Web.ComponentTests`
Expected: all green.

Then the other affected projects:

Run: `dotnet test tests/FlowHub.Core.Tests && dotnet test tests/FlowHub.Skills.Tests`
Expected: all green.

Per repo convention, run per-project rather than a solution-wide `just test`.

- [ ] **Step 2: Add the CHANGELOG entry**

Under `## [Unreleased]` → `### Added` in `CHANGELOG.md`:

```markdown
- Classifier can route a capture to Bridge when `Ai:EnableBridgeClassification` is enabled (default off). A Bridge result with no repo parks as Unhandled for triage. (#37)
```

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): note the Bridge classification option

Refs #37"
```

---

## Verification

The change is behaviourally invisible until the flag is set. To confirm the enabled path end to end without touching production:

```bash
Ai__EnableBridgeClassification=true dotnet run --project source/FlowHub.Web
```

Submit a capture such as `Auto dispatcher issues cross repo and milestone aligned` and confirm it reaches `Unhandled` with reason `bridge candidate — repo undetermined`, rather than `Vikunja/Inbox`.

**Expected consequence, not a defect:** with the flag on, dev-flavoured captures that used to land in Vikunja/Inbox now park as Unhandled. That is the regression the flag exists to contain, and it resolves when #38 supplies a repo.
