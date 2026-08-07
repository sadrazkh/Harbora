# 19 — Do-Not-Change List

Things that work, carry hard-won lessons (often documented in code comments referencing real incidents), and must survive every future phase. Implementation agents: treat edits to these as requiring explicit justification + the named protective tests green.

## Engine & state
1. **`DeploymentStateMachine` single-write-path** (illegal transitions throw) and the immutable `Deployment` row model. Tests: `DeploymentStateMachineTests`.
2. **Zero-downtime cutover order** (start new → wire proxy → retire old → release ports) + versioned container names. Tests: `DeploymentPipelineCutoverTests` (519 lines) — must pass unmodified through any refactor.
3. **Failure containment:** on a failed deploy only the new container is removed; the previous release is never touched.
4. **Rollback pre-flight** (image-still-exists check, both at plan and pipeline start) and the pruned-artifact refusal UX.
5. **`ServerEngineFactory` resolution order with terminal throw** — never add a silent local-daemon fallback (`ServerEngineFactory.cs:61-63` comment).
6. **`NodeWorkloadEngine.ListContainersAsync` throwing on failure** instead of returning empty (prevents cutover-onto-nothing; `NodeWorkloadEngine.cs:254-259`).
7. **`LogText.Clean` NUL-stripping** (Postgres 0x00 incident) and the secret-redaction path on every deploy log line.

## Node agent & contract
8. **Contract discipline:** schema + C# + conformance tests change in the same PR; additive-only within v1; unknown fields ignored; closed verb enum with the no-shell/exec conformance test.
9. **Durable outbox + command ledger semantics** (nonce+freshness+idempotency surviving restarts; Cancelled/TimedOut deliberately not idempotency-recorded — `CommandDispatcher.cs:126-128`).
10. **Marker-before-swap self-update + post-restart version adjudication + rollback**; enrollment-token shredding; loopback-only metrics bind (validated, not defaulted).
11. **Ingress tunnel's 4-byte-port-only `Open` frame and 127.0.0.1-only dialing** — the property that makes the tunnel not-a-port-forward. Tests: `IngressTunnelTests`, `IngressEndToEndTests`.
12. **Drain flag persisted before any stop**; undrain never auto-restarts workloads (control-plane owns intent).

## Data & upgrade safety
13. **Boot order: restore point → migrate → seed; exit-nonzero on failure** (`Program.cs:161-183` records the hang incident). `UpgradeSafetyService` refusal on failed/zero-byte dump (the zero-byte check exists because a helper once wrote to a differently-named volume and reported success).
14. **`harbora` recovery CLI running panel-down** (one-off container, never starts the web server) + `restore-db`'s own pre-restore dump + typed database-name confirm + `fix-key`'s typed `REPLACE` gate.
15. **`MigrationConsistencyTests`** and the one-migration-per-feature discipline; never renumber enum wire values.
16. **Tenancy: global query filters + `Guid.Empty` deny-by-default + explicit controller predicates + documented unfiltered-table rationale blocks.** Tests: `WorkspaceQueryFilterTests`, `CrossTenantIsolationTests`.
17. **Instance-size values copied onto rows at creation** (tier edits never retro-change running workloads).

## UI & product surface
18. **"Unmeasured ≠ zero"** — `_Metric.cshtml` as the only measured-number printer; `AllocationReading`; suppressed progress bars without denominators. Tests: `MetricDisplayTests`.
19. **Destructive-action confirmation pages** (`Databases/ConfirmRemove`, `Apps/ConfirmRollback`, `Networks/ConfirmMove`, `CloneEnvironment` pre-flight) — extend the pattern, never downgrade to native confirm().
20. **Live truth checks** (per-domain DNS/TLS buttons; template deploy disabled-with-reason; architecture map from real env vars).
21. **RTL craft**: logical properties, deliberate LTR islands (code/IDs/terminal/graph), Inter+Vazirmatn, `rtl:rotate-180` idiom.
22. **Design tokens** (semantic `surface/ink/line/ok/warn/...` CSS-variable palette, dark mode) — new UI must consume tokens (RouteDesigner is the counter-example to fix, not follow).
23. **PanelMode fold-never-remove principle** with live routes in both modes.
24. **Clone-environment exclusions** (domains, volume contents, DB passwords deliberately not copied) and its quota pre-flight.

## CLI & install
25. **CLI deploy-mode precedence** (`DeployPlan.Decide`, 14 pinned tests) and the reason-string it prints; upload as raw gzip stream (never buffered); `.env` never uploaded.
26. **`install.sh` idempotency contract:** existing `.env` never overwritten; `backfill_env` only-when-absent; `repair_env` before start on update; typed-prompt uninstall keeping volumes by default; server/CLI name-collision guard (`harbora-cli` fallback).
27. **`harbora.yml` unknown-key tolerance** (forward compatibility for the IaC growth path).
28. **Single version source** (`Directory.Build.props`) for panel+CLI; node-agent release artifacts with `.sha256` and installer verification (extend to CLI releases, never remove from agent).

## Process
29. **Honest-software delivery rules** from `docs/overhaul/17-next-roadmap.md`: documentation describes shipped behavior only; no phase is done because its UI exists; refusal-with-reason over silent degradation. This audit's phases (17) adopt them as exit-gate policy.
30. **`DockerFactAttribute` + CI `NotExecuted` guard** — container tests may skip locally, never silently in CI.
