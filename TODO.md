# TODO — post-split plumbing

Open operational items for the freshly-split `flowhub` product repo.

- [ ] **Re-add GitHub Actions secrets** the workflows need:
  - `EMBEDDINGS__APIKEY` — embeddings provider key (semantic search)
  - `Ai__Anthropic__ApiKey` / `Ai__OpenRouter__ApiKey` — LLM provider keys
  - (GHCR auth uses the built-in `GITHUB_TOKEN` — nothing to add)
  - Set via: `gh secret set <NAME> --repo freaxnx01/flowhub`
- [ ] **Fix the failing Release / Docker publish** — the `release.yml` Docker build
      fails on `NU1903` (Warning-As-Error): `Microsoft.OpenApi 2.0.0` known
      high-severity vuln (GHSA-v5pm-xwqc-g5wc). A `2.7.5` pin exists in
      `Directory.Packages.props`, but it is **not applied inside the Docker restore**
      (CI `dotnet build` on the runner is green; only the container build sees 2.0.0).
      Likely needs central transitive pinning to reach the container restore, or the
      props copied earlier in the Dockerfile. Until fixed, no `ghcr.io/freaxnx01/flowhub`
      image is published. (`Migrations Bundle` fails for the same reason.)
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
