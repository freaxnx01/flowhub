## Resume: FlowHub classifier quality — re-measure, then ship the privacy guard

**Artifact (detailed state, outside this repo — it references personal capture content):**
`~/flowhub-capture-run/RESUME-2026-09-14.md`

Supporting state, same directory: `CONVENTIONS.md` (ground-truth fixtures + decision
rules), `GLOSSARY.md` (operator shorthand), `notes/survey-2026-09-13.ndjson` (183 preview
results), `ledger.ndjson` (230 captures, 221 pending).

**Phase:** measurement complete, remediation starting. FlowHub v0.7.0 is deployed and
healthy on CT 136. A preview survey of 183 captures ran with zero writes; the operator
scored a random 10 and the classifier got **3 of 10** right. Five distinct defect classes
came out of that, filed as #92, plus a privacy gap filed as #93. The glossary on the
deployment has since been updated with person-scoping and prefix rules, and two Vikunja
projects (`TODO`, `Freunde`) were created — **none of it re-measured yet**.

**Next step:** two tasks, in order.

1. **Re-run the 10 scored captures** against the updated glossary via
   `POST /api/v1/captures/preview` and report the new figure against the 3/10 baseline.
   This isolates how much was fixable by configuration alone. The ids, correct targets
   and the token-minting recipe are all in the artifact. No code change involved.

2. **Enrich and dispatch flowhub#93** — sensitive captures must park rather than route.
   `/enrich 93` then `/gh:implement 93`. Use `superpowers:subagent-driven-development` for
   any implementation work. Note: every issue dispatched to agent-workflow in this project
   so far has returned `ai:review-blocked`, and in three consecutive cases the finding
   originated in the spec rather than the implementation — so review the spec's own
   assumptions before blaming the agent's code.

**Do not bulk-replay the capture backlog.** The survey exists because replaying it would
create a few hundred artifacts at an unknown error rate; repo inference alone misrouted at
least nine captures into unrelated repositories.
