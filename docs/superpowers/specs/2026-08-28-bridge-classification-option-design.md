# Allow the classifier to route a capture to Bridge — design

**Date:** 2026-08-28
**Issue:** #37 (blocker 2 of 4 for #35)
**Status:** Design, approved

---

## 1. Problem

`AiClassifier` cannot route anything to a forge. `source/FlowHub.AI/AiClassifier.cs:19`:

```csharp
private static readonly string[] AllowedSkills = ["Wallabag", "Vikunja", ""];
```

`AiPrompts.BuildSystemPrompt` matches it — the model is offered exactly three choices and is never told a
forge exists. A `"Bridge"` reply would be rejected by the allow-list as `schema_violation` and fall back
to the keyword classifier.

Bridge is therefore reachable **only** through the pre-LLM alias short-circuit
(`AiClassifier.cs:53-56`). A capture with no known repo alias in its text cannot reach the forge route
however obviously it is a dev task. Measured: **0 of 53** replayed candidates reached a forge (38
`Vikunja/Inbox`, 15 `Orphan`) — `docs/ai-notes/2026-08-25-telegram-capture-taxonomy.md` §10.

## 2. Goal

Make `"Bridge"` a legal classifier answer, **behind a configuration flag that is off by default.**

**Non-goals** — each is a separate issue:

- Inferring *which* repo (#38). This design deliberately produces Bridge results with no repo.
- Vision (#39). Deployment configuration (#36).

---

## 3. Key finding — the no-repo case is already handled

An LLM-returned `"Bridge"` arrives with `BridgeAction = Unknown` and `BridgeAlias = null` (both are
`ClassificationResult` defaults; only `ClassifyBridgeAsync` sets them). `CaptureEnrichmentConsumer.cs:55-63`
already guards exactly that shape:

```csharp
if (string.Equals(result.MatchedSkill, "Bridge", StringComparison.Ordinal)
    && result.BridgeAction == BridgeAction.Unknown)
{
    await _captureService.MarkUnhandledAsync(msg.CaptureId, "bridge action undetermined — needs triage", ct);
    return;
}
```

The guard fires **before** `CaptureClassified` is published, so the capture never reaches
`SkillRoutingConsumer` and `BridgeSkillIntegration.cs:41-43` never throws its
"routed to Bridge without an alias" exception.

**No new parking mechanism is needed.** The only defect is the reason string: it says *action*
undetermined when the real cause is *repo* undetermined. That is a triage-facing message and should be
accurate.

---

## 4. Why a configuration flag

Enabling this unconditionally is a **usefulness regression** for as long as #38 is outstanding.

| Capture | Today | After, unflagged |
|---|---|---|
| `"Auto dispatcher Issues cross repo and milestone aligned"` | `Vikunja/Inbox` — a task in the inbox, crude but actionable | `Bridge` → `Unhandled` — nothing lands anywhere |

38 of 53 replayed candidates currently reach `Vikunja/Inbox`. A prompt change that works well would move a
large share of those into limbo, with no fixed date for their return.

The flag makes #37 safely mergeable today and defers the behaviour change to a deliberate act — ideally
once #38 is ready.

**Setting:** `Ai:EnableBridgeClassification`, boolean, **default `false`**. Read in `AddFlowHubAi`
alongside the other AI configuration and supplied to the classifier; no `IOptionsMonitor` reload support
(consistent with `AiModelInfo` and the pricing options).

---

## 5. Design

### 5.1 Allow-list becomes instance state

`AllowedSkills` changes from `static readonly` to an instance field computed once in the constructor from
the flag:

- flag off → `["Wallabag", "Vikunja", ""]` (byte-identical to today)
- flag on → `["Wallabag", "Vikunja", "Bridge", ""]`

The re-validation at `AiClassifier.cs:71-74` is otherwise unchanged, so an unexpected `"Bridge"` while the
flag is off still degrades to the keyword classifier exactly as it does now.

### 5.2 Prompt

`AiPrompts.BuildSystemPrompt(IReadOnlyCollection<string> vikunjaBuckets)` gains a
`bool allowBridge` parameter. When true, one option is added to the `matched_skill` list:

> `"Bridge"` – the snippet is an actionable task, bug report, or feature request about one of the
> operator's **own software projects**

When false the prompt is emitted **character-for-character as today** — this is asserted by a test, since
prompt drift silently changes classification for every capture.

Deliberately *not* included: any instruction to name a repo. The model has no catalog and would
hallucinate one; supplying the catalog is #38.

### 5.3 Reason string

`CaptureEnrichmentConsumer`'s guard distinguishes the two causes:

- alias matched, action undetermined → `"bridge action undetermined — needs triage"` (unchanged)
- no alias (the LLM path) → `"bridge candidate — repo undetermined"`

Distinguished by `result.BridgeAlias is null`, not by a new field.

### 5.4 What is unchanged

The alias short-circuit, `ClassifyBridgeAsync`, `BridgeAction`, `BridgeSkillIntegration`,
`SkillRoutingConsumer`, the keyword fallback, and every persisted shape. No migration.

---

## 6. Acceptance criteria

- [ ] `Ai:EnableBridgeClassification` defaults to `false`; with it unset, classification behaviour and the
      emitted system prompt are byte-identical to today.
- [ ] With the flag on, the classifier can return `MatchedSkill = "Bridge"` without a `schema_violation`.
- [ ] With the flag on, the system prompt offers `"Bridge"` as a `matched_skill` option.
- [ ] With the flag off, a model returning `"Bridge"` still degrades to the keyword classifier.
- [ ] A Bridge result with no alias parks as `Unhandled` with reason `"bridge candidate — repo undetermined"`
      and never reaches `SkillRoutingConsumer`.
- [ ] A Bridge result *with* an alias and `BridgeAction.Unknown` keeps the existing reason string.
- [ ] Wallabag / Vikunja / orphan classification is unchanged in both flag states (regression tests).
- [ ] The alias short-circuit path is unchanged.

---

## 7. Testing

Unit (xUnit + NSubstitute + FluentAssertions), in `FlowHub.Web.ComponentTests` where the existing
`AiClassifier` tests live (`AiPrompts` is `internal` with `InternalsVisibleTo` for that assembly):

- Prompt with `allowBridge: false` equals the current prompt exactly (guards against drift).
- Prompt with `allowBridge: true` contains the Bridge option and still lists the Vikunja buckets.
- Flag on + model returns `"Bridge"` → `ClassificationResult.MatchedSkill == "Bridge"`, `BridgeAlias` null,
  `BridgeAction == Unknown`.
- Flag off + model returns `"Bridge"` → keyword fallback, `LogFellBack` with `schema_violation`.
- `CaptureEnrichmentConsumer`: Bridge + null alias → `MarkUnhandledAsync` with the repo-undetermined
  reason, and **no** `CaptureClassified` published.
- `CaptureEnrichmentConsumer`: Bridge + alias + `Unknown` → the existing reason.
- Wallabag / Vikunja / empty results unchanged under both flag states.

No integration test: no I/O boundary changes.

---

## 8. Risks

- **Prompt drift.** Adding an option changes model behaviour for *every* capture, not just dev ones — a
  capture that used to classify Vikunja may now classify Bridge. The flag contains the blast radius, and
  the byte-identical-prompt test guarantees the off path is untouched.
- **Over-selection.** Once enabled, the model may pick Bridge too eagerly. Not measurable until it is on;
  the replay harness used for §10 of the report can quantify it before flipping the flag in production.

---

## 9. Rollout

Merge with the flag off — production behaviour does not change. Turn it on once #38 lands, or earlier in
a non-production environment to measure over-selection.
