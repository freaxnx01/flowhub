# Telegram Inbound Channel — Design

**Date:** 2026-08-24
**Status:** Approved (brainstorm)
**Issue:** [#20](https://github.com/freaxnx01/flowhub/issues/20)
**Affects:** `source/FlowHub.Telegram/` (new), `source/FlowHub.Core/Channels/`, `source/FlowHub.Persistence/`, `source/FlowHub.Web/Program.cs`, `Directory.Packages.props`

---

## Problem

`ChannelKind.Telegram` exists in the domain, `Captures.razor` renders a Send icon
for it, and the empty state tells the operator to "send something via Telegram" —
but there is no implementation. No project in `source/`, no bot process, no token
handling. `docs/spec/system-context.md` draws `FlowHub.Telegram` in the C4 diagram
and then lists it under "Planned, not yet scaffolded".

Telegram is the capture surface the operator actually wants: content arrives on a
phone, from anywhere, without opening a dashboard.

## Goal

A Telegram bot that turns messages into Captures through the same pipeline the Web
Channel uses, gated to the operator alone, and that marks each message in-chat with
the outcome of its Capture — so the chat itself shows what has been processed.

## Non-goals

- **Voice memos / speech-to-text.** FlowHub has no transcription capability today
  (verified: no Whisper/STT reference anywhere in `source/`). Choosing an STT
  provider is its own decision — cost per minute, local vs. cloud, model quality —
  and belongs in its own spec. Voice gets a "not supported yet" reply here and a
  follow-up issue.
- **Webhook transport.** Long polling only (see D1).
- **Group chats and multi-user.** Single operator, private chat.
- **Editing or deleting Captures from Telegram.** Ingest and acknowledgement only.

---

## Decisions (locked during brainstorm)

### D1 — Transport: long polling

A `BackgroundService` calls `getUpdates` in a loop. Outbound-only: no public
ingress, no TLS certificate, no webhook secret.

The homelab deployment (CT 136, local-only, `flowhub.home.freaxnx01.ch`) has no
public entry point, so a webhook would require new infrastructure before a single
Capture could arrive. Polling needs none.

Rejected: **webhook** (needs public HTTPS ingress + secret token), and
**both, configurable** (double the surface; the repo guardrails forbid unrequested
configurability).

### D2 — Access control: allow-list of Telegram user IDs

`Telegram__AllowedUserIds` holds numeric Telegram user IDs. An update whose
`message.from.id` is not on the list is **acked and dropped** — no Capture, no
reply — with a warning logged.

Acked, not ignored: leaving it unconfirmed would make Telegram redeliver it
forever. Dropped silently rather than answered, so the bot does not confirm its own
existence to a stranger.

A bot's username is discoverable, so without this the bot is an open write endpoint
into the homelab — junk Captures and LLM classification spend on attacker-supplied
content.

Rejected: **first-sender-wins pairing** (adds a pairing table, a first-contact race,
and a reset path), and **accept from anyone**.

### D3 — Content scope: text, photos, documents

| Message type | Behaviour |
|---|---|
| Text (incl. URLs) | `message.text` → `Capture.Content` |
| Photo | `getFile` → download → `AttachmentInput`, caption → `Content` |
| Document | as photo, subject to the upload policy |
| Voice, video, sticker, location, poll | "not supported yet" reply, acked, no Capture |

Attachments reuse the existing `IUploadPolicy` (2 MB default; `application/pdf`,
`image/png`, `image/jpeg`). A file exceeding `MaxBytes` or carrying a disallowed
content type gets a specific reply naming the limit, not a generic failure.

### D4 — Hosting: in-process `BackgroundService`

`FlowHub.Telegram` is a class library. `FlowHub.Web` hosts it as an
`IHostedService`; it calls `ICaptureService` directly through DI.

**This resolves a contradiction in the existing docs.** ADR 0001 §2 lists
`FlowHub.Telegram` as "(bot, separate process)" consuming the REST API, while
`docs/spec/system-context.md` shows it reaching `FlowHub.Core` over "In-process DI —
Same process, same pattern". `system-context.md` wins: it matches the modular
monolith, avoids issuing a service credential for the `.RequireAuthorization()`
capture endpoints, and keeps `docker-compose.yml` at one `flowhub.web` service.
ADR 0001 gets an "As built" note recording the change, the same device ADR 0002 and
ADR 0003 already use.

Rejected: **separate container** (needs a credential, a second image, and its own
read path back to lifecycle changes), and **"library now, splittable later"**
(speculative flexibility, untested until actually split).

### D5 — Client: the `Telegram.Bot` NuGet package

One new `PackageVersion` entry in `Directory.Packages.props`. v1 needs five
endpoints — `getUpdates`, `setMessageReaction`, `sendMessage`, `getFile`, file
download — plus `Update`, `Message`, `User`, `Chat`, `PhotoSize`, `Document` and
`ReactionTypeEmoji`. Hand-rolling that is meaningful surface area for no
differentiation, and the reaction allow-list is exactly the sort of detail a typed
client gets right.

Approved explicitly, per the "do not install additional NuGet packages without
asking first" guardrail.

### D6 — Acknowledgement: emoji reaction via an `ICaptureService` decorator

**There is no "mark as read" for bots.** Read receipts are an MTProto (user-client)
concept; the Bot API has no equivalent. "Processed" is state FlowHub owns.

The bot reacts to the operator's original message with `setMessageReaction` when the
Capture reaches a terminal stage:

| `LifecycleStage` | Reaction |
|---|---|
| `Completed` | 👍 |
| `Orphan` | 💔 |
| `Unhandled` | 🤔 |

Bots may set **only one reaction per message**, so the marker replaces itself as the
lifecycle advances rather than accumulating — the desired behaviour. The emoji must
come from `ReactionTypeEmoji`'s fixed allow-list, which does **not** contain ✅, ⚠️
or ❓; the three above are chosen from what is permitted.

Lifecycle resolution happens at six call sites across `SkillRoutingConsumer`,
`CaptureEnrichmentConsumer` and `LifecycleFaultObserver`, all through
`ICaptureService.Mark*Async`, and **no event is published on completion**. So the
Telegram module registers a **decorator** over `ICaptureService` that intercepts
`MarkCompletedAsync` / `MarkOrphanAsync` / `MarkUnhandledAsync`, resolves the chat
and message coordinates by `CaptureId`, and fires the reaction. Zero edits to shared
pipeline files; the repo already decorates `ISkillRegistry` this way in
`AddE2EFaultInjectionIfEnabled`.

A plain reply was rejected as too noisy for a chat used as a capture inbox.

**Race — the lifecycle can resolve before the coordinates are stored.**
`EfCaptureService.SubmitAsync` publishes `CaptureCreated` itself
(`EfCaptureService.cs:42,68`), so with the in-memory transport the whole pipeline
can finish *before* `SubmitAsync` returns — i.e. before the handler has written the
`TelegramUpdate` row the decorator needs. A decorator alone would silently drop the
reaction whenever the pipeline outran the row write.

Resolution: both sides call one idempotent
`ApplyReactionAsync(captureId)` helper, and a missing row is a no-op.

1. The **decorator** calls it after each `Mark*Async` — the normal path, when the
   row already exists.
2. The **handler** calls it once more, after recording the row, if the Capture is
   already at a terminal stage — the path where the pipeline won the race.

`setMessageReaction` *sets* the single reaction rather than appending, so applying
it twice is harmless. This is why the operation must be idempotent by construction
rather than guarded by a lock.

Rejected: a **new `CaptureLifecycleResolved` event** (edits three shared pipeline
files and widens the vocabulary ADR 0003 §2 capped at two), and a **reconciliation
poll** (a second loop, a recurring query, and reaction lag).

### D7 — Idempotency: one table for dedup and coordinates

`TelegramUpdateEntity` — `UpdateId` (PK), `ChatId`, `MessageId`, `CaptureId`
(nullable), `ProcessedAt` — in `FlowHub.Persistence/Entities/` with an
`IEntityTypeConfiguration` and one migration. The port
`ITelegramUpdateRepository` lives in `FlowHub.Core/Channels/`, beside the existing
`IChannelRepository`.

One table does both jobs: the primary key is the dedup guard, and the row carries
the coordinates D6 needs to react later. `CaptureId` is nullable because rejected
and unsupported updates are recorded too — they must not be reprocessed.

**Ordering:** submit the Capture → record the update row → *then* advance the
offset. A crash replays the batch instead of losing a Capture, and the dedup row
makes the replay harmless.

**Known limitation.** The Bot API states: "If there are no new updates for at least
a week, then identifier of the next update will be chosen randomly instead of
sequentially." So `update_id` is a sound dedup key but **not** a durable monotonic
high-water mark. The offset is therefore the last update processed *chronologically*
(`ProcessedAt DESC`), never `MAX(UpdateId)`. The residual edge case — a week of
inactivity *and* a restart *and* a lower random id — is accepted and documented
rather than engineered around.

---

## Architecture

```text
Telegram Bot API
      │  getUpdates (long poll, outbound only)
      ▼
FlowHub.Web (host process)
 └── TelegramPollingService : BackgroundService
       └── TelegramUpdateHandler
             ├── allow-list filter        (D2)
             ├── dedup check              (D7)
             ├── ICaptureService.SubmitAsync(…, ChannelKind.Telegram, …)
             └── record TelegramUpdate row

 ... capture pipeline runs (enrich → classify → route) ...

 └── TelegramReactionDecorator : ICaptureService     (D6)
       └── on Mark{Completed,Orphan,Unhandled}Async
             └── setMessageReaction(chatId, messageId, 👍 / 💔 / 🤔)
```

### Files

```text
source/FlowHub.Telegram/                      (new class library)
  TelegramOptions.cs                          SectionName "Telegram"; IsConfigured
  TelegramPollingService.cs                   the getUpdates loop
  TelegramUpdateHandler.cs                    one update → Capture
  TelegramReactionDecorator.cs                lifecycle → reaction
  TelegramServiceCollectionExtensions.cs      AddFlowHubTelegram()

source/FlowHub.Core/Channels/
  ITelegramUpdateRepository.cs                port

source/FlowHub.Persistence/
  Entities/TelegramUpdateEntity.cs
  Entities/TelegramUpdateEntityTypeConfiguration.cs
  Repositories/EfTelegramUpdateRepository.cs
  Migrations/<generated-timestamp>_0002_TelegramUpdates.cs   (dotnet ef stamps the name)

tests/FlowHub.Telegram.UnitTests/             (new xUnit project)
```

## Configuration

| Variable | Required | Meaning |
|---|---|---|
| `Telegram__BotToken` | yes | BotFather token. **Secret — env var only, never `appsettings`.** |
| `Telegram__AllowedUserIds` | yes | Comma-separated numeric Telegram user IDs |

`AddFlowHubTelegram()` registers nothing unless `IsConfigured` (both values present),
mirroring `AddFlowHubDemoNotifications()`. An unconfigured FlowHub — including CI and
the agent-dev trial — never contacts Telegram.

## Error handling

- **Poll failure** — log, exponential backoff, never take down the host.
- **`409 Conflict`** (a webhook is registered, so `getUpdates` is refused) — log a
  specific, actionable error naming `deleteWebhook`, not a generic HTTP failure.
- **`401 Unauthorized`** — log that the token is invalid and stop the loop; retrying
  a bad token forever is noise.
- **Submit failure** — do not record the row, do not advance the offset; the update
  is redelivered.
- **Reaction failure** — log a warning and continue. **A failed reaction must never
  fail the lifecycle transition** it decorates.
- **Oversized / disallowed attachment** — reply naming the actual limit; record the
  row so it is not reprocessed.

## Testing

`tests/FlowHub.Telegram.UnitTests` (xUnit + FluentAssertions + NSubstitute), with the
Telegram client substituted — **no live API calls in any test**:

- Allow-listed user → `SubmitAsync` called once with `ChannelKind.Telegram`
- Non-allow-listed user → no Capture, update still recorded (acked)
- Replayed `update_id` → no second Capture
- Text, photo, and document each map to the expected `Content` / `AttachmentInput`
- Voice → "not supported yet" reply, no Capture
- Oversized attachment → limit named in the reply
- `Completed` / `Orphan` / `Unhandled` → 👍 / 💔 / 🤔
- Reaction throwing → inner `Mark*Async` still committed
- Lifecycle already terminal when the row is recorded → the handler applies the
  reaction (the race in D6)
- `ApplyReactionAsync` with no matching row → no-op, no throw

## Documentation updates

- **ADR 0001** — "As built" note on §2 recording in-process hosting (D4).
- **`docs/spec/system-context.md`** — move Telegram out of "Planned, not yet
  scaffolded" and "Not yet wired".
- **`docs/glossary.md`** — no change needed; `Channel` already covers this.

## Follow-up

- **Voice capture via speech-to-text** — its own issue and spec (see Non-goals).
