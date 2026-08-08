# 18 — Open Questions

Questions the code could not answer. Each has options + a default recommendation; analysis proceeded on the default. None blocks Phase 1.

**Q1 — Multi-service templates: actually deployable as a unit on a real host?**
Docs contradict (`15-phase-plan.md` P7 ❌ + progress.md "not deployable as a unit" vs `17-next-roadmap.md` "delivered baseline"); tests suggest dependency-first provisioning exists. Options: (a) live-host proof test, (b) treat as unfinished. **Default: (a)** in Phase 2's L5 lane; schedule 0044 accordingly.

**Q2 — Target market emphasis: self-hosted single operator vs reseller PaaS?**
Affects how hard Phase 13 (admin/plans) and metering push. **Default:** dual-track with reseller as differentiator (README already sells it), monetization shape à la Coolify Cloud noted in 20 — needs a product-owner call.

**Q3 — Legacy `Harbora.Agent` sunset timeline?** — **ANSWERED, Phase 2 Task 9.**
The default was taken: frozen and deprecated, no removal date. A badge on the Servers page (where the decision to use it is actually made), a deprecation block in `docs/node-agent/merge-notes.md`, the README's section rewritten as "for the fleets already on it", and the RUNBOOK no longer teaches it at all. **Support continues for at least two minor versions after v0.2.0**, stated in all three places; the EOL decision still waits on Node v1 GA telemetry. `DocumentationDriftTests` ties the badge to `src/Harbora.Agent`'s existence so it cannot silently disappear while the project does not. 0051.

**Q4 — AI subsystem: validate or gate?** — **HALF ANSWERED, Phase 2 Task 9.**
Gateway never exercised against a real provider; earlier docs gated AI behind demand validation, yet it shipped. Options: (a) verify one provider (OpenRouter) + keep, (b) flag off by default until validated, (c) remove from Simple mode only. Default was (a) + (c). **(c) is done** — Advanced-only in `NavigationMap`, a *Preview* block on `/ai` saying precisely what is unproven, and the same sentence in both READMEs and in `platform-expansion-v1.md`. **(a) is not**, and could not be here: it needs a provider account and a key, neither of which exists in this environment. (b) was rejected — nothing is disabled and no flag was invented; the routes answer in both modes and the gateway's own code is untouched, so undoing this is deleting a Razor block and one `Advanced: true`. 0054 stays open on (a) alone.

**Q5 — Do Git providers' webhook payloads carried today include PR open/close events needed for previews?**
Push/tag verified in code; PR events unverified. **Default:** assume GitHub yes / Gitea yes / GitLab MR events need mapping — verify during Phase 3 design spike.

**Q6 — Kopia + S3: is a config-file-based credential path acceptable?**
Current refusal is credential-on-cmdline based. **Default:** investigate `--config-file`/env approach in Phase 8; if not clean, native engine remains the S3 path and Kopia stays local/SFTP.

**Q7 — Node volume snapshot artifact transport: tunnel stream vs presigned HTTPS upload?**
Contract has no streaming-upload frame (known gap in CHANGELOG v1.0.0). **Default:** presigned HTTPS PUT to panel (or MinIO) — avoids a contract change; decide in Phase 5 design.

**Q8 — Volume `SizeLimitBytes`: enforce or relabel as advisory?**
Enforcement (quota at panel-controlled write paths only) is partial by nature. **Default:** relabel "measured usage / soft limit" + alert at threshold; revisit hard enforcement only with real demand.

**Q9 — Third language ambition?**
Localization consolidation cost depends on it (ternary→resx pays off triple if a third language ever lands). **Default:** consolidate anyway (Phase 12) — maintainability alone justifies it.

**Q10 — Mobile/RTL device audit.**
This environment couldn't run the browser against a live panel (no Docker, no DB creds). **Default:** add a Playwright mobile-viewport smoke (L6) in Phase 2 and a manual device pass during Phase 12.

**Q11 — Billing: manual credit ledger in Phase 13 or defer entirely?**
Metering basis exists; no ledger/invoicing. **Default:** defer ledger until Q2 answered; Phase 13 ships usage overviews + manual limit editing only.

**Q12 — Public docs site (tutorial is in-repo Persian only)?**
**Default:** Phase 15; English translation of the tutorial rides the Phase 12 localization effort.

**Q13 — `nodeagent` on Windows/macOS hosts?**
Agent is Linux-gated post-verification. **Default:** Linux-only stays; document explicitly (currently implicit).

**Q14 — Ingress-tunnel throughput ceiling for busy apps (README's own caveat "worth measuring")?**
No benchmark exists. **Default:** add a load micro-benchmark to the L5 lane (Phase 2, best-effort) before recommending tunnels for production traffic.

---

Added 2026-08-08, while folding the Phase 1–2 discoveries into `backlog.json`. Each is a question the code cannot answer because it is a decision about what to *tell* somebody, not about what is true.

**Q15 — What should a workspace-scoped operator see in the audit log?** (0056)
`AuditController` reads `AuditLogs` unscoped. The table is deliberately unfiltered — that is right for the writer, since most rows are platform events belonging to no workspace — but the reader is a page a tenant can open. Options: (a) provider-only page, hidden from workspace roles entirely; (b) scoped to the caller's workspace, which hides the platform events that are most of the table and makes the page look broken; (c) scoped for workspace roles, unscoped for the provider, with the page saying which view you are looking at. **Default: (c)** — it is the only one that does not either leak or lie, and the "which view is this" line is the same idiom the Nodes and Backups pages already use. Not a blocker for any phase before 4.

**Q16 — What do `runningWorkloads` and `activeTunnels` on the Nodes page mean?** (0063)
Today: the count of running *containers* the node manages (a two-container workload reads as two), and every connected tunnel *including the node's own ingress tunnel*. Both have been on the wire with those meanings since contract v1.0.0, and a field's meaning is frozen within a major version, so the code cannot be quietly corrected. Options: (a) change the labels to say containers, and tunnels-including-ingress; (b) add `runningContainers` and `databaseTunnels` as additive v1.4.0 fields and leave the originals alone; (c) both. **Default: (a)** — the numbers are honest, only the words are wrong, and a contract change to relabel a screen is a large price for a small clarity. Revisit if anything ever needs to *act* on the distinction rather than display it.
