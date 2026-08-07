# 20 — Final Recommendation

## The one-paragraph verdict
Harbora is far more real than a typical pre-1.0 self-hosted PaaS: the deploy engine, node protocol, upgrade safety, tenancy model and UI discipline are genuinely strong, test culture is unusually good (3,141 green tests, conformance suites, honest skips), and several subsystems (node agent v1, "unmeasured ≠ zero" UI, recovery CLI) are best-in-class for the category. What stands between this codebase and a product people trust with production data is **not missing features — it is six reliability defects** (serial queue, backup crash-lock, false-success on proxy failure, wrong-host backups on multi-node, broken node onboarding, no job timeout) plus a **documentation/truth debt** that the project's own "honest software" principle forbids. Fix truth first, capability second.

## Strategy in five sentences
1. **Phases 1–2 are non-negotiable and sequential** (deploy-engine truth & queue; node confidence + test lanes + docs truth) — they convert "works when I watch it" into "provable nightly".
2. Then finish the **application/environment model** (the repo's longest-open transition) and ship the two visible wins hiding in nearly-done code: **PR preview environments** and the **/activity job view**.
3. Build the **notification system** on the existing durable queue (09) before any email ambition; then customer email as **BYO-provider + local ingest relay + Dev Inbox** (10/11) — the Dev Inbox is the category-first differentiator.
4. **Managed relay later** (SES-class upstream, SES-style tenant sandboxing); **mailbox hosting never in core** — serve it as a Stalwart template or hosted-provider integration if demand appears.
5. Marketplace/CLI/API, Simple-mode depth, and admin/plan operations follow demand order (17), with the reseller story (real multi-tenancy — the structural gap in Coolify/Dokploy) as the differentiation axis and Coolify-Cloud-style hosted-control-plane pricing as the monetization shape to evaluate (needs Q2 product call).

## What NOT to do (as important)
No rewrite of the pipeline; no microservices; no Kubernetes; no in-house APM; no metered multi-SKU billing; no 280-template chase; no GPU/edge/scale-to-zero adventures; no second panel for Simple mode (deepen `PanelMode`); no deleting the legacy backup path until the module has live-host parity proof; no third parallel channel system — 09 consolidates.

## Success criteria for "v1.0 trustworthy" (measurable)
- L5 live-host lane green nightly ×30 days (install→node→deploy→backup→restore→upgrade).
- Zero P0/P1 open from 03; queue head-of-line demo passes; kill-mid-backup drill passes.
- README/RUNBOOK contain no claim the code can't demonstrate (R-16 closed).
- A newcomer reaches deployed-app-with-domain-and-backup in <15 min on a fresh VPS without touching a config file.
- Every alert-worthy event lands in the in-app center with delivery status visible.

## Effort shape (rough)
Phases 1–2 ≈ 25–35 % of total roadmap effort but ≈ 70 % of the risk retirement. Phase 3 is the largest single phase (env migration + previews). Notification+Email (9/10) together ≈ the size of Phase 3. Everything after is demand-driven and independently shippable.

## Final note on process
This audit found the codebase's own comments and docs to be exceptionally self-aware (incidents recorded at the fix site, deliberate-decision comments everywhere). The failure mode has not been carelessness — it is **plan/doc divergence across four phase-numbering schemes and feature claims outrunning verification**. Adopting 17 as the single roadmap and the delivery rules in 19 §29 as CI-enforced policy addresses the root cause, not just the symptoms.
