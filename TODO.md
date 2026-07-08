# TODO — post-split plumbing

Open operational items for the freshly-split `flowhub` product repo.

- [ ] **Re-add GitHub Actions secrets** the workflows need:
  - `EMBEDDINGS__APIKEY` — embeddings provider key (semantic search)
  - `Ai__Anthropic__ApiKey` / `Ai__OpenRouter__ApiKey` — LLM provider keys
  - (GHCR auth uses the built-in `GITHUB_TOKEN` — nothing to add)
  - Set via: `gh secret set <NAME> --repo freaxnx01/flowhub`
- [ ] **Free the `flowhub-migrations` GHCR package name** so the Release can publish it.
      The web image `ghcr.io/freaxnx01/flowhub` publishes fine, but the
      `flowhub-migrations` push is `denied: permission_denied: write_package` — that
      package already exists under the account, **owned by the CAS repo** (which
      published it at v0.3.0), so the new repo's `GITHUB_TOKEN` can't write it. Fix
      (pick one), then re-run the Release:
      - **(a, recommended)** Package `flowhub-migrations` → Settings → *Manage Actions
        access* → add `freaxnx01/flowhub` with Write.
      - **(b)** Delete the CAS-era `flowhub-web` + `flowhub-migrations` GHCR packages so
        the product owns the namespace cleanly (touches the archive's published images).
      - **(c)** Rename the product's migrations image in `release.yml` to a fresh name.
      Note: `NU1903` (Microsoft.OpenApi) was a red herring — it only failed the *first*
      run, which built the inherited-CAS `v0.1.0` commit predating the 2.7.5 pin. The
      pin works; the runner CI build is green.
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
