# U-D — a turn the person can leave and come back to (server) — report

Branch `r6/agent-core`, worktree `../Harbora-r6-d`, on top of master `d20cd0c` (the U contracts). The owner
paused the round during this work and then asked to finish it; nothing was lost in the pause.

**Status: DONE_WITH_CONCERNS.** Section D of `u-brief.md` is built and tested. The concerns are listed in
section 6: the Postgres lane is unverified locally, and there are two behaviour notes for agent E.

Commits (owner identity, no trailer):

```
108bd36 Assistant turns outlive the request: streamed text, a turn mark for a reload mid-answer, and a conversation created before its first message
d65317f Every run of streamed text ends in a message, including text before a tool call and a call that never finished
2d560a1 Claim a conversation's turn on no token, so a reload at that moment cannot leave a mark nothing clears
```

The coordinator's detail from agent E is satisfied:
- The user message is saved on the turn's own token: `AppendAsync(…, turn)` runs after
  `using var deadline = new CancellationTokenSource(TurnDeadline)`.
- For a `new` send, the conversation row is created by that same save.
- The only step still on the request's token is reading the existing conversation. The claim itself now runs
  on no token (`2d560a1`). A claim that committed and was then reported cancelled would have left a mark
  that no `finally` clears.

plus the commit adding this report.

## 1. What changed

**The turn no longer dies with the request** (`AgentConversationService.SendAsync`).
- The request's token (`ct`) now guards only the checks before a turn is taken on: the message itself, the
  entitlement, loading the conversation, and claiming its turn.
- From then on everything runs on a token of its own: the gates, superseding, saving, the model calls, the
  tools and the auto-run plan. That token has a hard deadline, `DefaultTurnDeadline` = 3 minutes.
- At the deadline the turn stops, its error event says so in both languages ("This answer took longer than
  3 minutes and was stopped. What was done so far is saved."), and `done` still follows.
- The end of the turn runs on `CancellationToken.None`: the remaining-today figure, `done`, and clearing the
  mark.
- Every event goes through a `Listener` wrapper. The first write that throws marks the listener gone, and
  nothing more is written.
- In `AssistantController` the SSE writer writes on `HttpContext.RequestAborted`, catches
  `OperationCanceledException`, `IOException` and `ObjectDisposedException`, and stops writing.
- The turn keeps running inside the same request, so the scoped database context stays alive until it ends.
- The plan engine already ran on its own token after a claim; that is unchanged.

**The turn is marked.**
- `AgentConversation.TurnStartedAt` (nullable). Migration `20261011135957_AgentTurnInProgress` adds that one
  column and nothing else (snapshot diff +3 lines). It was generated with `dotnet ef migrations add` right
  after a fresh `dotnet build src/Harbora.Data`.
- How the mark is set:
  - For an existing conversation it is claimed with one conditional `ExecuteUpdateAsync`
    (`WHERE TurnStartedAt IS NULL OR TurnStartedAt <= now − (deadline + 30 s grace)`), then the tracked copy
    is reloaded. EF InMemory uses a plain read-then-write fallback, as the plan engine does.
  - A new conversation is created with the mark already set.
- The mark is cleared in `finally` by `EndTurnAsync`, and only if it still holds this turn's own stamp, so a
  turn that overran cannot clear a newer turn's mark. It never throws.
- The stamp is cut to whole microseconds, because Postgres keeps no finer precision and the clear compares
  the stamp for equality.
- `GetAsync` returns `InProgress` = the mark is set and younger than deadline + grace, so a process that
  died mid-turn does not leave a conversation "typing" forever.
- A second message while the mark is live gets the new `AgentSendStatus.Busy`. Nothing is stored or sent
  for it, and the running turn's mark is not touched. The controller answers **409**:
  `{ error: "turn_in_progress", messageEn: "Still answering your previous message — wait for it to finish, then send this one.", messageFa: "هنوز در حال پاسخ به پیام قبلی شماست — صبر کنید تمام شود، بعد این یکی را بفرستید." }`.

**Saving as it goes — checked.** Each message is written to the database as soon as it is produced:
- the user message (before `started`, as before);
- each assistant message, through `AppendAsync`;
- each tool result, as its call finishes;
- the plan, before its `plan` event.

A test reads the conversation from a second context in the middle of a turn and finds the user message and
the finished tool result already there.

**A conversation can be created before its first message.**
- `POST /assistant/conversations` creates the conversation. It has `[ValidateAntiForgeryToken]` and the
  class's `[RequireFeature]`, and the service checks the entitlement again.
- It answers **200** with an `AgentConversationSummaryView` of a new conversation: empty title, no messages,
  `inProgress` false. Without the entitlement the answer is 403 with `{ error, messageEn, messageFa }`.
- The first message sent to it gives it its title.
- `GET /assistant/conversations` leaves out conversations that still have no title — a place the pane
  reserved and never used.
- Sending to `new` still works.

**Text is streamed.**
- The loop calls the streaming overload `IAgentChatClient.CompleteAsync(request, onTextDelta, ct)` and
  emits each non-empty piece as a `delta` event, `{ text }` (`AgentDeltaData`).
- The coordinator's two details are both in place:
  - **Every run of deltas is followed by a `message` that closes it.** This holds:
    - after a call that ends in text;
    - after a call that ends in tool calls with text written before them — the message comes before the
      tool runs;
    - after an answer of only whitespace (the message's text is null);
    - after a call that fails or reaches the deadline part-way. Its partial text is saved as the model's
      message (`FinalizePartialAsync`), so a reload shows what was shown, and is sent as the message before
      the `error`.
  - **The delta callback never throws because the browser left.** It goes through the `Listener`, which
    swallows a failed write; a test proves the client never sees an exception.
- A client that cannot stream gets the interface's default overload: one call, no deltas, same events
  otherwise.
- Deltas are model text, not provider errors, so they reach every member. The rule that shows provider
  error text verbatim only to platform managers is unchanged.

## 2. Exact public signatures (new or changed)

```csharp
// Harbora.Domain.Agent.AgentConversation
public DateTimeOffset? TurnStartedAt { get; set; }

// Harbora.Infrastructure.Agent (AgentCoreModels.cs)
public enum AgentSendStatus { Streamed, Invalid, NotEntitled, NotFound, Busy }
public sealed record AgentDeltaData(string Text);

// Harbora.Infrastructure.Agent.AgentConversationService
public static readonly TimeSpan DefaultTurnDeadline;   // 3 minutes
public static readonly TimeSpan TurnMarkGrace;         // 30 seconds
internal TimeSpan TurnDeadline { get; init; }          // tests shorten it
public Task<AgentConversationSummaryView?> CreateAsync(AgentActor actor, CancellationToken ct);   // null = not entitled
// SendAsync / GetAsync / ListAsync keep their signatures; GetAsync now fills AgentConversationView.InProgress.

// Harbora.Web.Controllers.AssistantController
[HttpPost("conversations")] NewConversation   // 200 summary · 403 · 400 without the antiforgery header
// SendMessage: + 409 turn_in_progress
```

## 3. Tests

| File | Tests |
|---|---|
| `tests/Harbora.Tests/Agent/AgentTurnTests.cs` (new, 15) | deltas in order, then the whole message; text before a tool call closed by a message before the tool runs; a whitespace-only answer still closed; a call that fails part-way closes and keeps its partial text; a deadline mid-sentence closes the sentence and releases the mark; the delta callback never throws for a departed browser (probe client); a client that cannot stream still answers without deltas; a listener that leaves mid-turn — the answer is still produced and saved, only `started` was written, `InProgress` clears; mid-turn from a second context: the saved-so-far messages, `InProgress` true, a second message `Busy` and not stored; a stale mark is neither in progress nor in the way; a fresh mark refuses the message with nothing stored, sent or changed; a turn past its deadline stops, keeps the finished read and releases its mark; a conversation created ahead is empty, unlisted and titled by its first message; creating one without the entitlement is refused; a new conversation carries its mark while being answered |
| `tests/Harbora.Tests/Http/AssistantHttpTests.cs` (+3) | `POST /assistant/conversations`: 400 without the antiforgery header, 200 summary with an empty title; the conversation view shows no messages and `inProgress` false; a send to it streams `started, delta, delta, message, done`. A second message while a turn is live → 409 `turn_in_progress` with `messageEn`/`messageFa`, and the view says `inProgress` true. **Leaving mid-answer:** the client reads `started`, disposes the response, and the gated model answer is then still saved, the mark cleared and `inProgress` false |
| `tests/Harbora.Postgres.Tests/AgentTurnClaimTests.cs` (new, 1) | two messages to one conversation at once start exactly one turn (the other `Busy`, refused without waiting); the winner clears its own mark, which proves the microsecond stamp round-trips the equality check; only one user message is stored. **Unverified locally — it reports Skipped here (no Docker)** |
| `tests/Harbora.Tests/Agent/AgentTestKit.cs` | the scripted client now implements the streaming overload (pieces per answer, a pause before or after), counts streamed calls, and gains `PlainChatClient` (no streaming); the harness gains `ServiceOver(db, chat, turnDeadline)` and `NewContext()` (a second request on the same store) |

Mutation check: with the turn running on the request's token again (`var turn = ct`), both the unit test
"a listener that leaves mid-turn…" and the HTTP test "Leaving in the middle of an answer does not lose it"
failed. The HTTP failure also confirms that disposing the response in `TestServer` really aborts the
request. Both files were restored and rebuilt before the runs below.

## 4. Commands and output

All with `export MSBUILDDISABLENODEREUSE=1`, `-p:UseSharedCompilation=false`, `LOG=/tmp/test-r6-agent-core.log`.

- `dotnet build src/Harbora.Data` then
  `dotnet ef migrations add AgentTurnInProgress --project src/Harbora.Data --startup-project src/Harbora.Data`
  → `EF_EXIT=0`; `Up()` adds the one nullable column.
- Focused: `dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~Harbora.Tests.Agent.|FullyQualifiedName~AssistantHttpTests|FullyQualifiedName~MigrationConsistencyTests"`
  → `Passed!  - Failed:     0, Passed:   145, Skipped:     0, Total:   145 - Harbora.Tests.dll`.
- `dotnet test tests/Harbora.Postgres.Tests --no-build --filter "FullyQualifiedName~AgentTurnClaimTests"`
  → `Skipped! - Failed: 0, Passed: 0, Skipped: 1, Total: 1`.
- First full run, on `d65317f`: build exit 0, `28 Warning(s)`, `0 Error(s)`. One failure,
  `RateLimitHttpTests.The_login_form_shares_the_auth_window_with_the_CLI_token_endpoint` (6845 passed). That
  test spends a one-minute fixed rate-limit window, and the run took 4 m 19 s on a machine shared with other
  agents. Re-run on its own, `RateLimitHttpTests` passed 14/14. My work does not touch authentication or rate
  limiting.
- **Final, on `2d560a1`:** `dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false` →
  `BUILD_EXIT=0`, `28 Warning(s)`, `0 Error(s)` (the baseline's package warnings; no compiler warnings).
  Then `dotnet test Harbora.slnx --nologo -v q --no-build` → `EXIT=0`:
  ```
  Skipped! - Failed:     0, Passed:     0, Skipped:   109, Total:   109 - Harbora.Postgres.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:  6846, Skipped:     0, Total:  6846 - Harbora.Tests.dll (net10.0)
  ```
  `grep -c "Harbora.Tests.dll"` → 2. This branch adds 18 Harbora.Tests (15 turn tests + 3 HTTP) and 1
  Postgres-lane test. Master's own count was not re-run separately.

## 5. For agent E (the pane)

- `POST /assistant/conversations` returns `{ id, title: "", updatedAt }`. Remember the `id` and send to it.
- A 409 on send has `error: "turn_in_progress"` plus `messageEn`/`messageFa`.
- Every run of `delta` ends with a `message`. Its `text` may be null — an answer of only whitespace — which
  should simply close the bubble.
- When a call breaks part-way, a `message` carrying the partial text comes **before** the `error`.
- `GET /assistant/conversations` leaves out conversations that still have no title. A conversation the pane
  created and has not used yet will not be in the list, but `GET conversations/{id}` works for it.

## 6. Concerns

1. **Postgres lane unverified locally:** the new migration and `AgentTurnClaimTests` (the conditional claim,
   and the microsecond equality in the clear) have never run against Postgres here.
2. **What happens when a process dies mid-turn:** the conversation reads as in progress for up to
   3½ minutes (deadline + grace), and any message sent in that window is refused with 409. Nothing cleans
   it up sooner; it lapses on its own.
3. **The turn holds its request open:** it keeps running inside the request that started it. A load balancer
   that kills long requests on its own deadline would end the turn early, though it would still be marked
   and lapse. Traefik's defaults do not do that; worth knowing if a proxy timeout is ever added.
4. **Partial text is now saved:** text from a call that broke part-way is stored as an assistant message.
   It is what the person saw, but it is truncated, and the next turn's history contains it.

## 7. Review minors and the secrets vault (`a0cf82f`)

Rebased onto master `9c8617c` (G's database tools and the secrets contract) first — no conflicts.

### Review minors
1. **A fully streamed answer is saved on `CancellationToken.None`** when any text was streamed, so a deadline
   at that moment cannot lose text the person already watched. If the save itself throws, a `message` with
   the text still closes the bubble before the error.
2. **Comment at the auto-run call:** a claimed plan runs to its end on no token and is not bound by the turn's
   deadline or by a shutdown.
3. **The try that clears the mark starts right after a successful claim.** The relational reload of the
   tracked copy moved out of `TryClaimTurnAsync` into `RefreshAfterClaimAsync`, which runs inside that try, so
   a reload that throws after the `UPDATE` committed still ends in the `finally` that clears the mark.
4. **Shutdown:** the turn token is linked to `IHostApplicationLifetime.ApplicationStopping` (an optional
   constructor parameter, injected by the host). A self-deploy mid-turn now ends the turn with a bilingual
   "The server is restarting, so this answer was stopped. What was done so far is saved — ask again in a
   moment.", saves what was written, and clears the mark, instead of leaving 409s for 3½ minutes.
5. **Listener logging:** a failed write is logged at Information only for `OperationCanceledException`,
   `IOException` or `ObjectDisposedException` (a departure); anything else is a Warning, with the exception.

### Secrets as black boxes — D (server)
- `src/Harbora.Infrastructure/Agent/AgentSecretVault.cs`:
  - `InMemoryAgentSecretVault(ISecretProtector, ISystemClock) : IAgentSecretVault, IAgentSecretValues`, a
    singleton. Values are encrypted with the panel's protector while held, keyed by a random 16-byte id
    (base64url, 22 characters), scoped to workspace + user, and expire after `AgentSecretHandles.Lifetime`
    (30 minutes).
  - Expired entries are swept on every `Put` and on `Resolve` of an expired handle.
  - At most `MaxPerPerson` = 50 per person. Past that, that person's **oldest** handle is forgotten to make
    room rather than refusing the lock button; a plan still pointing at it is refused at execution.
  - Someone else's handle resolves exactly like an unknown one (null). A restart forgets everything, by
    design.
- New `IAgentSecretValues.LiveValues(workspaceId, userId)` — my own interface, the contract is untouched:
  - The turn and the plan engine scrub every person's held values, by value, from what they send the model
    (tool answers, refusals, outcome messages) and from what they store or show (tool summaries, tool
    events, plan results and errors).
  - So a tool that echoes a value it resolved cannot pass it on.
  - Both services take it as an optional constructor parameter, injected by DI.
- `AgentSecretService.HoldAsync(actor, request, ct)`:
  - Checks the entitlement, the name (trimmed, 1–100) and the value (1–8,000, taken exactly as typed).
  - Puts it in the vault and audits `assistant.secret_held` with `{ name, expiresAt }` only.
  - The value is never logged, audited or returned.
- `POST /assistant/secrets` (`[Authorize]`, `[RequireFeature]`, `[ValidateAntiForgeryToken]`) answers:
  - 200 `AgentSecretView { handle, name, expiresAt }`;
  - 400 `{ error: "invalid", messageEn, messageFa }`;
  - 403 without the entitlement.
- DI: the vault is registered once as a singleton and exposed as both `IAgentSecretVault` and
  `IAgentSecretValues`; `AgentSecretService` is scoped. All of it sits in my existing DI block. F's tools
  resolve handles through `IAgentSecretVault`.
- System prompt: text of the form `{{secret:…}}` is a secret the user entered with the lock button. Pass it on
  exactly as written as the tool argument where it belongs; never change, shorten, split or describe it.
  Never ask for a secret in the chat — point the user at the lock button beside the message box.

### Tests (21 new, all passing)
| File | New tests |
|---|---|
| `tests/Harbora.Tests/Agent/AgentSecretTests.cs` (new, 13) | a held secret comes back as a handle of the right shape, held encrypted (the protector saw it once), resolvable only by its person in its workspace, whitespace tolerated, not-exactly-a-handle refused; every handle different; expiry at exactly 30 minutes, then forgotten; the cap of 50 forgets the oldest and never touches another person's; name/value bounds (5 refusal cases + the 100 / 8,000 limits); audited by name only, and refused without the entitlement; **the model sees the handle and never the value even when a read tool echoes it** (requests, tool events, stored messages); **a plan step that echoes the value in its error leaves it in no result, message or plan** (the plan stores the handle) |
| `tests/Harbora.Tests/Agent/AgentTurnTests.cs` (+7) | a fully streamed answer is saved even when the deadline passes as it finishes; a streamed answer whose save fails still closes its bubble before the error; a server shutting down mid-turn ends it cleanly and releases the mark; the listener's log level by kind of failure (3 cases) |
| `tests/Harbora.Tests/Http/AssistantHttpTests.cs` (+1) | `POST /assistant/secrets`: 400 without the antiforgery header, 400 for an empty value, 200 `{ handle, name, expiresAt }` with no value in the body; the audit row names it without the value; a message carrying the handle reaches the scripted model with the handle and not the value, and no stored message contains the value |

Mutation check: with the turn's and the plan engine's held values forced to empty, both value-redaction tests
in `AgentSecretTests` failed; restored and rebuilt before the run below. Not tested: minor 3's reload failure,
which is relational-only and cannot be forced in InMemory (the change is structural — the reload now sits
inside the try). The request-logging side of "value absent from every log" is covered only by inspection: the
service and the controller log nothing about a secret, and the app has no request-body logging.

### Commands and output
- `dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false` → `BUILD_EXIT=0`, `28 Warning(s)`,
  `0 Error(s)`.
- Focused (agent tests, assistant HTTP tests, `MigrationConsistencyTests`) →
  `Passed!  - Failed:     0, Passed:   286, Skipped:     0, Total:   286 - Harbora.Tests.dll` (the count now
  includes F's and G's agent tests from master).
- Full: `dotnet test Harbora.slnx --nologo -v q --no-build` → `EXIT=0`:
  ```
  Skipped! - Failed:     0, Passed:     0, Skipped:   109, Total:   109 - Harbora.Postgres.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:  6989, Skipped:     0, Total:  6989 - Harbora.Tests.dll (net10.0)
  ```
  `grep -c "Harbora.Tests.dll"` → 2.

### Further concerns
5. **Handles do not survive a restart, on purpose:** after a deploy, a pending plan whose step carries a
   handle is refused at execution ("enter the secret again"). F's tools must say so.
6. **Scrubbing is by value only:** a held value shorter than 4 characters is not replaced, the same floor
   `AssistantRedaction` applies to every known secret. The lock button accepts it, so such a secret can be
   held but is not redacted.
7. **Two separate panel processes would not share the vault.** One panel container runs today, so that is
   fine; it would matter only if the panel were scaled out.
