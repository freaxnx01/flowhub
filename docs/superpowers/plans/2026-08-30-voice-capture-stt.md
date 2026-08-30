# Voice Capture via Speech-to-Text Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A voice memo sent to the Telegram bot becomes a Capture whose `Content` is its transcript, then classifies and routes like any other text capture.

**Architecture:** A new `ISpeechToText` port in `FlowHub.AI` over the OpenAI-compatible `/v1/audio/transcriptions` endpoint, configured by `BaseUrl` exactly as embeddings already are. The Telegram handler submits immediately with a `NeedsTranscription` flag; a new pipeline consumer transcribes, fills `Content`, and re-publishes `CaptureCreated` so the normal classify→route path runs on real text.

**Tech Stack:** .NET 10 · OpenAI SDK 2.10.0 (already transitively present via `Microsoft.Extensions.AI.OpenAI`) · MassTransit · Telegram.Bot 22.6.0 · xUnit + FluentAssertions + NSubstitute

**Spec:** [`docs/superpowers/specs/2026-08-30-voice-capture-stt-design.md`](../specs/2026-08-30-voice-capture-stt-design.md)

## Global Constraints

- `TargetFramework` is `net10.0`; `Nullable` is `enable`; **`TreatWarningsAsErrors` is `true`** — a warning fails the build.
- `GenerateDocumentationFile` is on; public types need XML doc comments.
- Central Package Management: never put `Version=` on a `PackageReference`. **This plan adds no packages** — `AiServiceCollectionExtensions.cs` already constructs `OpenAIClient` directly, so the `OpenAI` types are in scope in `FlowHub.AI` today.
- Logging uses `[LoggerMessage]` source-generated partial methods with scoped `EventId` ranges (ADR 0008). The Telegram module uses 5000-range ids; use **5100-range** for the new transcription code so the ranges stay separable.
- `dotnet test` on the full solution is unreliable locally (NU1903 via the slnx). **Run the per-project commands in each task.**
- Every commit message follows Conventional Commits and ends with `Refs #21`.

## The one ordering constraint that matters

`CaptureEnrichmentConsumer.cs:41` routes **any** attachment-bearing Capture straight to Paperless with no classification:

```csharp
if (msg.HasAttachment)
{
    await _captureService.MarkClassifiedAsync(msg.CaptureId, "Paperless", cancellationToken: ct);
```

A voice memo is an audio attachment. **Task 4's early return must sit before that branch**, or voice memos are filed in the document scanner. If you find yourself adding the guard after it, stop and re-read this section.

---

### Task 1: The `ISpeechToText` port and its OpenAI-compatible adapter

**Files:**
- Create: `source/FlowHub.Core/Captures/ISpeechToText.cs`
- Create: `source/FlowHub.AI/AiSpeechToText.cs`
- Test: `tests/FlowHub.AI.IntegrationTests/AiSpeechToTextTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ISpeechToText` with `Task<string?> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken = default)` — returns the transcript, or **null** when transcription fails or produces nothing. Null is the failure signal; the port never throws for provider errors.

- [ ] **Step 1: Write the port**

`source/FlowHub.Core/Captures/ISpeechToText.cs`:

```csharp
namespace FlowHub.Core.Captures;

/// <summary>
/// Driven port for turning recorded audio into text. Implementations are
/// best-effort: a provider failure returns null rather than throwing, so a
/// transcription problem becomes a lifecycle outcome rather than a pipeline fault.
/// </summary>
public interface ISpeechToText
{
    /// <summary>
    /// Transcribes <paramref name="audio"/>, or returns null when the provider fails
    /// or returns nothing usable. <paramref name="fileName"/> carries the extension
    /// the provider uses to detect the container format.
    /// </summary>
    Task<string?> TranscribeAsync(
        Stream audio, string fileName, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Write the failing test**

`tests/FlowHub.AI.IntegrationTests/AiSpeechToTextTests.cs`. Follow the arrangement the sibling tests in that project already use; these assert on the wrapper's behaviour, not on a live provider.

```csharp
using FlowHub.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Audio;

namespace FlowHub.AI.IntegrationTests;

public class AiSpeechToTextTests
{
    [Fact]
    public async Task TranscribeAsync_ProviderThrows_ReturnsNullRatherThanPropagating()
    {
        // The consumer turns null into an Orphan; an exception here would instead
        // surface as a MassTransit fault and be retried as if transient.
        var client = Substitute.For<AudioClient>();
        client.TranscribeAudioAsync(Arg.Any<Stream>(), Arg.Any<string>(),
                Arg.Any<AudioTranscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<System.ClientModel.ClientResult<AudioTranscription>>>(
                _ => throw new HttpRequestException("provider down"));
        var sut = new AiSpeechToText(client, NullLogger<AiSpeechToText>.Instance);

        using var audio = new MemoryStream(new byte[8]);
        var result = await sut.TranscribeAsync(audio, "voice.ogg");

        result.Should().BeNull();
    }
}
```

If `AudioClient` proves un-substitutable because its members are not virtual, **do not** restructure the production code to suit the test. Instead introduce a thin internal seam — an `interface IAudioTranscriber { Task<string?> TranscribeAsync(...) }` implemented by a one-line wrapper over `AudioClient` — and substitute that. Say so in the commit message.

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test tests/FlowHub.AI.IntegrationTests/FlowHub.AI.IntegrationTests.csproj --filter AiSpeechToTextTests`
Expected: FAIL — `AiSpeechToText` does not exist.

- [ ] **Step 4: Write the adapter**

`source/FlowHub.AI/AiSpeechToText.cs`:

```csharp
using FlowHub.Core.Captures;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;

namespace FlowHub.AI;

/// <summary>
/// <see cref="ISpeechToText"/> over the OpenAI-compatible /v1/audio/transcriptions
/// endpoint. The provider is chosen by the client's configured BaseUrl, so the same
/// adapter serves a cloud provider or a local whisper server — see the design's D1.
/// Provider failures return null; the caller decides what that means for the Capture.
/// </summary>
public sealed partial class AiSpeechToText : ISpeechToText
{
    private readonly AudioClient _client;
    private readonly ILogger<AiSpeechToText> _log;

    public AiSpeechToText(AudioClient client, ILogger<AiSpeechToText> log)
    {
        _client = client;
        _log = log;
    }

    public async Task<string?> TranscribeAsync(
        Stream audio, string fileName, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.TranscribeAudioAsync(
                audio, fileName, new AudioTranscriptionOptions(), cancellationToken);
            var text = result.Value?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            LogTranscriptionFailed(ex, fileName);
            return null;
        }
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Warning,
        Message = "Transcription failed (fileName={FileName})")]
    private partial void LogTranscriptionFailed(Exception ex, string fileName);
}
```

An empty transcript returns null deliberately: a silent recording should become a visible failure, not an empty Capture.

- [ ] **Step 5: Run it to verify it passes**

Run: `dotnet test tests/FlowHub.AI.IntegrationTests/FlowHub.AI.IntegrationTests.csproj --filter AiSpeechToTextTests`
Expected: PASS.

- [ ] **Step 6: Commit and push**

```bash
git add source/FlowHub.Core/Captures/ISpeechToText.cs source/FlowHub.AI/AiSpeechToText.cs tests/FlowHub.AI.IntegrationTests/AiSpeechToTextTests.cs
git commit -m "feat(ai): add a speech-to-text port over the transcriptions endpoint

Refs #21"
git push
```

---

### Task 2: Register the speech service, dormant unless configured

**Files:**
- Modify: `source/FlowHub.AI/AiServiceCollectionExtensions.cs`
- Modify: `source/FlowHub.Web/Program.cs`
- Modify: `.env.example`
- Test: `tests/FlowHub.AI.IntegrationTests/AiSpeechRegistrationTests.cs`

**Interfaces:**
- Consumes: `ISpeechToText`, `AiSpeechToText` (Task 1).
- Produces: `IServiceCollection AddFlowHubSpeech(this IServiceCollection services, IConfiguration configuration)`, and `SpeechOptions` with `MaxSeconds` (default 300) resolvable via `IOptions<SpeechOptions>`.

- [ ] **Step 1: Write the failing test**

`tests/FlowHub.AI.IntegrationTests/AiSpeechRegistrationTests.cs`:

```csharp
using FlowHub.AI;
using FlowHub.Core.Captures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.AI.IntegrationTests;

public class AiSpeechRegistrationTests
{
    private static IServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlowHubSpeech(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddFlowHubSpeech_WithoutApiKey_RegistersNothing()
    {
        var sp = Build(("Speech:Model", "whisper-1"));

        sp.GetService<ISpeechToText>().Should().BeNull();
    }

    [Fact]
    public void AddFlowHubSpeech_WithApiKey_RegistersTheService()
    {
        var sp = Build(("Speech:ApiKey", "sk-test"), ("Speech:BaseUrl", "http://localhost:8000/v1"));

        sp.GetService<ISpeechToText>().Should().NotBeNull();
    }

    [Fact]
    public void AddFlowHubSpeech_MaxSecondsDefaultsTo300()
    {
        var sp = Build(("Speech:ApiKey", "sk-test"));

        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SpeechOptions>>()
          .Value.MaxSeconds.Should().Be(300);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/FlowHub.AI.IntegrationTests/FlowHub.AI.IntegrationTests.csproj --filter AiSpeechRegistrationTests`
Expected: FAIL — `AddFlowHubSpeech` does not exist.

- [ ] **Step 3: Add the options type**

`source/FlowHub.AI/SpeechOptions.cs`:

```csharp
namespace FlowHub.AI;

/// <summary>
/// Configuration for speech-to-text. Inactive unless <see cref="ApiKey"/> is set, so
/// an unconfigured FlowHub never calls a transcription provider.
/// </summary>
public sealed class SpeechOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Speech";

    /// <summary>Provider API key. Secret — env var only. Absent means the feature is off.</summary>
    public string? ApiKey { get; set; }

    /// <summary>OpenAI-compatible base URL; a cloud provider or a local whisper server.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Transcription model name.</summary>
    public string Model { get; set; } = "whisper-1";

    /// <summary>Per-request HTTP timeout.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Longest audio accepted, in seconds. Checked before download, because
    /// transcription is billed per minute and a mis-sent recording should not
    /// become an unbounded charge.
    /// </summary>
    public int MaxSeconds { get; set; } = 300;

    /// <summary>True when the feature has everything it needs to run.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
```

- [ ] **Step 4: Add the registration**

Append to `source/FlowHub.AI/AiServiceCollectionExtensions.cs`, mirroring `AddFlowHubEmbeddings` directly above it:

```csharp
    /// <summary>
    /// Registers speech-to-text when <c>Speech:ApiKey</c> is set. The provider is
    /// whatever <c>Speech:BaseUrl</c> points at — a cloud endpoint or a local whisper
    /// server — because both implement /v1/audio/transcriptions (design D1).
    /// </summary>
    public static IServiceCollection AddFlowHubSpeech(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(SpeechOptions.SectionName).Get<SpeechOptions>()
            ?? new SpeechOptions();

        // Options are bound even when the feature is off, so MaxSeconds is always
        // resolvable — the handler's duration check must work with STT unconfigured.
        services.Configure<SpeechOptions>(configuration.GetSection(SpeechOptions.SectionName));

        if (!options.IsConfigured)
        {
            return services;
        }

        var endpoint = new Uri(string.IsNullOrWhiteSpace(options.BaseUrl)
            ? "https://api.openai.com/v1"
            : options.BaseUrl);

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        var audioClient = new OpenAIClient(new ApiKeyCredential(options.ApiKey!), clientOptions)
            .GetAudioClient(options.Model);

        services.AddSingleton<ISpeechToText>(sp => new AiSpeechToText(
            audioClient, sp.GetRequiredService<ILogger<AiSpeechToText>>()));

        return services;
    }
```

Add `using OpenAI.Audio;` at the top if the analyzer requires it. `OpenAIClient`, `OpenAIClientOptions` and `ApiKeyCredential` are already used by `AddFlowHubEmbeddings` in this file, so their usings exist.

- [ ] **Step 5: Wire it into the host**

In `source/FlowHub.Web/Program.cs`, immediately after the existing embeddings registration:

```csharp
// Speech-to-text — dormant unless Speech__ApiKey is set (design D1).
builder.Services.AddFlowHubSpeech(builder.Configuration);
```

Find the embeddings line by `grep -n "AddFlowHubEmbeddings" source/FlowHub.Web/Program.cs` rather than assuming a line number.

- [ ] **Step 6: Document the configuration**

Append to `.env.example`:

```bash
# Speech-to-text for Telegram voice memos (optional — voice stays unsupported unless set).
# BaseUrl selects the provider: a cloud endpoint, or a local whisper server on the homelab.
Speech__ApiKey=
Speech__BaseUrl=
Speech__Model=whisper-1
Speech__MaxSeconds=300
```

- [ ] **Step 7: Run the tests and build**

Run:
```bash
dotnet test tests/FlowHub.AI.IntegrationTests/FlowHub.AI.IntegrationTests.csproj --filter AiSpeechRegistrationTests
dotnet build source/FlowHub.Web/FlowHub.Web.csproj
```
Expected: PASS, then `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 8: Commit and push**

```bash
git add source/FlowHub.AI source/FlowHub.Web/Program.cs .env.example tests/FlowHub.AI.IntegrationTests
git commit -m "feat(ai): register speech-to-text, dormant unless configured

Refs #21"
git push
```

---

### Task 3: Map Telegram voice messages

**Files:**
- Modify: `source/FlowHub.Telegram/ITelegramGateway.cs` (the `TelegramFile` record)
- Modify: `source/FlowHub.Telegram/TelegramMessageMapper.cs`
- Test: `tests/FlowHub.Telegram.Tests/TelegramMessageMapperTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `TelegramFile` gains `int DurationSeconds = 0` as its last positional parameter — non-zero only for voice and audio. Existing construction sites keep working because it is defaulted.

- [ ] **Step 1: Write the failing test**

Create `tests/FlowHub.Telegram.Tests/TelegramMessageMapperTests.cs` if it does not exist; otherwise append. `TelegramMessageMapper` is `internal`, so the test project needs `InternalsVisibleTo` — check whether `source/FlowHub.Telegram/FlowHub.Telegram.csproj` already grants it and add it if not:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="FlowHub.Telegram.Tests" />
  </ItemGroup>
```

```csharp
using Telegram.Bot.Types;

namespace FlowHub.Telegram.Tests;

public class TelegramMessageMapperTests
{
    private static Update VoiceUpdate(int duration, string mime = "audio/ogg") => new()
    {
        Id = 7,
        Message = new Message
        {
            MessageId = 3,
            Chat = new Chat { Id = 55 },
            From = new User { Id = 42 },
            Voice = new Voice { FileId = "voice-abc", Duration = duration, MimeType = mime, FileSize = 2048 },
        },
    };

    [Fact]
    public void Map_VoiceMessage_ProducesAFileWithDurationAndMimeType()
    {
        var message = TelegramMessageMapper.Map(VoiceUpdate(12));

        message.Should().NotBeNull();
        message!.File.Should().NotBeNull();
        message.File!.FileId.Should().Be("voice-abc");
        message.File.ContentType.Should().Be("audio/ogg");
        message.File.DurationSeconds.Should().Be(12);
    }

    [Fact]
    public void Map_VoiceMessage_WithNoMimeType_FallsBackToAudioOgg()
    {
        var update = VoiceUpdate(5);
        update.Message!.Voice!.MimeType = null;

        var message = TelegramMessageMapper.Map(update);

        message!.File!.ContentType.Should().Be("audio/ogg");
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj --filter TelegramMessageMapperTests`
Expected: FAIL — `TelegramFile` has no `DurationSeconds`, and `Voice` is not mapped.

- [ ] **Step 3: Extend `TelegramFile`**

In `source/FlowHub.Telegram/ITelegramGateway.cs`, add the parameter and document it:

```csharp
/// <param name="DurationSeconds">Playback length for voice and audio; 0 for other file types.</param>
public sealed record TelegramFile(
    string FileId, string FileName, string ContentType, long SizeBytes, int DurationSeconds = 0);
```

- [ ] **Step 4: Map voice and audio**

In `source/FlowHub.Telegram/TelegramMessageMapper.cs`, add these branches to `MapFile`, **before** the `Photo` branch and after `Document`:

```csharp
        if (message.Voice is { } voice)
        {
            return new TelegramFile(
                voice.FileId,
                $"voice-{message.MessageId}.ogg",
                voice.MimeType ?? "audio/ogg",
                voice.FileSize ?? 0,
                voice.Duration);
        }

        if (message.Audio is { } audio)
        {
            return new TelegramFile(
                audio.FileId,
                audio.FileName ?? $"audio-{message.MessageId}.mp3",
                audio.MimeType ?? "audio/mpeg",
                audio.FileSize ?? 0,
                audio.Duration);
        }
```

The filename extension matters: the transcription provider uses it to detect the container format.

- [ ] **Step 5: Run them to verify they pass**

Run: `dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj`
Expected: PASS — the new mapper tests plus every existing Telegram test.

- [ ] **Step 6: Commit and push**

```bash
git add source/FlowHub.Telegram tests/FlowHub.Telegram.Tests
git commit -m "feat(telegram): map voice and audio messages

Refs #21"
git push
```

---

### Task 4: Submit voice for transcription instead of refusing it

**Files:**
- Modify: `source/FlowHub.Core/Events/CaptureCreated.cs`
- Modify: `source/FlowHub.Core/Captures/ICaptureService.cs`
- Modify: `source/FlowHub.Persistence/EfCaptureService.cs`
- Modify: `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs:41`
- Modify: `source/FlowHub.Telegram/TelegramUpdateHandler.cs`
- Test: `tests/FlowHub.Telegram.Tests/TelegramUpdateHandlerVoiceTests.cs`
- Test: `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerTests.cs`

**Interfaces:**
- Consumes: `TelegramFile.DurationSeconds` (Task 3), `SpeechOptions.MaxSeconds` (Task 2).
- Produces: `CaptureCreated` gains `bool NeedsTranscription = false` as its last positional parameter. `ICaptureService.SubmitAsync` gains an optional `bool needsTranscription = false` on the attachment overload.

**Read the ordering constraint at the top of this plan before starting.**

- [ ] **Step 1: Write the failing tests**

`tests/FlowHub.Telegram.Tests/TelegramUpdateHandlerVoiceTests.cs` — copy the `Build()` arrangement from `TelegramUpdateHandlerAttachmentTests.cs` verbatim, adding an `IOptions<SpeechOptions>` with `MaxSeconds = 300`, then:

```csharp
    private static TelegramMessage VoiceMessage(int duration) =>
        new(UpdateId: 9L, ChatId: 55L, MessageId: 4, FromUserId: AllowedUser, Text: null,
            File: new TelegramFile("voice-abc", "voice-4.ogg", "audio/ogg", 2048, duration));

    [Fact]
    public async Task HandleAsync_VoiceWithinTheCap_SubmitsForTranscription()
    {
        var (sut, captures, _) = Build();

        await sut.HandleAsync(VoiceMessage(30), CancellationToken.None);

        await captures.Received(1).SubmitAsync(
            Arg.Any<string?>(), ChannelKind.Telegram, Arg.Any<AttachmentInput?>(),
            needsTranscription: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_VoiceOverTheCap_RepliesWithTheLimitAndSubmitsNothing()
    {
        var (sut, captures, gateway) = Build();

        await sut.HandleAsync(VoiceMessage(3000), CancellationToken.None);

        await gateway.Received(1).SendTextAsync(55L,
            Arg.Is<string>(s => s.Contains("300", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await captures.DidNotReceiveWithAnyArgs().SubmitAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task HandleAsync_VoiceDoesNotDownloadInTheHandler()
    {
        var (sut, _, gateway) = Build();

        await sut.HandleAsync(VoiceMessage(30), CancellationToken.None);

        // Downloading belongs to the transcription consumer; doing it here would
        // block the single-threaded poll loop (design D3).
        await gateway.DidNotReceiveWithAnyArgs().DownloadFileAsync(default!, default);
    }
```

And in `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureEnrichmentConsumerTests.cs`, add:

```csharp
    [Fact]
    public async Task Consume_NeedsTranscription_DoesNotClassifyOrPaperlessRoute()
    {
        var classifier = Substitute.For<IClassifier>();
        await using var provider = PipelineTestBase.Build(
            configure: s => s.AddSingleton(classifier),
            configureBus: x => x.AddConsumer<CaptureEnrichmentConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new CaptureCreated(
            Guid.NewGuid(), "[voice message]", ChannelKind.Telegram, DateTimeOffset.UtcNow,
            HasAttachment: true, NeedsTranscription: true));

        (await harness.Consumed.Any<CaptureCreated>()).Should().BeTrue();
        // Neither path may run: not classification, and not the Paperless shortcut
        // that any attachment would otherwise take.
        await classifier.DidNotReceiveWithAnyArgs().ClassifyAsync(default!, default);
        (await harness.Published.Any<CaptureClassified>()).Should().BeFalse();
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run:
```bash
dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj --filter TelegramUpdateHandlerVoiceTests
dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj --filter CaptureEnrichmentConsumerTests
```
Expected: FAIL — `needsTranscription` is not a parameter, `NeedsTranscription` is not a field, and voice currently gets the unsupported reply.

- [ ] **Step 3: Add the flag to the event and the service**

`source/FlowHub.Core/Events/CaptureCreated.cs`:

```csharp
public sealed record CaptureCreated(
    Guid CaptureId,
    string Content,
    ChannelKind Source,
    DateTimeOffset CreatedAt,
    bool HasAttachment = false,
    bool NeedsTranscription = false);
```

In `source/FlowHub.Core/Captures/ICaptureService.cs`, add the optional parameter to the attachment overload:

```csharp
    Task<Capture> SubmitAsync(
        string? caption,
        ChannelKind source,
        AttachmentInput? attachment,
        bool needsTranscription = false,
        CancellationToken cancellationToken = default);
```

Add the transcript setter to the same interface (design D8 — nothing can change `Content` after creation today, so without this the stored Capture keeps `"[voice message]"` forever):

```csharp
    /// <summary>
    /// Replaces the placeholder content of a Capture awaiting transcription with its
    /// transcript. Deliberately narrow: this is not a general content mutator.
    /// </summary>
    Task<Capture> SetTranscriptAsync(Guid id, string transcript, CancellationToken cancellationToken = default);
```

In `EfCaptureService`, implement it by loading the Capture, writing `Content`, and saving — mirroring how `MarkClassifiedAsync` mutates and persists directly above it.

Then update every implementer to match — `EfCaptureService`, `CaptureServiceStub`, and `TelegramReactionCaptureServiceDecorator` (which forwards it unchanged) — and pass the flag through to the published `CaptureCreated` in `EfCaptureService`. Compile after this step (`dotnet build FlowHub.slnx`) to find implementers mechanically rather than by memory.

- [ ] **Step 4: Guard enrichment — before the Paperless branch**

In `source/FlowHub.Web/Pipeline/CaptureEnrichmentConsumer.cs`, insert **above** the existing `if (msg.HasAttachment)` at line 41:

```csharp
        // A Capture still awaiting its transcript has placeholder content and an audio
        // attachment. Classifying it would classify the placeholder, and falling into
        // the HasAttachment branch below would file a voice memo in Paperless. The
        // transcription consumer re-publishes without this flag when the text is ready.
        if (msg.NeedsTranscription)
        {
            return;
        }

```

- [ ] **Step 5: Handle voice in the Telegram handler**

In `source/FlowHub.Telegram/TelegramUpdateHandler.cs`, inject `IOptions<SpeechOptions>` (store `.Value` in a `_speech` field), and add this branch **before** the existing `if (message.File is not null)` block:

```csharp
        if (message.File is { DurationSeconds: > 0 } audio)
        {
            if (audio.DurationSeconds > _speech.MaxSeconds)
            {
                await _gateway.SendTextAsync(message.ChatId,
                    $"That recording is too long — the limit is {_speech.MaxSeconds} seconds.",
                    cancellationToken);
                await RecordAsync(message, captureId: null, cancellationToken);
                return;
            }

            // Submitted without the audio: the transcription consumer fetches it. The
            // handler must not download here — the poll loop is single-threaded (D3).
            var voiceCapture = await _captures.SubmitAsync(
                "[voice message]", ChannelKind.Telegram, attachment: null,
                needsTranscription: true, cancellationToken);
            await RecordAsync(message, voiceCapture.Id, cancellationToken);
            return;
        }

```

The `DurationSeconds: > 0` pattern is what distinguishes audio from a photo or document, since Task 3 leaves it at 0 for those.

The consumer needs the file id to fetch the audio later. Store it by extending the `TelegramUpdate` record and its entity with a nullable `string? FileId`, set it in `RecordAsync` from `message.File?.FileId`, and add an EF migration (`dotnet ef migrations add 0015_TelegramUpdateFileId --project source/FlowHub.Persistence --startup-project source/FlowHub.Web`). Task 5 reads it back.

- [ ] **Step 6: Run everything touched**

Run:
```bash
dotnet build FlowHub.slnx
dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj
dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj
dotnet test tests/FlowHub.Persistence.Tests/FlowHub.Persistence.Tests.csproj
```
Expected: all PASS, build with `0 Warning(s)`. Docker must be running for the Persistence project.

- [ ] **Step 7: Commit and push**

```bash
git add -A
git commit -m "feat(telegram): submit voice memos for transcription

Refs #21"
git push
```

---

### Task 5: The transcription consumer

**Files:**
- Create: `source/FlowHub.Web/Pipeline/CaptureTranscriptionConsumer.cs`
- Modify: `source/FlowHub.Web/ProgramRegistration.cs` (consumer registration, near line 107)
- Test: `tests/FlowHub.Web.ComponentTests/Pipeline/CaptureTranscriptionConsumerTests.cs`

**Interfaces:**
- Consumes: `ISpeechToText` (Task 1), `CaptureCreated.NeedsTranscription` (Task 4), `TelegramUpdate.FileId` (Task 4), `ITelegramGateway.DownloadFileAsync` (existing).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

`tests/FlowHub.Web.ComponentTests/Pipeline/CaptureTranscriptionConsumerTests.cs`, using `PipelineTestBase.Build` exactly as the sibling consumer tests do:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using FlowHub.Core.Events;
using FlowHub.Telegram;
using FlowHub.Web.Pipeline;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlowHub.Web.ComponentTests.Pipeline;

public sealed class CaptureTranscriptionConsumerTests
{
    private static (ISpeechToText Stt, ITelegramGateway Gateway, ITelegramUpdateRepository Repo) Stubs(
        string? transcript, Guid captureId)
    {
        var stt = Substitute.For<ISpeechToText>();
        stt.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(transcript));
        var gateway = Substitute.For<ITelegramGateway>();
        gateway.DownloadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(new MemoryStream(new byte[8])));
        var repo = Substitute.For<ITelegramUpdateRepository>();
        repo.FindByCaptureIdAsync(captureId, Arg.Any<CancellationToken>())
            .Returns(new TelegramUpdate(1L, 55L, 4, captureId, DateTimeOffset.UtcNow, FileId: "voice-abc"));
        return (stt, gateway, repo);
    }

    [Fact]
    public async Task Consume_SuccessfulTranscription_RepublishesWithoutTheFlag()
    {
        var captureId = Guid.NewGuid();
        var (stt, gateway, repo) = Stubs("buy milk on the way home", captureId);
        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(stt); s.AddSingleton(gateway); s.AddSingleton(repo); },
            configureBus: x => x.AddConsumer<CaptureTranscriptionConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new CaptureCreated(
            captureId, "[voice message]", ChannelKind.Telegram, DateTimeOffset.UtcNow,
            HasAttachment: false, NeedsTranscription: true));

        (await harness.Published.Any<CaptureCreated>(
            x => x.Context.Message.CaptureId == captureId
                 && !x.Context.Message.NeedsTranscription
                 && x.Context.Message.Content == "buy milk on the way home"))
            .Should().BeTrue();

        // The row must carry it too, not just the event (D8).
        var captureService = provider.GetRequiredService<ICaptureService>();
        (await captureService.GetByIdAsync(captureId))?.Content
            .Should().Be("buy milk on the way home");
    }

    [Fact]
    public async Task Consume_TranscriptionReturnsNull_MarksOrphanAndDoesNotRepublish()
    {
        var captureId = Guid.NewGuid();
        var (stt, gateway, repo) = Stubs(null, captureId);
        var captures = Substitute.For<ICaptureService>();
        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(stt); s.AddSingleton(gateway); s.AddSingleton(repo); s.AddSingleton(captures); },
            configureBus: x => x.AddConsumer<CaptureTranscriptionConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new CaptureCreated(
            captureId, "[voice message]", ChannelKind.Telegram, DateTimeOffset.UtcNow,
            HasAttachment: false, NeedsTranscription: true));

        await captures.Received(1).MarkOrphanAsync(captureId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_WithoutTheFlag_DoesNothing()
    {
        var captureId = Guid.NewGuid();
        var (stt, gateway, repo) = Stubs("ignored", captureId);
        await using var provider = PipelineTestBase.Build(
            configure: s => { s.AddSingleton(stt); s.AddSingleton(gateway); s.AddSingleton(repo); },
            configureBus: x => x.AddConsumer<CaptureTranscriptionConsumer>());

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new CaptureCreated(
            captureId, "ordinary text", ChannelKind.Telegram, DateTimeOffset.UtcNow));

        await stt.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default!, default);
    }
}
```

`PipelineTestBase.Build` registers a `CaptureServiceStub` by default; the second test overrides it with a substitute so `MarkOrphanAsync` is observable. If registration order means the stub wins, register the substitute after the base call or adjust `PipelineTestBase` to skip its default when one is already present — do not weaken the assertion.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj --filter CaptureTranscriptionConsumerTests`
Expected: FAIL — `CaptureTranscriptionConsumer` does not exist.

- [ ] **Step 3: Write the consumer**

`source/FlowHub.Web/Pipeline/CaptureTranscriptionConsumer.cs`:

```csharp
using FlowHub.Core.Captures;
using FlowHub.Core.Channels;
using FlowHub.Core.Events;
using FlowHub.Telegram;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FlowHub.Web.Pipeline;

/// <summary>
/// Turns a voice Capture's audio into text. Runs off the band so the Telegram poll
/// loop is never blocked (design D3): on success it fills Content and re-publishes
/// CaptureCreated without the flag, so the ordinary classify → route path runs on
/// real text. On failure the Capture becomes an Orphan, which surfaces in Needs
/// attention and is retryable through the existing retry endpoint.
/// </summary>
public sealed partial class CaptureTranscriptionConsumer : IConsumer<CaptureCreated>
{
    private readonly ISpeechToText _speech;
    private readonly ITelegramGateway _gateway;
    private readonly ITelegramUpdateRepository _updates;
    private readonly ICaptureService _captures;
    private readonly ILogger<CaptureTranscriptionConsumer> _log;

    public CaptureTranscriptionConsumer(
        ISpeechToText speech,
        ITelegramGateway gateway,
        ITelegramUpdateRepository updates,
        ICaptureService captures,
        ILogger<CaptureTranscriptionConsumer> log)
    {
        _speech = speech;
        _gateway = gateway;
        _updates = updates;
        _captures = captures;
        _log = log;
    }

    public async Task Consume(ConsumeContext<CaptureCreated> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (!msg.NeedsTranscription)
        {
            return;
        }

        var update = await _updates.FindByCaptureIdAsync(msg.CaptureId, ct);
        if (update?.FileId is null)
        {
            await FailAsync(msg.CaptureId, "no Telegram file recorded for this capture", update, ct);
            return;
        }

        var audio = await _gateway.DownloadFileAsync(update.FileId, ct);
        if (audio is null)
        {
            await FailAsync(msg.CaptureId, "the audio could not be downloaded from Telegram", update, ct);
            return;
        }

        string? transcript;
        await using (audio)
        {
            transcript = await _speech.TranscribeAsync(audio, $"voice-{msg.CaptureId}.ogg", ct);
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            await FailAsync(msg.CaptureId, "the recording could not be transcribed", update, ct);
            return;
        }

        LogTranscribed(msg.CaptureId, transcript.Length);

        // Persist before re-publishing so the row and the event agree. Without this the
        // Capture keeps its placeholder in the grids, the search filter and the
        // embedding, even though classification sees the real text (design D8).
        await _captures.SetTranscriptAsync(msg.CaptureId, transcript, ct);

        // Re-publishing CaptureCreated is how the pipeline is re-entered — the same
        // mechanism CaptureRetryEndpoint uses. Without the flag this time, so
        // enrichment classifies the transcript.
        await context.Publish(
            new CaptureCreated(msg.CaptureId, transcript, msg.Source, msg.CreatedAt), ct);
    }

    private async Task FailAsync(
        Guid captureId, string reason, Core.Channels.TelegramUpdate? update, CancellationToken ct)
    {
        LogTranscriptionFailed(captureId, reason);
        await _captures.MarkOrphanAsync(captureId, reason, ct);

        if (update is not null)
        {
            // Best-effort: the operator should learn from the chat, not only the dashboard.
            try
            {
                await _gateway.SendTextAsync(update.ChatId, $"Sorry — {reason}.", ct);
            }
            catch (HttpRequestException ex)
            {
                LogReplyFailed(ex, captureId);
            }
        }
    }

    [LoggerMessage(EventId = 5110, Level = LogLevel.Information,
        Message = "Transcribed capture {CaptureId} ({Length} chars)")]
    private partial void LogTranscribed(Guid captureId, int length);

    [LoggerMessage(EventId = 5111, Level = LogLevel.Warning,
        Message = "Transcription failed for capture {CaptureId}: {Reason}")]
    private partial void LogTranscriptionFailed(Guid captureId, string reason);

    [LoggerMessage(EventId = 5112, Level = LogLevel.Warning,
        Message = "Could not send the transcription-failure reply for capture {CaptureId}")]
    private partial void LogReplyFailed(Exception ex, Guid captureId);
}
```

- [ ] **Step 4: Register the consumer**

In `source/FlowHub.Web/ProgramRegistration.cs`, alongside the existing consumers near line 107:

```csharp
            x.AddConsumer<CaptureTranscriptionConsumer>();
```

Register it unconditionally: it returns immediately when `NeedsTranscription` is false, and with STT unconfigured no voice Capture is ever created, so it never runs. If `ISpeechToText` being unregistered breaks resolution, guard the registration the way `CaptureNotificationConsumer` is guarded (`ProgramRegistration.cs:123`).

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj`
Expected: PASS, including the whole existing pipeline suite.

- [ ] **Step 6: Commit and push**

```bash
git add source/FlowHub.Web tests/FlowHub.Web.ComponentTests
git commit -m "feat(pipeline): transcribe voice captures off the poll loop

Refs #21"
git push
```

---

### Task 6: Documentation and whole-change verification

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/spec/system-context.md`
- Modify: `docs/glossary.md`

- [ ] **Step 1: Changelog**

Under `## [Unreleased]` → `### Added`:

```markdown
- Telegram voice memos are transcribed to text and captured like any other message,
  when `Speech__ApiKey` is configured. The provider is chosen by `Speech__BaseUrl`,
  so a cloud endpoint or a local whisper server both work (#21).
```

- [ ] **Step 2: System context**

In `docs/spec/system-context.md`, add speech-to-text to the `FlowHub.AI` description so the C4 view matches what the code does — it currently reads "AI classification"; extend it to note transcription and that the provider is URL-selected.

- [ ] **Step 3: Glossary**

Append to the **Capture lifecycle** entry in `docs/glossary.md`:

```markdown
A voice Capture is created `Raw` with placeholder content and a transcription flag;
its transcript replaces the placeholder before classification runs. A failed
transcription ends at `Orphan`.
```

- [ ] **Step 4: Verify the whole change**

Run:
```bash
dotnet build FlowHub.slnx
dotnet test tests/FlowHub.AI.IntegrationTests/FlowHub.AI.IntegrationTests.csproj
dotnet test tests/FlowHub.Telegram.Tests/FlowHub.Telegram.Tests.csproj
dotnet test tests/FlowHub.Web.ComponentTests/FlowHub.Web.ComponentTests.csproj
dotnet test tests/FlowHub.Persistence.Tests/FlowHub.Persistence.Tests.csproj
dotnet test tests/FlowHub.Core.Tests/FlowHub.Core.Tests.csproj
```
Expected: build with `0 Warning(s)`, all suites PASS. Docker must be running.

- [ ] **Step 5: Confirm the feature is inert when unconfigured**

Run: `grep -n "IsConfigured" source/FlowHub.AI/SpeechOptions.cs source/FlowHub.AI/AiServiceCollectionExtensions.cs`
Expected: the registration returns early without `Speech:ApiKey`.

**This exposes one behaviour to fix, not to deliberate.** With STT unconfigured the handler would still submit voice for transcription and the consumer would Orphan every memo — a worse experience than the honest "not supported yet". So the handler's voice branch must check configuration first:

```csharp
        if (message.File is { DurationSeconds: > 0 } audio)
        {
            if (!_speech.IsConfigured)
            {
                await _gateway.SendTextAsync(message.ChatId,
                    "Voice messages are not supported yet — send text, a photo, or a document.",
                    cancellationToken);
                await RecordAsync(message, captureId: null, cancellationToken);
                return;
            }
```

Add a test in `TelegramUpdateHandlerVoiceTests` that with `SpeechOptions.ApiKey` unset, a voice message gets the unsupported reply and creates no Capture.

- [ ] **Step 6: Commit and push**

```bash
git add CHANGELOG.md docs/
git commit -m "docs(voice): record speech-to-text capture

Refs #21"
git push
```
