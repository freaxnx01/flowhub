# TODO — post-split plumbing

Open operational items for the freshly-split `flowhub` product repo.

- [ ] **Re-add GitHub Actions secrets** the workflows need:
  - `EMBEDDINGS__APIKEY` — embeddings provider key (semantic search)
  - `Ai__Anthropic__ApiKey` / `Ai__OpenRouter__ApiKey` — LLM provider keys
  - (GHCR auth uses the built-in `GITHUB_TOKEN` — nothing to add)
  - Set via: `gh secret set <NAME> --repo freaxnx01/flowhub`
- [ ] (Optional) Deep de-CAS pass of the ADRs + `docs/spec/*` — strip "Block N /
      Nachbereitung" provenance and dead `vault/`/`docs/insights/` links, if you don't
      want them kept as historical record.
- [ ] (Cosmetic) Bump pinned GitHub Actions off Node-20 (checkout@v4, setup-dotnet@v4,
      docker/*@v3–5, action-gh-release@v2) when convenient — deprecation warning only.

## Done (2026-07-07 / 08)

- History split from `FlowHub-CAS-AISE` (CAS scrubbed from tree + history).
- Full-history PII/homelab redaction (address, DOB, internal IPs, host, admin email).
- Bus defaults to in-memory; RabbitMQ opt-in overlay.
- GHCR web image renamed `flowhub-web` → `flowhub`.
- Migrations image renamed `flowhub-migrations` → `flowhub-db-migrations` (fresh package
  the repo owns; the old name is held by the CAS archive).
- CI `build-and-test` green on `main`.
- Branch protection on `main` (require `build-and-test` for collaborators/bots, no
  force-push/deletions; `enforce_admins: false`, so the owner can push docs directly).
- **Release green**: `v0.1.0` tag → `ghcr.io/freaxnx01/flowhub` + `ghcr.io/freaxnx01/flowhub-db-migrations`
  published, and the `v0.1.0` GitHub Release created.
- Roadmap issues #1–#4 opened.
