# Design — Signal Note-to-Self Import

**Issue:** #8 · **Date:** 2026-07-12 · **Status:** approved (brainstorm)

## Purpose

Bulk-import the user's Signal **Note-to-Self** messages — used as a personal capture
inbox — into FlowHub as Captures, so the existing classify → route pipeline (and the
downstream training-mode issues #9/#10) can work over a large real backlog.

This is a **one-time backfill** capability (re-runnable if a fresh export is produced),
**not** a live/recurring Signal channel.

## Source format (reverse-engineered from the user's real export)

The export is a **Signal Desktop native backup** `.zip` containing:

```
<name>/metadata.json      {"version":1}
<name>/main.jsonl         NDJSON — line 1 header, remaining lines are frames
<name>/files/<xx>/<stem>.<ext>   media, stored as PLAINTEXT (see "Media")
```

`main.jsonl` frames (one JSON object per line):

- **header** (line 1): `{backupTimeMs, currentAppVersion, mediaRootBackupKey, version}`.
- **`recipient`**: one carries a `self` marker (`{"id":"20","self":{...}}`) — this identifies the account itself.
- **`chat`**: `{id, recipientId, ...}` — the Note-to-Self chat is the one whose `recipientId` equals the `self` recipient's id.
- **`chatItem`**: a message. Relevant shape:
  ```json
  {"chatItem": {"chatId": "20", "authorId": "...", "dateSent": "1734989094736",
    "outgoing": {...},
    "standardMessage": {
      "text": {"body": "..."},
      "linkPreview": [{...}],                     // present ⇒ URL note
      "attachments": [{"pointer": {"contentType": "image/jpeg", "fileName": null,
        "locatorInfo": {"plaintextHash": "<b64 32B>", "localKey": "<b64 64B>", "size": 236898, ...}}}]
    }}}
  ```
- Non-message chatItems carry `updateMessage` or `remoteDeletedMessage` instead of `standardMessage` — **skipped**.

### Facts established against the real export (263 Note-to-Self messages)

| Fact | Value |
|---|---|
| Note-to-Self chatId | `20` in this export — **must be derived** via `self` recipient → chat, never hardcoded |
| Message text | `standardMessage.text.body` |
| Timestamp | `chatItem.dateSent` — **JSON string**, epoch **milliseconds** |
| Stable message id | **none exists**; identity = `dateSent` (Signal's canonical per-message id) |
| Attachments per message | **at most 1** (36/36 single) — the single-`Attachment?` model fits every message |
| Attachment filename | almost always `null` (1/36) → derive `signal-{dateSent}.{ext}` from `contentType` |
| Content breakdown | 150 plain text · 71 text+URL · 35 attachment-only · 1 text+attachment · 6 non-standard (skip) |

### Media are plaintext (no decryption)

Proven against all 84 media files in the export: the on-disk bytes are the **plaintext image**.
The filename is the only obfuscation:

```
stem = SHA256( plaintextHash(32B) ‖ localKey(64B) )        // concat, plaintextHash first
path = files/<stem[:2]>/<stem>.<ext>
verify: SHA256(file_bytes) == plaintextHash                // 84/84 matched
        file_bytes.length  == locatorInfo.size             // 84/84 matched
```

`mediaRootBackupKey`, `key`, and the transit-CDN fields are **not needed** for local files
(they only matter when fetching the encrypted blob from Signal's CDN). No AES on the happy path.

## Scope

**In scope**

- Admin endpoint that accepts the whole export `.zip` and imports Note-to-Self messages.
- One Capture per message (`Source=Signal`, `Stage=Raw`), pushed through the **full AI pipeline**
  via the existing `CaptureCreated` publish.
- Media import: resolve + integrity-check plaintext bytes → existing `AttachmentInput` → Paperless path.
- Original message timestamp as `CreatedAt`; `ExternalRef` idempotency key.
- Time-proximity **grouping** via a new nullable `ImportGroupId` (approach "B").
- Notification suppression for Signal-sourced captures.
- Small Blazor admin upload page showing result counts.

**Out of scope** (noted on the issue)

- Live/recurring Signal channel or Channel-registry health entry.
- Merged multi-attachment capture model (approach "A") — possible follow-up.
- Non-Note-to-Self chats; HTML/CSV/third-party exports.
- Encrypted-CDN media path (kept only as a dormant, unimplemented fallback note).

## Architecture

Reuse `Capture` and the existing classify → route pipeline. No separate table, no separate
pipeline, no new `LifecycleStage`. "Imported" = `Source == Signal`; the untriaged tail =
existing `Stage == Orphan`.

### Components

1. **`ChannelKind.Signal`** — append to the enum (`source/FlowHub.Core/Captures/ChannelKind.cs`).
   No migration: `Source` is stored as `varchar(32)`.

2. **Signal parser** (new, `source/FlowHub.Skills/Signal/` or a dedicated `FlowHub.Import` area —
   follow existing placement; keep it a library type with no ASP.NET dependency so it unit-tests
   cleanly). Responsibilities:
   - Read `main.jsonl` line-by-line (streaming; the file is small but keep it streamable).
   - Resolve the Note-to-Self `chatId` via the `self` recipient → matching `chat`.
   - Emit an ordered sequence of `SignalMessage` value objects: `{ DateSentMs, Text?, Attachment? }`
     where `Attachment` is `{ ContentType, PlaintextHashB64, LocalKeyB64, Size, FileName? }`.
   - Skip non-standard chatItems; use the current revision of edited messages.

3. **Signal media resolver** (new): given `{ plaintextHash, localKey, contentType }` and the
   extracted archive root, compute `stem = SHA256(plaintextHash ‖ localKey)`, locate
   `files/<stem[:2]>/<stem>.*`, read bytes, and verify `SHA256(bytes) == plaintextHash`. Returns
   an openable stream + derived filename. Throws a specific exception on mismatch/missing file.

4. **Import service** (new, orchestrates a run):
   - Extract the uploaded `.zip` to a temp dir (cleaned up in `finally`).
   - Parse → ordered messages.
   - Apply the **grouping** pass (below) to assign `ImportGroupId`s.
   - For each message: build `ExternalRef = signal:{chatId}:{dateSent}`, skip if
     `ExistsByExternalRefAsync`, else import via the new `ICaptureService.ImportAsync`.
   - Accumulate and return a `SignalImportResult`.

5. **`ICaptureService.ImportAsync`** (new seam) — the current `SubmitAsync` hardcodes
   `CreatedAt = UtcNow` and cannot set `ExternalRef`/`ImportGroupId`. Signature:
   ```csharp
   Task<Capture> ImportAsync(
       string? content, AttachmentInput? attachment,
       ChannelKind source, DateTimeOffset createdAt,
       string externalRef, Guid? importGroupId,
       CancellationToken ct = default);
   ```
   Builds the `Capture` at `LifecycleStage.Raw` with the provided values, persists via
   `ICaptureRepository.AddAsync`, saves attachment bytes via `IAttachmentStorage`, and publishes
   `CaptureCreated`. A dedicated method (not a flag/optional-arg bolt-on to `SubmitAsync`) per the
   repo's "no flag arguments / command-query" rules.

6. **`ICaptureRepository.ExistsByExternalRefAsync(string, CancellationToken)`** (new) —
   `Captures.AsNoTracking().AnyAsync(c => c.ExternalRef == externalRef, ct)`. Used for idempotency.
   No unique-index migration: a single admin import is not run concurrently with itself, so the
   existence check is sufficient; a partial unique index can be added later if needed.

7. **`Capture.ImportGroupId`** (new nullable `Guid?`) + EF column + migration
   `..._0014_AddImportGroupId` (nullable `uuid`, indexed). The domain `Capture` record gains the
   optional field; `CaptureEntity` + `CaptureEntityTypeConfiguration` + repository mapping updated.

8. **Admin endpoint** `POST /api/v1/admin/imports/signal`
   (`source/FlowHub.Api/Endpoints/AdminEndpoints.cs`) — inherits the `"Admin"` policy from the
   `/api/v1/admin` group; `.DisableAntiforgery()`, binds `IFormFile file` (the `.zip`); returns
   `Results<Ok<SignalImportResult>, ProblemHttpResult>` following the `RebuildEmbeddings` pattern.

9. **Notification suppression** — guard in
   `source/FlowHub.Web/Pipeline/CaptureNotificationConsumer.cs`:
   `if (context.Message.Source == ChannelKind.Signal) return Task.CompletedTask;`
   (`Source` is already a field on `CaptureCreated` — no event/plumbing change).

10. **Blazor admin page** — a small MudBlazor page: upload `.zip`, POST to the endpoint (or call
    the import service via DI), render `SignalImportResult` counts in a `MudDataGrid`/cards. bUnit tested.

### Grouping pass (approach B)

Walk the Note-to-Self messages in chronological order. For each **attachment-only** message,
attach it to the nearest **adjacent text** message when the time gap ≤ **120s** (`GroupingWindow`,
configurable): both members receive the same freshly-minted `ImportGroupId`. Text-with-text is
**never** fused. Messages that don't group keep `ImportGroupId = null`.

Grouping is organizational: each capture still classifies/routes independently (text → classifier,
attachment → Paperless). The `ImportGroupId` is a durable link reused by #9's triage view and #10's
clustering. (True merged "one thought = one capture" — approach A — is explicitly out of scope.)

### `SignalImportResult`

```csharp
public sealed record SignalImportResult(
    int Imported, int SkippedDuplicates, int SkippedNonStandard,
    int AttachmentsImported, int Groups, int Failed,
    IReadOnlyList<string> Errors);
```

## Data flow

```
POST .zip → extract temp dir
  → parse main.jsonl → ordered SignalMessage[]
  → grouping pass → ImportGroupId assignments
  → per message:
      externalRef = signal:{chatId}:{dateSent}
      if ExistsByExternalRefAsync(externalRef): SkippedDuplicates++; continue
      if attachment: resolve+verify bytes → AttachmentInput
      ImportAsync(text, attachment?, Signal, dateSent, externalRef, importGroupId)
        → Capture @ Raw → AddAsync → publish CaptureCreated
            → CaptureEnrichmentConsumer: attachment ⇒ Paperless; text ⇒ AI classify (else Orphan)
            → CaptureEmbeddingConsumer: embed
            → CaptureNotificationConsumer: suppressed for Signal
  → SignalImportResult
```

## Error handling

- **Bad zip / missing main.jsonl / no self-recipient** → 400-style `ProblemHttpResult` (validation).
- **Per-message failure** (media missing, hash mismatch, storage error) → caught, counted in
  `Failed`, message appended to `Errors`; the import continues (one bad row must not abort the batch).
- **Media hash mismatch** → specific exception, logged with the stem; that message counts as `Failed`.
- **Temp dir** cleaned in `finally`. Never leak extracted files.
- No generic `catch (Exception)` — catch the specific parse/IO/crypto exceptions.

## Testing (TDD — failing test first, per repo rules)

1. **Parser unit tests** (`tests/FlowHub.Skills.Tests` or the parser's test project) against a
   trimmed `main.jsonl` fixture: self-recipient→chat resolution, text extraction, link handling,
   attachment metadata, non-standard skip, edited-message current revision, `dateSent` string parse.
2. **Media resolver unit test**: `stem = SHA256(plaintextHash ‖ localKey)` mapping + integrity
   verification (fixture: a tiny real file from the export, or a synthetic hash-matched pair).
3. **Grouping unit tests**: attachment-within-120s-of-text groups; >120s does not; text+text never fuses.
4. **Repository test** (Testcontainers, `tests/FlowHub.Persistence.Tests`): `ExistsByExternalRefAsync`
   true/false; `ImportGroupId` round-trips; migration applies.
5. **API integration test** (`tests/FlowHub.Api.IntegrationTests`): POST a small fixture `.zip` →
   expected captures created with `Source=Signal`, correct `CreatedAt`/`ExternalRef`; **re-POST →
   all `SkippedDuplicates`** (idempotency); admin auth (operator → 403, admin → 200) via the
   `AdminEndpointsAuthTests` pattern.
6. **bUnit** (`tests/FlowHub.Web.ComponentTests`): admin upload page renders and shows result counts
   (using `Substitute.For<...>` for the import service), following `NewCaptureUploadTests`.

Run the **full** suite (`dotnet test FlowHub.slnx`) after implementation, not just the new tests.

## Acceptance criteria

- [ ] `ChannelKind.Signal` exists; a Signal-sourced capture round-trips through persistence and the pipeline.
- [ ] `POST /api/v1/admin/imports/signal` accepts the export `.zip`, requires the `Admin` policy, and returns a `SignalImportResult`.
- [ ] Note-to-Self `chatId` is derived from the `self` recipient (not hardcoded); non-Note-to-Self chats are ignored.
- [ ] Text and text+URL messages import as text captures with `CreatedAt` = original `dateSent` and `ExternalRef = signal:{chatId}:{dateSent}`.
- [ ] Attachment messages import as attachment captures: media located via `SHA256(plaintextHash ‖ localKey)`, bytes verified against `plaintextHash`, routed to Paperless via the existing path.
- [ ] `updateMessage`/`remoteDeletedMessage`/non-standard chatItems are skipped and counted in `SkippedNonStandard`.
- [ ] Re-importing the same export creates no duplicates — every message is counted in `SkippedDuplicates` on the second run (idempotent via `ExistsByExternalRefAsync`).
- [ ] An attachment-only message within 120s of an adjacent text note shares its `ImportGroupId`; text+text is never fused; ungrouped captures have `ImportGroupId = null`.
- [ ] Imported captures do **not** trigger `CaptureNotificationConsumer` notifications.
- [ ] A Blazor admin page uploads the `.zip` and displays the result counts.
- [ ] Full test suite passes: parser, media-resolver, grouping, repository (Testcontainers), API integration (incl. idempotent re-import + admin auth), and bUnit page tests.

## Open items deferred to enrichment/implementation

- Exact project placement of the parser/importer (new `FlowHub.Import` vs a folder under an existing
  library) — follow whatever keeps it ASP.NET-free and unit-testable; decide in the plan.
- Whether the Blazor page calls the import service directly (in-process) or POSTs to the endpoint —
  prefer in-process DI to match `QuickCaptureField`, with the endpoint as the API surface.
