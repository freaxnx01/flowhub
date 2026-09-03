# Bridge Inferred Target Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Send an inferred repo to bridge as `owner`/`repo` (issue) or `target` (idea), instead of stuffing the repo name into the `alias` field where bridge cannot resolve it.

**Architecture:** Add a `BridgeTarget` field that travels beside the existing `BridgeAlias` through `ClassificationResult` → `CaptureClassified` → `Capture` → `BridgeSkillIntegration`. Exactly one of the two is set: `BridgeAlias` by the pre-LLM alias short-circuit, `BridgeTarget` by `RepoResolver`. The integration picks the payload shape from whichever it has. `BridgeRepo` gains `Owner` so the resolver can produce an owner-qualified target.

**Tech Stack:** .NET 10 / C#, Microsoft.Extensions.AI, MassTransit, xUnit + FluentAssertions + NSubstitute + RichardSzalay.MockHttp.

**Spec:** `docs/superpowers/specs/2026-09-03-bridge-inferred-target-design.md`

## Global Constraints

- **No migration.** The `Bridge*` fields on `Capture` are transient event-only — `SkillRoutingConsumer.cs:55-61` copies them from the event into an in-memory `capture with { … }` and they are never persisted. Adding `BridgeTarget` follows that pattern exactly.
- **The alias path must not change.** `AiClassifier.cs:53-56` (`BridgeAliasMatcher`) and every existing `alias` payload keep their current behaviour, asserted by the pre-existing tests in `BridgeSkillIntegrationTests`.
- **`BridgeTarget` is always owner-qualified** — `owner/repo`. A value without `/` is a programming error and throws; it is never posted half-formed.
- The two endpoints differ: **issue** takes `owner` + `repo` separately, **idea** takes a single `target` string. See `bridge/internal/api/capture.go:46-47,73-75`.
- New optional record parameters go **last** so existing positional construction keeps compiling — the same constraint that applied in #37 and #38.
- No `#nullable disable`, no warning suppressions; `IDE0005` (unused using) is an error in this repo.
- Conventional Commits; scopes `skills`, `ai`, `core`.

---

## File Structure

- `source/FlowHub.Core/Skills/IBridgeCatalog.cs` — `BridgeRepo` gains `Owner`.
- `source/FlowHub.Skills/Bridge/BridgeCatalog.cs` — map `owner` from the DTO.
- `source/FlowHub.Core/Classification/ClassificationResult.cs` — gains `BridgeTarget`.
- `source/FlowHub.Core/Captures/Capture.cs` — gains `BridgeTarget`.
- `source/FlowHub.Core/Events/CaptureClassified.cs` — gains `BridgeTarget`.
- `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs` — publish it.
- `source/FlowHub.Web/Pipeline/SkillRoutingConsumer.cs` — carry it onto the capture.
- `source/FlowHub.AI/RepoResolver.cs` — owner-qualify the resolution; return it as the target.
- `source/FlowHub.AI/AiClassifier.cs` — put the resolution in `BridgeTarget`, not `BridgeAlias`.
- `source/FlowHub.Skills/Bridge/BridgeSkillIntegration.cs` — choose the payload shape.
- Tests: `BridgeCatalogReposTests`, `RepoResolverTests`, `AiClassifierRepoInferenceTests`, `BridgeSkillIntegrationTests`.

---

### Task 1: Catalogue carries the owner

**Files:**
- Modify: `source/FlowHub.Core/Skills/IBridgeCatalog.cs:22-27`
- Modify: `source/FlowHub.Skills/Bridge/BridgeCatalog.cs` (the `BridgeRepoDto` record and `BuildRepoList`)
- Test: `tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogReposTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `BridgeRepo(string Name, string? Owner, string? Alias, string? Desc, IReadOnlyList<string> Topics, DateTimeOffset? LastUsed)`. Task 3 reads `Owner`.

- [ ] **Step 1: Write the failing test**

Add to `tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogReposTests.cs`. The existing `Payload` constant needs an `owner` field — update it and add:

```csharp
    [Fact]
    public async Task GetReposAsync_MapsOwner()
    {
        // /api/repos has always returned owner; the DTO dropped it, exactly as it dropped
        // desc before bridge#252. Repo inference needs it to build an owner-qualified target.
        var repos = await Sut(new StubHandler(Payload), TimeProvider.System).GetReposAsync(default);

        repos.Single(r => r.Name == "flowhub").Owner.Should().Be("freaxnx01");
    }

    [Fact]
    public async Task GetReposAsync_MissingOwner_IsNull()
    {
        var repos = await Sut(new StubHandler(Payload), TimeProvider.System).GetReposAsync(default);

        repos.Single(r => r.Name == "bare-repo").Owner.Should().BeNull();
    }
```

and add `"owner":"freaxnx01"` to the `flowhub` entry in `Payload`, leaving `bare-repo` without one.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter "FullyQualifiedName~BridgeCatalogReposTests"`
Expected: FAIL — `BridgeRepo` has no `Owner`.

- [ ] **Step 3: Add Owner to the record**

In `source/FlowHub.Core/Skills/IBridgeCatalog.cs`:

```csharp
public sealed record BridgeRepo(
    string Name,
    string? Owner,
    string? Alias,
    string? Desc,
    IReadOnlyList<string> Topics,
    DateTimeOffset? LastUsed);
```

`Owner` goes second, next to `Name`, because the two are read together to form a target. This is a breaking positional change to a record only constructed inside `BridgeCatalog` and the tests, so update those call sites rather than adding a trailing optional.

- [ ] **Step 4: Map it in the catalogue**

In `source/FlowHub.Skills/Bridge/BridgeCatalog.cs`, add `Owner` to the DTO:

```csharp
    private sealed record BridgeRepoDto(
        string? Name,
        string? Owner,
        string? Alias,
        string? Desc,
        string[]? Topics,
        DateTimeOffset? Last_Used);
```

and to the projection in `BuildRepoList`, immediately after the name:

```csharp
                string.IsNullOrWhiteSpace(r.Owner) ? null : r.Owner.Trim(),
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter "FullyQualifiedName~BridgeCatalog"`
Expected: PASS, including the pre-existing alias and cache-sharing tests.

- [ ] **Step 6: Commit**

```bash
git add source/FlowHub.Core/Skills/IBridgeCatalog.cs source/FlowHub.Skills/Bridge/BridgeCatalog.cs tests/FlowHub.Skills.Tests/Bridge/BridgeCatalogReposTests.cs
git commit -m "feat(skills): carry the repo owner through the bridge catalogue

/api/repos has always returned owner and the DTO dropped it, the same
omission that hid desc before bridge#252. Repo inference needs it to
build an owner-qualified target.

Refs #66"
```

---

### Task 2: BridgeTarget travels through the pipeline

**Files:**
- Modify: `source/FlowHub.Core/Classification/ClassificationResult.cs`
- Modify: `source/FlowHub.Core/Captures/Capture.cs:22-24`
- Modify: `source/FlowHub.Core/Events/CaptureClassified.cs`
- Modify: `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs`
- Modify: `source/FlowHub.Web/Pipeline/SkillRoutingConsumer.cs:55-61`
- Test: `tests/FlowHub.Web.ComponentTests/Pipeline/SkillRoutingConsumerBridgeTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `BridgeTarget` as a trailing optional `string?` on `ClassificationResult`, `Capture` and `CaptureClassified`, carried end to end. Tasks 3 and 4 read and write it.

- [ ] **Step 1: Write the failing test**

Add to `tests/FlowHub.Web.ComponentTests/Pipeline/SkillRoutingConsumerBridgeTests.cs`, following that file's existing harness style:

```csharp
    [Fact]
    public async Task Consume_BridgeTargetOnTheEvent_ReachesTheIntegration()
    {
        // BridgeTarget is transient event-only, like BridgeAlias: the routing consumer is
        // the only thing that puts it on the Capture handed to the skill. If it is not
        // copied here the integration sees null and cannot build the payload.
        Capture? seen = null;
        var integration = Substitute.For<ISkillIntegration>();
        integration.Name.Returns("Bridge");
        integration.HandleAsync(Arg.Do<Capture>(c => seen = c), Arg.Any<CancellationToken>())
            .Returns(SkillResult.Ok("https://example.test/issues/1"));

        await WhenRouting(integration, bridgeTarget: "freaxnx01/bridge");

        seen!.BridgeTarget.Should().Be("freaxnx01/bridge");
    }
```

If the file has no `WhenRouting` helper, inline the harness pattern its neighbouring tests already use, publishing a `CaptureClassified` carrying `BridgeTarget`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~SkillRoutingConsumerBridgeTests"`
Expected: FAIL — compile error, no `BridgeTarget`.

- [ ] **Step 3: Add the field to the three records**

`source/FlowHub.Core/Classification/ClassificationResult.cs` — append after `BridgeBody`:

```csharp
    string? BridgeBody = null,
    string? BridgeTarget = null);
```

`source/FlowHub.Core/Captures/Capture.cs` — same, after `BridgeBody`:

```csharp
    string? BridgeBody = null,
    string? BridgeTarget = null);
```

`source/FlowHub.Core/Events/CaptureClassified.cs` — same, after `BridgeBody`:

```csharp
    string? BridgeBody = null,
    string? BridgeTarget = null);
```

Trailing and optional in all three, so every existing positional construction keeps compiling.

- [ ] **Step 4: Publish and carry it**

In `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs`, wherever `CaptureClassified` is constructed with the Bridge fields, add `BridgeTarget: result.BridgeTarget`.

In `source/FlowHub.Web/Pipeline/SkillRoutingConsumer.cs:55-61`, add it to the `capture with` block:

```csharp
        capture = capture with
        {
            EnrichmentDescription = msg.EnrichmentDescription,
            BridgeAlias = msg.BridgeAlias,
            BridgeAction = msg.BridgeAction,
            BridgeBody = msg.BridgeBody,
            BridgeTarget = msg.BridgeTarget,
        };
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~SkillRouting"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add source/FlowHub.Core source/FlowHub.Web/Pipeline tests/FlowHub.Web.ComponentTests/Pipeline/SkillRoutingConsumerBridgeTests.cs
git commit -m "feat(core): carry BridgeTarget alongside BridgeAlias

Transient event-only, like the other Bridge fields - no migration. The
alias is what the operator typed; the target is what inference resolved,
and they need different payload shapes downstream.

Refs #66"
```

---

### Task 3: The resolver returns an owner-qualified target

**Files:**
- Modify: `source/FlowHub.AI/RepoResolver.cs:10,20,85` and the success return
- Modify: `source/FlowHub.AI/AiClassifier.cs` (the `TryResolveBridgeAsync` result construction)
- Test: `tests/FlowHub.Web.ComponentTests/Ai/RepoResolverTests.cs`, `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierRepoInferenceTests.cs`

**Interfaces:**
- Consumes: `BridgeRepo.Owner` (Task 1), `ClassificationResult.BridgeTarget` (Task 2).
- Produces: `RepoResolution.Repo` is owner-qualified (`owner/repo`); `AiClassifier` puts it in `BridgeTarget` and leaves `BridgeAlias` null.

- [ ] **Step 1: Write the failing tests**

In `tests/FlowHub.Web.ComponentTests/Ai/RepoResolverTests.cs`, update the substituted catalogue to carry owners and add:

```csharp
    [Fact]
    public async Task ResolveAsync_ModelPicksAListedRepo_ReturnsAnOwnerQualifiedTarget()
    {
        ChatReturns(new { repo = "game-nibbles", action = "issue", title = "Snake too fast", body = "It speeds up." });

        var result = await Sut().ResolveAsync("the snake game is too fast", default);

        result!.Repo.Should().Be("freaxnx01/game-nibbles");
    }

    [Fact]
    public async Task ResolveAsync_ModelAbstains_TargetsIdeasLabOwnerQualified()
    {
        ChatReturns(new { repo = (string?)null, action = "idea", title = "Minigolf game", body = "Browser minigolf." });

        var result = await Sut().ResolveAsync("Game browser Minigolf", default);

        result!.Repo.Should().Be("freaxnx01/ideas-lab");
        result.Action.Should().Be(BridgeAction.Idea);
    }

    [Fact]
    public async Task ResolveAsync_CatalogueEntryHasNoOwner_ReturnsNull()
    {
        // Without an owner the target cannot be qualified, and posting a bare name is what
        // this issue exists to stop. Park instead.
        CatalogueReturns(new BridgeRepo("orphan-repo", null, null, "no owner", [], null));
        ChatReturns(new { repo = "orphan-repo", action = "issue", title = "t", body = "b" });

        var result = await Sut().ResolveAsync("something about orphan-repo", default);

        result.Should().BeNull();
    }
```

In `tests/FlowHub.Web.ComponentTests/Ai/AiClassifierRepoInferenceTests.cs`, change the existing assertion from `BridgeAlias` to `BridgeTarget` and add:

```csharp
    [Fact]
    public async Task ClassifyAsync_InferredRepo_SetsTargetAndLeavesAliasNull()
    {
        // The alias field means "shorthand the operator typed". An inferred repo is not
        // that, and bridge resolves the two differently.
        _chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                JsonResponse(new { tags = new[] { "dev" }, matched_skill = "Bridge", title = "t", project = (string?)null, entities = (object?)null }),
                JsonResponse(new { repo = "game-nibbles", action = "issue", title = "Snake too fast", body = "It speeds up." }));

        var result = await SutWithResolver().ClassifyAsync("the snake game is too fast", default);

        result.BridgeTarget.Should().Be("freaxnx01/game-nibbles");
        result.BridgeAlias.Should().BeNull();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~RepoResolverTests|FullyQualifiedName~AiClassifierRepoInference"`
Expected: FAIL — bare names returned, and `BridgeTarget` unset.

- [ ] **Step 3: Owner-qualify in the resolver**

In `source/FlowHub.AI/RepoResolver.cs`, change the fallback constant:

```csharp
    internal const string IdeaFallbackRepo = "freaxnx01/ideas-lab";
```

On the abstain path (line ~85) that constant is already returned unchanged. On the success path, qualify from the catalogue entry rather than returning `payload.Repo` directly:

```csharp
            // The catalogue is authoritative: only a name we offered is acceptable.
            var chosen = shortlist.FirstOrDefault(r =>
                string.Equals(r.Name, payload.Repo, StringComparison.Ordinal));
            if (chosen is null)
            {
                LogUnlistedRepo(payload.Repo);
                return null;
            }

            // Bridge resolves an unqualified name against .bridge-alias files, which no repo
            // has — an owner-qualified target is what its capture endpoints actually take.
            if (string.IsNullOrWhiteSpace(chosen.Owner))
            {
                LogUnqualifiableRepo(chosen.Name);
                return null;
            }

            return new RepoResolution($"{chosen.Owner}/{chosen.Name}", action, payload.Title, payload.Body);
```

Add the logger message beside the existing ones:

```csharp
    [LoggerMessage(EventId = 3023, Level = LogLevel.Warning,
        Message = "Repo {RepoName} has no owner in the catalogue; cannot build a target — parking for triage")]
    private partial void LogUnqualifiableRepo(string repoName);
```

- [ ] **Step 4: Set the target, not the alias**

In `source/FlowHub.AI/AiClassifier.cs`, in the block that builds the result from a `RepoResolution`, move the value from `BridgeAlias` to `BridgeTarget`:

```csharp
                    return new ClassificationResult(
                        payload.Tags,
                        "Bridge",
                        Title: resolution.Title ?? payload.Title,
                        Trace: BuildTrace(sw, response),
                        BridgeAction: resolution.Action,
                        BridgeBody: resolution.Body,
                        BridgeTarget: resolution.Repo);
```

`BridgeAlias` is now left at its default null on this path — the alias short-circuit remains the only thing that sets it.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~Ai"`
Expected: PASS, including all #37 and #38 tests.

- [ ] **Step 6: Commit**

```bash
git add source/FlowHub.AI tests/FlowHub.Web.ComponentTests/Ai
git commit -m "feat(ai): resolve to an owner-qualified target, not an alias

Bridge resolves an unqualified name against .bridge-alias files that no
repo has. A catalogue entry with no owner now parks rather than posting
a name bridge cannot resolve.

Refs #66"
```

---

### Task 4: The integration picks the payload shape

**Files:**
- Modify: `source/FlowHub.Skills/Bridge/BridgeSkillIntegration.cs:40-68`
- Test: `tests/FlowHub.Skills.Tests/Bridge/BridgeSkillIntegrationTests.cs`

**Interfaces:**
- Consumes: `Capture.BridgeTarget` (Task 2), owner-qualified (Task 3).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing contract tests**

These are the tests whose absence let the bug ship — every existing test asserted on `ClassificationResult`, never on the JSON posted. Add to `tests/FlowHub.Skills.Tests/Bridge/BridgeSkillIntegrationTests.cs`, using the file's existing `MockHttpMessageHandler` style:

```csharp
    private static Capture TargetCapture(BridgeAction action, string target = "freaxnx01/bridge",
        string? title = "Login 500", string? body = "the login 500s") =>
        new(Guid.NewGuid(), ChannelKind.Web, body ?? "", DateTimeOffset.UtcNow,
            LifecycleStage.Routed, "Bridge", Title: title, BridgeAction: action,
            BridgeBody: body, BridgeTarget: target);

    [Fact]
    public async Task HandleAsync_IssueWithTarget_PostsOwnerAndRepoNotAlias()
    {
        var (sut, mock) = Build();
        mock.Expect(HttpMethod.Post, $"{BaseUrl}/api/capture/issue")
            .WithJson(new { owner = "freaxnx01", repo = "bridge", title = "Login 500", body = "the login 500s" })
            .Respond("application/json", """{"url":"https://example.test/issues/1"}""");

        var result = await sut.HandleAsync(TargetCapture(BridgeAction.Issue), default);

        result.Success.Should().BeTrue();
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task HandleAsync_IdeaWithTarget_PostsTargetNotAlias()
    {
        var (sut, mock) = Build();
        mock.Expect(HttpMethod.Post, $"{BaseUrl}/api/capture/idea")
            .WithJson(new { target = "freaxnx01/bridge", text = "the login 500s" })
            .Respond("application/json", """{"url":"https://example.test/ideas.md"}""");

        var result = await sut.HandleAsync(TargetCapture(BridgeAction.Idea), default);

        result.Success.Should().BeTrue();
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task HandleAsync_NeitherAliasNorTarget_ThrowsAndPostsNothing()
    {
        var (sut, mock) = Build();
        var capture = TargetCapture(BridgeAction.Issue, target: "") with { BridgeAlias = null };

        var act = async () => await sut.HandleAsync(capture, default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*alias or target*");
        mock.GetMatchCount(mock.When("*")).Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_TargetWithoutASlash_Throws()
    {
        // The resolver always owner-qualifies, so a bare name here is a programming error.
        // Posting owner="" would create an issue on the wrong place or 404 confusingly.
        var (sut, _) = Build();

        var act = async () => await sut.HandleAsync(TargetCapture(BridgeAction.Issue, target: "bridge"), default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owner/repo*");
    }
```

If `WithJson` is unavailable in the installed MockHttp version, use `.With(req => …)` and deserialize `req.Content` — the assertion must be on the serialized body either way, not on the capture.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter "FullyQualifiedName~BridgeSkillIntegrationTests"`
Expected: FAIL — `alias` is posted rather than `owner`/`repo`.

- [ ] **Step 3: Choose the shape**

In `source/FlowHub.Skills/Bridge/BridgeSkillIntegration.cs`, replace the guard and the two body builders:

```csharp
        var hasTarget = !string.IsNullOrWhiteSpace(capture.BridgeTarget);
        var hasAlias = !string.IsNullOrWhiteSpace(capture.BridgeAlias);
        if (!hasTarget && !hasAlias)
        {
            throw new InvalidOperationException(
                $"Capture {capture.Id} routed to Bridge without an alias or target.");
        }

        return capture.BridgeAction switch
        {
            BridgeAction.Issue => await SendAsync("/api/capture/issue", IssueBody(capture), cancellationToken),
            BridgeAction.Idea => await SendAsync("/api/capture/idea", IdeaBody(capture), cancellationToken),
            _ => throw new InvalidOperationException(
                $"Capture {capture.Id} routed to Bridge with undetermined action '{capture.BridgeAction}'."),
        };
    }

    /// <summary>
    /// Bridge takes an owner-qualified target as owner+repo on the issue endpoint but as a
    /// single "target" string on the idea endpoint — see bridge/internal/api/capture.go.
    /// </summary>
    private static (string Owner, string Repo) SplitTarget(Capture capture)
    {
        var target = capture.BridgeTarget!;
        var slash = target.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == target.Length - 1)
        {
            throw new InvalidOperationException(
                $"Capture {capture.Id} has a Bridge target '{target}' that is not owner/repo.");
        }

        return (target[..slash], target[(slash + 1)..]);
    }

    private static object IssueBody(Capture capture)
    {
        var title = !string.IsNullOrWhiteSpace(capture.Title)
            ? capture.Title!.Trim()
            : Truncate(capture.BridgeBody ?? capture.Content, FallbackTitleMaxLength);
        var body = capture.BridgeBody ?? string.Empty;

        if (string.IsNullOrWhiteSpace(capture.BridgeTarget))
        {
            return new { alias = capture.BridgeAlias, title, body };
        }

        var (owner, repo) = SplitTarget(capture);
        return new { owner, repo, title, body };
    }

    private static object IdeaBody(Capture capture)
    {
        var text = !string.IsNullOrWhiteSpace(capture.BridgeBody)
            ? capture.BridgeBody!.Trim()
            : capture.Content.Trim();

        if (string.IsNullOrWhiteSpace(capture.BridgeTarget))
        {
            return new { alias = capture.BridgeAlias, text };
        }

        // Validate the shape even though the idea endpoint takes it joined, so a malformed
        // target fails the same way on both paths rather than reaching bridge.
        _ = SplitTarget(capture);
        return new { target = capture.BridgeTarget, text };
    }
```

Update the XML doc at the top of the class to say it posts either an alias or an owner-qualified target.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Skills.Tests --filter "FullyQualifiedName~Bridge"`
Expected: PASS — the four new contract tests plus every pre-existing alias test, unchanged.

- [ ] **Step 5: Commit**

```bash
git add source/FlowHub.Skills/Bridge/BridgeSkillIntegration.cs tests/FlowHub.Skills.Tests/Bridge/BridgeSkillIntegrationTests.cs
git commit -m "fix(skills): post an inferred repo as owner/repo, not as an alias

Bridge takes owner+repo on the issue endpoint and a joined target on the
idea endpoint; an alias is a .bridge-alias lookup that no repo has, so
every inferred route 404'd with 'unknown alias'.

Adds contract tests asserting the serialized JSON for all four
combinations. Their absence is why this shipped: every existing test
substituted the catalogue and asserted on ClassificationResult, never on
the body actually posted.

Refs #66"
```

---

### Task 5: Full suite and documentation

**Files:**
- Modify: `CHANGELOG.md`
- Test: the whole suite

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: nothing.

- [ ] **Step 1: Run the affected suites**

Per repo convention, run per-project rather than a solution-wide `just test`:

```bash
dotnet test tests/FlowHub.Skills.Tests
dotnet test tests/FlowHub.Web.ComponentTests
dotnet test tests/FlowHub.Core.Tests
```

Expected: all green.

- [ ] **Step 2: Add the CHANGELOG entry**

Under `## [Unreleased]` → `### Fixed` in `CHANGELOG.md`:

```markdown
- An inferred repo is sent to bridge as `owner`/`repo` (issue) or `target` (idea) instead of as an `alias`, which bridge resolves against `.bridge-alias` files that no repo has — every inferred route previously failed with `404 unknown alias`. (#66)
```

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): note the inferred-target fix

Refs #66"
```

---

## Verification

The path is inert unless `Ai:EnableBridgeClassification` is on and `Skills:Bridge` is configured — both are true on CT 136. After deploying, submit:

```
bridge mcp from outside LAN?
```

Expected: classifies `Bridge`, resolves `freaxnx01/bridge`, and reaches `Completed` with `externalRef` set to a real issue URL. Before this change it reached `Unhandled` with `404 (Not Found)`.

Also submit `Game browser Minigolf`, which has no matching repo: it should abstain to `freaxnx01/ideas-lab` and append to `ideas.md` rather than parking.
