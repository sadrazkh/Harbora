Last verified: 2026-11-08
Scope: `README.md`, `docs/overhaul/04-product-vision.md`, design specs and business rules in code at commit `b3fd974`. The stated vision is from 2026-07; the features were checked against the code.

# Product specification

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md). Status per feature lives in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md).

## Vision

[Verified] From `docs/overhaul/04-product-vision.md`: "the self-hosted PaaS that makes shipping and running apps on your own servers feel as calm and reliable as a managed cloud: installable in one command, honest about what it's doing, and safe by default". The bet is being trustworthy on the boring, critical things: install, deploy, rollback, backup, SSL, logs and monitoring.

[Verified] From the owner on 2026-11-04 (`docs/superpowers/specs/2026-11-04-launch-design.md`): the biggest problem is that customers find the product hard to learn. Someone who wrote an app should press one button and have it deployed, its database created and connected, and the result checked.

## Who it is for

[Verified] Named in the vision document: the solo developer with one server, the small agency hosting client apps, the small business running internal tools, and the provider reselling hosting with the multi-tenant layer.

[Assumption] Which of these leads the product today is undecided. See OQ-01.

## Principles

**[Verified]**, each visible in code or policy (`docs/product-audit/19-do-not-change-list.md`):

- Documentation describes shipped behaviour only. No feature is done because its screen exists.
- Refuse with a reason rather than degrade silently.
- Unmeasured is not zero: a number nobody measured is never shown as 0.
- A destructive action has a confirmation page.
- A locked control is a courtesy; the server is the check.

## Features

**[Verified]** that each exists in the code. Quality and gaps are in the status document.

### Getting an app running

- **Launch** (`/launch`, `harbora launch`): give a Git address, zip or folder. One screen shows what was found, what will be created and what it costs. One button creates databases, connects them under the names the code reads, deploys, and checks the result.
- **Other sources**: Git with or without a Dockerfile, Docker Compose, static site, prebuilt image, CLI upload, one-click templates, inline functions.
- **Git integration**: GitHub, GitLab and Gitea by token or OAuth; deploy on push or tag through verified webhooks; rollback.
- **Routing**: domains, automatic certificates, a visual route designer.

### Data

- Eight managed services: PostgreSQL, MySQL, MariaDB, Redis, MongoDB, Meilisearch, RabbitMQ, NATS. Encrypted credentials, private networking, one-click attach.
- Object storage buckets on MinIO.
- Backups: scheduled, encrypted, retained, to local disk, S3 or SFTP, with copies to Telegram or email.
- Point-in-time recovery and read replicas for PostgreSQL.

### Operations

- Live logs, metrics, alerts by email, Telegram, Discord or webhook, uptime checks, public status pages.
- Node agents for more servers, including behind NAT.
- A bilingual interface with Simple and Advanced modes, and a Learning Centre.

### Selling it

- Plans, instance sizes, quotas, a capacity-aware scheduler, per-tenant isolation.
- Prepaid balances and hourly billing.
- Feature entitlements per plan and per workspace.
- A provider console.

### Extras

- Functions in C#, JavaScript and Python, triggered by HTTP, cron, platform events or queue messages.
- An in-panel assistant that proposes changes as plans a person approves, gated by entitlement.
- An OpenAI-compatible AI gateway, in preview.

## Business rules

**[Verified]** against the code named.

| Rule | Where |
|---|---|
| A balance belongs to a workspace, never a person | `Billing/WalletService.cs` |
| Creating a billable resource prepays one running hour atomically; a refused creation leaves nothing behind | `ResourceCreationBilling.SaveAsync` |
| Hourly charge is the larger of the plan base rate and the sum of resource rates | design `2026-08-09-payg-billing-design.md`, `BillingTick` |
| Charging is on what is reserved, not measured | same |
| An instance size with no price cannot be created | `README.md`, `ResourceCreationBilling` |
| A wallet at zero suspends workloads and keeps their data | `Billing/BillingSuspension.cs` |
| Funding is by operator credit or single-use voucher only | `Billing/VoucherService.cs`, `appsettings.json` |
| Instance-size values are copied onto rows at creation, so editing a tier never changes running work | `docs/product-audit/19-do-not-change-list.md` item 17 |
| Every account gets one personal workspace and may own or join shared ones | `README.md`, `Workspaces` |
| Invitations are single-use and last seven days | `README.md` |
| Launch creates nothing before the confirm screen | `Launch/LaunchService.Confirm.cs` |
| A protected environment needs a second approver, and auto-approves when none exists | `DeploymentEngine.cs:125` |

## Non-goals

**[Verified]** as stated in `docs/product-audit/20-final-recommendation.md` and the design specs.

- No rewrite of the pipeline, no microservices, no Kubernetes.
- No in-house APM.
- No online payment gateway at present.
- No mailbox hosting in core (though a Stalwart path exists; see OQ-05).
- No scale-to-zero functions.
- No second panel for Simple mode.
