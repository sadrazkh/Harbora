# Temporary Volume Download Link Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Somebody can produce a time-limited link to a file in an app's volume that works without a panel session — usable with `curl`, or handed to a colleague — and that stops working on its own.

**Architecture:** A one-time token minted by the panel, redeemed at a route that streams the file through the existing volume-file path. No new port, no new protocol, no credential store: the token in the link *is* the authentication, and it expires.

**Tech Stack:** .NET 10, ASP.NET MVC, EF Core, Razor, xUnit, FluentAssertions.

## The owner's three decisions, which this plan is shaped by

Asked and answered on 2026-08-16:

1. **A temporary HTTPS download link**, not SFTP and not WebDAV.
2. **A one-time token the panel mints** — the `AdminerService` model — not a customer-supplied key and not a credential store.
3. **No new port** unless an operator explicitly turns one on. This design opens none, so that question does not arise; it is recorded because it rules SFTP out of scope rather than merely deprioritising it.

Together these make D4 much smaller than its spec assumed. It rides Traefik, which already terminates TLS for the panel.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-16-volume-external-access-design.md`.
- **Zero NEW build warnings.** Exactly **2 pre-existing NU1903** on SSH.NET in `Harbora.Postgres.Tests` — leave them. **Security review is out of scope by the owner's standing instruction. The expiry, single-use and path rules below are functional requirements the owner chose, so implement them as specified without writing attack-scenario analysis.**
- **Baseline that must not drop:** build 0 errors; **4,376 passing, 0 failing**.
- **One migration**, for the token record. Generate it with a **fresh build — never `--no-build`**, which captures a stale model; `MigrationConsistencyTests` diffs the snapshot.
- **Never renumber an existing enum value.**
- **Bilingual.** Every user-visible string through `@T["…"]` or the `isFa` ternary.
- **The panel renders Persian by default in tests.** Assert on `data-` attributes, route fragments and status codes.
- **Test names read as sentences.**
- **Narrative commit messages** — read `git log --oneline -10`. End each with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Shared tree.** Never `git stash`, `git stash pop`, `git checkout -- .`, `git reset --hard` or `git clean`. Stage by explicit path; never `git add -A`.
- **`main.ts` carries a hand-maintained lucide icon list**; `IconCoverageTests` fails on a missing icon. Seven agents have hit this.
- **Environmental trap.** `MSB3491 "Access to the path … denied"` on `obj/` with a green suite means leftover MSBuild processes hold locks. Run `dotnet build-server shutdown`, then rebuild.

---

## Task 1: The token, and the rules it obeys

**Files:**
- Create: `src/Harbora.Domain/Storage/VolumeDownloadToken.cs`
- Create: `src/Harbora.Infrastructure/Storage/VolumeDownloadTokens.cs`
- Modify: `src/Harbora.Data/HarboraDbContext.cs` + one migration
- Test: `tests/Harbora.Tests/VolumeDownloadTokenTests.cs`

**Interfaces produced:** `VolumeDownloadToken` (entity), `VolumeDownloadTokens.MintAsync(app, volume, path, ct)`, `VolumeDownloadTokens.RedeemAsync(token, ct)` returning what to serve or a reason it cannot be served.

**Borrow the lifetime, do not restate it.** `AdminerSession.Lifetime` is `TimeSpan.FromHours(1)` and `AdminerSession.Expired(startedAt, now)` is the comparison (`src/Harbora.Infrastructure/Services/AdminerSession.cs:26,52`). **Read that file first.** If a download link should live for a different span, that is a different *value* — not a second notion of expiry with its own arithmetic. Two mechanisms would eventually answer differently about the same question.

**Four rules, each with its own test:**

1. **Single use.** A redeemed token is spent; redeeming it again is refused. The whole point of a shareable link is that it can be forwarded, so "used once" is what bounds where it ends up.
2. **Expires on its own.** Past the lifetime it is refused whether or not it was used, and a sweeper retires spent and expired rows the way `AdminerService.cs:186` does — an unbounded table of dead tokens is its own problem.
3. **The path is fixed at mint time**, never taken from the redeeming request. The token names exactly one file. A token that carried a path a caller could vary would be the volume-path defect this project fixed twice already.
4. **It belongs to one app.** Minting resolves through the app's tenant-filtered collection, as D1 does. The cross-tenant defect fixed in `6b0f91a` was a lookup that crossed that boundary.

**Store a hash, not the token itself** — the same reason the platform does not store passwords in plaintext, and it costs nothing here because the value is only ever compared.

- [ ] **Step 1:** Write the failing tests for all four rules plus a successful mint-then-redeem.
- [ ] **Step 2:** Run and watch them fail.
- [ ] **Step 3:** Implement the entity, the service, the `DbSet` and the migration. **Fresh build before generating it.**
- [ ] **Step 4:** Run the covering tests, then the full suite. Commit.

---

## Task 2: Minting from the screen, and redeeming without one

**Files:**
- Modify: `src/Harbora.Web/Controllers/AppDataController.cs`, `src/Harbora.Web/Views/AppData/Index.cshtml`
- Create: `src/Harbora.Web/Controllers/VolumeDownloadController.cs` — the redemption route, **deliberately outside the app routes** because it must work without a panel session
- Test: `tests/Harbora.Tests/Http/VolumeDownloadLinkHttpTests.cs`

**Interfaces consumed:** everything Task 1 produced, plus `VolumeFileService`'s existing read path — **reuse it; do not write a second way to read a file out of a volume.**

**The redemption route is the one piece that is deliberately unauthenticated**, because a link that needs a panel session is not a shareable link and would not answer the request. Everything that makes that acceptable lives in Task 1: single use, expiry, a fixed path, one app. **Do not add a second gate here that would make the link useless, and do not remove one of those four rules to make something else easier.**

**A refused token returns 404, not 403.** Expired, spent and never-existed are one answer to the caller. The panel is where somebody learns which it was.

- [ ] **Step 1:** Write the failing HTTP tests. Follow `tests/Harbora.Tests/Http/VolumeBackupHttpTests.cs`, which seeds an app with volumes, and use `client.AntiforgeryTokenFrom` for the POST. Cover: minting returns a link; that link serves the file **on a client with no session**; a second redemption 404s; an expired token 404s; an app in another workspace cannot mint.
- [ ] **Step 2:** Run and watch them fail.
- [ ] **Step 3:** Implement the mint action, the redemption controller and the button on the data screen. The screen shows the link, when it expires, and that it works once — a link whose one-shot nature is a surprise is a support ticket.
- [ ] **Step 4:** Run the covering tests, then the full suite. Commit.

---

## What this plan is not

SFTP or WebDAV — ruled out by decision 3, not merely deferred · a credential store · browsing, upload
and download inside the panel, which `apps/{id}/data` already does · a whole-volume archive; this is
one file per link, and a volume archive is what D1's backup already produces.
