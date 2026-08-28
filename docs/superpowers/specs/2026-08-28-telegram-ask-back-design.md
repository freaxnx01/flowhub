# Telegram ask-back — design

**Date:** 2026-08-28
**Status:** Design, awaiting review
**Scope:** Two independent changes — (A) persist the caption of an attachment Capture, (B) ask the
operator a clarifying question when a Telegram-originated Capture resolves to Unhandled or Orphan.

---

## 1. Problem

A Telegram Capture that FlowHub cannot route ends as `Unhandled` or `Orphan`. Today the only signal is a
reaction emoji (🤔 / 💔) on the original message. The information needed to route it usually exists —
in the operator's head, and often in words they already typed — but there is no way to ask for it.

The motivating case, from `docs/ai-notes/2026-08-25-telegram-capture-taxonomy.md` §4 cluster 19: a
screenshot of the Tschau Sepp game with the caption *"Jetz hani de bewiis. 2x 8i gspilt vom compi.
Nachenand."* — a real bug report with evidence. It is currently unroutable twice over: the caption is
discarded at ingestion, and nothing asks what the image shows.

Two failures, one visible and one not:

1. **The caption is thrown away.** `EfCaptureService.SubmitAsync(content, source, attachment, …)` ignores
   `content` when an attachment is present and stores the **filename** as `Capture.Content`
   (`source/FlowHub.Persistence/EfCaptureService.cs:56-63`). The operator's own words are lost before
   the classifier ever sees them.
2. **Nothing asks.** The Channel can already speak — `ITelegramGateway.SendTextAsync` is used for
   rejection messages — but a resolved-unhandled Capture produces only an emoji.

---

## 2. Goals / non-goals

**Goals**

- Persist an attachment Capture's caption as its content.
- When a Telegram Capture resolves to Unhandled/Orphan, ask one question in-chat and use the reply to
  re-run classification.
- Change no lifecycle stage, no classifier, no skill.

**Non-goals** (explicitly out of scope; each is its own future work)

- Repo inference from capture content.
- Vision / multimodal classification — `IClassifier.ClassifyAsync` keeps its `string` signature.
- Forge-issue routing, `AllowedSkills`, Bridge configuration.
- Multi-turn conversation. Exactly one question, one answer.

**Success criterion.** Send the Tschau Sepp screenshot with its caption; the caption is the Capture's
content. If it still resolves Unhandled, the bot replies asking what it is; answering that reply
re-runs the pipeline against caption + answer, without creating a second Capture.

---

## 3. Part A — persist the caption

### Change

`EfCaptureService.SubmitAsync(string? content, ChannelKind source, AttachmentInput? attachment, …)`:
use `content` as the Capture's content when it is non-blank, falling back to the file name otherwise.
The file name remains available on `Attachment.FileName`, so nothing is lost either way.

Remove the now-inaccurate comment and `LogDroppingCaption` (EventId 5003) from
`TelegramUpdateHandler.HandleFileAsync`.

### Blast radius

`SubmitAsync` is shared with the Web upload path, which today also discards its caption. The fix
improves both consistently. Stored Captures are untouched — this changes only what new rows record.

### Tests

- Attachment + non-blank caption → `Content` is the caption; `Attachment.FileName` is the file name.
- Attachment + null/whitespace caption → `Content` is the file name (existing behaviour preserved).
- Attachment metadata (content type, size, relative path) unchanged in both cases.
- `TelegramUpdateHandler` no longer logs 5003 when a caption is present.

Part A ships on its own merit and does not depend on Part B.

---

## 4. Part B — the ask-back

### 4.1 Trigger

`TelegramReactionCaptureServiceDecorator` already wraps `MarkUnhandledAsync` and `MarkOrphanAsync` and
calls `TelegramReactionService.ApplyAsync`. The ask-back hangs off the same two hooks. No new lifecycle
stage, no pipeline change, no classifier change.

The reaction stays exactly as it is; the question is additional.

### 4.2 Components

| Component | Change |
|---|---|
| `TelegramMessage` | add `int? ReplyToMessageId` |
| `TelegramMessageMapper` | populate it from Telegram's `reply_to_message.message_id` |
| `ITelegramQuestionRepository` + entity | new: `(CaptureId, ChatId, QuestionMessageId, AskedAt)` |
| `TelegramQuestionService` | new: decides whether to ask, sends, records |
| `TelegramUpdateHandler` | branch: a reply to a recorded question is an **answer**, not a new Capture |
| `ICaptureService` | new method to append answer text to an existing Capture |

The question record mirrors `TelegramUpdate` in shape and lifetime; it is a separate table because its
key is the **outbound** message id, not the inbound update id.

### 4.3 Flow — asking

1. A Capture reaches `Unhandled`/`Orphan`; the decorator calls the reaction service (unchanged) and then
   `TelegramQuestionService.AskIfUsefulAsync(captureId, stage)`.
2. The service resolves the originating message via `ITelegramUpdateRepository.FindByCaptureIdAsync`.
   **No row → no question** (the Capture did not come from Telegram).
3. **Ask-once guard:** if a question already exists for this Capture, stop. One question per Capture,
   ever — a second failed pass reacts but stays silent.
4. `SendTextAsync` posts the question **as a reply to the operator's original message**, so the thread is
   visually anchored and the reply carries `reply_to_message_id`.
5. Record `(CaptureId, ChatId, QuestionMessageId, AskedAt)`.

Question text is fixed, not generated — it must not imply FlowHub understood more than it did:

> *"I couldn't route this one. What is it — and which project or repo does it belong to?"*

### 4.4 Flow — answering

In `TelegramUpdateHandler.HandleAsync`, **before** the existing new-Capture path:

1. If `ReplyToMessageId` is set and matches a recorded question → this is an answer.
2. Append the answer to the target Capture's content.
3. Reset and republish, mirroring `CaptureRetryEndpoint` (`source/FlowHub.Api/Endpoints/CaptureRetryEndpoint.cs:52-58`):
   `ResetForRetryAsync` sets `Stage = Raw` and clears `FailureReason` but **does not publish** — the
   endpoint publishes `CaptureCreated` itself, and so must this path.
4. Record the inbound update with the **existing** `CaptureId` (not null, not a new one), so dedup and
   reactions continue to work and the answer is auditable.
5. No new Capture is created.

**`HasAttachment` must be set correctly on the republished event.** `CaptureRetryEndpoint:58` publishes
`CaptureCreated(id, content, source, createdAt)` with `HasAttachment` defaulting to **false**, which is
wrong for an attachment Capture. This spec's answer path must pass the real value — and the same latent
defect in the retry endpoint should be fixed or filed separately (see §7).

### 4.5 Appending the answer

The Capture's content becomes the original content followed by the answer, separated by a blank line.

Chosen over a dedicated field because a dedicated field means touching the entity, a migration, the
detail page and the classifier's input assembly — disproportionate for one line of extra text. The
trade-off is accepted explicitly: the original and the answer are not separable afterwards. Revisit if a
second conversational feature needs the distinction.

### 4.6 Error handling

Identical posture to `TelegramReactionService`: **best-effort, logged, never thrown.**

- A failed send must not fail the lifecycle transition that triggered it. Catch `HttpRequestException`
  and non-cancellation `OperationCanceledException`, log, return.
- Recording the question after a successful send means a crash between the two costs one lost question,
  not a duplicate — consistent with the handler's existing submit-then-record ordering.
- An answer whose Capture has since been deleted is a no-op.

### 4.7 Guards

- **Allow-list**: answers pass through the existing `IsAllowed` check unchanged.
- **Ask-once**: enforced by the presence of a question row.
- **Telegram-only**: no `TelegramUpdate` row means no question, so Web and API Captures are unaffected.
- **No nagging**: FlowHub never initiates; it only replies to something the operator sent.

### 4.8 Tests

Unit (xUnit + NSubstitute + FluentAssertions):

- Unhandled Capture with a Telegram origin → one question sent, one row recorded.
- Second resolve of the same Capture → no second question.
- Capture with no `TelegramUpdate` row → no question.
- Gateway throws → lifecycle transition still completes, warning logged.
- Reply matching a recorded question → content appended, `CaptureCreated` republished with the correct
  `HasAttachment`, **no new Capture**.
- Reply to an unrelated message → ordinary new Capture (existing behaviour).
- Reply from an unlisted user → rejected by the allow-list, recorded, no answer applied.

Integration: question repository round-trip (Testcontainers, per the stack overlay).

---

## 5. What this does and does not fix

**Fixes:** the caption loss; the silence after an unroutable Capture; the "ask for what the user already
sent" failure the ask-back would otherwise cause.

**Does not fix:** the Tschau Sepp capture still cannot become a forge issue. That needs repo inference,
`"Bridge"` in `AllowedSkills`, and a configured Bridge — see the taxonomy report §5.1 and §10. This
design makes that capture *answerable*, not *routable*.

---

## 6. Rollout

Part A and Part B are separate PRs, in that order. Part B is inert unless the Telegram Channel is
configured (its services register only alongside the Channel), so it is safe to merge ahead of any
deployment change.

---

## 7. Related defect, not fixed here

`CaptureRetryEndpoint:58` publishes `CaptureCreated` without `HasAttachment`, so retrying an attachment
Capture tells the pipeline it has none. Found while writing this spec, out of scope for it — worth its
own issue.

---

## 8. Open questions

- Should the question be sent for `Orphan` as well as `Unhandled`, or only `Unhandled`? Orphan means
  "classified as nothing", which is arguably the case most worth asking about — but it is also the
  stage a deliberately unroutable note lands in. Defaulting to **both**; narrow if it proves noisy.
- Is a fixed question sufficient, or should it name what FlowHub does know (the file type, the title it
  guessed)? Fixed text is the safer start and avoids implying false understanding.
