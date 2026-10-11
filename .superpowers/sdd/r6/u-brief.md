# U — The chat must behave like a chat (owner feedback after the first deploy)

The owner used the live assistant and reported, in their words: when the page is refreshed or changed
while an answer is coming, the message that was sent disappears — "it should be available like a chat";
the answer appears all at once — it should be written out as other agents do; and while the assistant is
thinking or working there should be a three-dot indicator, as if it is writing or thinking.

Contract changes are on master (commit after `2f8777f`): `AgentStreamEvents.Delta` (`{ text }`),
`AgentConversationView.InProgress`, and a streaming overload of `IAgentChatClient.CompleteAsync` with an
`onTextDelta` callback (its default implementation does not stream). Use them; do not reshape them.

## C — stream the model's text (client)

Implement the streaming overload in `OpenAiCompatibleAgentChatClient`: `stream: true` (plus OpenRouter's
usage-in-stream option, `stream_options: { include_usage: true }`), read the SSE body incrementally, call
`onTextDelta` for each `choices[0].delta.content` piece, accumulate `delta.tool_calls` by `index` (id, name,
argument fragments concatenated), take `finish_reason`, the answering `model`, and usage from the final
chunk. Ignore SSE comment lines (OpenRouter sends `: OPENROUTER PROCESSING` keep-alives). An `error`
object inside the stream, a stream that ends without `[DONE]`/finish, or a non-2xx before the stream
starts are failures as values, bilingual, like today. If a provider refuses streaming outright (a 4xx that
says so), fall back once to the non-streaming call. The returned `AgentChatResponse` must equal what the
non-streaming call would have produced for the same answer. Timeouts: keep the overall 90 s; a stream
that goes silent for 45 s is a timeout. Tests with a fake SSE handler: deltas in order across chunk
boundaries, tool calls assembled from fragments, usage from the last chunk, comments ignored, mid-stream
error, truncated stream, fallback to non-streaming.

## D — a turn the person can leave and come back to (server)

1. **The turn must not die with the request.** Today a refresh aborts the request and `RunTurnAsync` is
   cancelled with it, so the answer is never written. Run the model calls and tools on a token that is
   not the request's (keep a hard turn deadline, e.g. 3 minutes), and make the SSE `emit` swallow a
   disconnected client (stop writing, keep working). The plan engine already runs on its own token after
   a claim — keep that.
2. **Mark the turn.** A nullable `TurnStartedAt` on `AgentConversation` (migration — you are the only one
   adding one in this round), set when a turn starts and cleared when it ends (in `finally`). `GetAsync`
   returns `InProgress = TurnStartedAt is not null && it is younger than the deadline` (a crashed process
   must not leave a conversation "typing" forever). A second message to a conversation whose turn is in
   progress is refused with 409 `{ messageEn, messageFa }` ("still answering the previous message").
3. **Save as you go.** The user message is already saved before `started`; keep it so. Every assistant
   message and tool result is saved when it is produced (already mostly true — verify), so a reload
   mid-turn shows everything up to that point.
4. **Create a conversation before the first message.** `POST /assistant/conversations` → 200
   `AgentConversationSummaryView` of a new, empty conversation (same entitlement/antiforgery rules), so
   the pane can remember the id before it sends anything. Sending to `new` keeps working.
5. **Stream text.** Call the streaming overload and emit `delta` events as text arrives (only for the
   text of the final answer of a model call — tool-call fragments are never shown). The `message` event
   still follows with the whole text. Deltas pass through the same provider-error rule (nothing verbatim
   to non-managers) — they are model text, not provider errors, so they are fine to show.
6. Tests: a client that disconnects mid-turn — the answer is still saved and `InProgress` clears; a
   conversation mid-turn reports `InProgress`; a stale `TurnStartedAt` does not; a second send while in
   progress → 409; `POST conversations` creates an empty one; deltas are emitted in order before
   `message`, and a non-streaming fake still works.

## E — typing indicator, streamed bubble, and coming back mid-answer (pane)

1. **Three-dot indicator** (animated, accessible: `aria-live` text «در حال فکر کردن…» / "Thinking…"
   for screen readers, reduced-motion friendly) shown from send until the first `delta`/`tool`/`message`,
   and again after a tool finishes until the next output, and whenever a reloaded conversation is
   `inProgress`.
2. **Streamed bubble.** `delta` events append to a live assistant bubble (text only, same formatting and
   escaping rules as messages; never HTML); the following `message` event replaces it with the final text.
   No fake per-character animation that delays real text — render what arrived.
3. **Coming back.** Before the first send of a new conversation, call `POST /assistant/conversations` and
   remember the id at once, so a reload always knows the conversation. On load, if the conversation is
   `inProgress`, show the messages so far plus the indicator, and re-read it every 2–3 s until it is not
   (then render the final state; stop polling on close/unmount). The person's own message must be
   visible immediately on reload. A 409 "still answering" on send is shown as a gentle notice, not an
   error, and the composer waits.
4. Tests (node): delta accumulation and replacement, indicator state transitions (send → dots → delta →
   no dots; tool done → dots), resume-polling state machine (starts on inProgress, stops when done, stops
   on unmount), the create-then-send order. HTTP test only if markup changes.

## Everyone

CONSTRAINTS apply (owner identity, no trailer, branch-named logs, full suite once at the end, honest
report). Report to `.superpowers/sdd/r6/u-<letter>-report.md` (`git add -f`).

# S — Secrets as black boxes (owner decision 2026-10-12, before the deploy)

Owner: «سکرت رو بگیره ولی برای مدل به عنوان بلک باکس بفرسته و بگیره اینور اعمال کنه». Contract on master:
`src/Harbora.Infrastructure/Agent/AgentSecretContracts.cs` — `IAgentSecretVault` (Put/Resolve),
`AgentSecretRequest`, `AgentSecretView`, `AgentSecretHandles` (format `{{secret:<22 url-safe chars>}}`,
30-minute lifetime, `IsHandle`, `Masked`). The value never reaches the provider, the history or a plan.

## D — the vault and the endpoint (server)
- `InMemoryAgentSecretVault : IAgentSecretVault`, singleton: values encrypted with `ISecretProtector`
  while held, keyed by a random 16-byte id (URL-safe base64, 22 chars), scoped to workspace + user,
  expiring after `Lifetime` (sweep expired entries on access; cap entries per user, e.g. 50). A restart
  forgets everything — by design.
- `POST /assistant/secrets` (`AgentSecretRequest`) → `AgentSecretView`; same entitlement/antiforgery/
  [Authorize] as the other endpoints; name 1–100 chars, value 1–8,000 chars; never logs or audits the
  value (audit `assistant.secret_held` with the name only).
- System prompt: text of the form `{{secret:…}}` is a secret the person entered privately; pass it
  through unchanged as the tool argument where it belongs; never ask the person to type a secret into the
  chat — tell them to use the lock button in the composer.
- Redaction: also treat any resolved value as a known secret when redacting what goes to the model.
- Tests: put/resolve, other user/workspace cannot resolve, expiry, cap, endpoint shape, value absent from
  every log/audit/model request.

## F — tools resolve handles (set_env_vars, deploy_template)
- An argument value that `AgentSecretHandles.IsHandle` is resolved with the vault at **execute** (and
  checked to exist at validate — never resolved into card text); a handle forces the variable secret.
- A plain (non-handle) value for a variable marked `secret: true`, or for a template variable the
  template marks secret, is refused with guidance in both languages: use the lock button so the value
  never goes to the AI.
- Card text and `ResultJson` show `AgentSecretHandles.Masked` for handle values. An expired/unknown
  handle at execute → `Ok = false`, "the secret expired — enter it again with the lock button".
- Tests: handle applied encrypted; card/result/audit never contain the value; plain secret refused;
  expired handle refused at execute; non-secret plain values still work.

## E — the lock button (pane)
- A lock button beside the composer opens a small inline form (name + masked value, show/hide toggle);
  submit → `POST /assistant/secrets` → insert a chip/placeholder showing the name, whose underlying text
  is the handle, into the draft at the cursor. The value is never put in the draft, the outbox or
  localStorage, and the form clears on submit/close.
- Before sending, if the draft contains something that looks like a secret (`password=`, `token:`,
  `api_key`, a long `sk-…`/`ghp_…` style token), show a gentle inline warning offering the lock button
  (the person may still send).
- Messages that contain handles render the handle as a lock chip with the name if known, else
  «secret».
- Tests (node): the value never reaches draft/outbox/storage; chip insertion; the warning heuristic;
  rendering of handles in history.
