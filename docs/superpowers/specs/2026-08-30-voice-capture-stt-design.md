# Voice Capture via Speech-to-Text — Design

**Date:** 2026-08-30
**Status:** Approved (brainstorm)
**Issue:** [#21](https://github.com/freaxnx01/flowhub/issues/21)
**Affects:** `source/FlowHub.AI/` (new port + adapter), `source/FlowHub.Core/Events/CaptureCreated.cs`, `source/FlowHub.Telegram/TelegramMessageMapper.cs`, `source/FlowHub.Telegram/TelegramUpdateHandler.cs`, `source/FlowHub.Web/Pipeline/` (new consumer + one guard)

---

## Problem

A voice memo sent to the Telegram bot is answered with *"That message type is not supported yet"*. Voice is the format a capture tool most wants: it is what you use when your hands are busy, which is exactly when writing a note is impractical.

Two things block it today:

- `TelegramMessageMapper.MapFile` maps only `Document` and `Photo`. `message.Voice` is never seen, so a voice message arrives with no text and no file and falls through to the unsupported branch.
- FlowHub has no transcription capability. `FlowHub.AI` does classification, embeddings, and enrichment; there is no Whisper/STT reference anywhere in `source/`.

## Goal

A voice memo from an allow-listed user becomes a Capture whose `Content` is its transcript, classified and routed like any other text capture.

## Non-goals

- **Voice from any channel other than Telegram.** The Web and API paths have no audio entry point and no user asking for one.
- **Speaker identification, diarisation, or translation.** Transcription only, in whatever language was spoken.
- **Keeping the audio.** See D5.
- **Local-only inference as a precondition.** D1 makes the provider a URL, so moving local later is configuration, not a rewrite — this issue does not block on ADR 0007.

---

## Corrections to the issue as filed

The issue proposed **"Whisper via OpenRouter"**. OpenRouter routes *chat completions* and exposes no `/v1/audio/transcriptions` endpoint, so that specific route does not exist. (Some multimodal chat models accept audio inline; that is a different mechanism with different cost and output, and is not what is being built here.)

The issue also framed **cloud vs local** as the central decision. D1 dissolves it.

---

## Decisions (locked during brainstorm)

### D1 — The provider is a `BaseUrl`, not a fork

A new `ISpeechToText` port in `FlowHub.AI`, implemented over the OpenAI-compatible `/v1/audio/transcriptions` endpoint, configured exactly like embeddings already are:

| Key | Meaning |
|---|---|
| `Speech:ApiKey` | Secret; **absent ⇒ the feature is dormant** |
| `Speech:BaseUrl` | Provider endpoint; cloud today, a local sidecar later |
| `Speech:Model` | e.g. `whisper-1` |
| `Speech:TimeoutSeconds` | Per-request cap |
| `Speech:MaxSeconds` | Duration cap, default 300 (D7) |

This mirrors `AddFlowHubEmbeddings` (`AiServiceCollectionExtensions.cs:136-170`), which points an OpenAI-compatible client at a configurable `BaseUrl` and returns early when no key is set. `/v1/audio/transcriptions` is implemented by cloud providers **and** by local servers (`faster-whisper-server`/Speaches, `whisper.cpp` server), so cloud-vs-local becomes one config value.

That satisfies ADR 0007's local-by-default *target* without blocking this issue on it: start cloud, move the URL when a sidecar exists, change no code.

### D2 — `message.Voice` is mapped

`TelegramMessageMapper.MapFile` gains a `Voice` branch (and `Audio`, which is the same shape for a sent audio file), producing a `TelegramFile` carrying the reported MIME type and duration. Without this the feature is unreachable regardless of what else is built.

### D3 — Transcription runs asynchronously, in a pipeline consumer

The handler does **not** download or transcribe. It submits the Capture immediately with placeholder content and returns, so the poll loop is never blocked. A new `CaptureTranscriptionConsumer` in `FlowHub.Web/Pipeline/` does the `getFile` + download + transcribe, writes the transcript, and re-publishes.

The poll loop is single-threaded: a 30-second transcription done inline stalls every message behind it, and a hung provider call stalls ingestion until the HTTP timeout. Async also inherits the retry and fault handling the MassTransit pipeline already has (ADR 0003).

Rejected: **inline in the handler**, and **inline with a short timeout** — the latter times out on long memos, which are precisely the ones worth capturing.

### D4 — `NeedsTranscription` on `CaptureCreated`, and enrichment defers on it

One new bool alongside the existing `HasAttachment`:

```csharp
public sealed record CaptureCreated(
    Guid CaptureId, string Content, ChannelKind Source, DateTimeOffset CreatedAt,
    bool HasAttachment = false, bool NeedsTranscription = false);
```

Flow:

```text
CaptureCreated(NeedsTranscription: true)
  ├─ CaptureEnrichmentConsumer   → early return
  └─ CaptureTranscriptionConsumer → fill Content
        └▶ CaptureCreated(NeedsTranscription: false)
             └─ CaptureEnrichmentConsumer → classify → route
```

Re-publishing `CaptureCreated` to re-trigger the pipeline is an existing mechanism, not a new one — `CaptureRetryEndpoint.cs:58` already does exactly this. So this adds a field, not a concept, and keeps ADR 0003 §2's two-event vocabulary intact.

**The guard must sit before the `HasAttachment` branch.** `CaptureEnrichmentConsumer.cs:41` routes *any* attachment-bearing Capture straight to Paperless with no classification. A voice memo is an audio attachment; without the early return in the right place, voice memos are filed in the document scanner.

Rejected: a **dedicated `CaptureTranscribed` event** (a third event type, two entry points into enrichment to keep in step, against ADR 0003 §2); **submitting only after transcription** (the inline option by another name).

### D5 — The audio is discarded after transcription

The transcript is the Capture. The audio is downloaded to a stream, transcribed, and dropped.

Two reasons. First, storing it sets `HasAttachment: true`, and the re-published `CaptureCreated` would then hit the Paperless branch — the collision D4 exists to avoid, reintroduced. Second, voice recordings are more sensitive than text: they carry whoever else was audible, and keeping them on disk is a privacy cost with no current consumer.

Rejected: **store as an Attachment** (widens the change into the shared enrichment consumer, and puts recordings on disk); **store only on failure** (two storage paths, and the collision still has to be solved for the failure case).

### D6 — Transcription failure marks the Capture `Orphan`

On failure the consumer calls `MarkOrphanAsync` with the reason, and the bot replies once so it is visible from the chat as well as the dashboard.

`Orphan` is the existing terminal failure stage: it surfaces in the *Needs attention* card, and the existing `POST /api/v1/captures/{id}/retry` re-publishes `CaptureCreated` — which re-runs transcription with no new retry mechanism. The Telegram reaction from #20 maps `Orphan` to 💔, so the chat shows it too.

### D7 — A duration cap, checked before download

`Speech:MaxSeconds` (default 300). Telegram reports the duration in the update — `Voice.Duration` and `Audio.Duration` both exist in Telegram.Bot 22.6.0, verified against the package's own XML docs — so an over-long memo is rejected **before** any download or provider call, with a reply naming the limit.

Transcription is billed per minute. Without a cap, one mis-sent hour-long recording is an unbounded charge for something nobody meant to capture.

---

## Architecture

```text
Telegram voice message
      │
      ▼
TelegramMessageMapper.MapFile            ← D2: Voice/Audio branch
      │
TelegramUpdateHandler
      ├── duration > Speech:MaxSeconds → reply + record, no Capture   (D7)
      └── SubmitAsync(placeholder, NeedsTranscription: true)          (D3)
            │
            ▼
      CaptureCreated(NeedsTranscription: true)
            ├── CaptureEnrichmentConsumer → early return              (D4)
            └── CaptureTranscriptionConsumer
                  ├── getFile + download (stream, never persisted)    (D5)
                  ├── ISpeechToText.TranscribeAsync                   (D1)
                  ├── success → Content = transcript
                  │             └▶ CaptureCreated(NeedsTranscription: false)
                  │                  └── classify → route
                  └── failure → MarkOrphanAsync(reason) + one reply   (D6)
```

## Error handling

| Failure | Behaviour |
|---|---|
| `Speech:ApiKey` unset | Feature dormant; voice keeps getting "not supported yet". Inert, not broken. |
| Duration over cap | Rejected before download, reply names the limit, update recorded so it is not reprocessed |
| Download fails | `Orphan` with the reason; retryable |
| Provider error or timeout | `Orphan` with the reason; retryable |
| Empty transcript (silence) | `Orphan` — an empty Capture is worse than a visible failure |

A failed transcription must never fail the poll loop or the consumer's siblings; the pipeline's existing fault handling applies.

## Testing

- `MapFile` maps `Voice` and `Audio` to a `TelegramFile` with the reported MIME type and duration
- A voice message over `Speech:MaxSeconds` is refused before download, with the limit in the reply
- The handler submits with `NeedsTranscription: true` and does not download
- `CaptureEnrichmentConsumer` returns early on `NeedsTranscription`, **and does not Paperless-route** despite the audio being an attachment
- The transcription consumer writes the transcript to `Content` and re-publishes with `NeedsTranscription: false`
- The re-published event classifies normally
- Download failure, provider failure, and empty transcript each mark `Orphan` with a distinct reason
- With no `Speech:ApiKey`, nothing is registered and voice still gets the unsupported reply

## Consequences

- **A voice Capture is briefly visible with placeholder content** before the transcript lands. On a busy dashboard that is a row that changes under you.
- **Cost is per-minute and paid before value is known.** A memo that turns out to be noise still bills. The cap bounds a single message, not a bad day.
- **Audio leaves the network** while `Speech:BaseUrl` points at a cloud provider — a deeper version of the gap ADR 0007 / NfA-P1 already track for text, since voice identifies the speaker. D1 makes the fix a config change, but it is not made by this issue.
- **Transcription quality is unmeasured.** Routing depends on the transcript; a bad transcript misroutes silently, and there is no signal distinguishing "classified badly" from "heard wrong".

## Follow-up

- Point `Speech:BaseUrl` at a local `faster-whisper-server` on CT 136 — closes the ADR 0007 gap for audio, config-only.
