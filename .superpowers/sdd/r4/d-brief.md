# D — A one-time sign-in token

## What the owner asked for

A way to mint a token, hand it to an AI agent, and have that agent sign in to the **panel UI** as
them, do its work, and be done. Not an API token — a real browser session, because the agent drives
the UI.

## There is already a template for this, and you must follow it

`src/Harbora.Domain/Identity/SupportSession.cs` solves the identical problem — temporary borrowed
access to somebody's panel session — and its doc comment states the architecture in one sentence:

> the cookie a support session issues carries nothing but this row's id; every request re-reads the
> row and stops the moment the row says stop. A cookie that outlives its row is inert, which is the
> only reason a one-hour lifetime means anything at all.

Read that file, `SupportAccess` at the bottom of it, and then all of:

- `src/Harbora.Web/Infrastructure/HttpCurrentUser.cs` — `HarboraClaims`, and how a support session's
  row id rides as a claim.
- `src/Harbora.Web/Infrastructure/SessionPrincipalFactory.cs` — how the principal is built.
- `src/Harbora.Web/Infrastructure/WorkspaceMembershipValidationMiddleware.cs` around line 78 — the
  per-request re-read, and the comment explaining why an absent claim, a malformed claim and a claim
  with no live row behind it are three different cases.
- `src/Harbora.Web/Infrastructure/SupportSessionView.cs` and
  `src/Harbora.Web/Views/Shared/_SupportSessionBanner.cshtml` — how an active borrowed session is
  shown on every page.
- `src/Harbora.Infrastructure/Security/AccountSessionService.cs` — `CreateAsync`, `RevokeAsync`, and
  `Lifetime`. Sessions here are already server-side rows re-read per request; **reuse this**, do not
  invent a second session mechanism.
- `src/Harbora.Web/Controllers/AccountController.cs` — `SignInAsync` and `CompleteSignInAsync`.

Build the analogue, in this codebase's own vocabulary. Anything you find yourself inventing from
scratch, check first whether `SupportSession` already answers it.

## Domain

`src/Harbora.Domain/Identity/SignInToken.cs` — a row that the access **depends on**, not a log
written after it:

- `UserId` — whose session it grants. The creator's own account; a token cannot elevate.
- `WorkspaceId` — the scope the session opens in.
- `TokenHash` — the hash only. The token itself is shown exactly once, at creation, and never stored
  or logged. **Find the existing hashing used for `TokenType.Api`/`Cli` tokens and reuse it** rather
  than introducing a second scheme.
- `Purpose` — required free text, shown on the banner. Same reasoning as `SupportSession.Reason`:
  "a token session is active" is not something the owner can act on; "gave a token to an agent to
  fix the failing deploy on shop" is.
- `CreatedByUserId`, `CreatedAt`, `ExpiresAt` (written once, for the reason `SupportSession.ExpiresAt`
  gives — a later change to the lifetime must not retroactively move an end time somebody was told).
- `RedeemedAt`, `RedeemedIp`, `SessionId` — null until redeemed. `SessionId` is what lets revoking
  the token kill the session it created.
- `RevokedAt`, `EndedBy` (an enum distinguishing revoked / expired-unused / session-ended, because
  "ended" and "ran out" read differently — `SupportSessionEnding` makes the same distinction).

Plus a pure `SignInTokenAccess` rules class beside it, shaped like `SupportAccess`: the two lifetimes,
`MaxPurposeLength`, `Expired(createdAt, now)` with the clock as a parameter, and a `RefuseCreate`/
`RefuseRedeem` returning null or a sentence the caller can act on. Pure, so the refusals are testable
without a request.

**Two separate lifetimes, and say why in the code:**
- the **token** stays redeemable only briefly — it is a bearer credential in transit, sitting in a
  chat log or a clipboard;
- the **session** it creates is bounded on its own, like `SupportAccess.Lifetime`.

Pick defaults and justify them in the doc comment. Do not make them configurable this round.

## The correctness requirement that matters most

**"One-time" must be a constraint, not a check.** A read-then-write — load the row, see
`RedeemedAt is null`, then save — is the exact defect `HARBORA-0057` was opened for: two concurrent
redemptions both read null and both proceed. Read commit `3c755ac` for how that was settled there.

Redeem with an **atomic conditional update** (`ExecuteUpdateAsync` with `RedeemedAt == null` in the
predicate, or equivalent) and branch on rows-affected. One winner gets a session; the loser is refused
with the same message a spent token gets. A test must cover two concurrent redemptions producing one
session — if it needs Postgres it goes in the Postgres lane and is reported as unverified locally.

## Per-request enforcement

A session created by a token carries a claim with the token row's id, exactly as a support session
does. The middleware re-reads that row on every request; a token revoked a second ago stops the very
next request. A claim present with no live row behind it **ends the request** — it must not continue
as an ordinary session, for the same reason the support path refuses to continue without a banner.

## Panel

- A page under the owner's own account area to mint a token: purpose (required), and the token shown
  **once** with a copy control and a plain statement that it will not be shown again.
- A list of this user's tokens with status — unredeemed / redeemed-and-live / spent / revoked /
  expired — and a revoke button for each.
- A redemption route. Accept it as a `POST` form as well as the link, so the credential is not forced
  through a query string for the form path.
- A banner on every page while a token session is live, naming the purpose and offering to end it —
  the same shape as `_SupportSessionBanner`.
- Bilingual, English and Persian, matching the surrounding copy. The request culture is `fa`.

## Audit

`AuditLog` already carries `SupportSessionId` so actions under borrowed access are attributable. Add
the analogous attribution for a token session, so what the agent did is distinguishable from what the
owner did. Follow how the support path threads it.

## Scope boundaries

Not this round: per-token permission scoping or read-only tokens, multi-use tokens, IP allow-lists,
configurable lifetimes, email delivery of the link. One token, one session, bounded and revocable.

Do not touch `MonitoringController`, the Monitoring views, `deploy/`, `.github/workflows/ci.yml`,
`_Layout.cshtml`, `Design/_Sidebar.cshtml`, `Scripts/main.ts` or `Scripts/app.css` — three parallel
agents own those.

## Migration

You are the only agent adding one this round. **Build the project before scaffolding** — a migration
scaffolded against a stale assembly captures the old model, and `MigrationConsistencyTests` will
catch it only after you have wasted the round trip.

## Tests

- The token is never stored in plaintext, and the row's hash matches what redemption checks.
- Redeeming once works and opens a session scoped to the right user and workspace.
- Redeeming a second time is refused, and the first session keeps working.
- Two concurrent redemptions produce exactly one session (Postgres lane if it needs a real database).
- An expired unredeemed token is refused.
- A revoked token's live session stops at the next request.
- Revoking after redemption ends the session it created.
- A claim naming a row that does not exist ends the request rather than continuing.
- The banner renders while such a session is live, in both languages, and names the purpose.
- `MigrationConsistencyTests` passes.

## Verify

Per `.superpowers/sdd/r4/CONSTRAINTS.md`. Baseline 6224 in `Harbora.Tests.dll`.

**You are building an authentication path.** Getting it right matters more than getting it done
quickly: state plainly in your report anything you were unsure of, and anything you could not prove
on a machine with no Postgres. Do not perform a security review or write attack scenarios — that is
explicitly out of scope for this project; build the feature carefully and say what you built.
