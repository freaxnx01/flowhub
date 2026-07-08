# TODO — post-split plumbing

Open operational items for the freshly-split `flowhub` product repo.

- [ ] **Re-add GitHub Actions secrets** the workflows need:
  - `EMBEDDINGS__APIKEY` — embeddings provider key (semantic search)
  - `Ai__Anthropic__ApiKey` / `Ai__OpenRouter__ApiKey` — LLM provider keys
  - (GHCR auth uses the built-in `GITHUB_TOKEN` — nothing to add)
  - Set via: `gh secret set <NAME> --repo freaxnx01/flowhub`
- [x] ~~Free the migrations GHCR package name~~ — resolved by renaming the migrations
      image `flowhub-migrations` → **`flowhub-db-migrations`** (the CAS archive owns the
      old `flowhub-migrations` package; the new name is a fresh package the `flowhub`
      repo owns). Web image is `ghcr.io/freaxnx01/flowhub`.
      Note: `NU1903` (Microsoft.OpenApi) was a red herring — it only failed the *first*
      run, which built the inherited-CAS `v0.1.0` commit predating the 2.7.5 pin.
- [ ] (Optional) Deep de-CAS pass of the ADRs + `docs/spec/*` — strip "Block N /
      Nachbereitung" provenance and dead `vault/`/`docs/insights/` links, if you don't
      want them kept as historical record.

## Done (2026-07-07)

- History split from `FlowHub-CAS-AISE` (CAS scrubbed from tree + history).
- Full-history PII/homelab redaction (address, DOB, internal IPs, host, admin email).
- Bus defaults to in-memory; RabbitMQ opt-in overlay.
- GHCR web image renamed `flowhub-web` → `flowhub`.
- CI `build-and-test` green on `main`.
- Branch protection on `main` (require `build-and-test`, no force-push/deletions).
- First product tag `v0.1.0` at product HEAD (CAS tags left in the archive).
- Roadmap issues #1–#4 opened.
