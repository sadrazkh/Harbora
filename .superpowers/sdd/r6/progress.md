# Round six — the in-panel AI agent — progress

Design: `docs/superpowers/specs/2026-10-11-harbora-assistant-agent-design.md` (owner-approved 2026-10-11).
Owner's consent rule: safe writes may be approved once ("don't ask again"); dangerous writes always ask;
delete asks for the typed name. Everything else as the design's defaults.

Baseline: master `2331c22`, 6429 `Harbora.Tests`.

## Wave A (parallel)

| Agent | Brief | Branch / worktree | Status |
|---|---|---|---|
| A — app services (0a) | `a-brief.md` | `r6/app-services` · `../Harbora-r6-a` | dispatched |
| B — database services (0b) | `b-brief.md` | `r6/db-services` · `../Harbora-r6-b` | dispatched |
| C — chat client + settings (1) | `c-brief.md` | `r6/chat-client` · `../Harbora-r6-c` | dispatched |
| D — agent core (2) | `d-brief.md` | `r6/agent-core` · `../Harbora-r6-d` | dispatched |
| E — chat pane (3) | `e-brief.md` | `r6/chat-pane` · `../Harbora-r6-e` | dispatched |

## Wave B (after A, B, D land)

Write and destructive tools over the phase-0 services; then the learning-center page and end-to-end checks.
