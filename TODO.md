# TODO — post-split plumbing

Open operational items for the freshly-split `flowhub` product repo.

- [ ] **Re-add GitHub Actions secrets** the workflows need:
  - `EMBEDDINGS__APIKEY` — embeddings provider key (semantic search)
  - `Ai__Anthropic__ApiKey` / `Ai__OpenRouter__ApiKey` — LLM provider keys
  - (GHCR auth uses the built-in `GITHUB_TOKEN` — nothing to add)
  - Set via: `gh secret set <NAME> --repo freaxnx01/flowhub`
- [ ] Verify the first CI run on `main` goes green (`ci.yml` → `build-and-test`).
- [ ] Publish the first product Docker image by pushing a `v*` tag (`release.yml` → `ghcr.io/freaxnx01/flowhub`).
- [ ] (Optional) Deep de-CAS pass of the ADRs + `docs/spec/*` — strip "Block N / Nachbereitung" provenance and dead `vault/`/`docs/insights/` links, if you don't want them kept as historical record.
