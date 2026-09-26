# D — A one-time sign-in token — report

## Status: DONE_WITH_CONCERNS

Everything in the brief is built and the full suite is green, but I want to be plain about one
consequence of the design the brief specifically asked for (see "What I could not prove locally"):
the true concurrency guarantee, and therefore the deepest part of "one-time is a constraint", is
proven only in the Postgres lane and is unverified on this machine.

## Commits

- `be8401c` — the feature (this is the coordinator's WIP-rescue commit; my own work was mid-run when
  it fired, but everything in it is mine and it is what I built to)
- `a5979d7` — `Refuse minting a sign-in token under a support session` (fixes
  `SupportRestrictionCensusTests`, found by running the full suite after the commit above)

Both carry only the owner's name/email, per `CONSTRAINTS.md`.

## Test summary

Final full-suite run: `Passed! - Failed: 0, Passed: 6286, Skipped: 0, Total: 6286 - Harbora.Tests.dll`.
(Baseline in the brief was 6224; the difference includes my 41 new facts plus another agent's work
that landed via a mid-session rebase onto a shared integration branch — not mine, but confirmed still
green.) `Harbora.NodeAgent.Tests.dll` and `Harbora.NodeIngress.Tests.dll` unaffected.
`Harbora.Postgres.Tests.dll`: my 5 new facts report `Skipped` with an honest reason (no Docker on this
machine) rather than a false pass. `MigrationConsistencyTests` (both facts) pass.

New test files: `tests/Harbora.Tests/SignInTokenAccessTests.cs` (17 facts, pure rules),
`tests/Harbora.Tests/SignInTokenServiceTests.cs` (14 facts, EF InMemory),
`tests/Harbora.Tests/Http/SignInTokenHttpTests.cs` (10 facts, full pipeline),
`tests/Harbora.Postgres.Tests/SignInTokenRedemptionTests.cs` (5 facts, real Postgres — unverified here).

## The two lifetimes, and why

- **Token redemption window — 15 minutes** (`SignInTokenAccess.RedemptionLifetime`). The token is a
  bearer secret in transit — a clipboard, a chat log handed to an agent — so it should stay usable
  for about as long as it takes to actually hand it over and have the agent use it, not for the
  length of the work that follows.
- **Session lifetime — 1 hour** (`SignInTokenAccess.SessionLifetime`), and deliberately **the same
  constant as `SupportAccess.Lifetime`**, not a second one chosen independently. An agent let loose in
  the panel to finish a task and be done is the same shape of temporary, bounded, revocable access as
  a support engineer borrowing an hour — not the shape of an ordinary seven-day sliding sign-in.
  `SignInTokenAccessTests` pins the equality so the two cannot silently drift apart.

Both are written once on the row (`SignInToken.ExpiresAt` at creation, `SignInToken.SessionExpiresAt`
at redemption) rather than recomputed from a constant at check time, for the same reason
`SupportSession.ExpiresAt` is: a later change to either constant must not retroactively move an end
time somebody was already shown. `SessionExpiresAt` is a field beyond what the brief's own bullet list
names explicitly — it has to be, because the session's own clock only starts at redemption, which is a
different moment than the token's `CreatedAt`. I flagged this to myself as the one deliberate addition
to the brief's literal field list and am flagging it to you too.

## How single-use was made atomic

`SignInTokenService.RedeemAsync` never does read-then-write on `RedeemedAt`. The claim is one
conditional `ExecuteUpdateAsync`:

```csharp
db.SignInTokens.Where(t => t.Id == tokenId
        && t.RedeemedAt == null && t.RevokedAt == null && t.ExpiresAt > now)
    .ExecuteUpdateAsync(s => s
        .SetProperty(t => t.RedeemedAt, now)
        .SetProperty(t => t.RedeemedIp, ipAddress), ct);
```

Rows-affected is the only signal the caller gets: `1` means this call is provably the only one that
could have won, `0` means someone else already had (an honest second use and the loser of a race read
the identical refusal, deliberately — there is no outside way, and no need, to tell them apart). Only
the winner goes on to open a `UserSession` via the existing `AccountSessionService.CreateAsync` and
stamp `SessionId`/`SessionExpiresAt` back onto the row. This is HARBORA-0057's own fix (`3c755ac`) in
the shape the brief asked for specifically — a conditional update, not that commit's unique index —
because this race is between two updates of one already-existing row, not two inserts.

**The one thing I want called out plainly:** EF's InMemory provider — what every HTTP and most unit
tests in this project run against — does not implement `ExecuteUpdateAsync` at all; it throws
`InvalidOperationException` the instant the query is compiled. I discovered this the hard way (every
redemption-touching test failed with that exception on the first run). Rather than leave the whole
feature untestable locally, `RedeemAsync`'s claim step branches on `db.Database.IsRelational()`: the
real conditional `ExecuteUpdateAsync` for Postgres, and a plain, **deliberately non-atomic**
read-then-write fallback for InMemory, gated and commented exactly like the existing
`DataRetentionSweeper`/`UptimeChecker`/`MetricsCollector` precedent for `ExecuteDeleteAsync`. The
fallback is what lets the *ordinary* path — mint, redeem, the banner in both languages, a second
honest attempt, revoke-ends-the-live-session — run through the real HTTP pipeline on this machine. It
is not what proves the concurrency guarantee, and I was careful not to let any InMemory test pretend
it does: `SignInTokenRedemptionTests.Two_concurrent_redemptions_of_the_same_token_produce_exactly_one_session`,
the fact that actually races two independent connections against one row, lives only in the Postgres
lane and only runs against the real `ExecuteUpdateAsync`.

## What I could not prove on this machine

- **The concurrency guarantee itself.** No Postgres, no Docker here, so
  `SignInTokenRedemptionTests` (two concurrent redemptions → one winner; redeem-once; redeem-twice;
  expired-unredeemed; revoke-before-redemption) reports `Skipped` with an honest reason rather than a
  false pass. I read them over carefully and they follow `PartialUniqueIndexTests`' own shape closely,
  but nobody has watched them go green.
- **As a direct consequence of the above:** the InMemory fallback that makes the rest of the feature
  testable locally is, by its own design, not proof that Postgres's real row-lock behaves the way I
  built for. I'm confident in the `ExecuteUpdateAsync` shape because it mirrors HARBORA-0057's
  already-shipped, already-understood pattern, but "confident by analogy" is not "watched it pass."

## Things I was unsure about and decided on my own judgement

- **Which workspace a minted token scopes to.** The brief says "the scope the session opens in" but
  doesn't say how it's chosen. I bound it to the owner's *current* workspace at mint time (the one
  already in their session claim) rather than adding a workspace picker — simplest thing that could
  work, and consistent with "not this round: ... configurable lifetimes" reading as "keep this round
  small."
- **The banner's render path.** `_Layout.cshtml` is off-limits and already renders
  `_SupportSessionBanner` unconditionally. I added one line to that existing partial
  (`<partial name="_SignInTokenSessionBanner" />`) rather than touch the layout, and put all the new
  banner's own markup in its own new file. This is a one-line change to a file that isn't on the
  forbidden list, but it's adjacent enough to it that I want it named rather than left to be noticed
  later.
- **Reusing `SupportRestrictedAct.ApiToken`** for `[RefuseUnderSupportSession]` on `CreateToken`
  rather than adding a new enum value. `SupportRestrictionCensusTests` flagged the action by name
  (its regex already lists `CreateToken`) the moment I added it — I read that as the census doing its
  job, not as scope creep, and fixed it rather than widening the allowlist. A sign-in token is exactly
  the "durable, self-owned way into the account" the existing restriction already refuses for API
  tokens, so reusing the value rather than adding a sibling seemed right, but it's a judgement call.
- **Hashing and token shape.** Hash via `TokenService.Sha256` (reused exactly, per the brief).
  Plaintext generation borrows `PasswordReset.Issue()`'s shape (256-bit, base64url) rather than
  `ApiToken`'s prefixed form, since this token needs to survive both a query string and a pasted-into-
  a-form flow and doesn't need a prefix for fast lookup (redemption is rare enough that a hash-equality
  lookup is fine).

## Files touched

Domain: `src/Harbora.Domain/Identity/SignInToken.cs` (new — entity, `SignInTokenEnding`,
`SignInTokenStatus`, `SignInTokenAccess`), `src/Harbora.Domain/Auditing/AuditLog.cs`
(`SignInTokenId`). Data: `src/Harbora.Data/HarboraDbContext.cs`, migration
`20260925205235_SignInTokens`. Application: `src/Harbora.Application/Abstractions/SecurityAbstractions.cs`
(`ISignInTokenSession`, `NoSignInTokenSession`). Infrastructure:
`src/Harbora.Infrastructure/Identity/SignInTokenService.cs` (new),
`src/Harbora.Infrastructure/DependencyInjection.cs`,
`src/Harbora.Infrastructure/Auditing/AuditLogger.cs` (token attribution + `token.` prefix). Web:
`AccountController.cs` (+dependency), `AccountController.SignInTokens.cs` (new),
`HttpCurrentUser.cs` (+`HttpSignInTokenSession`, +claim), `SessionPrincipalFactory.cs`,
`SignInTokenSessionView.cs` (new), `WorkspaceMembershipValidationMiddleware.cs`, `Program.cs`,
`ViewModels/SignInTokenViewModels.cs` (new), `Views/Account/Tokens.cshtml` (new),
`Views/Account/TokenSignIn.cshtml` (new), `Views/Shared/_SignInTokenSessionBanner.cshtml` (new),
`Views/Shared/_SupportSessionBanner.cshtml` (+1 line). Tests: as listed above.
