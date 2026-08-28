# Attachment Captions — Design

**Date:** 2026-08-28
**Status:** Approved (brainstorm)
**Issue:** [#31](https://github.com/freaxnx01/flowhub/issues/31)
**Affects:** `source/FlowHub.Persistence/EfCaptureService.cs`, `source/FlowHub.Web/Components/Pages/NewCapture.razor.cs`, `source/FlowHub.Web/Components/Pages/Captures.razor`, `source/FlowHub.Web/Components/DashboardCards/RecentCapturesCard.razor`, `source/FlowHub.Telegram/TelegramUpdateHandler.cs`

---

## Problem

A Capture submitted with an attachment loses whatever text came with it. The caption is usually the actual note — *"invoice for the boiler service"*, *"the bit on page 3"* — and the file is just the evidence.

It is discarded in three different places, which is why it reads as one bug and is really three:

| Where | What happens |
|---|---|
| `EfCaptureService.cs:62` | `Content` is set to `fileName`; the `content` argument is ignored |
| `NewCapture.razor.cs:85` | Web passes `content: null` — and the form *disables* its text field once a file is staged (see D5) |
| `CaptureWriteEndpoints.cs` `/upload` | The multipart request has no caption field at all |

Telegram is the exception: `TelegramUpdateHandler` already passes `message.Text`, and logs a debug line saying the caption is being dropped.

## Goal

Text submitted alongside an attachment is preserved on the Capture, reaches the classifier and the embedder, and is visible in the UI — without losing the filename or making attachments harder to spot in a list.

## Non-goals

- **The REST API's `/upload` endpoint.** Adding a caption field there changes a documented public request contract and needs OpenAPI + Bruno updates. No non-UI consumer is asking for it. Deferred deliberately; the API stays inconsistent with the other channels until then.
- **Backfilling existing Captures.** Not possible — captions were never persisted, so there is no data to recover. See *Consequences*.
- **Re-embedding existing Captures.** `/api/v1/admin/embeddings/rebuild` would regenerate embeddings from unchanged content and change nothing.

---

## Decisions (locked during brainstorm)

### D1 — The caption becomes `Content`; the filename is the fallback

```csharp
var content = string.IsNullOrWhiteSpace(caption) ? fileName : caption.Trim();
```

Whitespace-only is treated as absent, so a stray space cannot produce a blank-looking Capture. With no caption the behaviour is byte-for-byte what it is today.

**Nothing is lost.** `Attachment.FileName` already stores the filename (`EfCaptureService.cs:60`), so today's `Content = fileName` is pure duplication. The change replaces a duplicated value with the one piece of information that was being thrown away.

`Content` is what the classifier (`CaptureEnrichmentConsumer.cs:52`) and the embedder (`CaptureEmbeddingConsumer.cs:35`) read, so this is the field the caption has to reach to matter.

Rejected: **`EnrichmentDescription`** — it is written by the enricher, not by submitters; using it for inbound text overloads its meaning, and the classifier would still see only a filename. Rejected: **combining both into `Content`** — it bakes a display concern into stored data, makes the separator a parsing hazard, and glues filename noise onto every caption the classifier reads.

### D2 — Scope: Core and Web. Telegram needs no change. The API is deferred

- **Core** — the fix above.
- **Telegram** — already passes `message.Text`; the Core fix alone makes it work.
- **Web** — see D5. Larger than first assessed.
- **API** — out of scope (see Non-goals).

### D3 — Attachments get an explicit icon in the grids

Neither `Captures.razor` nor `RecentCapturesCard.razor` has an attachment indicator today. An attachment row is currently recognisable **only because its `Content` looks like a filename** — an accident of the bug, not a design.

Once `Content` is a caption, that accidental marker disappears and attachments become indistinguishable from plain text captures. So both grids gain an `Icons.Material.Filled.AttachFile` icon beside `Content` when `Attachment is not null`, with the filename as its tooltip.

This is not scope creep: without it the change introduces a real regression in list scannability.

Rejected: **no UI change** (accepts the regression); **caption plus filename in the cell** (two lines per row in a dense grid, and it re-creates the mixing rejected in D1, just in the view).

### D4 — The test that pins the current behaviour is inverted deliberately

`tests/FlowHub.Persistence.Tests/EfCaptureServiceAttachmentTests.cs:24-26` asserts the bug:

```csharp
var capture = await sut.SubmitAsync(content: "ignored typed text", ChannelKind.Web, input);
capture.Content.Should().Be("invoice.pdf");
```

The parameter is literally named *"ignored typed text"*, so the behaviour was understood and pinned.

`CLAUDE.md` says **never modify a test to make it green — fix the implementation.** That rule is about bending a test to hide a defect. This is the opposite: the specification changed, and the test encodes the old one. It must be updated, and the implementer should not read the rule as a reason to preserve the bug. Any implementation that keeps this assertion passing has not done the work.

The updated test asserts the caption wins, and a **new** test covers the no-caption path so the fallback stays pinned.
### D5 — Web re-enables the text field as a caption

**Corrected during planning.** An earlier reading of D2 called this a one-line call-site fix. It is not. `NewCapture.razor:26,29` *deliberately* disables the text area when a file is staged and tells the user **"File overrides text"**:

```razor
HelperText="@(_stagedFile is null ? "…" : "File overrides text")"
Disabled="_isSubmitting || _stagedFile is not null"
```

So `content: null` at the call site is not an oversight — it is consistent with a UI that refuses captions by design, and `tests/FlowHub.Web.ComponentTests/Pages/NewCaptureUploadTests.cs` pins that behaviour in `StagingFile_DisablesTextAreaAndShowsHelperText`.

Passing `_content` without touching the UI would be worse than today: the field is disabled, not cleared, so it would submit whatever the user typed *before* staging the file.

The Web change is therefore:

1. Stop disabling the text area when a file is staged — `Disabled="_isSubmitting"`.
2. Change the staged-file helper text from `"File overrides text"` to `"Caption (optional) — describe the file"`.
3. Pass `_content` as the caption.
4. Invert `StagingFile_DisablesTextAreaAndShowsHelperText`, which asserts the behaviour being replaced — the same deliberate-inversion reasoning as D4.

This brings Web in line with Telegram, where sending a file with a caption is the ordinary case.

Rejected: **dropping Web from the issue** (leaves a user unable to attach a note to a file — the original complaint); **keeping the disable but clearing `_content`** (removes the stale-text hazard while entrenching the no-captions decision, and throws away text already typed).


---

## Data flow

```text
submit with attachment + caption
        │
        ▼
EfCaptureService.SubmitAsync
        ├── attachment bytes → IAttachmentStorage        (unchanged)
        ├── Attachment { FileName, ContentType, … }      (unchanged — filename lives here)
        └── Capture.Content = caption ?? fileName        ← D1
                    │
                    ├──► CaptureEnrichmentConsumer → IClassifier   (now sees real text)
                    ├──► CaptureEmbeddingConsumer  → embeddings    (now semantic)
                    └──► Captures grid / RecentCapturesCard        + 📎 icon (D3)
```

## Error handling

No new failure modes. The attachment-storage rollback on a repository failure (`EfCaptureService.cs:73-77`) is untouched, and a caption cannot fail independently of the submit it arrives with.

## Testing

- `SubmitAsync` with an attachment **and** a caption → `Content` is the caption (inverted from D4)
- `SubmitAsync` with an attachment and **no** caption → `Content` is the filename (new; pins the fallback)
- `SubmitAsync` with an attachment and a **whitespace-only** caption → `Content` is the filename
- A caption with surrounding whitespace is trimmed
- `Attachment.FileName` is the filename in every case above
- bUnit: a Capture with an attachment renders the attach icon in `Captures.razor` and `RecentCapturesCard.razor`; one without renders no icon
- Web: the text area stays enabled when a file is staged, and shows the caption helper text (inverts `StagingFile_DisablesTextAreaAndShowsHelperText`)
- Web: `NewCapture` submits `_content` as the caption alongside a staged file

## Consequences

Effects nobody chose, inherited by the change:

- **Routing changes for attachment captures.** They currently classify on a filename and will classify on real text. This is the point of the fix, but the same file submitted before and after can route to a different Skill — existing attachment Captures were very likely misrouted, and this does not retroactively correct them.
- **Embeddings improve going forward only.** New captures embed the caption; existing rows keep filename-derived embeddings and will remain poor semantic-search hits. No backfill is possible.
- **Search behaviour shifts.** `Captures.razor.cs:73` filters on `Content`, so filename search stops matching attachment captures that have a caption. The filename remains on the Attachment but is not searched.
- **The API and the other channels diverge** until `/upload` gains a caption field.

## Follow-up

- Add an optional caption field to `POST /api/v1/captures/upload` — its own issue, since it changes a public request contract.
