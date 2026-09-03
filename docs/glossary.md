# Glossary

Canonical definitions of the FlowHub domain vocabulary. These terms appear in the
ADRs, the spec documents, the code (`source/FlowHub.Core/`), and the agent skills —
they mean the same thing in all of them.

> Origin: the terms were first defined in `Projektarbeit/Glossary.md` in the CAS
> Obsidian vault. This file is the in-repo copy, aligned with the code as built.

---

## Domain terms

### Capture

The central FlowHub noun: **a single piece of incoming content**, from any Channel —
a URL, a text snippet, a quote, an image, a voice memo. German working term:
*Infoschnipsel*.

A Capture carries its source Channel, its content, a lifecycle stage, the Skill it
matched, and (once processed) the downstream `ExternalRef` it produced.

For a Capture with an attachment, `Content` is the caption submitted alongside the
file, falling back to the filename when no caption was given. The filename is always
available on the `Attachment` record regardless.

Type: `FlowHub.Core.Captures.Capture`

### Capture lifecycle

The stages a Capture moves through. `Completed` is the happy terminal state;
`Orphan` and `Unhandled` are the two failure terminal states surfaced on the
dashboard's *Needs attention* card.

| Stage | Meaning |
|---|---|
| `Raw` | Just arrived, no classification yet |
| `Classified` | AI has assigned a category / target Skill |
| `Routed` | Handed off to a Skill (in-flight, processing) |
| `Completed` | Skill processed and Integration write succeeded |
| `Orphan` | Skill or Integration failed during processing |
| `Unhandled` | No matching Skill — triggers a Skill suggestion |

Type: `FlowHub.Core.Captures.LifecycleStage`

A voice Capture is created `Raw` with placeholder content and a transcription flag;
its transcript replaces the placeholder before classification runs. A failed
transcription ends at `Orphan`.

### Channel

An **inbound source of Captures** — the route content enters FlowHub through. A
Channel is metadata on the Capture, *not* a separate code path: every Channel funnels
into the same `ICaptureService.Submit(...)` entry point.

Kinds: `Telegram`, `Web` (dashboard quick-add and `/captures/new`), `Api` (REST).

A Channel can be enabled/disabled and has its own health and last-active timestamp.

Types: `FlowHub.Core.Captures.ChannelKind`, `FlowHub.Core.Channels.Channel`
(see also ADR 0001 §4 — "the Web UI is itself a Channel")

### Skill

The **routing/handling unit**: it decides what to do with a classified Capture and
writes it to a downstream service. A Skill has a name, health status, and a count of
Captures routed to it. Its outcome is a `SkillResult` — success plus the downstream
`ExternalRef`, or a failure reason.

Implemented Skills live in `source/FlowHub.Skills/` (Wallabag, Vikunja).

Types: `FlowHub.Core.Skills.ISkillIntegration`, `SkillResult`, `FlowHub.Core.Health.SkillHealth`

### Integration

A **downstream, self-hosted target system** a Skill writes into — Wallabag
(read-later), Vikunja (tasks), Paperless-ngx (DMS), Obsidian (markdown via git).

The distinction from *Skill*: the Skill is FlowHub's own handling logic, the
Integration is the external service it talks to. In the code as built the two are
adjacent — the adapters implement `ISkillIntegration` — but health is tracked
separately per Integration (reachability, last successful write, write duration).

Types: `FlowHub.Core.Health.IntegrationHealth`, `IntegrationHealthSample`
(see also ADR 0002 "As built" on why the adapters live under `FlowHub.Skills`)

### Classification

The step that turns a `Raw` Capture into a `Classified` one: an LLM (with a keyword
fallback) assigns tags, a title, the matched Skill, and the target Vikunja project.

Types: `FlowHub.Core.Classification.IClassifier`, `ClassificationResult`, `ClassifierTrace`

---

## UI terms

### Page

A **routable Blazor component** with an `@page` directive — one entry in the site's
route table (`/`, `/captures`, `/captures/{id}`, `/captures/new`, `/skills`,
`/integrations`). Pages live in `source/FlowHub.Web/Components/Pages/`.

### Component

Any **non-routable, reusable Blazor unit** (`.razor` + `.razor.cs` code-behind) —
e.g. `LifecycleBadge`, `HealthDot`, `ClassifierTracePanel` in
`source/FlowHub.Web/Components/Shared/`. No business logic in the `.razor` file;
logic goes in the code-behind or a service.

### Card

A **dashboard tile** — a Component that presents one bounded slice of state on the
dashboard: *Recent Captures*, *Skill health*, *Integration health*, *Needs attention*.
Lives in `source/FlowHub.Web/Components/DashboardCards/`.

### Widget

A **small interactive control embedded in a layout or Card** rather than a full Card
of its own — e.g. the persistent quick-add field in the app bar. Distinguished from a
Card by scope: a Card *displays a slice of state*, a Widget *takes an action*.

### Render mode

Which Blazor **hosting/interactivity model** a Page or Component runs under. FlowHub's
default is **Interactive Server** (SignalR circuit, components run in-process so they
can `@inject` application services directly); **Static SSR** is used for the
non-interactive auth and status endpoints. WebAssembly and Auto are out of scope.

See ADR 0001 §1 for the per-Page render-mode table and the rationale.

---

## Related documents

- `docs/adr/0001-frontend-render-mode-and-architecture.md` — render modes, the Web UI as a Channel
- `docs/spec/system-context.md` — C4 Level 1 context, how Channels/Skills/Integrations connect
- `docs/spec/use-cases.md` — the flows these terms appear in
