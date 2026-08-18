# Signal Note-to-Self Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Import a user's Signal Desktop **Note-to-Self** backup export (`.zip`) into FlowHub as Captures that flow through the existing classify → route pipeline.

**Architecture:** A new admin endpoint accepts the export `.zip`. A parser reads `main.jsonl`, isolates the Note-to-Self chat, and yields messages; a media resolver locates the (plaintext) attachment files by a hash of their key material; an import orchestrator creates one `Capture` per message via a new `ImportAsync` service seam (original timestamp + idempotency key + optional group id) and publishes `CaptureCreated`, reusing every downstream consumer unchanged.

**Tech Stack:** .NET 10, C#, ASP.NET Core Minimal APIs, EF Core + PostgreSQL/pgvector, MassTransit, MudBlazor, xUnit + FluentAssertions + NSubstitute + Testcontainers + bUnit.

## Global Constraints

- Target framework and SDK: **.NET 10** (pinned in `global.json`); do **not** change target frameworks.
- **Warnings are errors** (`Directory.Build.props`); nullable enabled — no `!`/`#nullable disable` to silence.
- **Do not add NuGet packages** without asking; central versions live in `Directory.Packages.props`.
- Test naming: `MethodName_StateUnderTest_ExpectedBehavior` (CA1707 suppressed in test projects).
- TDD is non-negotiable: write the failing test first; never weaken a test to pass; no generic `catch (Exception)`; always pass `CancellationToken` to async I/O.
- `Source` is stored as `varchar(32)` — adding an enum member needs **no** migration.
- Media addressing (verbatim): `stem = SHA256( plaintextHash(32B) ‖ localKey(64B) )`, file at `files/<stem[:2]>/<stem>.<ext>`, verify `SHA256(bytes) == plaintextHash`.
- `ExternalRef` idempotency key format (verbatim): `signal:{chatId}:{dateSent}`.
- Grouping window default: **120 seconds**; text-with-text is never fused.

---

### Task 1: Add `ChannelKind.Signal` + suppress notifications for it

**Files:**
- Modify: `source/FlowHub.Core/Captures/ChannelKind.cs`
- Modify: `source/FlowHub.Web/Pipeline/CaptureNotificationConsumer.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureNotificationConsumerTests.cs` (create if absent; else add to the existing pipeline test project used for consumers)
- Modify (docs): `bruno/captures/submit-capture.bru`

**Interfaces:**
- Produces: `ChannelKind.Signal` enum member consumed by every later task.

- [ ] **Step 1: Write the failing test** — a Signal-sourced `CaptureCreated` must not notify.

```csharp
[Fact]
public async Task Consume_SignalSource_DoesNotNotify()
{
    var notifier = Substitute.For<ICaptureNotifier>();
    var sut = new CaptureNotificationConsumer(notifier);
    var msg = new CaptureCreated(Guid.NewGuid(), "hi", ChannelKind.Signal, DateTimeOffset.UtcNow);
    var ctx = Substitute.For<ConsumeContext<CaptureCreated>>();
    ctx.Message.Returns(msg);

    await sut.Consume(ctx);

    await notifier.DidNotReceiveWithAnyArgs().NotifyCaptureCreatedAsync(default!, default);
}
```

- [ ] **Step 2: Run test to verify it fails** — Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter Consume_SignalSource_DoesNotNotify`. Expected: FAIL (`ChannelKind.Signal` doesn't compile / notifier is called).

- [ ] **Step 3: Add the enum member** — append `Signal` to `ChannelKind`:

```csharp
public enum ChannelKind
{
    Telegram,
    Web,
    Api,
    Signal,
}
```

- [ ] **Step 4: Guard the consumer** — in `CaptureNotificationConsumer.Consume`:

```csharp
public Task Consume(ConsumeContext<CaptureCreated> context)
{
    if (context.Message.Source == ChannelKind.Signal)
    {
        return Task.CompletedTask;
    }

    return _notifier.NotifyCaptureCreatedAsync(context.Message, context.CancellationToken);
}
```

- [ ] **Step 5: Update Bruno docs** — in `bruno/captures/submit-capture.bru`, extend the `Source` comment to `ChannelKind in [Telegram, Web, Api, Signal]`.

- [ ] **Step 6: Run test to verify it passes** — Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter Consume_SignalSource_DoesNotNotify`. Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add source/FlowHub.Core/Captures/ChannelKind.cs source/FlowHub.Web/Pipeline/CaptureNotificationConsumer.cs tests/FlowHub.Web.ComponentTests/Pipeline/CaptureNotificationConsumerTests.cs bruno/captures/submit-capture.bru
git commit -m "feat(captures): add ChannelKind.Signal and suppress notifications for it"
```

---

### Task 2: `Capture.ImportGroupId` + `ExistsByExternalRefAsync` + migration

**Files:**
- Modify: `source/FlowHub.Core/Captures/Capture.cs`
- Modify: `source/FlowHub.Persistence/Entities/CaptureEntity.cs`
- Modify: `source/FlowHub.Persistence/Entities/CaptureEntityTypeConfiguration.cs`
- Modify: `source/FlowHub.Core/Captures/ICaptureRepository.cs`
- Modify: `source/FlowHub.Persistence/Repositories/EfCaptureRepository.cs`
- Create (generated): `source/FlowHub.Persistence/Migrations/*_0014_AddImportGroupId.cs`
- Test: `tests/FlowHub.Persistence.Tests/Repositories/EfCaptureRepositoryImportTests.cs`

**Interfaces:**
- Produces:
  - `Capture` record gains `Guid? ImportGroupId = null` (last positional/optional param).
  - `Task<bool> ICaptureRepository.ExistsByExternalRefAsync(string externalRef, CancellationToken ct)`.

- [ ] **Step 1: Write the failing repository test** (Testcontainers Postgres, `[Collection("Postgres")]`):

```csharp
[Fact]
public async Task ExistsByExternalRefAsync_WhenPresent_ReturnsTrue()
{
    await using var ctx = await _fixture.CreateFreshDbAsync();
    var repo = new EfCaptureRepository(ctx);
    var groupId = Guid.NewGuid();
    var capture = new Capture(Guid.NewGuid(), ChannelKind.Signal, "note",
        DateTimeOffset.UtcNow, LifecycleStage.Raw, MatchedSkill: null,
        ExternalRef: "signal:20:1734989094736", ImportGroupId: groupId);
    await repo.AddAsync(capture, CancellationToken.None);

    (await repo.ExistsByExternalRefAsync("signal:20:1734989094736", CancellationToken.None)).Should().BeTrue();
    (await repo.ExistsByExternalRefAsync("signal:20:0", CancellationToken.None)).Should().BeFalse();

    var reloaded = await repo.GetByIdAsync(capture.Id, CancellationToken.None);
    reloaded!.ImportGroupId.Should().Be(groupId);
}
```

*(Match the exact `Capture` constructor argument order to the record definition; use named args for the trailing optional fields as shown.)*

- [ ] **Step 2: Run test to verify it fails** — Run: `dotnet test tests/FlowHub.Persistence.Tests --filter ExistsByExternalRefAsync_WhenPresent_ReturnsTrue`. Expected: FAIL (member/method missing).

- [ ] **Step 3: Add `ImportGroupId` to the domain record** — add `Guid? ImportGroupId = null` as the final optional parameter of the `Capture` record (after `ClassifierTrace`), so existing call sites keep compiling.

- [ ] **Step 4: Add the entity column** — add `public Guid? ImportGroupId { get; set; }` to `CaptureEntity`; map it in `EfCaptureRepository.ToEntity` (`ImportGroupId = c.ImportGroupId`) and `ToDomain` (`ImportGroupId: e.ImportGroupId`).

- [ ] **Step 5: Configure the column + index** — in `CaptureEntityTypeConfiguration.Configure`:

```csharp
builder.Property(c => c.ImportGroupId);
builder.HasIndex(c => c.ImportGroupId).HasDatabaseName("IX_Captures_ImportGroupId");
```

- [ ] **Step 6: Add the repository method** — in `ICaptureRepository`:

```csharp
Task<bool> ExistsByExternalRefAsync(string externalRef, CancellationToken cancellationToken);
```

and in `EfCaptureRepository`:

```csharp
public Task<bool> ExistsByExternalRefAsync(string externalRef, CancellationToken cancellationToken) =>
    _db.Captures.AsNoTracking().AnyAsync(c => c.ExternalRef == externalRef, cancellationToken);
```

- [ ] **Step 7: Generate the migration**

Run:
```bash
dotnet ef migrations add 0014_AddImportGroupId \
  --project source/FlowHub.Persistence --startup-project source/FlowHub.Web
```
Expected: creates `Migrations/*_0014_AddImportGroupId.cs` adding a nullable `ImportGroupId uuid` column + `IX_Captures_ImportGroupId`. Verify the `Up`/`Down` only touch this column/index.

- [ ] **Step 8: Run test to verify it passes** — Run: `dotnet test tests/FlowHub.Persistence.Tests --filter ExistsByExternalRefAsync_WhenPresent_ReturnsTrue`. Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add source/FlowHub.Core/Captures/Capture.cs source/FlowHub.Core/Captures/ICaptureRepository.cs source/FlowHub.Persistence tests/FlowHub.Persistence.Tests/Repositories/EfCaptureRepositoryImportTests.cs
git commit -m "feat(persistence): add Capture.ImportGroupId and ExistsByExternalRefAsync"
```

---

### Task 3: `ICaptureService.ImportAsync` seam

**Files:**
- Modify: `source/FlowHub.Core/Captures/ICaptureService.cs`
- Modify: `source/FlowHub.Persistence/EfCaptureService.cs`
- Test: `tests/FlowHub.Persistence.Tests/EfCaptureServiceImportTests.cs`

**Interfaces:**
- Consumes: `ICaptureRepository.AddAsync`, `IAttachmentStorage.SaveAsync`, `IPublishEndpoint`, `AttachmentInput`.
- Produces:
```csharp
Task<Capture> ImportAsync(
    string? content, AttachmentInput? attachment,
    ChannelKind source, DateTimeOffset createdAt,
    string externalRef, Guid? importGroupId,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 1: Write the failing test** — imports preserve `CreatedAt`, `ExternalRef`, `ImportGroupId`, publish `CaptureCreated`:

```csharp
[Fact]
public async Task ImportAsync_TextCapture_PersistsProvidedMetadataAndPublishes()
{
    var repo = Substitute.For<ICaptureRepository>();
    repo.AddAsync(Arg.Any<Capture>(), Arg.Any<CancellationToken>())
        .Returns(ci => ci.Arg<Capture>());
    var storage = Substitute.For<IAttachmentStorage>();
    var publish = Substitute.For<IPublishEndpoint>();
    var sut = new EfCaptureService(repo, storage, publish);
    var created = new DateTimeOffset(2024, 12, 23, 0, 0, 0, TimeSpan.Zero);
    var groupId = Guid.NewGuid();

    var result = await sut.ImportAsync("hello", null, ChannelKind.Signal, created,
        "signal:20:1734989094736", groupId, CancellationToken.None);

    result.CreatedAt.Should().Be(created);
    result.ExternalRef.Should().Be("signal:20:1734989094736");
    result.ImportGroupId.Should().Be(groupId);
    result.Source.Should().Be(ChannelKind.Signal);
    result.Stage.Should().Be(LifecycleStage.Raw);
    await publish.Received(1).Publish(
        Arg.Is<CaptureCreated>(e => e.Source == ChannelKind.Signal && e.CreatedAt == created && !e.HasAttachment),
        Arg.Any<CancellationToken>());
}
```

*(Match `EfCaptureService`'s real constructor dependencies — confirm names/order against the file before writing the `new EfCaptureService(...)` call.)*

- [ ] **Step 2: Run test to verify it fails** — Run: `dotnet test tests/FlowHub.Persistence.Tests --filter ImportAsync_TextCapture_PersistsProvidedMetadataAndPublishes`. Expected: FAIL (method missing).

- [ ] **Step 3: Declare the method on the interface** — add the `ImportAsync` signature above to `ICaptureService`.

- [ ] **Step 4: Implement it** — in `EfCaptureService` (mirroring the two existing `SubmitAsync` overloads but taking `createdAt`/`externalRef`/`importGroupId` instead of defaulting them):

```csharp
public async Task<Capture> ImportAsync(
    string? content, AttachmentInput? attachment,
    ChannelKind source, DateTimeOffset createdAt,
    string externalRef, Guid? importGroupId,
    CancellationToken cancellationToken = default)
{
    Attachment? att = null;
    string? relativePath = null;
    if (attachment is not null)
    {
        relativePath = await _attachmentStorage.SaveAsync(attachment, cancellationToken);
        att = new Attachment(attachment.FileName, attachment.ContentType,
            attachment.SizeBytes, relativePath, createdAt);
    }

    var capture = new Capture(
        Guid.NewGuid(), source, content ?? att!.FileName, createdAt,
        LifecycleStage.Raw, MatchedSkill: null,
        Attachment: att, ExternalRef: externalRef, ImportGroupId: importGroupId);

    try
    {
        var saved = await _repository.AddAsync(capture, cancellationToken);
        await _publishEndpoint.Publish(
            new CaptureCreated(saved.Id, saved.Content, saved.Source, saved.CreatedAt,
                HasAttachment: att is not null),
            cancellationToken);
        return saved;
    }
    catch (Exception) when (relativePath is not null)
    {
        await _attachmentStorage.DeleteAsync(relativePath, CancellationToken.None);
        throw;
    }
}
```

*(Confirm the exact field names of `Capture`'s constructor and the `IAttachmentStorage.SaveAsync`/`DeleteAsync` signatures against the real files; adjust argument order to match. The `catch/when` re-throws — it does not swallow.)*

- [ ] **Step 5: Run test to verify it passes** — Run: `dotnet test tests/FlowHub.Persistence.Tests --filter ImportAsync_TextCapture_PersistsProvidedMetadataAndPublishes`. Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add source/FlowHub.Core/Captures/ICaptureService.cs source/FlowHub.Persistence/EfCaptureService.cs tests/FlowHub.Persistence.Tests/EfCaptureServiceImportTests.cs
git commit -m "feat(captures): add ImportAsync service seam for backfill imports"
```

---

### Task 4: Signal backup parser

**Files:**
- Create: `source/FlowHub.Skills/Signal/SignalMessage.cs`
- Create: `source/FlowHub.Skills/Signal/SignalAttachmentRef.cs`
- Create: `source/FlowHub.Skills/Signal/SignalBackupParser.cs`
- Test: `tests/FlowHub.Skills.Tests/Signal/SignalBackupParserTests.cs`
- Create (fixture): `tests/FlowHub.Skills.Tests/Signal/Fixtures/note-to-self.jsonl`

> Placement note: put the parser in a new `Signal/` folder under `FlowHub.Skills` (an existing ASP.NET-free library). If the team prefers a dedicated `FlowHub.Import` project, that is fine too, but do **not** add a project reference to ASP.NET — the parser must unit-test without a web host.

**Interfaces:**
- Produces:
  - `sealed record SignalAttachmentRef(string ContentType, string PlaintextHashB64, string LocalKeyB64, long Size, string? FileName)`
  - `sealed record SignalMessage(long DateSentMs, string? Text, SignalAttachmentRef? Attachment)`
  - `SignalBackupParser.ParseNoteToSelf(Stream jsonlStream) : (string ChatId, IReadOnlyList<SignalMessage> Messages)` — ordered by `DateSentMs` ascending.

- [ ] **Step 1: Create the fixture** — `note-to-self.jsonl` with these exact lines (header, self recipient id=20, chat 20, a text msg, a URL msg, an attachment-only msg, an update msg to skip):

```jsonl
{"version":1,"backupTimeMs":"1","currentAppVersion":"x","mediaRootBackupKey":"AA=="}
{"recipient":{"id":"20","self":{"avatarColor":"A130"}}}
{"recipient":{"id":"7","contact":{}}}
{"chat":{"id":"20","recipientId":"20","expireTimerVersion":5}}
{"chat":{"id":"3","recipientId":"7","expireTimerVersion":1}}
{"chatItem":{"chatId":"20","authorId":"20","dateSent":"1734989094736","outgoing":{},"standardMessage":{"text":{"body":"buy milk"}}}}
{"chatItem":{"chatId":"20","authorId":"20","dateSent":"1734989154736","outgoing":{},"standardMessage":{"text":{"body":"https://example.com read later"},"linkPreview":[{"url":"https://example.com"}]}}}
{"chatItem":{"chatId":"20","authorId":"20","dateSent":"1734989160000","outgoing":{},"standardMessage":{"attachments":[{"pointer":{"contentType":"image/jpeg","fileName":null,"locatorInfo":{"plaintextHash":"IbNl2V4xe4dvFlfrpLjaWrSbAGMHunOU0EyZt8Xo3mE=","localKey":"AAAA","size":1164}}}]}}}
{"chatItem":{"chatId":"20","authorId":"20","dateSent":"1734989170000","directionless":{},"updateMessage":{}}}
{"chatItem":{"chatId":"3","authorId":"7","dateSent":"1734989180000","outgoing":{},"standardMessage":{"text":{"body":"not note to self"}}}}
```

- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public void ParseNoteToSelf_RealFixture_ReturnsOnlyNoteToSelfStandardMessages()
{
    using var stream = File.OpenRead("Signal/Fixtures/note-to-self.jsonl");
    var (chatId, messages) = SignalBackupParser.ParseNoteToSelf(stream);

    chatId.Should().Be("20");
    messages.Should().HaveCount(3); // 2 text + 1 attachment; update + other-chat excluded
    messages[0].Text.Should().Be("buy milk");
    messages[0].DateSentMs.Should().Be(1734989094736);
    messages[1].Text.Should().Contain("example.com");
    messages[2].Text.Should().BeNull();
    messages[2].Attachment!.ContentType.Should().Be("image/jpeg");
    messages[2].Attachment!.Size.Should().Be(1164);
}
```

*(Ensure the fixture is copied to output: add `<None Include="Signal/Fixtures/**" CopyToOutputDirectory="PreserveNewest" />` to the test `.csproj` item group, matching how the project already ships test assets.)*

- [ ] **Step 3: Run test to verify it fails** — Run: `dotnet test tests/FlowHub.Skills.Tests --filter ParseNoteToSelf_RealFixture_ReturnsOnlyNoteToSelfStandardMessages`. Expected: FAIL (type missing).

- [ ] **Step 4: Implement the records + parser** — `SignalBackupParser.ParseNoteToSelf`:

```csharp
public static (string ChatId, IReadOnlyList<SignalMessage> Messages) ParseNoteToSelf(Stream jsonlStream)
{
    using var reader = new StreamReader(jsonlStream);
    string? selfRecipientId = null;
    string? noteToSelfChatId = null;
    var byRecipient = new Dictionary<string, string>(); // recipientId -> chatId
    var pending = new List<JsonDocument>();

    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (line.Length == 0) continue;
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.TryGetProperty("recipient", out var rec))
        {
            var id = rec.GetProperty("id").GetString()!;
            if (rec.TryGetProperty("self", out _)) selfRecipientId = id;
        }
        else if (root.TryGetProperty("chat", out var chat))
        {
            byRecipient[chat.GetProperty("recipientId").GetString()!] = chat.GetProperty("id").GetString()!;
        }
        else if (root.TryGetProperty("chatItem", out _))
        {
            pending.Add(JsonDocument.Parse(line)); // keep for a second pass
        }
    }

    if (selfRecipientId is null || !byRecipient.TryGetValue(selfRecipientId, out noteToSelfChatId))
        throw new SignalImportException("No Note-to-Self chat found (missing self recipient).");

    var messages = new List<SignalMessage>();
    foreach (var d in pending)
    {
        var ci = d.RootElement.GetProperty("chatItem");
        if (ci.GetProperty("chatId").GetString() != noteToSelfChatId) continue;
        if (!ci.TryGetProperty("standardMessage", out var sm)) continue; // skip update/deleted

        var dateSent = long.Parse(ci.GetProperty("dateSent").GetString()!, CultureInfo.InvariantCulture);
        string? text = sm.TryGetProperty("text", out var t) && t.TryGetProperty("body", out var b)
            ? b.GetString() : null;

        SignalAttachmentRef? att = null;
        if (sm.TryGetProperty("attachments", out var atts) && atts.GetArrayLength() > 0)
        {
            var p = atts[0].GetProperty("pointer");
            var loc = p.GetProperty("locatorInfo");
            att = new SignalAttachmentRef(
                p.GetProperty("contentType").GetString()!,
                loc.GetProperty("plaintextHash").GetString()!,
                loc.GetProperty("localKey").GetString()!,
                loc.GetProperty("size").GetInt64(),
                p.TryGetProperty("fileName", out var fn) ? fn.GetString() : null);
        }

        if (text is null && att is null) continue; // nothing to import
        messages.Add(new SignalMessage(dateSent, text, att));
        d.Dispose();
    }

    messages.Sort((a, b) => a.DateSentMs.CompareTo(b.DateSentMs));
    return (noteToSelfChatId, messages);
}
```

Also create `SignalImportException : Exception` in the same folder.

- [ ] **Step 5: Run test to verify it passes** — Run: `dotnet test tests/FlowHub.Skills.Tests --filter ParseNoteToSelf_RealFixture_ReturnsOnlyNoteToSelfStandardMessages`. Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add source/FlowHub.Skills/Signal tests/FlowHub.Skills.Tests/Signal
git commit -m "feat(signal): parse Note-to-Self messages from backup main.jsonl"
```

---

### Task 5: Signal media resolver

**Files:**
- Create: `source/FlowHub.Skills/Signal/SignalMediaResolver.cs`
- Test: `tests/FlowHub.Skills.Tests/Signal/SignalMediaResolverTests.cs`

**Interfaces:**
- Consumes: `SignalAttachmentRef` (Task 4).
- Produces: `SignalMediaResolver.Resolve(string archiveRoot, SignalAttachmentRef att) : (string FilePath, string FileName)` — throws `SignalImportException` on missing file or hash mismatch.

- [ ] **Step 1: Write the failing test** — build a temp archive with a byte payload whose `plaintextHash`+`localKey` derive the on-disk name:

```csharp
[Fact]
public void Resolve_ValidMedia_ReturnsPathAndVerifiesHash()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        byte[] payload = Encoding.UTF8.GetBytes("fake-image-bytes");
        byte[] plaintextHash = SHA256.HashData(payload);   // the file's content hash
        byte[] localKey = new byte[64];                    // arbitrary but fixed
        byte[] stemInput = plaintextHash.Concat(localKey).ToArray();
        string stem = Convert.ToHexStringLower(SHA256.HashData(stemInput));
        var dir = Path.Combine(root, "files", stem[..2]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{stem}.jpeg"), payload);

        var att = new SignalAttachmentRef("image/jpeg",
            Convert.ToBase64String(plaintextHash), Convert.ToBase64String(localKey), payload.Length, null);

        var (path, name) = SignalMediaResolver.Resolve(root, att);

        File.ReadAllBytes(path).Should().Equal(payload);
        name.Should().EndWith(".jpeg");
    }
    finally { Directory.Delete(root, recursive: true); }
}
```

- [ ] **Step 2: Run test to verify it fails** — Run: `dotnet test tests/FlowHub.Skills.Tests --filter Resolve_ValidMedia_ReturnsPathAndVerifiesHash`. Expected: FAIL (type missing).

- [ ] **Step 3: Implement the resolver**

```csharp
public static (string FilePath, string FileName) Resolve(string archiveRoot, SignalAttachmentRef att)
{
    byte[] plaintextHash = Convert.FromBase64String(att.PlaintextHashB64);
    byte[] localKey = Convert.FromBase64String(att.LocalKeyB64);
    byte[] stemInput = new byte[plaintextHash.Length + localKey.Length];
    plaintextHash.CopyTo(stemInput, 0);
    localKey.CopyTo(stemInput, plaintextHash.Length);
    string stem = Convert.ToHexStringLower(SHA256.HashData(stemInput));

    var dir = Path.Combine(archiveRoot, "files", stem[..2]);
    var match = Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, stem + ".*").FirstOrDefault()
        : null;
    if (match is null)
        throw new SignalImportException($"Media file not found for stem {stem}.");

    byte[] bytes = File.ReadAllBytes(match);
    if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(plaintextHash))
        throw new SignalImportException($"Media integrity check failed for stem {stem}.");

    var ext = Path.GetExtension(match);
    var fileName = att.FileName ?? $"signal-{stem[..12]}{ext}";
    return (match, fileName);
}
```

- [ ] **Step 4: Run test to verify it passes** — Run: `dotnet test tests/FlowHub.Skills.Tests --filter Resolve_ValidMedia_ReturnsPathAndVerifiesHash`. Expected: PASS.

- [ ] **Step 5: Add a mismatch test + commit**

```csharp
[Fact]
public void Resolve_MissingFile_Throws()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        var att = new SignalAttachmentRef("image/jpeg",
            Convert.ToBase64String(new byte[32]), Convert.ToBase64String(new byte[64]), 0, null);
        var act = () => SignalMediaResolver.Resolve(root, att);
        act.Should().Throw<SignalImportException>();
    }
    finally { Directory.Delete(root, recursive: true); }
}
```

```bash
git add source/FlowHub.Skills/Signal/SignalMediaResolver.cs tests/FlowHub.Skills.Tests/Signal/SignalMediaResolverTests.cs
git commit -m "feat(signal): resolve plaintext backup media by key-material hash"
```

---

### Task 6: Grouping pass

**Files:**
- Create: `source/FlowHub.Skills/Signal/SignalGrouping.cs`
- Test: `tests/FlowHub.Skills.Tests/Signal/SignalGroupingTests.cs`

**Interfaces:**
- Consumes: `SignalMessage` (Task 4).
- Produces: `SignalGrouping.Assign(IReadOnlyList<SignalMessage> ordered, int windowSeconds = 120) : IReadOnlyList<Guid?>` — index-aligned group ids; `null` where ungrouped.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Assign_AttachmentWithinWindowOfText_SharesGroup_TextTextNeverFused()
{
    var t0 = 1_000_000L;
    var msgs = new List<SignalMessage>
    {
        new(t0,              "note A", null),                                   // 0
        new(t0 + 30_000,     null, new SignalAttachmentRef("image/jpeg","","",1,null)), // 1 within 120s of 0
        new(t0 + 5_000_000,  "note B", null),                                   // 2 far away
        new(t0 + 5_010_000,  "note C", null),                                   // 3 close to B but text+text
    };

    var groups = SignalGrouping.Assign(msgs);

    groups[0].Should().NotBeNull();
    groups[1].Should().Be(groups[0]);       // attachment joined the text
    groups[2].Should().BeNull();            // lone text
    groups[3].Should().BeNull();            // text+text never fused
}
```

- [ ] **Step 2: Run test to verify it fails** — Run: `dotnet test tests/FlowHub.Skills.Tests --filter Assign_AttachmentWithinWindowOfText_SharesGroup_TextTextNeverFused`. Expected: FAIL.

- [ ] **Step 3: Implement grouping** — each attachment-only message binds to the nearest adjacent text within the window:

```csharp
public static IReadOnlyList<Guid?> Assign(IReadOnlyList<SignalMessage> ordered, int windowSeconds = 120)
{
    var groups = new Guid?[ordered.Count];
    long windowMs = windowSeconds * 1000L;

    for (int i = 0; i < ordered.Count; i++)
    {
        if (ordered[i].Attachment is null || ordered[i].Text is not null) continue; // only attachment-only rows anchor
        int? partner = NearestAdjacentText(ordered, i, windowMs);
        if (partner is null) continue;

        var gid = groups[partner.Value] ?? Guid.NewGuid();
        groups[partner.Value] = gid;
        groups[i] = gid;
    }
    return groups;
}

private static int? NearestAdjacentText(IReadOnlyList<SignalMessage> m, int i, long windowMs)
{
    int? best = null; long bestGap = long.MaxValue;
    foreach (var j in new[] { i - 1, i + 1 })
    {
        if (j < 0 || j >= m.Count || m[j].Text is null) continue;
        long gap = Math.Abs(m[j].DateSentMs - m[i].DateSentMs);
        if (gap <= windowMs && gap < bestGap) { best = j; bestGap = gap; }
    }
    return best;
}
```

- [ ] **Step 4: Run test to verify it passes** — Run: `dotnet test tests/FlowHub.Skills.Tests --filter Assign_AttachmentWithinWindowOfText_SharesGroup_TextTextNeverFused`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add source/FlowHub.Skills/Signal/SignalGrouping.cs tests/FlowHub.Skills.Tests/Signal/SignalGroupingTests.cs
git commit -m "feat(signal): time-proximity grouping of attachments with adjacent notes"
```

---

### Task 7: Import orchestrator + `SignalImportResult`

**Files:**
- Create: `source/FlowHub.Skills/Signal/SignalImportResult.cs`
- Create: `source/FlowHub.Skills/Signal/SignalImportService.cs`
- Create: `source/FlowHub.Skills/Signal/ISignalImportService.cs`
- Modify: `source/FlowHub.Skills/SkillsServiceCollectionExtensions.cs` (DI registration)
- Test: `tests/FlowHub.Skills.Tests/Signal/SignalImportServiceTests.cs`

**Interfaces:**
- Consumes: `SignalBackupParser`, `SignalMediaResolver`, `SignalGrouping`, `ICaptureService.ImportAsync`, `ICaptureRepository.ExistsByExternalRefAsync`.
- Produces:
  - `SignalImportResult(int Imported, int SkippedDuplicates, int SkippedNonStandard, int AttachmentsImported, int Groups, int Failed, IReadOnlyList<string> Errors)`
  - `ISignalImportService.ImportAsync(Stream zipStream, CancellationToken ct) : Task<SignalImportResult>`

- [ ] **Step 1: Write the failing test** — a zip with 2 text messages imports 2 captures; a re-run skips both (idempotent). Build the zip in-memory from the Task-4 fixture lines:

```csharp
[Fact]
public async Task ImportAsync_TwiceSameZip_SecondRunSkipsAllAsDuplicates()
{
    var seen = new HashSet<string>();
    var repo = Substitute.For<ICaptureRepository>();
    repo.ExistsByExternalRefAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
        .Returns(ci => seen.Contains(ci.Arg<string>()));
    var captures = Substitute.For<ICaptureService>();
    captures.ImportAsync(Arg.Any<string?>(), Arg.Any<AttachmentInput?>(), Arg.Any<ChannelKind>(),
        Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
        .Returns(ci => { seen.Add(ci.ArgAt<string>(4)); return Task.FromResult<Capture>(null!); });

    var sut = new SignalImportService(repo, captures, NullLogger<SignalImportService>.Instance);

    var zip = BuildZip(TwoTextMessagesJsonl());        // helper: writes main.jsonl into a MemoryStream zip
    var first = await sut.ImportAsync(zip, CancellationToken.None);
    first.Imported.Should().Be(2);
    first.SkippedDuplicates.Should().Be(0);

    zip.Position = 0;
    var second = await sut.ImportAsync(RewindOrRebuild(zip), CancellationToken.None);
    second.Imported.Should().Be(0);
    second.SkippedDuplicates.Should().Be(2);
}
```

*(Provide `BuildZip`/`TwoTextMessagesJsonl` test helpers in the test file — a `ZipArchive` over a `MemoryStream` with a single `main.jsonl` entry containing the header + self recipient + chat + two text chatItems from Task 4's fixture. Rebuild the stream for the second call rather than relying on re-reading a consumed stream.)*

- [ ] **Step 2: Run test to verify it fails** — Run: `dotnet test tests/FlowHub.Skills.Tests --filter ImportAsync_TwiceSameZip_SecondRunSkipsAllAsDuplicates`. Expected: FAIL.

- [ ] **Step 3: Implement the orchestrator**

```csharp
public async Task<SignalImportResult> ImportAsync(Stream zipStream, CancellationToken ct)
{
    var tempRoot = Directory.CreateTempSubdirectory("signal-import-").FullName;
    try
    {
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true))
            archive.ExtractToDirectory(tempRoot, overwriteFiles: true);

        var (backupRoot, jsonlPath) = LocateBackup(tempRoot); // find the dir containing main.jsonl
        (string chatId, IReadOnlyList<SignalMessage> messages) parsed;
        using (var fs = File.OpenRead(jsonlPath))
            parsed = SignalBackupParser.ParseNoteToSelf(fs);

        var groups = SignalGrouping.Assign(parsed.messages);
        int imported = 0, dup = 0, attachments = 0, failed = 0;
        var errors = new List<string>();
        var distinctGroups = new HashSet<Guid>();

        for (int i = 0; i < parsed.messages.Count; i++)
        {
            var m = parsed.messages[i];
            var externalRef = $"signal:{parsed.chatId}:{m.DateSentMs}";
            try
            {
                if (await _repository.ExistsByExternalRefAsync(externalRef, ct)) { dup++; continue; }

                AttachmentInput? input = null;
                if (m.Attachment is not null)
                {
                    var (path, name) = SignalMediaResolver.Resolve(backupRoot, m.Attachment);
                    input = new AttachmentInput
                    {
                        Content = File.OpenRead(path), FileName = name,
                        ContentType = m.Attachment.ContentType, SizeBytes = m.Attachment.Size,
                    };
                    attachments++;
                }

                var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(m.DateSentMs);
                if (groups[i] is Guid g) distinctGroups.Add(g);
                using (input?.Content as IDisposable)
                    await _captures.ImportAsync(m.Text, input, ChannelKind.Signal, createdAt, externalRef, groups[i], ct);
                imported++;
            }
            catch (SignalImportException ex) { failed++; errors.Add($"{externalRef}: {ex.Message}"); }
            catch (IOException ex)           { failed++; errors.Add($"{externalRef}: {ex.Message}"); }
        }

        return new SignalImportResult(imported, dup, SkippedNonStandard: 0, attachments,
            distinctGroups.Count, failed, errors);
    }
    finally { Directory.Delete(tempRoot, recursive: true); }
}
```

Notes for the implementer: `LocateBackup` searches `tempRoot` recursively for `main.jsonl` and returns its directory (the export nests everything under one folder). `SkippedNonStandard` is currently 0 because the parser silently drops non-standard items; if you want that count surfaced, have the parser also return the dropped count and thread it through — optional, not required for AC. Register `ISignalImportService → SignalImportService` in `SkillsServiceCollectionExtensions`.

- [ ] **Step 4: Run test to verify it passes** — Run: `dotnet test tests/FlowHub.Skills.Tests --filter ImportAsync_TwiceSameZip_SecondRunSkipsAllAsDuplicates`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add source/FlowHub.Skills/Signal source/FlowHub.Skills/SkillsServiceCollectionExtensions.cs tests/FlowHub.Skills.Tests/Signal/SignalImportServiceTests.cs
git commit -m "feat(signal): import orchestrator with idempotency and grouping"
```

---

### Task 8: Admin endpoint `POST /api/v1/admin/imports/signal`

**Files:**
- Modify: `source/FlowHub.Api/Endpoints/AdminEndpoints.cs`
- Test: `tests/FlowHub.Api.IntegrationTests/Admin/SignalImportEndpointTests.cs`

**Interfaces:**
- Consumes: `ISignalImportService` (Task 7), the `/api/v1/admin` group with `"Admin"` policy.
- Produces: route `POST /api/v1/admin/imports/signal` returning `Ok<SignalImportResult>` or `ProblemHttpResult`.

- [ ] **Step 1: Write the failing integration test** (multipart `.zip`; admin role granted via host setting, mirroring `AdminEndpointsAuthTests`):

```csharp
[Fact]
public async Task ImportSignal_ValidZip_ReturnsCountsAndCreatesCaptures()
{
    using var factory = _factory.WithWebHostBuilder(b =>
        b.UseSetting("Demo:Auth:Roles", "Operator,Admin"));
    var client = factory.CreateClient();

    using var content = new MultipartFormDataContent();
    var zipBytes = SignalZipFixture.TwoTextMessages(); // shared helper producing a valid export zip
    var file = new ByteArrayContent(zipBytes);
    file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
    content.Add(file, "file", "signal-export.zip");

    var resp = await client.PostAsync("/api/v1/admin/imports/signal", content);

    resp.StatusCode.Should().Be(HttpStatusCode.OK);
    var result = await resp.Content.ReadFromJsonAsync<SignalImportResult>(JsonOpts);
    result!.Imported.Should().Be(2);
}

[Fact]
public async Task ImportSignal_OperatorOnly_Returns403()
{
    var client = _factory.CreateClient(); // default operator, no Admin
    using var content = new MultipartFormDataContent();
    content.Add(new ByteArrayContent(SignalZipFixture.TwoTextMessages()), "file", "x.zip");
    var resp = await client.PostAsync("/api/v1/admin/imports/signal", content);
    resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
}
```

- [ ] **Step 2: Run test to verify it fails** — Run: `dotnet test tests/FlowHub.Api.IntegrationTests --filter SignalImport`. Expected: FAIL (404/route missing).

- [ ] **Step 3: Add the route** — in `AdminEndpoints.MapAdminEndpoints`, inside the existing `group`:

```csharp
group.MapPost("/imports/signal", ImportSignalAsync)
    .WithName("ImportSignal")
    .DisableAntiforgery()
    .Accepts<IFormFile>("multipart/form-data")
    .Produces<SignalImportResult>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest);
```

and the handler:

```csharp
private static async Task<Results<Ok<SignalImportResult>, ProblemHttpResult>> ImportSignalAsync(
    IFormFile? file, ISignalImportService importer, CancellationToken ct)
{
    if (file is null || file.Length == 0)
        return TypedResults.Problem("No file uploaded.", statusCode: StatusCodes.Status400BadRequest,
            type: ProblemTypes.Validation);
    try
    {
        await using var stream = file.OpenReadStream();
        var result = await importer.ImportAsync(stream, ct);
        return TypedResults.Ok(result);
    }
    catch (SignalImportException ex)
    {
        return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest,
            type: ProblemTypes.Validation);
    }
}
```

*(Confirm `ProblemTypes.Validation` exists and the `Results<...>` pattern against the `RebuildEmbeddings` handler in the same file; match its style exactly.)*

- [ ] **Step 4: Run test to verify it passes** — Run: `dotnet test tests/FlowHub.Api.IntegrationTests --filter SignalImport`. Expected: PASS.

- [ ] **Step 5: Add a Bruno request + commit** — add `bruno/admin/import-signal.bru` (multipart POST to `{{baseUrl}}/api/v1/admin/imports/signal`), mirroring `bruno/admin/rebuild-embeddings.bru`.

```bash
git add source/FlowHub.Api/Endpoints/AdminEndpoints.cs tests/FlowHub.Api.IntegrationTests/Admin/SignalImportEndpointTests.cs bruno/admin/import-signal.bru
git commit -m "feat(api): admin endpoint to import a Signal Note-to-Self export zip"
```

---

### Task 9: Blazor admin upload page

**Files:**
- Create: `source/FlowHub.Web/Components/Pages/SignalImport.razor`
- Create: `source/FlowHub.Web/Components/Pages/SignalImport.razor.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Pages/SignalImportTests.cs`

**Interfaces:**
- Consumes: `ISignalImportService` (via DI, in-process — matches `QuickCaptureField` calling services directly).
- Produces: a page at `@page "/admin/signal-import"`.

- [ ] **Step 1: Write the failing bUnit test** — after a successful import the result counts render:

```csharp
[Fact]
public void SignalImport_AfterImport_ShowsImportedCount()
{
    using var ctx = new TestContext();
    ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    ctx.Services.AddMudServices();
    var importer = Substitute.For<ISignalImportService>();
    importer.ImportAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
        .Returns(new SignalImportResult(221, 0, 6, 36, 12, 0, Array.Empty<string>()));
    ctx.Services.AddSingleton(importer);
    ctx.RenderComponent<MudPopoverProvider>();

    var cut = ctx.RenderComponent<SignalImport>();
    cut.Instance.GetType(); // ensure it compiles/renders

    // Simulate the component's result state directly (upload UI drives OnUpload → Result):
    cut.InvokeAsync(() => cut.Instance.ShowResultForTest(
        new SignalImportResult(221, 0, 6, 36, 12, 0, Array.Empty<string>())));
    cut.Markup.Should().Contain("221");
}
```

*(If you prefer not to expose a test hook, drive the `MudFileUpload` `OnFilesChanged` instead; either is acceptable as long as the assertion exercises real rendering of the result. Follow `NewCaptureUploadTests` for the MudBlazor test setup.)*

- [ ] **Step 2: Run test to verify it fails** — Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter SignalImport_AfterImport_ShowsImportedCount`. Expected: FAIL.

- [ ] **Step 3: Implement the page** — `SignalImport.razor` (shell) with a `MudFileUpload` accepting `.zip`, an import button, and a results panel bound to `Result`; `SignalImport.razor.cs` code-behind injects `ISignalImportService`, reads the selected file to a stream, calls `ImportAsync`, stores `Result`, and shows a `MudProgressCircular` while running. Keep no business logic in `.razor`. Render counts (Imported / SkippedDuplicates / SkippedNonStandard / AttachmentsImported / Groups / Failed) and any `Errors` in a `MudSimpleTable`/cards.

- [ ] **Step 4: Run test to verify it passes** — Run: `dotnet test tests/FlowHub.Web.ComponentTests --filter SignalImport_AfterImport_ShowsImportedCount`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add source/FlowHub.Web/Components/Pages/SignalImport.razor source/FlowHub.Web/Components/Pages/SignalImport.razor.cs tests/FlowHub.Web.ComponentTests/Pages/SignalImportTests.cs
git commit -m "feat(web): admin page to upload and import a Signal export"
```

---

### Task 10: Full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Build the solution** — Run: `dotnet build FlowHub.slnx`. Expected: succeeds with **no warnings** (warnings are errors).

- [ ] **Step 2: Run the full test suite** — Run: `dotnet test FlowHub.slnx`. Expected: all tests pass, including the new parser, media-resolver, grouping, repository (Testcontainers), service, API integration (import + idempotent re-import + admin auth), and bUnit page tests.

- [ ] **Step 3: Apply the migration locally** — Run: `dotnet ef database update --project source/FlowHub.Persistence --startup-project source/FlowHub.Web`. Expected: `0014_AddImportGroupId` applies cleanly.

- [ ] **Step 4: Manual smoke (optional but recommended)** — start the app (`just run`), open `/admin/signal-import` as an Admin principal, upload the real export `.zip`, and confirm ~221 text/URL + ~36 attachment captures appear with original timestamps, Signal source, grouped where a screenshot trails a note, and **no** notification storm.

- [ ] **Step 5: Commit any fixups** — if the full run surfaced issues, fix and commit per the TDD loop (failing test → fix → pass). Do not weaken tests to go green.

---

## Notes for the implementer

- **Never** hardcode the Note-to-Self chat id (`20` is specific to this export) — always derive it from the `self` recipient → chat mapping.
- The parser must tolerate frames it doesn't recognize (future Signal fields) — only read the properties named here, ignore the rest.
- If any exact type name / constructor argument order in this plan disagrees with the real source (record fields, DI constructor params, `IAttachmentStorage`/`ProblemTypes` members), the **real source wins** — adjust the call and keep the test's intent.
- If you hit a blocker, solve it and note the fix here so the next run doesn't re-derive it.
