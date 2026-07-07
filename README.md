<p align="center">
  <img src="docs/assets/flowhub-logo.png" alt="FlowHub" width="420" />
</p>

# FlowHub

<p align="center">
  <a href="https://github.com/freaxnx01/flowhub/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/freaxnx01/flowhub/ci.yml?branch=main&label=CI&logo=githubactions&logoColor=white&style=flat-square" alt="CI" /></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white&style=flat-square" alt=".NET 10" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-AGPL--3.0-blue?style=flat-square" alt="License: AGPL-3.0" /></a>
  <a href="https://demo.flowhub.freaxnx01.ch"><img src="https://img.shields.io/badge/live-demo-2ea44f?logo=icloud&logoColor=white&style=flat-square" alt="Live demo" /></a>
  <a href="https://github.com/freaxnx01/flowhub/commits/main"><img src="https://img.shields.io/github/last-commit/freaxnx01/flowhub?style=flat-square" alt="Last commit" /></a>
</p>

> **Capture anything. Let AI file it for you.**

**FlowHub is the inbox for your whole digital life.** Drop in a URL, a note, or a
file from anywhere — FlowHub reads it, classifies it with an LLM, and routes it to
the right self-hosted tool automatically: articles to your read-later app, tasks to
your task manager, documents to your DMS. One capture box, zero filing.

Single-user, self-hostable, modular monolith on **.NET 10**.

- **Live demo:** <https://demo.flowhub.freaxnx01.ch> — public, rate-limited, self-resetting; see [`docs/runbooks/public-demo.md`](docs/runbooks/public-demo.md)
- **Roadmap / backlog:** [`ROADMAP.md`](ROADMAP.md)
- **Full feature list:** [`FEATURES.md`](FEATURES.md)

---

## How it works

1. **Capture** — paste a link, type a note, or upload a file (app-bar quick-capture, REST API, more channels coming). Any text/URL/file becomes a *Capture*.
2. **Classify** — an LLM tags it and decides where it belongs; a deterministic keyword/URL classifier is the always-on safety net, so it never just fails.
3. **Route** — FlowHub hands it to the matching *Skill*, which writes it into the real service (Vikunja, Wallabag, paperless-ngx) — sending only a tag + link, never your content body.
4. **Find it later** — semantic search (pgvector embeddings) over everything you've ever captured.

## Why it's different

- **AI does the filing, not you** — capture is one action; routing is automatic.
- **Pluggable Skills** — every destination is a small `ISkillIntegration` adapter; add your own.
- **Privacy by design** — single-user, self-hostable, local-LLM default; cloud AI is strictly opt-in, and outbound writes carry tag + URL only. Built to sit inside your own homelab trust boundary.
- **Honest AI** — every AI-classified item is visibly marked (EU AI Act-aware).
- **Degrades gracefully** — LLM down or out of quota? Keyword routing keeps it working.

---

## Features

- **Capture** — app-bar quick-capture, REST API (`POST /api/v1/captures`), and file upload.
- **AI classification** — LLM classifier (Anthropic / OpenRouter via Microsoft.Extensions.AI) with keyword + URL heuristics as a graceful fallback; produces tags + a title.
- **Skill routing** — classified captures route to *Skills* → *Integrations* (Wallabag, Vikunja, paperless-ngx); outbound payloads carry tag + URL only.
- **Async pipeline** — MassTransit consumers (classify, enrich, embed) with per-consumer retry. Runs on an in-memory bus by default; RabbitMQ is an opt-in overlay for durable, crash-safe redelivery.
- **Semantic search** — pgvector embeddings + `GET /api/v1/captures/search` (cosine). Provider-agnostic (OpenAI-compatible); disabled on the public demo (returns 503).
- **Web UI** — Blazor (MudBlazor, Interactive Server): Dashboard, Captures list/detail, New Capture, Skills, Integrations.
- **Persistence** — PostgreSQL + EF Core 10; migrations run as a separate init container (12-Factor XII).
- **Ops & security** — `/health/*`, `/metrics` (Prometheus) + Grafana, OpenTelemetry, Serilog → stdout; OIDC (Authentik) or dev/demo auth; AI-transparency posture (EU AI Act).
- **Deployment** — multi-stage Docker, full Compose stack, GitHub Actions (CI + tagged release → GHCR).

Full list — including config-gated and demo-disabled features — in **[`FEATURES.md`](FEATURES.md)**.

---

## Explainer videos

Two short, code-generated explainer videos (English narration). Built with Remotion + a local Piper voice — fully reproducible via `just video`; see [`video/`](video/).

**For everyone — what FlowHub does** (~46s)

https://github.com/user-attachments/assets/7e80b3a2-4c0f-4e92-9228-9ca2ea031031

**Technical — how it's built** (~48s)

https://github.com/user-attachments/assets/c000f88a-d31f-4ef4-a5a8-3ad84d4f4828

**See it in action — using the live demo** (~70s, no narration)

Screenshot walkthrough of the [public demo](https://demo.flowhub.freaxnx01.ch): each sample captured → AI-classified → landing in its service (Vikunja / Zitate / Wallabag / paperless), with an animated cursor.

https://github.com/user-attachments/assets/4eeffe75-91c9-4144-bec6-03c6ec94e43c

---

## Quickstart

### Option A — see it running, zero install

Open <https://demo.flowhub.freaxnx01.ch>. The demo is intentionally open (no login) and resets every 15 min. What it shows — and what's intentionally disabled (embeddings, external skill writes) — is documented in [`DEMO.md`](DEMO.md) and [`docs/runbooks/public-demo.md`](docs/runbooks/public-demo.md).

### Option B — local stack via Docker

Requires Docker (Compose v2). Boots Web + Postgres + Prometheus + Grafana (in-memory bus).

```bash
git clone https://github.com/freaxnx01/flowhub.git
cd flowhub
cp .env.example .env         # fill in Postgres password, OIDC, AI keys
docker compose up -d --wait
# → http://localhost:5070  (health: /health/live · metrics: /metrics · API docs: /scalar)
```

Want durable, broker-backed message delivery? Add the RabbitMQ overlay:

```bash
docker compose -f docker-compose.yml -f docker-compose.rabbitmq.yml up -d --wait
```

### Option C — build and test locally

Requires .NET 10 SDK (`global.json` pins the version). The `justfile` exposes every common task — run `make` with no target for a help listing.

```bash
just build       # build the full solution (warnings as errors)
just test        # run the default suite (excludes live AI / BetaSmoke / E2E)
just run         # run FlowHub.Web on http://localhost:5070
just watch       # same, with hot reload
```

CI on `main`: [GitHub Actions runs](https://github.com/freaxnx01/flowhub/actions/workflows/ci.yml?query=branch%3Amain).

---

## Repository layout

```
source/                 ← application code (one project per capability)
  FlowHub.Core/         ← domain model + driving ports
  FlowHub.Web/          ← Blazor Web App (MudBlazor, Interactive Server)
  FlowHub.Api/          ← Minimal API endpoints (registered into Web)
  FlowHub.AI/           ← LLM-backed classifier + embeddings (MEAI)
  FlowHub.Persistence/  ← EF Core + Npgsql + pgvector
  FlowHub.Skills/       ← Wallabag + Vikunja + paperless ISkillIntegration adapters
tests/                  ← test projects (unit, component, integration, E2E)
docs/                   ← architecture, specs, runbooks, design notes
  adr/                  ← Architecture Decision Records
  spec/                 ← use-cases, NfA, acceptance criteria, DB model, testing strategy
  design/               ← UI workflow output, API surface, data flow
  runbooks/             ← acceptance, demo, OIDC setup, test services
  monitoring/           ← Grafana dashboards
ROADMAP.md              ← forward-looking product backlog
DEMO.md                 ← what the public demo shows (and what's disabled)
```

## justfile cheat sheet

`make` with no target prints the help listing. The most useful targets:

### Run + build

| Target | What it does |
|---|---|
| `just run` | Run `FlowHub.Web` on http://localhost:5070 (no hot reload) |
| `just watch` | Same, with `dotnet watch` (hot reload) |
| `just build` | Build the full solution (warnings as errors) |
| `just restore` | Restore NuGet packages |
| `just clean` | Remove build artifacts |
| `just format` | Apply `dotnet format` |

### Tests

| Target | What it does |
|---|---|
| `just test` | All non-live tests (skips AI, BetaSmoke, E2E categories) |
| `just test-backend` | Backend unit + skill contract tests |
| `just test-frontend` | Frontend (bUnit) component tests |
| `just test-ai` | Live integration tests against real AI providers (needs API keys) |
| `just test-contract` | WireMock contract tests for Vikunja + Wallabag (offline) |
| `just test-services` | Live skill-integration tests against `flowhub-test-services` |
| `just playwright-install` | One-time Playwright browser install |

### Database

| Target | What it does |
|---|---|
| `just db-up` | Start PostgreSQL in Docker (detached, waits until healthy) |
| `just db-ping` | Verify Postgres connectivity |
| `just db-migrate` | Apply EF Core migrations against the Docker Postgres |

### AI smoke tests

| Target | What it does |
|---|---|
| `just ai-ping` | Smoke-test the configured AI provider with a tiny chat call |
| `just ai-classify "…"` | Run `IClassifier` against an input |
| `just ai-embed "…"` | Run `IEmbeddingService` against an input |

### End-to-end smoke

| Target | What it does |
|---|---|
| `just smoke-prod` | Boot full prod compose stack and smoke health, `/metrics`, capture submit + embedding round-trip |
| `just smoke-down` | Stop the prod stack (volumes preserved) |

## Configuration

All config is environment-driven (12-Factor III). The main keys:

| Variable | Purpose |
|---|---|
| `ConnectionStrings__Default` | PostgreSQL connection string |
| `Ai__Provider` | `Anthropic` or `OpenRouter` |
| `Ai__Anthropic__ApiKey` / `Ai__OpenRouter__ApiKey` | LLM provider keys |
| `Embeddings__ApiKey`, `Embeddings__Model` | Embeddings provider (for semantic search) |
| `Skills__Wallabag__*`, `Skills__Vikunja__*` | Real integration endpoints + tokens |
| `Auth__OIDC__*` | Authentik OIDC client (unset → `DemoAuthHandler` auto-signs all requests) |
| `Bus__Transport` | `InMemory` (default) or `RabbitMq` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Optional OpenTelemetry collector |

Never commit secrets. See [`docs/runbooks/authentik-oidc-setup.md`](docs/runbooks/authentik-oidc-setup.md) for the OIDC client registration walkthrough.

## Versioning

SemVer 2.0.0. See [`CHANGELOG.md`](CHANGELOG.md) for the release history.

## License

Licensed under the **GNU Affero General Public License v3.0** — see [`LICENSE`](LICENSE).

Copyright © 2026 Andreas Imboden. AGPL-3.0 is a strong copyleft license: you may self-host, study, and modify FlowHub freely, but if you run a modified version as a network service you must make your source available under the same terms.

## Agent conventions

This repository was developed with heavy AI-assisted engineering. Agent conventions, skills, and the mandatory UI workflow are documented in [`CLAUDE.md`](CLAUDE.md).

## Related repositories

Part of the same AI-assisted engineering toolchain:

- [`freaxnx01/ai-instructions`](https://github.com/freaxnx01/ai-instructions) — reusable AI-agent instruction templates (base conventions + per-stack overlays) that this repo's [`CLAUDE.md`](CLAUDE.md), Copilot instructions, and `.ai/` skills are synced from (via the `sync-ai-instructions` skill).
- [`freaxnx01/agent-pipeline`](https://github.com/freaxnx01/agent-pipeline) — reusable GitHub Actions workflow for autonomous issue → implementation that this repo delegates to via [`.github/workflows/claude.yml`](.github/workflows/claude.yml).
