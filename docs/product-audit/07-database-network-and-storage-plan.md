# 07 — Database, Network & Storage Plan

## 1. Managed databases

**Keep:** 7-engine catalog with curated/overridable versions; prefixed attach env (`HARBORA_DB_*` — lets two DBs of one engine coexist); encrypted creds decrypted on demand; throwaway 1-hour Adminer; honest "not measured" metrics; per-environment network scoping.

### Phase 3 work
1. **Silent provision failure (R-15):** persist failure reason on `ManagedService`, raise alert, show on details page with a "retry provision" action (job retry from 06 §1.2 gives this for free for transient causes).
2. **Rotation completes the loop (R-29):** after env rewrite, offer "redeploy N attached apps now" (batch queue) — default prompt, not automatic.
3. **TLS bootstrap policy (R-31):** explicit `RequireTls` toggle per service; failure = provision `Failed`, not silent plaintext.
4. **Connection snippets (S):** per-engine, per-language (C#, Node, Python, PHP, Go) rendered from the same values the attach panel shows.
5. **Logical export/import (M):** download `pg_dump`/`mysqldump` artifact; upload-restore with typed confirm — reuse the containerized dump commands from the backup module (`DatabaseDumpCommands`), which already pin client image = server image.

### Phase 5+ work
6. **Engine version upgrade (L):** snapshot → parallel container on target version → logical restore → verify (row counts + rehearsal) → cutover attach env → old container retired after grace. PG/MySQL/MariaDB first (dump tooling exists); Redis/Mongo excluded until their dump support lands.
7. **External access on nodes (L):** implement real `INodeAgentClient` against the shipped grant verbs + TCP tunnel (`FixedTunnelTarget` path is agent-complete and e2e-tested); replace `FakeNodeAgentClient` (R-08 fixes the local branch first in Phase 2).
8. **DB metrics beyond container stats:** engine-level (connections, slow queries) via sidecar exporters — P3, only when Monitoring phase lands.

## 2. Networking

**Keep:** per-environment networks + architecture map derived from real env vars; connection-rules table with "move the database" remedies; visual route designer with validate-before-apply.

### Work
1. **Finish the workspace→environment network migration (Phase 3, M):** today every workload dual-attaches `harbora-ws-{slug}` + env network (`NetworkPlan.For`). Plan: stop attaching the workspace network for new deployments behind a flag → sweep existing on redeploy → make `App.EnvironmentId`/`ManagedService.EnvironmentId` non-nullable in one migration (closing the 46-migration transition) → remove dual-attach code. Test: cross-environment isolation asserted (exists: `EnvironmentNetworkTests`, `CrossTenantIsolationTests`).
2. **Cron network parity (R-12, Phase 1, S).**
3. **Reference variables (Phase 5, M):** generalize `TemplateReferences` so any app env var can be `${service.KEY}` — resolved at deploy, re-resolved on rotation (pairs with R-29).
4. **Generic TCP exposure (P3):** the DB gateway generalizes (`TcpGatewayPlan` is engine-agnostic); productize only on demand.
5. **gRPC/h2c toggle + real-IP guidance (Phase 5, S/M):** surface Traefik `h2c` scheme per route; document real-IP header behavior per topology (direct, tunnel, Cloudflare).
6. **Wildcard/DNS-01 (P3):** provider-plugin certs (Cloudflare token first); until then the per-subdomain HTTP-01 default stands (README already explains it).

## 3. Volumes & object storage

**Keep:** lazy volume creation; mount-path validation; file browser with audited writes; MinIO bucket subsystem (own credential per bucket, object browser confined by that credential, platform-unique names, quota measurement).

### Work
1. **Multi-node truth for data (R-04 + 06 §2.3, Phase 1–2):** volume backup/restore per-server engine or explicit refusal; node snapshot verbs dispatched.
2. **Deletion safety (Phase 3, S/M):** wire the existing `deleteData` parameter into the unmount form with a typed confirm; add `Volume.Protected` flag blocking app-delete-with-volumes; orphan-volume report (list Docker volumes labeled `harbora-*` without rows — needs `ListVolumesAsync` addition to `IDockerEngine`, additive).
3. **Resize semantics (Phase 3, S):** `SizeLimitBytes` is advisory today (measured vs limit). Either enforce via quota check at write-time paths the panel controls (uploads) + document advisory nature, or drop the "limit" label. Decision in 18 (Q8).
4. **Bucket lifecycle (P3):** retention rules per bucket; bucket backup story (S3→S3 sync via existing sync? out of scope until asked).
5. **Roadmap acknowledgment (Phase 2, docs):** object storage exists only in the tutorial — bring it into the feature inventory/roadmap officially (this audit does so; keep it in 17).

## 4. Data-model consequences (details in 14)
- `Volume.Protected`, `ManagedService.ProvisionError`, unique active-backup index, `Route`/`DomainName` unchanged.
- No breaking migrations; the one required-column change (EnvironmentId) is the flagged finisher of an already-started transition.

## 5. Acceptance criteria bundle
- Remote-node service backup either produces a byte-identical restoreable artifact from the correct host or refuses with a named reason. Never silent-wrong.
- A cron app in `staging` resolves its staging DB by hostname.
- Killing MinIO container → Storage page shows the exact missing dependency (already good) and bucket ops fail with actionable copy.
- After the env-network finisher, `docker inspect` of a fresh workload shows exactly one Harbora network.
