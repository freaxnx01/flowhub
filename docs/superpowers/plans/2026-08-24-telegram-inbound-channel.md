# Telegram Inbound Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Telegram bot that turns the operator's messages into Captures through the existing pipeline and marks each message in-chat with the outcome.

**Architecture:** `FlowHub.Telegram` is a class library hosted in-process by `FlowHub.Web` as an `IHostedService`. A `BackgroundService` long-polls `getUpdates`, filters by an allow-list, dedupes on `update_id`, and calls `ICaptureService.SubmitAsync` directly through DI. Acknowledgement rides an `ICaptureService` decorator that fires `setMessageReaction` when the lifecycle resolves.

**Tech Stack:** .NET 10 · `Telegram.Bot` · EF Core + PostgreSQL · xUnit + FluentAssertions + NSubstitute

**Spec:** [`docs/superpowers/specs/2026-08-24-telegram-inbound-channel-design.md`](../specs/2026-08-24-telegram-inbound-channel-design.md)

## Global Constraints

- `TargetFramework` is `net10.0`; `Nullable` is `enable`; **`TreatWarningsAsErrors` is `true`** — a warning fails the build.
- `GenerateDocumentationFile` is on. Public types need XML doc comments or the build breaks on the repo's analyzer settings.
- Central Package Management: **never** put a `Version=` on a `PackageReference`. Versions go in `Directory.Packages.props` only.
- Test projects are named `<Project>.Tests` (e.g. `FlowHub.Skills.Tests`) — **not** `.UnitTests`. The spec's prose says `FlowHub.Telegram.UnitTests`; the repo convention wins, so this plan uses `tests/FlowHub.Telegram.Tests`.
- Entities and their configurations are `internal sealed`. Repositories are `internal sealed`. Ports are `public` and live in `FlowHub.Core`.
- `AsNoTracking()` on every read-only EF query.
- Never log the bot token or the raw message body of a rejected sender.
- Secrets come from environment variables only — never `appsettings*.json`.
- Every commit message follows Conventional Commits and ends with `Refs #20`.

## Known deviation from the spec (decided at plan time)

The spec's D3 says a photo caption becomes `Capture.Content`. It cannot, without a Core change: `EfCaptureService.SubmitAsync` **overwrites** `Content` with the attachment's filename when an attachment is present — `tests/FlowHub.Persistence.Tests/EfCaptureServiceAttachmentTests.cs:26` asserts this, passing `content: "ignored typed text"` and expecting `"invoice.pdf"`.

**Decision:** v1 matches the existing Web-upload behaviour — the caption is not persisted. Changing `SubmitAsync`'s semantics would alter the Web Channel too, which is outside this issue. Task 4 logs the dropped caption at debug level so the loss is visible, and the follow-up is noted in Task 7.

---

### Task 1: Project scaffold, options, and allow-list

**Files:**
- Create: `source/FlowHub.Telegram/FlowHub.Telegram.csproj`
- Create: `source/FlowHub.Telegram/TelegramOptions.cs`
- Create: `tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj`
- Create: `tests/FlowHub.Telegram.Tests/Usings.cs`
- Create: `tests/FlowHub.Telegram.Tests/TelegramOptionsTests.cs`
- Modify: `Directory.Packages.props`
- Modify: `FlowHub.slnx`

**Interfaces:**
- Consumes: nothing.
- Produces: `TelegramOptions` with `const string SectionName = "Telegram"`, `string? BotToken`, `IReadOnlyList<long> AllowedUserIds`, `bool IsConfigured`, `bool IsAllowed(long userId)`.

- [ ] **Step 1: Add the package version**

In `Directory.Packages.props`, inside the existing `<ItemGroup>`, in alphabetical position:

```xml
<PackageVersion Include="Telegram.Bot" Version="22.6.0" />
```

- [ ] **Step 2: Create the library project file**

`source/FlowHub.Telegram/FlowHub.Telegram.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>FlowHub.Telegram</RootNamespace>
    <AssemblyName>FlowHub.Telegram</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Telegram.Bot" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FlowHub.Core\FlowHub.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create the test project file**

`tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>FlowHub.Telegram.Tests</RootNamespace>
    <AssemblyName>FlowHub.Telegram.Tests</AssemblyName>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <NoWarn>$(NoWarn);CA1707;CA1861</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <PackageReference Include="Microsoft.Extensions.Options" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\source\FlowHub.Core\FlowHub.Core.csproj" />
    <ProjectReference Include="..\..\source\FlowHub.Telegram\FlowHub.Telegram.csproj" />
  </ItemGroup>

</Project>
```

`tests/FlowHub.Telegram.Tests/Usings.cs`:

```csharp
global using FluentAssertions;
global using NSubstitute;
global using Xunit;
```

- [ ] **Step 4: Register both projects in the solution**

In `FlowHub.slnx`, add to the `/source/` folder (alphabetical, after `FlowHub.Skills`):

```xml
    <Project Path="source/FlowHub.Telegram/FlowHub.Telegram.csproj" />
```

and to the `/tests/` folder (after `FlowHub.Skills.Tests`):

```xml
    <Project Path="tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj" />
```

- [ ] **Step 5: Write the failing test**

`tests/FlowHub.Telegram.Tests/TelegramOptionsTests.cs`:

```csharp
namespace FlowHub.Telegram.Tests;

public class TelegramOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("123:ABC", true)]
    public void IsConfigured_RequiresBotTokenAndAtLeastOneAllowedUser(string? token, bool expected)
    {
        var options = new TelegramOptions { BotToken = token, AllowedUserIds = [42L] };

        options.IsConfigured.Should().Be(expected);
    }

    [Fact]
    public void IsConfigured_WithTokenButNoAllowedUsers_IsFalse()
    {
        var options = new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [] };

        options.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_UserOnTheList_IsTrue()
    {
        var options = new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [42L, 43L] };

        options.IsAllowed(43L).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_UserNotOnTheList_IsFalse()
    {
        var options = new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [42L] };

        options.IsAllowed(99L).Should().BeFalse();
    }
}
```

- [ ] **Step 6: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj`
Expected: FAIL — `TelegramOptions` does not exist.

- [ ] **Step 7: Write the minimal implementation**

`source/FlowHub.Telegram/TelegramOptions.cs`:

```csharp
namespace FlowHub.Telegram;

/// <summary>
/// Configuration for the Telegram inbound Channel. Inactive unless both a bot token
/// and at least one allowed user id are present, so an unconfigured FlowHub never
/// contacts Telegram. Both values are secrets-adjacent — supply via environment
/// variables (<c>Telegram__BotToken</c>, <c>Telegram__AllowedUserIds</c>), never
/// appsettings.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Telegram";

    /// <summary>BotFather token. Secret.</summary>
    public string? BotToken { get; set; }

    /// <summary>Numeric Telegram user ids permitted to submit Captures.</summary>
    public IReadOnlyList<long> AllowedUserIds { get; set; } = [];

    /// <summary>True when the Channel has everything it needs to start.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BotToken) && AllowedUserIds.Count > 0;

    /// <summary>True when <paramref name="userId"/> may submit Captures.</summary>
    public bool IsAllowed(long userId) => AllowedUserIds.Contains(userId);
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj`
Expected: PASS (6 tests).

- [ ] **Step 9: Commit**

```bash
git add Directory.Packages.props FlowHub.slnx source/FlowHub.Telegram tests/FlowHub.Telegram.Tests
git commit -m "feat(telegram): scaffold FlowHub.Telegram with options and allow-list

Refs #20"
```

---

### Task 2: Persist processed updates

**Files:**
- Create: `source/FlowHub.Core/Channels/ITelegramUpdateRepository.cs`
- Create: `source/FlowHub.Core/Channels/TelegramUpdate.cs`
- Create: `source/FlowHub.Persistence/Entities/TelegramUpdateEntity.cs`
- Create: `source/FlowHub.Persistence/Entities/TelegramUpdateEntityTypeConfiguration.cs`
- Create: `source/FlowHub.Persistence/Repositories/EfTelegramUpdateRepository.cs`
- Modify: `source/FlowHub.Persistence/FlowHubDbContext.cs`
- Modify: `source/FlowHub.Persistence/PersistenceServiceCollectionExtensions.cs`
- Test: `tests/FlowHub.Persistence.Tests/Repositories/EfTelegramUpdateRepositoryTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `TelegramUpdate(long UpdateId, long ChatId, int MessageId, Guid? CaptureId, DateTimeOffset ProcessedAt)` and `ITelegramUpdateRepository` with `Task<bool> ExistsAsync(long updateId, CancellationToken)`, `Task RecordAsync(TelegramUpdate update, CancellationToken)`, `Task<TelegramUpdate?> FindByCaptureIdAsync(Guid captureId, CancellationToken)`, `Task<long?> GetLastProcessedUpdateIdAsync(CancellationToken)`.

- [ ] **Step 1: Write the domain record and the port**

`source/FlowHub.Core/Channels/TelegramUpdate.cs`:

```csharp
namespace FlowHub.Core.Channels;

/// <summary>
/// A Telegram update FlowHub has already processed. Serves two purposes: the
/// <paramref name="UpdateId"/> is the idempotency key that makes a redelivered
/// update harmless, and the chat/message pair are the coordinates needed to react
/// to the original message once its Capture reaches a terminal stage.
/// </summary>
/// <param name="UpdateId">Telegram's update id — the dedup key.</param>
/// <param name="ChatId">Chat the message arrived in.</param>
/// <param name="MessageId">Message to react to.</param>
/// <param name="CaptureId">The Capture created, or null when the update was rejected or unsupported.</param>
/// <param name="ProcessedAt">When FlowHub finished handling it.</param>
public sealed record TelegramUpdate(
    long UpdateId,
    long ChatId,
    int MessageId,
    Guid? CaptureId,
    DateTimeOffset ProcessedAt);
```

`source/FlowHub.Core/Channels/ITelegramUpdateRepository.cs`:

```csharp
namespace FlowHub.Core.Channels;

/// <summary>Driven port for the Telegram Channel's own idempotency state.</summary>
public interface ITelegramUpdateRepository
{
    /// <summary>True when this update has already been processed.</summary>
    Task<bool> ExistsAsync(long updateId, CancellationToken cancellationToken = default);

    /// <summary>Records an update as processed. Idempotent — a duplicate id is ignored.</summary>
    Task RecordAsync(TelegramUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Finds the update that produced a Capture, or null.</summary>
    Task<TelegramUpdate?> FindByCaptureIdAsync(Guid captureId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recently processed update id, by <see cref="TelegramUpdate.ProcessedAt"/> —
    /// deliberately NOT <c>MAX(UpdateId)</c>. After a week of inactivity Telegram picks the
    /// next update id at random rather than sequentially, so the maximum is not a safe
    /// high-water mark.
    /// </summary>
    Task<long?> GetLastProcessedUpdateIdAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Write the failing repository test**

`tests/FlowHub.Persistence.Tests/Repositories/EfTelegramUpdateRepositoryTests.cs`. The fixture creates a fresh migrated database per test, so tests never share state:

```csharp
using FlowHub.Core.Channels;
using FlowHub.Persistence.Repositories;
using FlowHub.Persistence.Tests.Fixtures;

namespace FlowHub.Persistence.Tests.Repositories;

[Collection(PostgresGroup.Name)]
public sealed class EfTelegramUpdateRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ExistsAsync_AfterRecord_IsTrue()
    {
        var db = await fixture.CreateFreshDbAsync();
        var sut = new EfTelegramUpdateRepository(db);

        await sut.RecordAsync(new TelegramUpdate(1001L, 55L, 7, Guid.NewGuid(), DateTimeOffset.UtcNow));

        (await sut.ExistsAsync(1001L)).Should().BeTrue();
        (await sut.ExistsAsync(1002L)).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_SameUpdateTwice_DoesNotThrow()
    {
        var db = await fixture.CreateFreshDbAsync();
        var sut = new EfTelegramUpdateRepository(db);
        var update = new TelegramUpdate(2001L, 55L, 7, null, DateTimeOffset.UtcNow);

        await sut.RecordAsync(update);
        var act = async () => await sut.RecordAsync(update);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FindByCaptureIdAsync_ReturnsCoordinates()
    {
        var db = await fixture.CreateFreshDbAsync();
        var sut = new EfTelegramUpdateRepository(db);
        var captureId = Guid.NewGuid();
        await sut.RecordAsync(new TelegramUpdate(3001L, 88L, 9, captureId, DateTimeOffset.UtcNow));

        var found = await sut.FindByCaptureIdAsync(captureId);

        found.Should().NotBeNull();
        found!.ChatId.Should().Be(88L);
        found.MessageId.Should().Be(9);
    }

    [Fact]
    public async Task GetLastProcessedUpdateIdAsync_UsesProcessedAt_NotMaxUpdateId()
    {
        var db = await fixture.CreateFreshDbAsync();
        var sut = new EfTelegramUpdateRepository(db);
        var early = DateTimeOffset.UtcNow.AddMinutes(-5);
        await sut.RecordAsync(new TelegramUpdate(9999L, 1L, 1, null, early));
        await sut.RecordAsync(new TelegramUpdate(10L, 1L, 2, null, DateTimeOffset.UtcNow));

        // 10 was processed later than 9999 — the random-id-after-a-week case.
        (await sut.GetLastProcessedUpdateIdAsync()).Should().Be(10L);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Persistence.Tests/FlowHub.Persistence.Tests.csproj --filter EfTelegramUpdateRepositoryTests`
Expected: FAIL — `EfTelegramUpdateRepository` does not exist.

- [ ] **Step 4: Write the entity and its configuration**

`source/FlowHub.Persistence/Entities/TelegramUpdateEntity.cs`:

```csharp
namespace FlowHub.Persistence.Entities;

internal sealed class TelegramUpdateEntity
{
    public long UpdateId { get; set; }
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public Guid? CaptureId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
```

`source/FlowHub.Persistence/Entities/TelegramUpdateEntityTypeConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowHub.Persistence.Entities;

internal sealed class TelegramUpdateEntityTypeConfiguration : IEntityTypeConfiguration<TelegramUpdateEntity>
{
    public void Configure(EntityTypeBuilder<TelegramUpdateEntity> builder)
    {
        builder.ToTable("TelegramUpdates");
        builder.HasKey(t => t.UpdateId);
        builder.Property(t => t.UpdateId).ValueGeneratedNever();
        builder.HasIndex(t => t.CaptureId);
        builder.HasIndex(t => t.ProcessedAt);
    }
}
```

- [ ] **Step 5: Add the DbSet**

In `source/FlowHub.Persistence/FlowHubDbContext.cs`, after the `SkillRuns` line:

```csharp
    internal DbSet<TelegramUpdateEntity> TelegramUpdates => Set<TelegramUpdateEntity>();
```

`OnModelCreating` already calls `ApplyConfigurationsFromAssembly`, so the configuration is picked up automatically — do not register it by hand.

- [ ] **Step 6: Write the repository**

`source/FlowHub.Persistence/Repositories/EfTelegramUpdateRepository.cs`:

```csharp
using FlowHub.Core.Channels;
using FlowHub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlowHub.Persistence.Repositories;

internal sealed class EfTelegramUpdateRepository : ITelegramUpdateRepository
{
    private readonly FlowHubDbContext _db;

    public EfTelegramUpdateRepository(FlowHubDbContext db) => _db = db;

    public Task<bool> ExistsAsync(long updateId, CancellationToken cancellationToken = default) =>
        _db.TelegramUpdates.AsNoTracking().AnyAsync(t => t.UpdateId == updateId, cancellationToken);

    public async Task RecordAsync(TelegramUpdate update, CancellationToken cancellationToken = default)
    {
        if (await ExistsAsync(update.UpdateId, cancellationToken))
        {
            return;
        }

        _db.TelegramUpdates.Add(new TelegramUpdateEntity
        {
            UpdateId = update.UpdateId,
            ChatId = update.ChatId,
            MessageId = update.MessageId,
            CaptureId = update.CaptureId,
            ProcessedAt = update.ProcessedAt,
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TelegramUpdate?> FindByCaptureIdAsync(Guid captureId, CancellationToken cancellationToken = default)
    {
        var entity = await _db.TelegramUpdates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.CaptureId == captureId, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<long?> GetLastProcessedUpdateIdAsync(CancellationToken cancellationToken = default)
    {
        var entity = await _db.TelegramUpdates.AsNoTracking()
            .OrderByDescending(t => t.ProcessedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return entity?.UpdateId;
    }

    private static TelegramUpdate ToDomain(TelegramUpdateEntity e) => new(
        UpdateId: e.UpdateId,
        ChatId: e.ChatId,
        MessageId: e.MessageId,
        CaptureId: e.CaptureId,
        ProcessedAt: e.ProcessedAt);
}
```

- [ ] **Step 7: Register the repository**

In `source/FlowHub.Persistence/PersistenceServiceCollectionExtensions.cs`, alongside the other `AddScoped` repository registrations:

```csharp
        services.AddScoped<ITelegramUpdateRepository, EfTelegramUpdateRepository>();
```

- [ ] **Step 8: Generate the migration**

Run:

```bash
dotnet ef migrations add 0002_TelegramUpdates \
  --project source/FlowHub.Persistence \
  --startup-project source/FlowHub.Web
```

Expected: a new pair of files under `source/FlowHub.Persistence/Migrations/`. Open the generated `Up` method and confirm it creates only the `TelegramUpdates` table and its two indexes — if it contains anything else, the model has drifted; stop and report rather than editing the generated file.

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Persistence.Tests/FlowHub.Persistence.Tests.csproj --filter EfTelegramUpdateRepositoryTests`
Expected: PASS (4 tests). Docker must be running — this project uses Testcontainers.

- [ ] **Step 10: Commit**

```bash
git add source/FlowHub.Core/Channels source/FlowHub.Persistence tests/FlowHub.Persistence.Tests
git commit -m "feat(telegram): persist processed updates for dedup and reactions

Refs #20"
```

---

### Task 3: Handle a text update

**Files:**
- Create: `source/FlowHub.Telegram/ITelegramGateway.cs`
- Create: `source/FlowHub.Telegram/TelegramUpdateHandler.cs`
- Test: `tests/FlowHub.Telegram.Tests/TelegramUpdateHandlerTests.cs`

**Interfaces:**
- Consumes: `TelegramOptions` (Task 1), `ITelegramUpdateRepository` + `TelegramUpdate` (Task 2), `ICaptureService` (existing).
- Produces: `ITelegramGateway` with `Task SendTextAsync(long chatId, string text, CancellationToken)`, `Task SetReactionAsync(long chatId, int messageId, string emoji, CancellationToken)`, `Task<Stream?> DownloadFileAsync(string fileId, CancellationToken)`; and `TelegramUpdateHandler.HandleAsync(TelegramMessage message, CancellationToken)`.
- Produces: `TelegramMessage(long UpdateId, long ChatId, int MessageId, long FromUserId, string? Text, TelegramFile? File)` and `TelegramFile(string FileId, string FileName, string ContentType, long SizeBytes)`.

`ITelegramGateway` exists so tests never touch the network: `Telegram.Bot`'s own client is sealed-ish and awkward to substitute, and the handler only needs three calls. `TelegramMessage` is our own shape so the handler never depends on `Telegram.Bot` types — mapping happens in Task 6.

- [ ] **Step 1: Write the gateway port and the message shapes**

`source/FlowHub.Telegram/ITelegramGateway.cs`:

```csharp
namespace FlowHub.Telegram;

/// <summary>
/// The three Telegram operations FlowHub needs, behind a port so the handler can be
/// tested without a network. Implemented over Telegram.Bot in <c>TelegramGateway</c>.
/// </summary>
public interface ITelegramGateway
{
    /// <summary>Sends a plain-text reply into a chat.</summary>
    Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default);

    /// <summary>Sets the single bot reaction on a message, replacing any previous one.</summary>
    Task SetReactionAsync(long chatId, int messageId, string emoji, CancellationToken cancellationToken = default);

    /// <summary>Downloads a file by id, or null when it cannot be fetched.</summary>
    Task<Stream?> DownloadFileAsync(string fileId, CancellationToken cancellationToken = default);
}

/// <summary>A file attached to an inbound message.</summary>
/// <param name="FileId">Telegram file id, used for download.</param>
/// <param name="FileName">Best available name for the file.</param>
/// <param name="ContentType">MIME type as reported by Telegram, or inferred for photos.</param>
/// <param name="SizeBytes">Size reported by Telegram.</param>
public sealed record TelegramFile(string FileId, string FileName, string ContentType, long SizeBytes);

/// <summary>An inbound message, mapped off Telegram.Bot's types at the edge.</summary>
/// <param name="UpdateId">Telegram update id — the dedup key.</param>
/// <param name="ChatId">Chat the message arrived in.</param>
/// <param name="MessageId">The message itself, for reactions.</param>
/// <param name="FromUserId">Sender, checked against the allow-list.</param>
/// <param name="Text">Message text or caption, when present.</param>
/// <param name="File">Attached photo or document, when present.</param>
public sealed record TelegramMessage(
    long UpdateId,
    long ChatId,
    int MessageId,
    long FromUserId,
    string? Text,
    TelegramFile? File);
```

- [ ] **Step 2: Write the failing test**

`tests/FlowHub.Telegram.Tests/TelegramUpdateHandlerTests.cs`:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram.Tests;

public class TelegramUpdateHandlerTests
{
    private const long AllowedUser = 42L;

    private static TelegramMessage TextMessage(string text, long from = AllowedUser, long updateId = 1L) =>
        new(UpdateId: updateId, ChatId: 55L, MessageId: 7, FromUserId: from, Text: text, File: null);

    private static (TelegramUpdateHandler Sut, ICaptureService Captures, ITelegramUpdateRepository Repo, ITelegramGateway Gateway) Build()
    {
        var captures = Substitute.For<ICaptureService>();
        captures.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Capture(
                Guid.NewGuid(), ci.ArgAt<ChannelKind>(1), ci.ArgAt<string?>(0) ?? "", DateTimeOffset.UtcNow,
                LifecycleStage.Raw, null)));
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        var options = Options.Create(new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [AllowedUser] });
        var uploads = Substitute.For<IUploadPolicy>();
        uploads.MaxBytes.Returns(2L * 1024 * 1024);
        uploads.AllowedContentTypes.Returns(["application/pdf", "image/png", "image/jpeg"]);

        var sut = new TelegramUpdateHandler(captures, repo, gateway, uploads, options,
            NullLogger<TelegramUpdateHandler>.Instance);
        return (sut, captures, repo, gateway);
    }

    [Fact]
    public async Task HandleAsync_AllowedUserSendsText_SubmitsCaptureWithTelegramChannel()
    {
        var (sut, captures, _, _) = Build();

        await sut.HandleAsync(TextMessage("https://example.com/article"), CancellationToken.None);

        await captures.Received(1).SubmitAsync(
            "https://example.com/article", ChannelKind.Telegram, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AllowedUserSendsText_RecordsUpdateWithCaptureId()
    {
        var (sut, _, repo, _) = Build();

        await sut.HandleAsync(TextMessage("hello"), CancellationToken.None);

        await repo.Received(1).RecordAsync(
            Arg.Is<TelegramUpdate>(u => u.UpdateId == 1L && u.ChatId == 55L && u.MessageId == 7 && u.CaptureId != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DisallowedUser_SubmitsNothingButStillRecordsUpdate()
    {
        var (sut, captures, repo, gateway) = Build();

        await sut.HandleAsync(TextMessage("spam", from: 99L), CancellationToken.None);

        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
        await repo.Received(1).RecordAsync(
            Arg.Is<TelegramUpdate>(u => u.CaptureId == null), Arg.Any<CancellationToken>());
        await gateway.DidNotReceiveWithAnyArgs().SendTextAsync(default, default!, default);
    }

    [Fact]
    public async Task HandleAsync_AlreadyProcessedUpdate_DoesNothing()
    {
        var (sut, captures, repo, _) = Build();
        repo.ExistsAsync(1L, Arg.Any<CancellationToken>()).Returns(true);

        await sut.HandleAsync(TextMessage("replayed"), CancellationToken.None);

        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
        await repo.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_UnsupportedMessage_RepliesAndRecords()
    {
        var (sut, captures, repo, gateway) = Build();
        var voice = new TelegramMessage(2L, 55L, 8, AllowedUser, Text: null, File: null);

        await sut.HandleAsync(voice, CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L,
            Arg.Is<string>(s => s.Contains("not supported", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
        await repo.Received(1).RecordAsync(Arg.Any<TelegramUpdate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_SubmitThrows_DoesNotRecordUpdate()
    {
        var (sut, captures, repo, _) = Build();
        captures.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Capture>>(_ => throw new InvalidOperationException("db down"));

        var act = async () => await sut.HandleAsync(TextMessage("boom"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await repo.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj --filter TelegramUpdateHandlerTests`
Expected: FAIL — `TelegramUpdateHandler` does not exist.

- [ ] **Step 4: Write the handler (text path only)**

`source/FlowHub.Telegram/TelegramUpdateHandler.cs`:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram;

/// <summary>
/// Turns one inbound Telegram message into a Capture. Order is deliberate: submit
/// first, record second, so a crash replays the update rather than losing the
/// Capture — the recorded update id makes the replay harmless.
/// </summary>
public sealed class TelegramUpdateHandler
{
    private readonly ICaptureService _captures;
    private readonly ITelegramUpdateRepository _updates;
    private readonly ITelegramGateway _gateway;
    private readonly IUploadPolicy _uploads;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramUpdateHandler> _logger;

    public TelegramUpdateHandler(
        ICaptureService captures,
        ITelegramUpdateRepository updates,
        ITelegramGateway gateway,
        IUploadPolicy uploads,
        IOptions<TelegramOptions> options,
        ILogger<TelegramUpdateHandler> logger)
    {
        _captures = captures;
        _updates = updates;
        _gateway = gateway;
        _uploads = uploads;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Handles one message. Safe to call twice with the same update id.</summary>
    public async Task HandleAsync(TelegramMessage message, CancellationToken cancellationToken = default)
    {
        if (await _updates.ExistsAsync(message.UpdateId, cancellationToken))
        {
            _logger.LogDebug("Telegram update {UpdateId} already processed, skipping", message.UpdateId);
            return;
        }

        if (!_options.IsAllowed(message.FromUserId))
        {
            // Recorded but not answered: acking stops redelivery without confirming
            // the bot exists to a stranger. The body is deliberately not logged.
            _logger.LogWarning("Rejected Telegram update {UpdateId} from unlisted user {UserId}",
                message.UpdateId, message.FromUserId);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            await _gateway.SendTextAsync(message.ChatId,
                "That message type is not supported yet — send text, a photo, or a document.",
                cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        var capture = await _captures.SubmitAsync(message.Text, ChannelKind.Telegram, null, cancellationToken);
        await RecordAsync(message, capture.Id, cancellationToken);
    }

    private Task RecordAsync(TelegramMessage message, Guid? captureId, CancellationToken cancellationToken) =>
        _updates.RecordAsync(
            new TelegramUpdate(message.UpdateId, message.ChatId, message.MessageId, captureId, DateTimeOffset.UtcNow),
            cancellationToken);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj --filter TelegramUpdateHandlerTests`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add source/FlowHub.Telegram tests/FlowHub.Telegram.Tests
git commit -m "feat(telegram): ingest text messages as Captures

Refs #20"
```

---

### Task 4: Handle photos and documents

**Files:**
- Modify: `source/FlowHub.Telegram/TelegramUpdateHandler.cs`
- Test: `tests/FlowHub.Telegram.Tests/TelegramUpdateHandlerAttachmentTests.cs`

**Interfaces:**
- Consumes: everything from Task 3, plus `IUploadPolicy` (already injected) and `AttachmentInput` (existing).
- Produces: no new types.

**Note on captions:** `EfCaptureService.SubmitAsync` overwrites `Content` with the filename whenever an attachment is present, so the caption is not persisted. This matches the existing Web upload path. Log it at debug level; do not work around it here (see "Known deviation" above).

- [ ] **Step 1: Write the failing test**

`tests/FlowHub.Telegram.Tests/TelegramUpdateHandlerAttachmentTests.cs`:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowHub.Telegram.Tests;

public class TelegramUpdateHandlerAttachmentTests
{
    private const long AllowedUser = 42L;

    private static (TelegramUpdateHandler Sut, ICaptureService Captures, ITelegramGateway Gateway) Build(long maxBytes = 2L * 1024 * 1024)
    {
        var captures = Substitute.For<ICaptureService>();
        captures.SubmitAsync(Arg.Any<string?>(), Arg.Any<ChannelKind>(), Arg.Any<AttachmentInput?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Capture(
                Guid.NewGuid(), ChannelKind.Telegram, "file.pdf", DateTimeOffset.UtcNow, LifecycleStage.Raw, null)));
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        gateway.DownloadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(new MemoryStream(new byte[16])));
        var uploads = Substitute.For<IUploadPolicy>();
        uploads.MaxBytes.Returns(maxBytes);
        uploads.AllowedContentTypes.Returns(["application/pdf", "image/png", "image/jpeg"]);
        var options = Options.Create(new TelegramOptions { BotToken = "123:ABC", AllowedUserIds = [AllowedUser] });

        return (new TelegramUpdateHandler(captures, repo, gateway, uploads, options,
            NullLogger<TelegramUpdateHandler>.Instance), captures, gateway);
    }

    private static TelegramMessage FileMessage(string name, string contentType, long size, string? caption = null) =>
        new(UpdateId: 5L, ChatId: 55L, MessageId: 9, FromUserId: AllowedUser, Text: caption,
            File: new TelegramFile("file-abc", name, contentType, size));

    [Fact]
    public async Task HandleAsync_Document_SubmitsWithAttachmentInput()
    {
        var (sut, captures, _) = Build();

        await sut.HandleAsync(FileMessage("invoice.pdf", "application/pdf", 16), CancellationToken.None);

        await captures.Received(1).SubmitAsync(
            Arg.Any<string?>(), ChannelKind.Telegram,
            Arg.Is<AttachmentInput>(a => a.FileName == "invoice.pdf" && a.ContentType == "application/pdf" && a.SizeBytes == 16),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_FileTooLarge_RepliesWithTheLimitAndSubmitsNothing()
    {
        var (sut, captures, gateway) = Build(maxBytes: 10);

        await sut.HandleAsync(FileMessage("big.pdf", "application/pdf", 99), CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L, Arg.Is<string>(s => s.Contains("10", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_DisallowedContentType_RepliesAndSubmitsNothing()
    {
        var (sut, captures, gateway) = Build();

        await sut.HandleAsync(FileMessage("archive.zip", "application/zip", 16), CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_DownloadReturnsNull_SubmitsNothing()
    {
        var (sut, captures, gateway) = Build();
        gateway.DownloadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(null));

        await sut.HandleAsync(FileMessage("gone.pdf", "application/pdf", 16), CancellationToken.None);

        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj --filter TelegramUpdateHandlerAttachmentTests`
Expected: FAIL — files are currently answered with "not supported yet".

- [ ] **Step 3: Add the attachment branch**

In `TelegramUpdateHandler.HandleAsync`, replace the `string.IsNullOrWhiteSpace(message.Text)` block with:

```csharp
        if (message.File is not null)
        {
            await HandleFileAsync(message, message.File, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            await _gateway.SendTextAsync(message.ChatId,
                "That message type is not supported yet — send text, a photo, or a document.",
                cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }
```

and add these two members:

```csharp
    private async Task HandleFileAsync(TelegramMessage message, TelegramFile file, CancellationToken cancellationToken)
    {
        if (!IsAcceptable(file, out var rejection))
        {
            await _gateway.SendTextAsync(message.ChatId, rejection, cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        var content = await _gateway.DownloadFileAsync(file.FileId, cancellationToken);
        if (content is null)
        {
            await _gateway.SendTextAsync(message.ChatId,
                "That file could not be downloaded from Telegram — try sending it again.", cancellationToken);
            await RecordAsync(message, captureId: null, cancellationToken);
            return;
        }

        await using (content)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                // Captures with an attachment take the filename as Content; the caption
                // is not persisted. Same limitation as the Web upload path.
                _logger.LogDebug("Dropping caption on Telegram update {UpdateId}", message.UpdateId);
            }

            var input = new AttachmentInput
            {
                Content = content,
                FileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.SizeBytes,
            };

            var capture = await _captures.SubmitAsync(message.Text, ChannelKind.Telegram, input, cancellationToken);
            await RecordAsync(message, capture.Id, cancellationToken);
        }
    }

    private bool IsAcceptable(TelegramFile file, out string rejection)
    {
        if (file.SizeBytes > _uploads.MaxBytes)
        {
            rejection = $"That file is too large — the limit is {_uploads.MaxBytes} bytes.";
            return false;
        }

        if (!_uploads.AllowedContentTypes.Contains(file.ContentType))
        {
            rejection = $"{file.ContentType} is not an accepted file type — send a PDF, PNG, or JPEG.";
            return false;
        }

        rejection = "";
        return true;
    }
```

- [ ] **Step 4: Run the whole test project to verify nothing regressed**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj`
Expected: PASS (16 tests — 6 options, 6 handler, 4 attachment).

- [ ] **Step 5: Commit**

```bash
git add source/FlowHub.Telegram tests/FlowHub.Telegram.Tests
git commit -m "feat(telegram): ingest photos and documents as Capture attachments

Refs #20"
```

---

### Task 5: React when the lifecycle resolves

**Files:**
- Create: `source/FlowHub.Telegram/TelegramReactionService.cs`
- Create: `source/FlowHub.Telegram/TelegramReactionCaptureServiceDecorator.cs`
- Modify: `source/FlowHub.Telegram/TelegramUpdateHandler.cs`
- Test: `tests/FlowHub.Telegram.Tests/TelegramReactionTests.cs`

**Interfaces:**
- Consumes: `ITelegramGateway` (Task 3), `ITelegramUpdateRepository` (Task 2), `ICaptureService` (existing).
- Produces: `TelegramReactionService.ApplyAsync(Guid captureId, LifecycleStage stage, CancellationToken)` and `TelegramReactionService.EmojiFor(LifecycleStage stage)` returning `string?`; `TelegramReactionCaptureServiceDecorator : ICaptureService`.

**Why both a decorator and a handler call:** `EfCaptureService.SubmitAsync` publishes `CaptureCreated` itself (`EfCaptureService.cs:42,68`), so with the in-memory transport the pipeline can finish *before* the handler records the update row. The decorator covers the normal ordering; the handler re-checks after recording and covers the race. `ApplyAsync` is idempotent — `setMessageReaction` sets rather than appends, and a missing row is a no-op — so a double call is harmless.

- [ ] **Step 1: Write the failing test**

`tests/FlowHub.Telegram.Tests/TelegramReactionTests.cs`:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowHub.Telegram.Tests;

public class TelegramReactionTests
{
    [Theory]
    [InlineData(LifecycleStage.Completed, "👍")]
    [InlineData(LifecycleStage.Orphan, "💔")]
    [InlineData(LifecycleStage.Unhandled, "🤔")]
    public void EmojiFor_TerminalStages_MapToAllowListedEmoji(LifecycleStage stage, string expected)
    {
        TelegramReactionService.EmojiFor(stage).Should().Be(expected);
    }

    [Theory]
    [InlineData(LifecycleStage.Raw)]
    [InlineData(LifecycleStage.Classified)]
    [InlineData(LifecycleStage.Routed)]
    public void EmojiFor_NonTerminalStages_IsNull(LifecycleStage stage)
    {
        TelegramReactionService.EmojiFor(stage).Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_KnownCapture_SetsTheReaction()
    {
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        var captureId = Guid.NewGuid();
        repo.FindByCaptureIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(new TelegramUpdate(1L, 55L, 7, captureId, DateTimeOffset.UtcNow));
        var sut = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);

        await sut.ApplyAsync(captureId, LifecycleStage.Completed, CancellationToken.None);

        await gateway.Received(1).SetReactionAsync(55L, 7, "👍", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_NoMatchingUpdate_IsANoOp()
    {
        var repo = Substitute.For<ITelegramUpdateRepository>();
        repo.FindByCaptureIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TelegramUpdate?)null);
        var gateway = Substitute.For<ITelegramGateway>();
        var sut = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);

        var act = async () => await sut.ApplyAsync(Guid.NewGuid(), LifecycleStage.Completed, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await gateway.DidNotReceiveWithAnyArgs().SetReactionAsync(default, default, default!, default);
    }

    [Fact]
    public async Task ApplyAsync_GatewayThrows_DoesNotPropagate()
    {
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var captureId = Guid.NewGuid();
        repo.FindByCaptureIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(new TelegramUpdate(1L, 55L, 7, captureId, DateTimeOffset.UtcNow));
        var gateway = Substitute.For<ITelegramGateway>();
        gateway.SetReactionAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("telegram down"));
        var sut = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);

        var act = async () => await sut.ApplyAsync(captureId, LifecycleStage.Completed, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Decorator_MarkCompletedAsync_CallsInnerThenReacts()
    {
        var inner = Substitute.For<ICaptureService>();
        var repo = Substitute.For<ITelegramUpdateRepository>();
        var gateway = Substitute.For<ITelegramGateway>();
        var captureId = Guid.NewGuid();
        repo.FindByCaptureIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(new TelegramUpdate(1L, 55L, 7, captureId, DateTimeOffset.UtcNow));
        var reactions = new TelegramReactionService(repo, gateway, NullLogger<TelegramReactionService>.Instance);
        var sut = new TelegramReactionCaptureServiceDecorator(inner, reactions);

        await sut.MarkCompletedAsync(captureId, "wallabag-99", CancellationToken.None);

        await inner.Received(1).MarkCompletedAsync(captureId, "wallabag-99", Arg.Any<CancellationToken>());
        await gateway.Received(1).SetReactionAsync(55L, 7, "👍", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj --filter TelegramReactionTests`
Expected: FAIL — `TelegramReactionService` does not exist.

- [ ] **Step 3: Write the reaction service**

`source/FlowHub.Telegram/TelegramReactionService.cs`:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using Microsoft.Extensions.Logging;

namespace FlowHub.Telegram;

/// <summary>
/// Marks the operator's original Telegram message with the outcome of its Capture.
/// Telegram has no "mark as read" for bots, so this reaction is the only in-chat
/// signal that a message has been processed.
/// </summary>
public sealed class TelegramReactionService
{
    private readonly ITelegramUpdateRepository _updates;
    private readonly ITelegramGateway _gateway;
    private readonly ILogger<TelegramReactionService> _logger;

    public TelegramReactionService(
        ITelegramUpdateRepository updates,
        ITelegramGateway gateway,
        ILogger<TelegramReactionService> logger)
    {
        _updates = updates;
        _gateway = gateway;
        _logger = logger;
    }

    /// <summary>
    /// The emoji for a terminal stage, or null for a stage that is still in flight.
    /// Must come from ReactionTypeEmoji's fixed allow-list — ✅, ⚠️ and ❓ are NOT on it.
    /// </summary>
    public static string? EmojiFor(LifecycleStage stage) => stage switch
    {
        LifecycleStage.Completed => "👍",
        LifecycleStage.Orphan => "💔",
        LifecycleStage.Unhandled => "🤔",
        _ => null,
    };

    /// <summary>
    /// Applies the reaction for a resolved Capture. Idempotent and best-effort: an
    /// unknown Capture is a no-op, and a Telegram failure is logged, never thrown —
    /// a failed reaction must not fail the lifecycle transition that triggered it.
    /// </summary>
    public async Task ApplyAsync(Guid captureId, LifecycleStage stage, CancellationToken cancellationToken = default)
    {
        var emoji = EmojiFor(stage);
        if (emoji is null)
        {
            return;
        }

        try
        {
            var update = await _updates.FindByCaptureIdAsync(captureId, cancellationToken);
            if (update is null)
            {
                return;
            }

            await _gateway.SetReactionAsync(update.ChatId, update.MessageId, emoji, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not set Telegram reaction for capture {CaptureId}", captureId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timed out setting Telegram reaction for capture {CaptureId}", captureId);
        }
    }
}
```

- [ ] **Step 4: Write the decorator**

`source/FlowHub.Telegram/TelegramReactionCaptureServiceDecorator.cs`:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Classification;

namespace FlowHub.Telegram;

/// <summary>
/// Wraps <see cref="ICaptureService"/> so a Capture reaching a terminal stage marks its
/// originating Telegram message. Registered only when the Telegram Channel is
/// configured, so it is absent otherwise. Every non-terminal member delegates untouched.
/// </summary>
public sealed class TelegramReactionCaptureServiceDecorator : ICaptureService
{
    private readonly ICaptureService _inner;
    private readonly TelegramReactionService _reactions;

    public TelegramReactionCaptureServiceDecorator(ICaptureService inner, TelegramReactionService reactions)
    {
        _inner = inner;
        _reactions = reactions;
    }

    public Task<Capture?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<Capture>> GetAllAsync(CancellationToken cancellationToken = default) =>
        _inner.GetAllAsync(cancellationToken);

    public Task<IReadOnlyList<Capture>> GetRecentAsync(int count, CancellationToken cancellationToken = default) =>
        _inner.GetRecentAsync(count, cancellationToken);

    public Task<FailureCounts> GetFailureCountsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetFailureCountsAsync(cancellationToken);

    public Task<Capture> SubmitAsync(string content, ChannelKind source, CancellationToken cancellationToken = default) =>
        _inner.SubmitAsync(content, source, cancellationToken);

    public Task<Capture> SubmitAsync(string? content, ChannelKind source, AttachmentInput? attachment, CancellationToken cancellationToken = default) =>
        _inner.SubmitAsync(content, source, attachment, cancellationToken);

    public Task MarkClassifiedAsync(Guid id, string matchedSkill, string? title = null, string? vikunjaProject = null, string? enrichmentDescription = null, ClassifierTrace? trace = null, CancellationToken cancellationToken = default) =>
        _inner.MarkClassifiedAsync(id, matchedSkill, title, vikunjaProject, enrichmentDescription, trace, cancellationToken);

    public Task MarkRoutedAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.MarkRoutedAsync(id, cancellationToken);

    public async Task MarkCompletedAsync(Guid id, string? externalRef, CancellationToken cancellationToken = default)
    {
        await _inner.MarkCompletedAsync(id, externalRef, cancellationToken);
        await _reactions.ApplyAsync(id, LifecycleStage.Completed, cancellationToken);
    }

    public async Task MarkOrphanAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        await _inner.MarkOrphanAsync(id, reason, cancellationToken);
        await _reactions.ApplyAsync(id, LifecycleStage.Orphan, cancellationToken);
    }

    public async Task MarkUnhandledAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        await _inner.MarkUnhandledAsync(id, reason, cancellationToken);
        await _reactions.ApplyAsync(id, LifecycleStage.Unhandled, cancellationToken);
    }

    public Task<CapturePage> ListAsync(CaptureFilter filter, CancellationToken cancellationToken = default) =>
        _inner.ListAsync(filter, cancellationToken);

    public Task ResetForRetryAsync(Guid id, CancellationToken cancellationToken = default) =>
        _inner.ResetForRetryAsync(id, cancellationToken);
}
```

- [ ] **Step 5: Close the race in the handler**

Inject `TelegramReactionService` into `TelegramUpdateHandler` — add a `private readonly TelegramReactionService _reactions;` field, a constructor parameter after `_gateway`, and assign it. Then change the two places that submit a Capture to re-check the stage after recording. Replace the text-path tail:

```csharp
        var capture = await _captures.SubmitAsync(message.Text, ChannelKind.Telegram, null, cancellationToken);
        await RecordAsync(message, capture.Id, cancellationToken);
        await ReactIfAlreadyResolvedAsync(capture.Id, cancellationToken);
```

and the file-path tail (inside `HandleFileAsync`):

```csharp
            var capture = await _captures.SubmitAsync(message.Text, ChannelKind.Telegram, input, cancellationToken);
            await RecordAsync(message, capture.Id, cancellationToken);
            await ReactIfAlreadyResolvedAsync(capture.Id, cancellationToken);
```

and add:

```csharp
    /// <summary>
    /// The pipeline can resolve a Capture before the update row exists, in which case the
    /// decorator's reaction found nothing to react to. Re-check once the row is written.
    /// </summary>
    private async Task ReactIfAlreadyResolvedAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var current = await _captures.GetByIdAsync(captureId, cancellationToken);
        if (current is not null)
        {
            await _reactions.ApplyAsync(captureId, current.Stage, cancellationToken);
        }
    }
```

Update the two `Build()` helpers in `TelegramUpdateHandlerTests` and `TelegramUpdateHandlerAttachmentTests` to pass a `TelegramReactionService` built from the substituted `repo` and `gateway`.

- [ ] **Step 6: Run the whole test project**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj`
Expected: PASS (26 tests).

- [ ] **Step 7: Commit**

```bash
git add source/FlowHub.Telegram tests/FlowHub.Telegram.Tests
git commit -m "feat(telegram): react to messages when their Capture resolves

Refs #20"
```

---

### Task 6: Gateway, polling loop, and host wiring

**Files:**
- Create: `source/FlowHub.Telegram/TelegramGateway.cs`
- Create: `source/FlowHub.Telegram/TelegramPollingService.cs`
- Create: `source/FlowHub.Telegram/TelegramServiceCollectionExtensions.cs`
- Modify: `source/FlowHub.Web/FlowHub.Web.csproj`
- Modify: `source/FlowHub.Web/Program.cs`
- Modify: `.env.example`

**Interfaces:**
- Consumes: everything from Tasks 1–5.
- Produces: `IServiceCollection AddFlowHubTelegram(this IServiceCollection services, IConfiguration configuration)`.

The polling loop and the gateway are thin adapters over `Telegram.Bot`; their correctness is in the handler, which Tasks 3–5 already cover. Do not write unit tests that mock `Telegram.Bot` internals — assert the registration instead.

- [ ] **Step 1: Write the gateway**

`source/FlowHub.Telegram/TelegramGateway.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReactionTypes;

namespace FlowHub.Telegram;

/// <summary>Telegram.Bot-backed <see cref="ITelegramGateway"/>.</summary>
public sealed class TelegramGateway : ITelegramGateway
{
    private readonly ITelegramBotClient _client;
    private readonly ILogger<TelegramGateway> _logger;

    public TelegramGateway(ITelegramBotClient client, ILogger<TelegramGateway> logger)
    {
        _client = client;
        _logger = logger;
    }

    public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken = default) =>
        _client.SendMessage(chatId, text, cancellationToken: cancellationToken);

    public Task SetReactionAsync(long chatId, int messageId, string emoji, CancellationToken cancellationToken = default) =>
        _client.SetMessageReaction(
            chatId,
            messageId,
            [new ReactionTypeEmoji { Emoji = emoji }],
            cancellationToken: cancellationToken);

    public async Task<Stream?> DownloadFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var file = await _client.GetFile(fileId, cancellationToken);
            if (file.FilePath is null)
            {
                return null;
            }

            var buffer = new MemoryStream();
            await _client.DownloadFile(file.FilePath, buffer, cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not download Telegram file {FileId}", fileId);
            return null;
        }
    }
}
```

If any `Telegram.Bot` method name above does not resolve against the pinned version, correct it to the real signature and keep everything else identical — the shape of `ITelegramGateway` must not change.

- [ ] **Step 2: Write the polling service**

`source/FlowHub.Telegram/TelegramPollingService.cs`:

```csharp
using FlowHub.Core.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace FlowHub.Telegram;

/// <summary>
/// Long-polls getUpdates and feeds each message to <see cref="TelegramUpdateHandler"/>.
/// Outbound-only, so it needs no public ingress. The offset is restored from the last
/// processed update on start; failures back off instead of taking down the host.
/// </summary>
public sealed class TelegramPollingService : BackgroundService
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly ITelegramBotClient _client;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TelegramPollingService> _logger;

    public TelegramPollingService(
        ITelegramBotClient client,
        IServiceScopeFactory scopes,
        ILogger<TelegramPollingService> logger)
    {
        _client = client;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var offset = await RestoreOffsetAsync(stoppingToken);
        var backoff = MinBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _client.GetUpdates(
                    offset: offset,
                    timeout: 50,
                    allowedUpdates: [UpdateType.Message],
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    var message = TelegramMessageMapper.Map(update);
                    if (message is null)
                    {
                        continue;
                    }

                    using var scope = _scopes.CreateScope();
                    var handler = scope.ServiceProvider.GetRequiredService<TelegramUpdateHandler>();
                    await handler.HandleAsync(message, stoppingToken);
                }

                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 409)
            {
                // A webhook is registered, so getUpdates is refused for as long as it stands.
                // Backing off forever would look like silence; name the fix and stop.
                _logger.LogCritical(ex,
                    "Telegram refuses getUpdates because a webhook is registered. "
                    + "Call deleteWebhook for this bot, then restart FlowHub. Polling stopped.");
                return;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 401)
            {
                _logger.LogCritical(ex,
                    "Telegram rejected the bot token as unauthorized. Check Telegram__BotToken. Polling stopped.");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiRequestException or InvalidOperationException)
            {
                _logger.LogError(ex, "Telegram poll failed; retrying in {Backoff}", backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = backoff < MaxBackoff ? backoff * 2 : MaxBackoff;
            }
        }
    }

    private async Task<int> RestoreOffsetAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var updates = scope.ServiceProvider.GetRequiredService<ITelegramUpdateRepository>();
        var last = await updates.GetLastProcessedUpdateIdAsync(cancellationToken);
        return last is null ? 0 : (int)(last.Value + 1);
    }
}
```

Also create the mapper alongside it, in the same file's namespace — `source/FlowHub.Telegram/TelegramMessageMapper.cs`:

```csharp
using Telegram.Bot.Types;

namespace FlowHub.Telegram;

/// <summary>Maps Telegram.Bot's Update onto FlowHub's own <see cref="TelegramMessage"/>.</summary>
internal static class TelegramMessageMapper
{
    /// <summary>Returns null for updates FlowHub does not handle at all.</summary>
    public static TelegramMessage? Map(Update update)
    {
        var message = update.Message;
        if (message?.From is null)
        {
            return null;
        }

        return new TelegramMessage(
            UpdateId: update.Id,
            ChatId: message.Chat.Id,
            MessageId: message.MessageId,
            FromUserId: message.From.Id,
            Text: message.Text ?? message.Caption,
            File: MapFile(message));
    }

    private static TelegramFile? MapFile(Message message)
    {
        if (message.Document is { } document)
        {
            return new TelegramFile(
                document.FileId,
                document.FileName ?? "document",
                document.MimeType ?? "application/octet-stream",
                document.FileSize ?? 0);
        }

        // Photos arrive as a size ladder; the last entry is the largest.
        if (message.Photo is { Length: > 0 } photos)
        {
            var largest = photos[^1];
            return new TelegramFile(largest.FileId, $"photo-{message.MessageId}.jpg", "image/jpeg", largest.FileSize ?? 0);
        }

        return null;
    }
}
```

- [ ] **Step 3: Write the registration extension**

`source/FlowHub.Telegram/TelegramServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FlowHub.Core.Captures;
using Telegram.Bot;

namespace FlowHub.Telegram;

/// <summary>Host wiring for the Telegram inbound Channel.</summary>
public static class TelegramServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Channel only when <see cref="TelegramOptions.IsConfigured"/>. An
    /// unconfigured FlowHub — CI included — never contacts Telegram.
    /// </summary>
    public static IServiceCollection AddFlowHubTelegram(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TelegramOptions.SectionName).Get<TelegramOptions>() ?? new TelegramOptions();
        if (!options.IsConfigured)
        {
            return services;
        }

        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(options.BotToken!));
        services.AddScoped<ITelegramGateway, TelegramGateway>();
        services.AddScoped<TelegramReactionService>();
        services.AddScoped<TelegramUpdateHandler>();
        services.AddHostedService<TelegramPollingService>();
        services.Decorate<ICaptureService, TelegramReactionCaptureServiceDecorator>();
        return services;
    }
}
```

`Decorate` is not built into `Microsoft.Extensions.DependencyInjection`. If the repo already references Scrutor, use it. Otherwise replace that line with a manual decoration, which needs no new package:

```csharp
        var captureDescriptor = services.Last(d => d.ServiceType == typeof(ICaptureService));
        services.Add(ServiceDescriptor.Describe(
            typeof(ICaptureService),
            sp => new TelegramReactionCaptureServiceDecorator(
                (ICaptureService)ActivatorUtilities.CreateInstance(sp, captureDescriptor.ImplementationType!),
                sp.GetRequiredService<TelegramReactionService>()),
            captureDescriptor.Lifetime));
```

Check `PersistenceServiceCollectionExtensions.cs` first to confirm how `ICaptureService` is registered, and match its lifetime.

- [ ] **Step 4: Reference the project from the host**

In `source/FlowHub.Web/FlowHub.Web.csproj`, alongside the other `ProjectReference` entries:

```xml
    <ProjectReference Include="..\FlowHub.Telegram\FlowHub.Telegram.csproj" />
```

- [ ] **Step 5: Wire it into Program.cs**

In `source/FlowHub.Web/Program.cs`, immediately after the `builder.Services.AddFlowHubApi();` line:

```csharp
// Telegram inbound Channel — dormant unless Telegram__BotToken + Telegram__AllowedUserIds are set.
builder.Services.AddFlowHubTelegram(builder.Configuration);
```

Add `using FlowHub.Telegram;` to the top of the file if the analyzer requires it.

- [ ] **Step 6: Document the configuration**

Append to `.env.example`:

```bash
# Telegram inbound Channel (optional — the Channel stays off unless both are set)
Telegram__BotToken=
Telegram__AllowedUserIds=
```

- [ ] **Step 7: Verify the whole solution builds and all tests pass**

Run:

```bash
dotnet build FlowHub.slnx
dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj
dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj
```

Expected: build succeeds with **zero warnings** (`TreatWarningsAsErrors` is on), and both test projects pass. If `FlowHub.Web.ComponentTests` fails on a constructor change, fix the test's construction — do not change production code to suit it.

- [ ] **Step 8: Commit**

```bash
git add source/FlowHub.Telegram source/FlowHub.Web .env.example
git commit -m "feat(telegram): poll for updates and wire the Channel into the host

Refs #20"
```

---

### Task 7: Reconcile the documentation

**Files:**
- Modify: `docs/adr/0001-frontend-render-mode-and-architecture.md`
- Modify: `docs/spec/system-context.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Correct ADR 0001**

ADR 0001 §2 lists `FlowHub.Telegram (bot, separate process)` as a REST API consumer, which is no longer what was built. Add directly beneath that bullet list:

```markdown
> **As built (Telegram Channel, 2026-08-24).** `FlowHub.Telegram` is **not** a separate
> process and is **not** a REST API consumer. It ships as a class library hosted
> in-process by `FlowHub.Web` as an `IHostedService`, reaching `ICaptureService` through
> DI exactly as the Web UI does — matching `docs/spec/system-context.md`, which had
> always described it that way. Keeping it in-process avoids issuing a service
> credential for the `.RequireAuthorization()` capture endpoints and keeps
> `docker-compose.yml` at a single application service. See
> `docs/superpowers/specs/2026-08-24-telegram-inbound-channel-design.md` §D4.
```

- [ ] **Step 2: Update the current-state section of system-context.md**

In `docs/spec/system-context.md`, move the Telegram channel out of the planned/not-wired lists: remove it from "Planned, not yet scaffolded (no project in the source tree)" and from the "Not yet wired" line, and add `FlowHub.Telegram` to the implemented bullet list as:

```markdown
  - `FlowHub.Telegram` — inbound Telegram Channel (long-polling `IHostedService`,
    hosted in-process by `FlowHub.Web`; dormant unless configured)
```

Leave the "generic integrations layer" entry in the planned list untouched — that is still unbuilt.

- [ ] **Step 3: Add a changelog entry**

Under `## [Unreleased]` → `### Added` in `CHANGELOG.md`:

```markdown
- Telegram inbound Channel: messages from allow-listed users become Captures, and the
  original message is marked with a reaction when its Capture resolves (#20).
```

- [ ] **Step 4: Verify the docs are consistent**

Run:

```bash
grep -n "not yet scaffolded" -A6 docs/spec/system-context.md
grep -n "As built (Telegram" docs/adr/0001-frontend-render-mode-and-architecture.md
```

Expected: Telegram no longer appears under the planned list, and the ADR note is present.

- [ ] **Step 5: Commit**

```bash
git add docs CHANGELOG.md
git commit -m "docs(telegram): record the Channel as built and correct ADR 0001

Refs #20"
```
