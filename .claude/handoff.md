Resume the **Bridge alias capture routing** feature (spans FlowHub + the bridge repo).

**Artifact (approved design spec):**
`docs/superpowers/specs/2026-07-12-bridge-alias-capture-routing-design.md`
Read it first — it holds every locked decision, the bridge/FlowHub split, contracts, error paths, and rollout sequencing.

**Current phase:** brainstorming complete, spec written + committed (`16c7451`) and user-approved.

**Next step:** invoke the `superpowers:writing-plans` skill to turn the spec into an implementation plan. Then execute the plan with `superpowers:subagent-driven-development` (per the user's global default — dispatch independent tasks to subagents).

**Context:** the bridge target-side (`POST /api/capture/{issue,idea}`) already exists in `~/repos/github/freaxnx01/public/bridge`; the new work is `.bridge.yaml` alias indexing + `alias`/`body` fields + auth (bridge repo, separate PR) and a `BridgeSkillIntegration` + classifier changes (this FlowHub repo). FlowHub integration can merge ahead of the bridge-serve deploy (inert until `Bridge__BaseUrl` is set). Branch: `worktree-brains`.
