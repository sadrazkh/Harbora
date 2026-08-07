# 18 — Open Questions

Questions the code could not answer. Each has options + a default recommendation; analysis proceeded on the default. None blocks Phase 1.

**Q1 — Multi-service templates: actually deployable as a unit on a real host?**
Docs contradict (`15-phase-plan.md` P7 ❌ + progress.md "not deployable as a unit" vs `17-next-roadmap.md` "delivered baseline"); tests suggest dependency-first provisioning exists. Options: (a) live-host proof test, (b) treat as unfinished. **Default: (a)** in Phase 2's L5 lane; schedule 0044 accordingly.

**Q2 — Target market emphasis: self-hosted single operator vs reseller PaaS?**
Affects how hard Phase 13 (admin/plans) and metering push. **Default:** dual-track with reseller as differentiator (README already sells it), monetization shape à la Coolify Cloud noted in 20 — needs a product-owner call.

**Q3 — Legacy `Harbora.Agent` sunset timeline?**
Options: freeze+deprecate now (badge, docs removal, no removal date) / hard EOL in N releases / keep indefinitely. **Default: freeze+deprecate in Phase 2**, EOL decision after two releases of Node v1 GA telemetry.

**Q4 — AI subsystem: validate or gate?**
Gateway never exercised against a real provider; earlier docs gated AI behind demand validation, yet it shipped. Options: (a) verify one provider (OpenRouter) + keep, (b) flag off by default until validated, (c) remove from Simple mode only. **Default: (a) + (c)** — small effort, honest UI (0054).

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
