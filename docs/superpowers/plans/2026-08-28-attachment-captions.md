# Attachment Captions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the text submitted alongside an attachment, so it reaches the classifier, the embedder, and the UI instead of being silently replaced by the filename.

**Architecture:** One behavioural change in `EfCaptureService.SubmitAsync` (caption wins, filename is the fallback), one call-site fix in the Web form that currently hard-codes `content: null`, and an attachment icon in both grids to replace the visual marker the change removes. Telegram already passes its caption and needs no code change beyond deleting a log line that becomes false.

**Tech Stack:** .NET 10 · xUnit + FluentAssertions + NSubstitute · bUnit · MudBlazor

**Spec:** [`docs/superpowers/specs/2026-08-28-attachment-captions-design.md`](../specs/2026-08-28-attachment-captions-design.md)

## Global Constraints

- `TargetFramework` is `net10.0`; `Nullable` is `enable`; **`TreatWarningsAsErrors` is `true`** — a warning fails the build.
- `GenerateDocumentationFile` is on; public types need XML doc comments.
- Central Package Management: never put `Version=` on a `PackageReference`. This plan adds **no** packages.
- MudBlazor only — no raw HTML where a Mud component exists; icons come from `Icons.Material.Filled.*`.
- `dotnet test` on the full solution is unreliable locally (NU1903 via the slnx). **Run the per-project commands given in each task**, not `just test`.
- Every commit message follows Conventional Commits and ends with `Refs #31`.

## Read this before Task 1 — one test is meant to be inverted

`tests/FlowHub.Persistence.Tests/EfCaptureServiceAttachmentTests.cs:24-26` asserts the **current, wrong** behaviour:

```csharp
var capture = await sut.SubmitAsync(content: "ignored typed text", ChannelKind.Web, input);
capture.Content.Should().Be("invoice.pdf");
```

`CLAUDE.md` says *never modify a test to make it green — fix the implementation.* That rule exists to stop a defect being hidden. **This is the opposite case:** the specification changed, and that test encodes the old one. Task 1 rewrites it deliberately.

**Any implementation that leaves `capture.Content.Should().Be("invoice.pdf")` passing for a captioned submit has not done the work.**

---

### Task 1: Preserve the caption in `EfCaptureService`

**Files:**
- Modify: `source/FlowHub.Persistence/EfCaptureService.cs:56-63`
- Test: `tests/FlowHub.Persistence.Tests/EfCaptureServiceAttachmentTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: no signature change. `ICaptureService.SubmitAsync(string? content, ChannelKind source, AttachmentInput? attachment, CancellationToken)` keeps its shape; only the value written to `Capture.Content` changes.

- [ ] **Step 1: Rewrite the pinning test and add the fallback cases**

In `tests/FlowHub.Persistence.Tests/EfCaptureServiceAttachmentTests.cs`, replace the whole first test — the one named `SubmitAsync_WithAttachment_PersistsAttachmentAndUsesFileNameAsContent` — with these four. Note the rename: the old name asserts the bug.

```csharp
    private static (EfCaptureService Sut, IAttachmentStorage Storage) BuildSut()
    {
        var repo = Substitute.For<ICaptureRepository>();
        repo.AddAsync(Arg.Any<Capture>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<Capture>()));
        var storage = Substitute.For<IAttachmentStorage>();
        storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("2026/05/abc123.pdf");
        var publish = Substitute.For<IPublishEndpoint>();
        return (new EfCaptureService(repo, publish, storage), storage);
    }

    private static AttachmentInput PdfInput(Stream bytes) =>
        new() { Content = bytes, FileName = "invoice.pdf", ContentType = "application/pdf", SizeBytes = 10 };

    [Fact]
    public async Task SubmitAsync_WithAttachmentAndCaption_UsesTheCaptionAsContent()
    {
        // Deliberate inversion of the old assertion: the caption is the note, the
        // filename lives on the Attachment. See the spec's D4.
        var (sut, _) = BuildSut();
        using var bytes = new MemoryStream(new byte[10]);

        var capture = await sut.SubmitAsync("invoice for the boiler service", ChannelKind.Web, PdfInput(bytes));

        capture.Content.Should().Be("invoice for the boiler service");
        capture.Attachment.Should().NotBeNull();
        capture.Attachment!.FileName.Should().Be("invoice.pdf");
        capture.Attachment.RelativePath.Should().Be("2026/05/abc123.pdf");
        capture.Attachment.SizeBytes.Should().Be(10);
    }

    [Fact]
    public async Task SubmitAsync_WithAttachmentAndNoCaption_FallsBackToTheFileName()
    {
        var (sut, _) = BuildSut();
        using var bytes = new MemoryStream(new byte[10]);

        var capture = await sut.SubmitAsync(content: null, ChannelKind.Web, PdfInput(bytes));

        capture.Content.Should().Be("invoice.pdf");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task SubmitAsync_WithAttachmentAndBlankCaption_FallsBackToTheFileName(string caption)
    {
        var (sut, _) = BuildSut();
        using var bytes = new MemoryStream(new byte[10]);

        var capture = await sut.SubmitAsync(caption, ChannelKind.Web, PdfInput(bytes));

        capture.Content.Should().Be("invoice.pdf");
    }

    [Fact]
    public async Task SubmitAsync_WithAttachmentAndPaddedCaption_TrimsIt()
    {
        var (sut, _) = BuildSut();
        using var bytes = new MemoryStream(new byte[10]);

        var capture = await sut.SubmitAsync("  boiler invoice  ", ChannelKind.Web, PdfInput(bytes));

        capture.Content.Should().Be("boiler invoice");
    }
```

Leave the second existing test (`SubmitAsync_WithAttachment_RepositoryThrows_DeletesStoredFile`) untouched — the rollback behaviour is unchanged.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/FlowHub.Persistence.Tests/FlowHub.Persistence.Tests.csproj --filter EfCaptureServiceAttachmentTests`
Expected: FAIL — the caption test gets `"invoice.pdf"`, and the trim test gets `"invoice.pdf"` too. The two fallback tests already pass (that is today's behaviour, now pinned deliberately).

Docker must be running: this project uses Testcontainers for its other test classes.

- [ ] **Step 3: Make the caption win**

In `source/FlowHub.Persistence/EfCaptureService.cs`, replace these lines:

```csharp
        var att = new Attachment(fileName, attachment.ContentType, attachment.SizeBytes, relativePath, DateTimeOffset.UtcNow);
        var capture = new Capture(
            Guid.NewGuid(), source, fileName, DateTimeOffset.UtcNow,
            LifecycleStage.Raw, MatchedSkill: null, Attachment: att);
```

with:

```csharp
        var att = new Attachment(fileName, attachment.ContentType, attachment.SizeBytes, relativePath, DateTimeOffset.UtcNow);

        // The caption is the note; the file is the evidence. The filename is not lost —
        // it lives on the Attachment above, so this replaces a duplicated value rather
        // than displacing one. Content is what the classifier and the embedder read.
        // Whitespace-only counts as absent so a stray space cannot blank a Capture.
        var content = string.IsNullOrWhiteSpace(caption) ? fileName : caption.Trim();

        var capture = new Capture(
            Guid.NewGuid(), source, content, DateTimeOffset.UtcNow,
            LifecycleStage.Raw, MatchedSkill: null, Attachment: att);
```

Then rename the method's first parameter from `content` to `caption` so the local `content` above does not shadow it — the signature line becomes:

```csharp
    public async Task<Capture> SubmitAsync(
        string? caption, ChannelKind source, AttachmentInput? attachment, CancellationToken cancellationToken = default)
    {
        if (attachment is null)
        {
            return await SubmitAsync(caption ?? throw new ArgumentNullException(nameof(caption)), source, cancellationToken);
        }
```

Renaming a parameter changes the named-argument contract, so check for callers using `content:` before moving on — Task 3 fixes the Web one, and `source/FlowHub.Api/Endpoints/CaptureWriteEndpoints.cs` also passes `content: null`. Update that to `caption: null`; it is a rename only, no behaviour change, and the API endpoint stays out of scope otherwise.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Persistence.Tests/FlowHub.Persistence.Tests.csproj --filter EfCaptureServiceAttachmentTests`
Expected: PASS — 6 tests (1 caption, 1 null, 3 blank theory rows, 1 trim), plus the untouched rollback test.

- [ ] **Step 5: Build the affected projects with zero warnings**

Run:
```bash
dotnet build source/FlowHub.Persistence/FlowHub.Persistence.csproj
dotnet build source/FlowHub.Api/FlowHub.Api.csproj
```
Expected: `0 Warning(s)`, `0 Error(s)` for both. `TreatWarningsAsErrors` is on, so a stale `content:` named argument fails here rather than silently.

- [ ] **Step 6: Commit and push**

```bash
git add source/FlowHub.Persistence/EfCaptureService.cs source/FlowHub.Api/Endpoints/CaptureWriteEndpoints.cs tests/FlowHub.Persistence.Tests/EfCaptureServiceAttachmentTests.cs
git commit -m "fix(captures): keep the caption when a Capture has an attachment

Refs #31"
git push
```

---

### Task 2: Drop the Telegram log line that is now false

**Files:**
- Modify: `source/FlowHub.Telegram/TelegramUpdateHandler.cs`
- Test: `tests/FlowHub.Telegram.Tests/TelegramUpdateHandlerAttachmentTests.cs`

**Interfaces:**
- Consumes: Task 1's behaviour (a non-blank caption now survives).
- Produces: nothing new.

Telegram already passes `message.Text` to `SubmitAsync`, so Task 1 fixed it. What remains is a debug log asserting the opposite, which would mislead the next reader.

- [ ] **Step 1: Add a test that the caption reaches the Capture**

Append to `tests/FlowHub.Telegram.Tests/TelegramUpdateHandlerAttachmentTests.cs`, inside the existing class:

```csharp
    [Fact]
    public async Task HandleAsync_DocumentWithCaption_PassesTheCaptionThrough()
    {
        var (sut, captures, _) = Build();

        await sut.HandleAsync(
            FileMessage("invoice.pdf", "application/pdf", 16, caption: "boiler service invoice"),
            CancellationToken.None);

        await captures.Received(1).SubmitAsync(
            "boiler service invoice", ChannelKind.Telegram, Arg.Any<AttachmentInput>(), Arg.Any<CancellationToken>());
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj --filter TelegramUpdateHandlerAttachmentTests`
Expected: PASS immediately. The handler already forwards the caption — this test pins that it keeps doing so, and would have caught the regression if a future change reverted to passing `null`.

- [ ] **Step 3: Delete the stale log**

In `source/FlowHub.Telegram/TelegramUpdateHandler.cs`, remove this block from `HandleFileAsync`:

```csharp
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                // Captures with an attachment take the filename as Content; the caption
                // is not persisted. Same limitation as the Web upload path.
                LogDroppingCaption(message.UpdateId);
            }

```

and remove its declaration near the bottom of the file:

```csharp
    [LoggerMessage(EventId = 5003, Level = LogLevel.Debug,
        Message = "Dropping caption on Telegram update (updateId={UpdateId})")]
    private partial void LogDroppingCaption(long updateId);
```

Do not renumber the remaining EventIds — 5001 and 5002 stay as they are. Leaving 5003 retired is correct; reusing it would make old log entries ambiguous.

- [ ] **Step 4: Run the whole Telegram suite**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj`
Expected: PASS, 30 tests. A build error naming `LogDroppingCaption` means one of the two deletions was missed.

- [ ] **Step 5: Commit and push**

```bash
git add source/FlowHub.Telegram/TelegramUpdateHandler.cs tests/FlowHub.Telegram.Tests/TelegramUpdateHandlerAttachmentTests.cs
git commit -m "fix(telegram): stop logging that captions are dropped

Refs #31"
git push
```

---

### Task 3: Stop the Web form discarding typed text

**Files:**
- Modify: `source/FlowHub.Web/Components/Pages/NewCapture.razor.cs:84-92`
- Test: `tests/FlowHub.Web.ComponentTests/Pages/NewCaptureUploadTests.cs`

**Interfaces:**
- Consumes: Task 1's `caption` parameter name.
- Produces: nothing new.

The form renders a text field (`NewCapture.razor:19-20`, bound to `_content`) and then passes `content: null` when a file is staged — so a user can type a note, attach a file, and watch the note vanish.

- [ ] **Step 1: Write the failing test**

Open `tests/FlowHub.Web.ComponentTests/Pages/NewCaptureUploadTests.cs` and copy the arrangement its existing upload test uses (service substitutes, `AddMudServices()`, staging a file). Add:

```csharp
    [Fact]
    public async Task Submit_WithFileAndTypedText_PassesTheTypedTextAsCaption()
    {
        var cut = RenderComponent<NewCapturePage>();

        await StageFileAsync(cut, "invoice.pdf", "application/pdf", 10);
        cut.Find("input[type='text']").Change("boiler service invoice");
        await cut.Find("button[type='submit']").ClickAsync(new MouseEventArgs());

        await _captureService.Received(1).SubmitAsync(
            "boiler service invoice", ChannelKind.Web, Arg.Any<AttachmentInput>(), Arg.Any<CancellationToken>());
    }
```

If the existing file has no `StageFileAsync` helper, use whatever mechanism its current upload test uses to stage a file and follow that exactly — do not invent a second approach.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj --filter NewCaptureUploadTests`
Expected: FAIL — `SubmitAsync` receives `null`, not the typed text.

- [ ] **Step 3: Pass the typed text**

In `source/FlowHub.Web/Components/Pages/NewCapture.razor.cs`, change:

```csharp
                capture = await CaptureService.SubmitAsync(
                    content: null, ChannelKind.Web,
```

to:

```csharp
                capture = await CaptureService.SubmitAsync(
                    caption: _content, ChannelKind.Web,
```

`_content` may be null or blank, which Task 1 already treats as "no caption" — so no guard is needed here.

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj --filter NewCaptureUploadTests`
Expected: PASS.

- [ ] **Step 5: Commit and push**

```bash
git add source/FlowHub.Web/Components/Pages/NewCapture.razor.cs tests/FlowHub.Web.ComponentTests/Pages/NewCaptureUploadTests.cs
git commit -m "fix(web): submit typed text as the caption when a file is attached

Refs #31"
git push
```

---

### Task 4: Mark attachments in both grids

**Files:**
- Modify: `source/FlowHub.Web/Components/Pages/Captures.razor:121-127`
- Modify: `source/FlowHub.Web/Components/DashboardCards/RecentCapturesCard.razor:60-66`
- Test: `tests/FlowHub.Web.ComponentTests/Pages/CapturesTests.cs`
- Test: `tests/FlowHub.Web.ComponentTests/DashboardCards/RecentCapturesCardTests.cs`

**Interfaces:**
- Consumes: Task 1's behaviour (`Content` may now be a caption).
- Produces: nothing new.

With `Content` showing a caption, nothing in either grid distinguishes an attachment row. Today's marker — `Content` happening to look like a filename — was an accident of the bug, and Task 1 removes it.

- [ ] **Step 1: Write the failing tests**

In `tests/FlowHub.Web.ComponentTests/Pages/CapturesTests.cs`, add an attachment-carrying helper next to the existing `Cap(...)`:

```csharp
    private static Capture CapWithAttachment(string content, string fileName) =>
        new(
            Guid.NewGuid(),
            ChannelKind.Web,
            content,
            DateTimeOffset.UtcNow,
            LifecycleStage.Completed,
            "Wallabag",
            Attachment: new Attachment(fileName, "application/pdf", 10, "2026/08/x.pdf", DateTimeOffset.UtcNow));
```

and these two tests:

```csharp
    [Fact]
    public void Render_CaptureWithAttachment_ShowsTheAttachmentIconAndFileName()
    {
        GivenCaptures(CapWithAttachment("boiler service invoice", "invoice.pdf"));

        var cut = RenderComponent<CapturesPage>();

        cut.Markup.Should().Contain("boiler service invoice");
        cut.FindAll("[data-testid='attachment-indicator']").Should().HaveCount(1);
        cut.Markup.Should().Contain("invoice.pdf");
    }

    [Fact]
    public void Render_CaptureWithoutAttachment_ShowsNoAttachmentIcon()
    {
        GivenCaptures(Cap("just some text"));

        var cut = RenderComponent<CapturesPage>();

        cut.FindAll("[data-testid='attachment-indicator']").Should().BeEmpty();
    }
```

Add the same pair to `tests/FlowHub.Web.ComponentTests/DashboardCards/RecentCapturesCardTests.cs`, adapted to however that file arranges its captures (it substitutes `ICaptureService.GetRecentAsync`, not `GetAllAsync`) — copy its existing arrangement rather than inventing one, and use the same `CapWithAttachment` shape and the same two assertions.

- [ ] **Step 2: Run them to verify they fail**

Run:
```bash
dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj --filter "CapturesTests|RecentCapturesCardTests"
```
Expected: FAIL — no element carries `data-testid='attachment-indicator'`.

- [ ] **Step 3: Add the icon to the captures grid**

In `source/FlowHub.Web/Components/Pages/Captures.razor`, replace the `Content` column's `CellTemplate`:

```razor
            <PropertyColumn Property="x => x.Content" Title="Content">
                <CellTemplate>
                    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="1">
                        @if (context.Item.Attachment is not null)
                        {
                            <MudTooltip Text="@context.Item.Attachment.FileName">
                                <MudIcon Icon="@Icons.Material.Filled.AttachFile"
                                         Size="Size.Small"
                                         Color="Color.Default"
                                         data-testid="attachment-indicator" />
                            </MudTooltip>
                        }
                        <MudText Typo="Typo.body2" Style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:380px;">
                            @context.Item.Content
                        </MudText>
                    </MudStack>
                </CellTemplate>
            </PropertyColumn>
```

- [ ] **Step 4: Add the same icon to the dashboard card**

In `source/FlowHub.Web/Components/DashboardCards/RecentCapturesCard.razor`, replace its `Content` column's `CellTemplate` with the identical block from Step 3. The markup is the same; only the surrounding indentation differs.

- [ ] **Step 5: Run the component tests**

Run:
```bash
dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj --filter "CapturesTests|RecentCapturesCardTests"
```
Expected: PASS. If the tooltip does not render the filename into the markup in bUnit, assert on the icon's presence and drop the `Contain("invoice.pdf")` line rather than restructuring the component to satisfy the test — the tooltip is a real MudBlazor behaviour, not something to work around.

- [ ] **Step 6: Run the full component suite**

Run: `dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj`
Expected: PASS. This suite is sensitive to constructor changes elsewhere; if something unrelated fails, fix the test's construction rather than production code.

- [ ] **Step 7: Commit and push**

```bash
git add source/FlowHub.Web/Components/Pages/Captures.razor source/FlowHub.Web/Components/DashboardCards/RecentCapturesCard.razor tests/FlowHub.Web.ComponentTests
git commit -m "feat(web): mark captures that carry an attachment in the grids

Refs #31"
git push
```

---

### Task 5: Record the change

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/glossary.md`

- [ ] **Step 1: Add the changelog entry**

Under `## [Unreleased]` → `### Fixed` in `CHANGELOG.md` (create the `### Fixed` heading if the section has none):

```markdown
- Text submitted alongside an attachment is kept as the Capture's content instead of
  being replaced by the filename, so it reaches classification and search. Captures
  carrying a file are now marked with an icon in the grids (#31).
```

- [ ] **Step 2: Correct the glossary**

`docs/glossary.md`'s **Capture** entry describes `Content`. Append to that entry:

```markdown
For a Capture with an attachment, `Content` is the caption submitted alongside the
file, falling back to the filename when no caption was given. The filename is always
available on the `Attachment` record regardless.
```

- [ ] **Step 3: Verify**

Run:
```bash
grep -n "attachment" CHANGELOG.md | head -3
grep -n "falling back to the filename" docs/glossary.md
```
Expected: both print a line.

- [ ] **Step 4: Commit and push**

```bash
git add CHANGELOG.md docs/glossary.md
git commit -m "docs(captures): record the attachment-caption behaviour

Refs #31"
git push
```

---

### Task 6: Verify the whole change together

**Files:** none — verification only.

- [ ] **Step 1: Build the solution**

Run: `dotnet build FlowHub.slnx`
Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 2: Run every touched test project**

Run:
```bash
dotnet test tests/FlowHub.Persistence.Tests/FlowHub.Persistence.Tests.csproj
dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj
dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj
dotnet test tests/FlowHub.Core.Tests/FlowHub.Core.Tests.csproj
```
Expected: all PASS. Docker must be running for the Persistence project.

- [ ] **Step 3: Confirm no caller still passes `content:`**

Run: `grep -rn "content: null\|content:" source/ --include=*.cs --include=*.razor.cs | grep SubmitAsync`
Expected: no output. Any hit is a caller missed by the Task 1 rename.

- [ ] **Step 4: Commit if anything changed**

If steps 1–3 required fixes:

```bash
git add -A
git commit -m "fix(captures): resolve fallout from the caption change

Refs #31"
git push
```
