# Classifier Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `POST /api/v1/captures/preview`, which returns the full routing decision for a capture — matched skill, Vikunja project (name **and** resolved id), Bridge target, tags, title, entities, trace — while writing absolutely nothing.

**Architecture:** A new endpoint in the existing `/api/v1/captures` group calls `IClassifier.ClassifyAsync` directly and maps `ClassificationResult` onto a `CapturePreviewResponse`, adding a read-only `IVikunjaProjectCatalog` lookup so the response reports the project id that *would* be used. No `ICaptureService` call, no bus publish, no persistence.

**Tech Stack:** .NET 10 / C#, ASP.NET Core Minimal API, FluentValidation, xUnit + FluentAssertions + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-09-13-classifier-preview-design.md`

## Global Constraints

- **The preview must persist nothing.** No `Capture`, no event, no DB write. This is the acceptance criterion the whole feature rests on — a 223-capture survey must leave no trace.
- **Do not modify `AiClassifier`, `CaptureEnrichmentConsumer` or `SkillRoutingConsumer`.** The preview reads the existing classifier; it does not thread a flag through the pipeline.
- A preview never fails because a downstream service is unavailable — an unreachable Vikunja catalogue yields `vikunjaProjectId: null`, not a 500.
- All response fields are emitted even when null, so a survey can diff rows without special-casing absence.
- New optional record parameters go **last**, per #37/#38/#66/#83.
- No `#nullable disable`, no warning suppressions; `IDE0005` is an error in this repo.
- Conventional Commits; scope `api`.

---

## File Structure

- `source/FlowHub.Api/Requests/CapturePreviewResponse.cs` — **create.** The response DTO.
- `source/FlowHub.Api/Endpoints/CapturePreviewEndpoint.cs` — **create.** The endpoint.
- `source/FlowHub.Api/Endpoints/CaptureEndpoints.cs` — **modify.** Register the new endpoint on the group (it already calls `MapCaptureReadEndpoints` / `MapCaptureWriteEndpoints` / `MapCaptureRetryEndpoint` at `:17-19`).
- `tests/FlowHub.Web.ComponentTests/Api/CapturePreviewEndpointTests.cs` — **create.**

---

### Task 1: The response DTO and a preview that returns the classifier's decision

**Files:**
- Create: `source/FlowHub.Api/Requests/CapturePreviewResponse.cs`
- Create: `source/FlowHub.Api/Endpoints/CapturePreviewEndpoint.cs`
- Modify: `source/FlowHub.Api/Endpoints/CaptureEndpoints.cs:17-19`
- Test: `tests/FlowHub.Web.ComponentTests/Api/CapturePreviewEndpointTests.cs`

**Interfaces:**
- Consumes: `IClassifier.ClassifyAsync(string, CancellationToken) → Task<ClassificationResult>`; `CreateCaptureRequest(string Content, ChannelKind Source)`; `IValidator<CreateCaptureRequest>`.
- Produces: `CapturePreviewResponse`; `CapturePreviewEndpoint.MapCapturePreviewEndpoint(this RouteGroupBuilder)`.

- [ ] **Step 1: Write the failing test**

Mirror the harness the existing API endpoint tests use — find one with
`ls tests/FlowHub.Web.ComponentTests/Api/` and copy its factory/setup exactly rather than
inventing a new one.

```csharp
using System.Net;
using System.Net.Http.Json;
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;

namespace FlowHub.Web.ComponentTests.Api;

public sealed class CapturePreviewEndpointTests
{
    [Fact]
    public async Task Preview_ReturnsTheClassifiersDecision()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(
                ["game", "idea"], "Bridge", Title: "Add Giana Sisters Clone",
                BridgeTarget: "freaxnx01/game-n-s-clone", BridgeAction: BridgeAction.Issue));

        using var client = CreateClient(s => s.AddSingleton(classifier));   // harness helper

        var response = await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "Game: Platformer Giana Sisters Clone (Browser)", source = "Web" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CapturePreviewResponse>();
        body!.MatchedSkill.Should().Be("Bridge");
        body.Title.Should().Be("Add Giana Sisters Clone");
        body.BridgeTarget.Should().Be("freaxnx01/game-n-s-clone");
        body.Tags.Should().BeEquivalentTo(["game", "idea"]);
    }

    [Fact]
    public async Task Preview_PersistsNothing()
    {
        // The criterion the whole feature rests on: a 223-capture survey must leave no trace.
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(["t"], "Vikunja", VikunjaProject: "Inbox"));

        using var client = CreateClient(s => s.AddSingleton(classifier));

        await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "anything", source = "Web" });

        var listed = await client.GetFromJsonAsync<ListCapturesResponse>("/api/v1/captures");
        listed!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_InvalidBody_Returns400()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "", source = "Web" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

**Note:** `CreateClient` stands for whatever factory the sibling API tests already use
(likely a `WebApplicationFactory` wrapper). Do **not** write a new one — read a sibling
test first and use the same entry point, so the preview is exercised through the real
pipeline registration rather than a bespoke host.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~CapturePreviewEndpointTests"`
Expected: FAIL — build error, `CapturePreviewResponse` does not exist; once it compiles, 404 because the route is unmapped.

- [ ] **Step 3: Write the minimal implementation**

`source/FlowHub.Api/Requests/CapturePreviewResponse.cs`:

```csharp
using FlowHub.Core.Classification;

namespace FlowHub.Api.Requests;

/// <summary>
/// What a capture's classification *would* decide, with nothing written. Every field is
/// emitted even when null so a survey can diff rows without special-casing absence.
/// </summary>
/// <param name="VikunjaProjectId">The project id that would actually be used, resolved
/// against the live catalogue. Null when the classifier named no project, when the name
/// does not match, or when the catalogue is unreachable.</param>
/// <param name="VikunjaProjectResolved">False means the classifier named a project that
/// does not exist and the task would silently land in the fallback project instead.</param>
public sealed record CapturePreviewResponse(
    string MatchedSkill,
    string? Title,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string>? Entities,
    string? VikunjaProject,
    int? VikunjaProjectId,
    bool VikunjaProjectResolved,
    string? BridgeAlias,
    string? BridgeTarget,
    BridgeAction BridgeAction,
    string? BridgeBody,
    IReadOnlyList<string>? UnknownShorthand,
    ClassifierTrace? Trace);
```

`source/FlowHub.Api/Endpoints/CapturePreviewEndpoint.cs`:

```csharp
using FluentValidation;
using FlowHub.Api.Requests;
using FlowHub.Core.Classification;
using FlowHub.Core.Skills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace FlowHub.Api.Endpoints;

internal static class CapturePreviewEndpoint
{
    public static void MapCapturePreviewEndpoint(this RouteGroupBuilder captures)
    {
        captures.MapPost("/preview", PreviewAsync)
            .WithName("PreviewCapture")
            .Produces<CapturePreviewResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem();
    }

    private static async Task<Results<Ok<CapturePreviewResponse>, ValidationProblem>> PreviewAsync(
        CreateCaptureRequest request,
        IValidator<CreateCaptureRequest> validator,
        IClassifier classifier,
        IVikunjaProjectCatalog projects,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            var errors = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            return TypedResults.ValidationProblem(errors);
        }

        // Classify only. No ICaptureService, no bus, no persistence — see D2.
        var result = await classifier.ClassifyAsync(request.Content, ct);

        var (projectId, resolved) = await ResolveProjectAsync(projects, result.VikunjaProject, ct);

        return TypedResults.Ok(new CapturePreviewResponse(
            result.MatchedSkill,
            result.Title,
            result.Tags,
            result.Entities,
            result.VikunjaProject,
            projectId,
            resolved,
            result.BridgeAlias,
            result.BridgeTarget,
            result.BridgeAction,
            result.BridgeBody,
            result.UnknownShorthand,
            result.Trace));
    }

    /// <summary>
    /// Read-only catalogue lookup. A preview must never fail because Vikunja is down, so
    /// an unreachable catalogue reports "unresolved" rather than throwing.
    /// </summary>
    private static async Task<(int? Id, bool Resolved)> ResolveProjectAsync(
        IVikunjaProjectCatalog projects, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, false);
        }

        try
        {
            var catalog = await projects.GetAsync(ct);
            return catalog.TryGetValue(name, out var id) ? (id, true) : (null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, false);
        }
    }
}
```

In `source/FlowHub.Api/Endpoints/CaptureEndpoints.cs`, alongside the existing registrations:

```csharp
        captures.MapCapturePreviewEndpoint();
```

**On the broad catch:** the house rule is to catch specific exception types. This one is
deliberate and needs the comment above it — the catalogue is an HTTP call behind an
interface, its failure modes are not enumerable from here, and the spec requires a preview
to survive any of them. `OperationCanceledException` is deliberately excluded so
cancellation still propagates.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~CapturePreviewEndpointTests"`
Expected: PASS — 3 tests.

- [ ] **Step 5: Commit and push**

```bash
git add source/FlowHub.Api/Requests/CapturePreviewResponse.cs source/FlowHub.Api/Endpoints/CapturePreviewEndpoint.cs source/FlowHub.Api/Endpoints/CaptureEndpoints.cs tests/FlowHub.Web.ComponentTests/Api/CapturePreviewEndpointTests.cs
git commit -m "feat(api): add POST /api/v1/captures/preview (#11)"
git push
```

---

### Task 2: Report the project id, and when it would not resolve

**Files:**
- Modify: `tests/FlowHub.Web.ComponentTests/Api/CapturePreviewEndpointTests.cs`

**Interfaces:**
- Consumes: `CapturePreviewResponse` and `ResolveProjectAsync` from Task 1.
- Produces: nothing new — this task proves D3 and D4 behave.

Task 1 wrote the resolution logic; this task is the evidence that it does what the spec
claims, including the failure paths that are the whole point of the field.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task Preview_KnownProject_ResolvesToItsId()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(["t"], "Vikunja", VikunjaProject: "Games Ideen"));

        var catalog = Substitute.For<IVikunjaProjectCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["Games Ideen"] = 92, ["Inbox"] = 2 });

        using var client = CreateClient(s =>
        {
            s.AddSingleton(classifier);
            s.AddSingleton(catalog);
        });

        var body = await (await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "x", source = "Web" })).Content.ReadFromJsonAsync<CapturePreviewResponse>();

        body!.VikunjaProjectId.Should().Be(92);
        body.VikunjaProjectResolved.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_UnknownProject_ReportsUnresolved()
    {
        // The failure this field exists to surface: the classifier names a project that
        // does not exist, and the task would silently land in the fallback instead.
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(["t"], "Vikunja", VikunjaProject: "Does Not Exist"));

        var catalog = Substitute.For<IVikunjaProjectCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["Inbox"] = 2 });

        using var client = CreateClient(s =>
        {
            s.AddSingleton(classifier);
            s.AddSingleton(catalog);
        });

        var body = await (await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "x", source = "Web" })).Content.ReadFromJsonAsync<CapturePreviewResponse>();

        body!.VikunjaProjectId.Should().BeNull();
        body.VikunjaProjectResolved.Should().BeFalse();
        body.VikunjaProject.Should().Be("Does Not Exist");   // the name is still reported
    }

    [Fact]
    public async Task Preview_CatalogueUnreachable_StillReturns()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(["t"], "Vikunja", VikunjaProject: "Inbox"));

        var catalog = Substitute.For<IVikunjaProjectCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, int>>(_ => throw new HttpRequestException("down"));

        using var client = CreateClient(s =>
        {
            s.AddSingleton(classifier);
            s.AddSingleton(catalog);
        });

        var response = await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "x", source = "Web" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CapturePreviewResponse>();
        body!.VikunjaProjectResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Preview_NonVikunjaSkill_ReportsNoProject()
    {
        var classifier = Substitute.For<IClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ClassificationResult(["t"], "Bridge", BridgeTarget: "o/r"));

        using var client = CreateClient(s => s.AddSingleton(classifier));

        var body = await (await client.PostAsJsonAsync("/api/v1/captures/preview",
            new { content = "x", source = "Web" })).Content.ReadFromJsonAsync<CapturePreviewResponse>();

        body!.VikunjaProject.Should().BeNull();
        body.VikunjaProjectId.Should().BeNull();
        body.VikunjaProjectResolved.Should().BeFalse();
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter "FullyQualifiedName~CapturePreviewEndpointTests"`
Expected: PASS if Task 1's `ResolveProjectAsync` is correct. **If any fails, fix the implementation, never the test** — these encode the spec's D3.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test tests/FlowHub.Web.ComponentTests && dotnet test tests/FlowHub.Core.Tests && dotnet test tests/FlowHub.Skills.Tests`
Expected: all three green.

- [ ] **Step 4: Commit and push**

```bash
git add tests/FlowHub.Web.ComponentTests/Api/CapturePreviewEndpointTests.cs
git commit -m "test(api): cover project resolution and its failure paths in preview (#11)"
git push
```

---

### Task 3: Changelog

**Files:**
- Modify: `CHANGELOG.md` (`[Unreleased]` → `Added`)

- [ ] **Step 1: Add the entry**

```markdown
- `POST /api/v1/captures/preview` returns what a capture's classification *would* decide —
  matched skill, Vikunja project with its resolved id, Bridge target, tags, title, entities
  and the classifier trace — while writing nothing at all: no capture, no event, no
  downstream call. `vikunjaProjectResolved: false` surfaces a classifier naming a project
  that does not exist, which today silently falls back to the Inbox. (#11)
```

- [ ] **Step 2: Commit and push**

```bash
git add CHANGELOG.md
git commit -m "docs: changelog for the classifier preview endpoint (#11)"
git push
```

---

## Self-review

**Spec coverage:** D1 (separate endpoint) → Task 1. D2 (classify directly, persist nothing) → Task 1, asserted by `Preview_PersistsNothing`. D3 (project id + resolved flag, unreachable catalogue tolerated) → Task 2. D4 (duplicate titles report the id actually used) → falls out of using the same catalogue map; no separate task, and the spec says resolving the ambiguity is out of scope. Error-handling table → Tasks 1-2. Non-goals need no task.

**Placeholder scan:** the one thing that cannot be written blind is the test harness entry point, called out in Task 1 Step 1 with an instruction to copy a sibling rather than invent one.

**Type consistency:** `CapturePreviewResponse`'s parameter order is fixed in Task 1 and used positionally nowhere else — tests access by name. `ResolveProjectAsync` returns `(int?, bool)` and is consumed only inside the endpoint. `IVikunjaProjectCatalog.GetAsync` returns `IReadOnlyDictionary<string, int>`, matching `VikunjaSkillIntegration`'s usage.

**Known limitation carried deliberately:** the preview reports classification only. A capture whose classification is right can still fail at write time — a wrong Vikunja project id, a bridge 404. The spec states this; the survey it enables is about routing, which is where the observed errors are.
