# Sensitive captures must park, not route — design

**Issue:** [#93](https://github.com/freaxnx01/flowhub/issues/93)
**Date:** 2026-09-14
**Status:** Approved

## Problem

The classifier routes every capture it can classify. It has no notion that some captures
must not leave the machine.

Previewing a historical corpus (#11, zero writes) surfaced captures containing therapy
notes, a care journal concerning a child, medication details, and a full personal profile
of a named third party. All were classified as ordinary tasks and assigned to shared
projects. In two cases the chosen project made the exposure worse rather than neutral.

Had these been replayed rather than previewed, that material would now be sitting in a
shared task manager. This is the one defect on the list where being wrong is **not
reversible**: the classifier-quality defects (#92) cost wrong tasks that can be deleted;
this one costs disclosure.

### Why the existing mechanisms do not cover it

- **The glossary (#83) does not help.** It resolves shorthand; it says nothing about
  sensitivity.
- **The unknown-shorthand parking rule does not help.** It fires on unrecognised *tokens*.
  The most dangerous capture found in the corpus contains no medical vocabulary, no name
  and no marker — it reads as a note about driving and is in fact therapy material.
  Sensitivity is not detectable from the surface text by any keyword rule.
- **`Orphan` is not the same thing.** An orphan means "no skill matched". A sensitive
  capture may classify perfectly well; the point is that it must not be *sent* anywhere.

## Decisions

| # | Decision | Rejected alternative |
|---|---|---|
| D1 | A **dedicated pre-pass** decides sensitivity, not a field on the existing classification call | An extra field on `ClassificationResult` fails **open** — an omitted JSON field deserialises to `false`, which routes. It also puts disclosure protection behind the same model that scores 5/10 on project selection. |
| D2 | The verdict is **three-way** — `Sensitive` / `Unsure` / `Safe` — and only `Safe` routes | A binary verdict gives the model no way to express doubt, so doubt is expressed as a confident `false`. A confidence score is worse: LLM confidence is badly calibrated and the threshold is a knob with no principled setting. |
| D3 | A **new terminal `LifecycleStage.Withheld`**, not a reuse of `Unhandled` | `CaptureRetryEndpoint.cs:14` declares `RetryableStages = [Orphan, Unhandled]`. Filing sensitive material under `Unhandled` puts it directly in the retry endpoint's allow-list, and one retry routes it. |
| D4 | The screen **never throws** | A thrown exception reaches `LifecycleFaultObserver.cs:53` → `MarkUnhandledAsync` → `Unhandled` → retryable. A provider outage on the privacy screen would otherwise deposit an unscreened capture in the one bucket with a retry button. |
| D5 | Preview **short-circuits** on a non-`Safe` verdict and proposes no target | Running the classifier anyway costs a second call and produces a target that must never be used — which invites someone to act on it, and makes preview stop mirroring the production path. |
| D6 | Fixtures are **synthesised**, committed, and run in CI | Real corpus content cannot enter a public repo. |

## Architecture

### The port

A new driving port in `FlowHub.Core.Classification`, deliberately separate from
`IClassifier` so the guard is testable and auditable on its own:

```csharp
public enum Sensitivity { Sensitive, Unsure, Safe }

public sealed record SensitivityVerdict(
    Sensitivity Verdict,
    string Reason,
    ClassifierTrace? Trace = null);

public interface ISensitivityScreen
{
    Task<SensitivityVerdict> ScreenAsync(string content, CancellationToken cancellationToken);
}
```

`Reason` is always populated and names *why* — it is written to `FailureReason` on the
capture and shown in preview, so the operator can audit a park without reading the content
back out.

### The adapter

`AiSensitivityScreen` in `FlowHub.AI`, following the existing `AiClassifier` shape:

- Its own prompt in `AiPrompts`, asking exactly one question, with its own structured
  response schema. It does not see or care about the skill catalogue or the glossary.
- Its own model configuration, so a stronger model than the router can guard. Falls back
  to the configured default model when unset.
- No new NuGet packages.

Per **D4** the adapter is total: every failure path — malformed JSON, a missing field, an
HTTP error, a cancellation that is not the caller's, a timeout — returns
`Sensitivity.Unsure` with a reason naming the failure. It surfaces nothing by throwing.

The one exception is a cancellation originating from the caller's own
`CancellationToken`, which propagates normally — that is shutdown, not a screen failure,
and the capture is simply not processed yet.

### Where it runs

In `CaptureEnrichmentConsumer.ConsumeAsync`, **before** `_classifier.ClassifyAsync` and
**before** the `HasAttachment` → Paperless branch. Only `Safe` continues to the existing
flow; `Sensitive` and `Unsure` both call `MarkWithheldAsync` and return.

Ordering relative to the existing early-returns:

1. `NeedsTranscription` → return unchanged. The capture holds placeholder text; the
   transcription consumer re-publishes `CaptureCreated` once the transcript lands, and
   **that** republication is what gets screened. Screening the placeholder would be
   meaningless, and voice notes are a high-risk vector — so the ordering matters and is
   covered by an explicit test.
2. **Sensitivity screen** → `Withheld` on `Sensitive` or `Unsure`.
3. `HasAttachment` → Paperless (existing).
4. Classification and the existing shorthand / Bridge / Orphan branches (existing).

### The new stage

```csharp
/// <summary>Judged sensitive — withheld from every integration. Terminal, not retryable.</summary>
Withheld,
```

added to `LifecycleStage`, plus `MarkWithheldAsync(Guid id, string reason, CancellationToken)`
on `ICaptureService`, mirroring `MarkUnhandledAsync` (`EfCaptureService.cs:163`) exactly:

```csharp
capture with { Stage = LifecycleStage.Withheld, FailureReason = reason }
```

**No migration is required.** `Stage` is persisted as a `string` with `HasMaxLength(32)`
(`CaptureEntityTypeConfiguration.cs:15`), so a new enum value is a new string value.
`"Withheld"` is 8 characters.

`Withheld` is **not** added to `RetryableStages`. That omission is the point of **D3** and
is asserted by a test, not left to review.

### Preview

`CapturePreviewResponse` gains two fields:

```csharp
Sensitivity Sensitivity,
string? SensitivityReason,
```

`CapturePreviewEndpoint.PreviewAsync` runs the screen first. On a non-`Safe` verdict it
returns immediately with the verdict, the reason, and every target field null — no
`MatchedSkill`, no `VikunjaProject`, no project resolution. On `Safe` it proceeds exactly
as it does today, with `Sensitivity.Safe` on the response.

This makes the preview endpoint a usable calibration tool: the whole corpus can be run
through it to measure the park rate before any writes are enabled, at one screen call per
capture.

## Error handling

| Condition | Verdict | Reason |
|---|---|---|
| Model answers `sensitive` | `Sensitive` | the model's own reason |
| Model answers `unsure` | `Unsure` | the model's own reason |
| Model answers `safe` | `Safe` | empty string — `Reason` is non-nullable; only non-`Safe` verdicts carry text |
| Response missing, unparseable, or the verdict field absent | `Unsure` | names the parse failure |
| Provider HTTP error or outage | `Unsure` | names the transport failure |
| Screen timeout elapsed | `Unsure` | names the timeout |
| Caller's `CancellationToken` cancelled | *propagates* | shutdown, not a screen failure |

Every non-`Safe` outcome parks. There is no path from a screen failure to a routed
capture — this is the single invariant the whole design exists to hold.

## Testing

### Unit — the adapter

- Each of the three verdicts maps from a well-formed provider response.
- Malformed JSON → `Unsure`, does not throw.
- Missing verdict field → `Unsure`, does not throw.
- Provider exception → `Unsure`, does not throw.
- Timeout → `Unsure`, does not throw.
- Caller cancellation → propagates.

### Unit — the consumer

Using `NSubstitute`, with `DidNotReceive()` as the assertion that matters:

- `Sensitive` → `MarkWithheldAsync` called with the reason; `ClassifyAsync` **never**
  called; the Paperless attachment path **never** taken.
- `Unsure` → identical behaviour to `Sensitive`.
- `Safe` → classification proceeds and the existing branches are unaffected.
- `NeedsTranscription` → the screen is **not** called on the placeholder; it is called on
  the republished capture.

### Unit — the retry endpoint

- A `Withheld` capture is rejected by `CaptureRetryEndpoint`. This asserts **D3** directly
  rather than trusting that nobody adds the stage to `RetryableStages` later.

### Endpoint — preview

- A non-`Safe` verdict returns the verdict and reason with all target fields null.
- A `Safe` verdict returns today's shape plus `Sensitivity.Safe`.

### Fixtures

Synthesised, committed, entirely invented content reproducing the four shapes the survey
found:

1. A care-journal entry concerning a child.
2. A medication and dosage note.
3. A personal profile of a named third party.
4. **The unmarked case** — material that reads as an ordinary note about a mundane
   subject and is in fact therapy content.

Fixture 4 is the one that matters. It is the shape the keyword rules cannot catch, and a
suite that passes without it is testing the easy half of the problem.

**Known limitation:** synthesised fixtures may be easier than the real thing, so a green
suite is not proof the real corpus is safe. The calibration run described under *Preview*
— the real corpus through the preview endpoint, results recorded as counts, never as
content — is the out-of-band check that closes that gap. It is a manual step CI cannot
reproduce, and it is a precondition for enabling backlog writes, not part of this issue's
AC.

## Non-goals

- **No vault destination.** `#84` stays decoupled; it is unbuilt and still carries a
  superseded design. Coupling the irreversible-disclosure fix to it would delay the fix
  behind a feature that is not designed yet. `Withheld` means the capture stays in
  FlowHub.
- **No unpark or override.** A withheld capture stays withheld. An "approve and route
  anyway" affordance re-opens the disclosure path and needs its own design and its own
  issue.
- **Image and PDF contents are not screened.** Only text and attachment captions reach the
  screen, so a photograph of a medical document still reaches Paperless. Paperless is
  self-hosted, which makes this a materially smaller exposure than a shared task manager,
  but it is a real gap. It is called out here so the acceptance criteria do not imply
  coverage that does not exist, and is filed as a follow-up.

## Acceptance criteria

- [ ] A capture judged `Sensitive` or `Unsure` is marked `Withheld` with a reason naming
      why, and **no `ISkillIntegration.HandleAsync` runs**.
- [ ] `ClassifyAsync` is not called for a withheld capture.
- [ ] The screen never throws; every failure mode yields `Unsure` and parks.
- [ ] `Withheld` is not in `RetryableStages`, asserted by a test.
- [ ] The verdict and reason are visible in the preview endpoint, which proposes no target
      for a non-`Safe` verdict.
- [ ] A capture awaiting transcription is screened on its transcript, not its placeholder.
- [ ] Fixtures are synthesised, including the unmarked case; no real sensitive content
      enters this repo.
- [ ] Full suite green.
