# Withheld Operator Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a `Withheld` capture visible on every operator surface that already shows `Orphan` and `Unhandled`, and clear the housekeeping left by the sensitive-capture-parking PR.

**Architecture:** No new types. Three existing switch/query sites gain a `Withheld` arm, plus a CHANGELOG entry and two small cleanups.

**Tech Stack:** .NET 10 / C#, Blazor + MudBlazor, EF Core, xUnit + FluentAssertions + NSubstitute.

**Spec:** [`docs/superpowers/specs/2026-09-14-sensitive-capture-parking-design.md`](../specs/2026-09-14-sensitive-capture-parking-design.md) — decisions **D8** and **D9**.

## Dependency — read first

**This plan requires `LifecycleStage.Withheld`, `ISensitivityScreen` and
`CapturePreviewResponse.Sensitivity` to already exist on `main`.** They are delivered by
issue #93. If `grep -r "LifecycleStage.Withheld" source/` returns nothing, #93 has not
merged yet — **stop and say so** rather than implementing it here. This plan was split
out of #93 precisely because the combined work exceeded the pipeline's 160-turn ceiling.

## Global Constraints

- **No new NuGet packages**, no target-framework changes.
- **`sealed` by default; file-scoped namespaces.**
- **Logging:** hand-written `LoggerMessage.Define` delegates in `FlowHub.Api` only (the
  `[LoggerMessage]` source generator stops coverlet instrumenting that whole assembly —
  commit `5dbe439`, refs #11/#91). Match the existing pattern in other assemblies.
- **TDD:** the failing test comes first, every task.
- **The reason string is safe to display** — it names a category ("child care material"),
  never capture content. The prompt is explicit about that.
- **Commit after every task**, Conventional Commits, `Refs #<this issue>` footer.

---

### Task 1: `Withheld` becomes visible

A stage that is terminal, non-retryable **and** silent is indistinguishable from a lost
capture. `Orphan` and `Unhandled` each get an emoji, a labelled badge and a dashboard
count; `Withheld` currently gets none of the three.

**Files:**
- Modify: `source/FlowHub.Telegram/TelegramReactionService.cs` (the `EmojiFor` switch)
- Modify: the lifecycle badge component (`LifecycleBadge.razor`)
- Modify: `source/FlowHub.Persistence/Repositories/EfCaptureRepository.cs` (`GetFailureCountsAsync`)
- Test: the existing test files covering each of the three

**Interfaces:**
- Consumes: `LifecycleStage.Withheld` (from #93).
- Produces: nothing new.

- [ ] **Step 1: Write the three failing tests**

Find each site's existing test for `Orphan` or `Unhandled` and add the `Withheld` case
alongside it, matching that file's established style rather than inventing a new one:

1. `EmojiFor(LifecycleStage.Withheld)` returns a non-null emoji, distinct from the
   `Orphan` and `Unhandled` emoji.
2. The badge renders a readable label for `Withheld`, not the `?` fallback.
3. `GetFailureCountsAsync` includes withheld captures in its result.

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/FlowHub.Telegram.Tests tests/FlowHub.Web.ComponentTests tests/FlowHub.Persistence.Tests -v minimal`
Expected: FAIL — null emoji, `?` badge, count missing.

- [ ] **Step 3: Add the `Withheld` arm to `EmojiFor`**

Without it the switch falls through to `_ => null` and the reaction call is a silent
no-op. Choose an emoji that reads as *held back*, not *failed* — the capture is being
protected, not broken — and that is not already used by another stage.

- [ ] **Step 4: Add the badge label**

Follow the existing `Orphan` / `Unhandled` arms exactly, including how they source their
display string. If the component uses `IStringLocalizer<T>`, add the resource entries for
both `de` and `en` rather than inlining a literal.

- [ ] **Step 5: Include `Withheld` in `GetFailureCountsAsync`**

It currently counts only `Orphan` and `Unhandled`, so a withheld capture never reaches the
dashboard's needs-attention card.

- [ ] **Step 6: Run the tests, verify they pass, then run the full suite**

Run: `dotnet test -v minimal`
Expected: PASS.

> **Local gotcha:** `just test` and the `.slnx` solution build fail locally with `NU1903`.
> Use per-project `dotnet test` invocations. This is a local toolchain issue, not a code
> failure — do not change packages to "fix" it.

- [ ] **Step 7: Commit**

```bash
git commit -m "feat(ui): make Withheld captures visible to the operator

A terminal, non-retryable, silent stage is indistinguishable from a lost
capture. Adds the reaction emoji, badge label and dashboard count that
Orphan and Unhandled already have.

Refs #<this issue>"
```

---

### Task 2: Housekeeping from the #93 review

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `source/FlowHub.Api/Requests/CapturePreviewResponse.cs`
- Modify: `tests/FlowHub.Api.IntegrationTests/Captures/CaptureRetryWithheldTests.cs`
- Modify: `tests/FlowHub.Web.ComponentTests/Ai/SensitivityFixtureTests.cs`

**Interfaces:**
- Consumes: `CapturePreviewResponse` (from #93).
- Produces: nothing new.

- [ ] **Step 1: Add the CHANGELOG entry**

`CLAUDE.md` mandates a Keep-a-Changelog `CHANGELOG.md` whose `[Unreleased]` section
accumulates changes, and CI may gate a release on it being non-empty. Add under
`[Unreleased]`:

- **`Added`** — the sensitivity pre-pass and the `Withheld` lifecycle stage.
- **`Security`** — captures judged sensitive no longer reach shared destinations.

- [ ] **Step 2: Make the preview verdict non-defaultable (spec D9)**

`Sensitivity.Sensitive` is the zero value, which is deliberate and fail-closed — **keep
it that way**. The problem is only that `CapturePreviewResponse.Sensitivity` is a
non-nullable enum on a public response, so a client omitting the field silently receives
`Sensitive` by accident rather than by design.

Write a failing test first: a JSON payload omitting `sensitivity` must not deserialise
into a usable response. Then make the field required (`[JsonRequired]` or the equivalent
the codebase already uses) and add an XML doc comment stating the zero value is
intentionally fail-closed.

- [ ] **Step 3: Remove the dead code**

- `CaptureRetryWithheldTests.cs` — unused `System.Net.Http.Json` import and `JsonOpts`
  declaration. (`IDE0005` is only `suggestion` under `[**/*Tests/**.cs]`, so these do not
  break the build; they are still dead.)
- `SensitivityFixtureTests.cs` — `FakeLogger<T>` is duplicated verbatim from
  `AiSensitivityScreenTests.cs` and neither copy's `Records` list is asserted on. Use the
  existing one; delete the copy.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -m "chore: changelog, non-defaultable preview verdict, dead code

Refs #<this issue>"
```
