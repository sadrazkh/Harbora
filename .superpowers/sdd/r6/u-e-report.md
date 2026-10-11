# U-E — Typing indicator, streamed bubble, and coming back mid-answer (pane): report

Status: **DONE_WITH_CONCERNS**. Section E of `u-brief.md` is built and tested. The pane has not run
against D's new server: `POST /assistant/conversations`, `delta`, `InProgress` and the 409 are all on
D's branch, not on master (`d20cd0c`). Concerns are at the end.

Branch `r6/chat-pane` in `../Harbora-r6-e`, reset to master `d20cd0c`. Commits are in the owner's name
alone, with no trailer:

- `e10dec6` Assistant pane: a typing indicator, the answer written as it arrives, and a conversation that survives a reload
- `5f3ec8d` Assistant pane: a turn that ends with text still live keeps it, not marked cut off
- (this report)

## Why a reload lost the sent message — what the code shows

I read the pane on master and the server's send path (`AgentConversationService.SendAsync`,
`AssistantController.SendMessage`). The cause is on both sides.

**The pane — a new conversation (the common case: open the pane, ask, reload).**
- The pane posted the first message to `conversations/new/messages`.
- It learnt the conversation's id only from the stream's `started` event:
  `prefs.setConversationId` ran only inside `onEvent` for `started` and `done`.
- So a reload or page change before the pane had read `started` left localStorage with no
  conversation id. On the next page `restore()` had nothing to open, and the drawer came back empty.
- The message itself was on the server, but nothing on screen pointed to it. It was reachable only
  through the history list, which is what "it disappeared" looks like.
- The optimistic bubble (`addUserMessage`) lived only in memory and went with the page.

**The pane — an existing conversation.**
- The id was already remembered, so a reload reopened it, and the person's message showed *if the
  server had saved it*.
- But the conversation was read once, as it stood at the reload, and never again. There was no notion
  of "still being answered": no indicator, no re-read. A turn that did finish would only appear after
  another manual reopen.

**The server (for D, and covered by D's brief).**
- `SendAsync` does save the user message (`AppendAsync` → `SaveChangesAsync`) before it emits `started`.
- But every step, including that save, runs on the request's `CancellationToken`. A reload aborts the
  request, which:
  - cancels `RunTurnAsync`, so the answer is never written (D brief item 1);
  - also loses the message if the abort lands before `AppendAsync` has finished. That window is
    milliseconds: the feature gate and `GateAsync` are database and settings reads only, so it is rare
    but real.
- **For D:** the user-message save should also run on the non-request token, not only the model calls
  and tools.
- I found nothing in the pipeline that buffers the event stream: no response compression, no
  body-capturing middleware, `X-Accel-Buffering: no` is set, and each event is flushed. So `started`
  should reach the browser within moments. The window in which the pane did not know the conversation
  was short, but it was the first message of every new conversation.

**How this change covers each of those paths**
1. **The conversation is created first.** Before the first message of a new conversation, the pane
   calls `POST /assistant/conversations` and remembers the id at once. Only then does it send. A
   reload at any moment after that knows the conversation. A server without the endpoint (405/404, or
   any failure) falls back to sending to `new`, which behaves as before.
2. **The words are kept in an outbox.** `{ conversationId, text, at }` goes to localStorage before the
   request is sent and is cleared on `started`, when the server has saved it.
   - On the next page, if the outbox is still there and the conversation does not contain that
     message, the words go back into the composer with a gentle notice: «شاید پیام آخر شما … به
     دستیار نرسیده باشد. دوباره در کادر پایین است». The pane never resends on its own, to avoid
     duplicates.
   - An outbox older than an hour is dropped, and another conversation's outbox is left for that
     conversation.
   - This covers the server-side pre-save window as well.
3. **A conversation opened mid-answer is followed to its end.**
   - The person's own message shows at once, from the saved conversation, with the three-dot indicator.
   - The pane re-reads the conversation every 2.5 s while `inProgress` is true, draws each read, and
     stops when the server says the turn is over, drawing the final state.
   - Polling also stops when the drawer closes and resumes when it opens; it stops when another or a
     new conversation is opened, and on unmount.
   - A conversation that is gone (404/403) stops it, and so do 4 failed reads in a row or 150 reads.

## What changed

- **`assistantSession.js`** (new) holds sending, streaming and coming back as plain JS, with the API,
  storage and timers injected:
  - `createAssistantSession` provides `open`, `restore`, `startNew`, `send`, `pause`, `wake` and
    `dispose`;
  - `createResumePoller` is the re-read state machine.
  - `AssistantPanel.vue` now only wires it: `restore()` on mount, `wake()` on open, `pause()` on close,
    `dispose()` on unmount.
- **`assistantTimeline.js`**:
  - The timeline now carries `streaming`, `resuming`, `loading` and `notice`.
  - `delta` appends to a live assistant bubble, or starts one.
  - `message` replaces the live bubble's text with the whole answer: same item and key, so nothing is
    drawn twice and nothing jumps.
  - When a `tool`, `plan`, `executed` or `done` event arrives while a bubble is still live with no
    `message`, the bubble is **finalized with the text it has**. Later deltas start a fresh bubble.
    This follows your note about "On it." written before a tool call.
  - A bubble of whitespace only is dropped.
  - `error`, or a broken stream (`settleTurn`), keeps the text and marks it `cut`, shown as «بقیهٔ این
    پاسخ نرسید» / "The rest of this answer did not arrive". `done` does not mark it cut, since the
    turn ended normally.
  - `isThinking(state)` drives the dots: on from the send until the first text or tool line; off while
    text is arriving or a tool line is running (it has its own spinner); on again after a tool
    finishes until the next output; and on whenever `resuming` is set.
  - `withdrawUserMessage` and `markUnsent` are new.
- **`assistantStream.js`**: `delta` is added to the known events. Before this, an unknown event was
  dropped, so without it every delta would have been ignored.
- **`assistantApi.js`**:
  - `createConversation()` is new.
  - A 409 on send comes back as `{ completed: false, busy: true, notice }`, never as an error event. It
    uses the server's `messageEn`/`messageFa`, or the pane's own «دستیار هنوز در حال پاسخ به پیام
    قبلی شماست…» if the server sent none.
  - In the session, a 409 takes the message back out of the conversation, puts it in the composer,
    shows the notice and starts re-reading. The composer waits: typing is allowed, sending is not,
    until the answer in progress is over.
  - A message that never reached the server is kept visible, marked «فرستاده نشد» ("Not sent"), and
    its text goes back in the composer.
- **`AssistantPanel.vue`**:
  - The dots are a bubble of their own on the start side, with `role="status"` and a screen-reader
    text, «در حال فکر کردن…» / "Thinking…".
  - The live bubble renders through `AssistantText`, with the same formatter and escaping as messages
    and no HTML. There is no per-character animation: text is drawn as it arrives.
  - The notice line sits above the composer, in neutral ink-muted, not danger.
  - The composer, Run and Cancel wait while a turn is streaming or being re-read.
- **`app.css`**: `.assistant-dots` animation, three dots rising in turn. With reduced motion the dots
  stand still at three shades.
- **`assistantDrawer.js`**:
  - prefs gain an outbox (`harbora-assistant-outbox`);
  - closing the drawer dispatches `harbora:assistant-closed` to a mounted island.
- **`assistant-panel.preview.html`** has new scenarios: `resume`, `busy`, `nocreate` and `?keep`.
  Answers now arrive as deltas.

No Razor markup changed, so there is no new HTTP test; the existing `AssistantPaneHttpTests` still pass.

## Tests

Node, `node --test src/Harbora.Web/Scripts/tests/*.test.mjs`: **167 pass, 0 fail**.
- By file: api 33, drawer 20, format 20, plan 15, **session 16 (new file)**, stream 15, timeline 36,
  plus the 12 that were there before.
- New tests:
  - the create-then-send order, with the id in storage and the outbox written before the request goes out;
  - no create step for an existing conversation;
  - falling back to `new`;
  - a reload before `started` bringing the words back;
  - a reload after the save showing the message, with nothing restored;
  - an outbox with no conversation, a stale one, and another conversation's;
  - re-reading until done (2.5 s interval, then the final state, then stopped);
  - no polling when not in progress;
  - pause, wake and dispose;
  - a late read after stopping changing nothing;
  - starting a new conversation stopping the polling;
  - a 409 as a notice, with the draft restored and the composer waiting;
  - an unsent message;
  - the dots across a whole turn (send → dots, tool running → none, tool done → dots, delta → none,
    message → none);
  - poller retry and stop on 404;
  - delta accumulation and replacement, HTML in a delta staying text, empty deltas ignored;
  - a fresh bubble after a tool line;
  - a cut bubble on error or a broken stream;
  - finalize without `message` on tool, plan, executed and done (kept, not cut);
  - `createConversation`'s request, deltas in order before `message`, and the 409 outcome with and
    without the server's words;
  - the drawer's close event.
- I broke the code under four of these and watched the tests fail, then restored it:
  - removing the create step fails 3 session tests;
  - removing `resumeIfAnswering` fails 3;
  - removing the `done` finalize fails the not-cut test.

I also checked the pane in the browser pane against the harness, with Vite on port 5183, stopped
afterwards:
- **New conversation:** dots from the send; the id was in storage about 250 ms after the send, before
  `started`; dots off while a tool ran and back on after it finished; the live bubble grew word by word
  and was replaced by the final message.
- **Reload about 0.5 s after sending, before `started`:** the words came back in the composer with the
  notice.
- **`?scenario=resume`:** the person's message showed at once with the dots, and the answer appeared
  after the third read, with the dots gone.
- **`?scenario=busy`:** the second message left the conversation and came back to the composer, with
  the notice and no error line.
- **`?scenario=nocreate`:** the create got a 405, the message went to `new`, and the id was remembered
  from `started`.

## Commands and output

The final run came after the last code commit (`5f3ec8d`), with `MSBUILDDISABLENODEREUSE=1`,
`-p:UseSharedCompilation=false` and log `/tmp/test-r6-chat-pane.log`. `npm run build` ran alone first:
`NPM_EXIT=0`, `AssistantPanel-*.js 53.04 kB │ gzip: 17.82 kB`, `✓ built in 5.09s`, and the manifest
names `"Scripts/main.ts"`.

```
BUILD_EXIT=0   28 Warning(s) (48x NU1900 + 8x NU1903 lines, the package-feed warnings only)   0 Error(s)
dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~AssistantPaneHttpTests"
Passed!  - Failed: 0, Passed:    10, Skipped:  0, Total:   10 - Harbora.Tests.dll
dotnet test Harbora.slnx --no-build
EXIT=0
Passed!  - Failed: 0, Passed:   500, Skipped: 17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:    15, Skipped:  0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed:  6828, Skipped:  0, Total: 6828 - Harbora.Tests.dll
dll=2 msb3030=0
```

This branch added no tests to Harbora.Tests (no Razor change), so 6828 is master's count at `d20cd0c`.

An identical full run after `e10dec6`, before the finalize fix, also passed: Harbora.Tests 6828/6828,
NodeAgent 500 + 17 skipped, NodeIngress 15.

## Concerns

1. **Not verified against D's server.** The create endpoint, the deltas, `inProgress` and the 409 are
   coded to the contract on master and the brief, and checked only against the harness. The pane
   degrades on today's server: no create endpoint means it sends to `new`; no `inProgress` means it
   does not poll.
2. **For D: save the user message on the turn's own token.** The save in `SendAsync` runs on the
   request token, so an abort before `AppendAsync` completes loses the message server-side. The
   outbox covers it for the person, but the server should not lose it.
3. **The outbox matches by text.** It decides "delivered" by comparing text with the conversation's
   user messages. If the same words were sent earlier in that conversation, a lost repeat is not
   offered back. That errs on the side of not duplicating.
4. **No server-side typing state between reads.** A conversation re-read every 2.5 s shows the saved
   messages so far. Text being written right now on the server appears only when D saves it as a
   message; there are no deltas in a re-read.
5. **Ctrl+J** is still unverified in desktop Chrome and Firefox, as in earlier rounds.

## Fixes after review

The review praised the indicator, the streamed bubble, the polling guard and double-click safety, and
found three Important gaps plus minors. All are fixed in `eb4666b` (originally `5591ace`, before the
rebase onto master `9c8617c`).

### Important

1. **Dots stuck on and the composer locked after a failed open.**
   - `open()` remembers whether the conversation on screen was being followed, and whether a turn in
     it was just abandoned.
   - When the read fails, the conversation on screen is followed again through a new `follow()`.
     Otherwise `resuming` is cleared.
   - `resuming` is never left true without a poller. The one exception is while the drawer is closed,
     because of the `paused` flag below, and `wake()` restarts the poller on the next open.
2. **A reload during the create lost the words.** The outbox entry is now written before
   `createConversation()`, under `new`, and moved to the new id once it exists (`prefs.moveOutbox`). A
   reload mid-create finds the words under `new` and `restore()` gives them back.
3. **A failed read on reload was swallowed.**
   - `restore()` opens with `holdOnFailure`. A failure other than 404/403 sets
     `timeline.loadError = { conversationId, messageEn, messageFa }`.
   - The remembered id and the waiting outbox are kept.
   - `send()` is refused while `loadError` is set, so nothing can start a new conversation and orphan
     the old one.
   - The pane shows an error card with "Try again", which calls `session.retry()`, and "Start a new
     conversation".

### Minors

- **"Not sent" is checked before it is said.** When `started` never arrived and the conversation id is
  known, the pane re-reads the conversation first.
  - If the person's last message there is this one, the send counts as done: the conversation is drawn
    and followed if still answering, with nothing offered for resending.
  - If the read fails, the bubble says «شاید فرستاده نشده باشد» ("May not have been sent") with a
    softer notice.
  - Only a read that shows the message absent says «فرستاده نشد».
- **Notices are cleared** on every `open()`. A "still answering" notice (kind `busy`) also goes when the
  answer it was waiting for is in.
- **The outbox is keyed by conversation** (`new` for one not created yet), so one conversation's
  waiting words are never overwritten by another's. The single-entry format of the previous version is
  still read.
- **An abandoned create gives the words back** to the composer, and nothing is sent.
- **A drawer closed during the restore read** no longer starts polling. `pause()` sets `paused`, and
  `follow()` waits for `wake()`.
- **Streamed bubbles hold their direction** (`createSteadyDirection`).
  - A paragraph follows its first letter until it has 4 words, then is decided once by word count and
    held.
  - Pure first-letter direction would have laid out «shop-db ساخته شد…» left to right for good. In the
    browser that case corrected once, at the 4th word, then stayed RTL through the final message.
- **"Thinking…" for screen readers** now lives in a persistent `role="status"` region outside the log.
  The log is `aria-busy` during a turn, and changes inside a busy region are held back.
- **The over-claiming test was renamed** to "a plan arriving while a bubble is live finalizes it…",
  and the late-message case it named was added.
- **A `message` arriving after its `tool` line replaces the bubble instead of doubling it.**
  - The `message` handler first looks for an unconfirmed streamed bubble in this turn whose text is the
    same as the message, or the start of it, and replaces that one; otherwise it replaces the live
    bubble.
  - A late message for an earlier bubble does not touch the one being written now.
  - A cut-off bubble is completed by its late message and loses the cut note.

**Tests added:**
- session (13): every item above, including resuming with a poller after a failed open, words kept
  during the create, `loadError` with retry and starting new from it, the keyed outbox, the abandoned
  create, the "not sent" re-check (sent vs unsure), the busy notice clearing, notices cleared on open,
  and no polling while paused;
- timeline (4): the late message after the tool line, the late message not touching the live bubble,
  the cut bubble completed, an unmatched message drawn on its own;
- format (3): the held direction.

**Mutation checks:** I broke the code under each Important fix and its test failed, then I restored it.
- removing the follow-again → the failed-open test fails;
- moving the outbox write after the create → the mid-create reload test fails;
- disabling `holdOnFailure` → the reload-failure test fails;
- going back to live-bubble-only for `message` → 3 timeline tests fail.

**Browser harness** (new `?scenario=loadfail`): the error card showed, Send was disabled, the
remembered id was kept and no empty state was drawn; "Try again" loaded the conversation with its
plan.

## S — the lock button (pane)

Owner: «سکرت رو بگیره ولی برای مدل به عنوان بلک باکس بفرسته و بگیره اینور اعمال کنه». The branch was
rebased onto master `9c8617c`, which has the contract `AgentSecretContracts.cs`. Commit `7517273`.

- **`assistantSecrets.js`** (new):
  - `SECRET_HANDLE`, `isHandle` and `splitSecrets` follow the contract's pattern,
    `{{secret:` + 22 URL-safe characters + `}}`.
  - `looksLikeSecret` is the warning heuristic. It catches a secret-ish key (password, token,
    api_key, …, or «رمز»/«گذرواژه», with up to a few words before the `:`/`=`) followed by a value, and
    well-known token formats: `sk-…`, `ghp_…`, `github_pat_…`, `xox…-`, `AKIA…`, PEM private keys.
    Handles never count.
  - `createSecretBook` holds handle → name and placeholder, and never a value. It provides `add`,
    `insert` at the cursor, `expand` (placeholder → handle, for sending), `collapse` (handle →
    placeholder, for words given back to the composer) and `nameOf`.
  - `createSecretForm` is the form state. Name must be 1–100 characters and value 1–8,000, as on the
    server. It posts once and resets on success and on close. After a refused post the value stays
    only in the still-open form, so the person can retry.
- **`assistantApi.js`**: `holdSecret({ name, value })` → `POST /assistant/secrets`, with the
  `X-CSRF-TOKEN` header. It is the only request that ever carries a value.
- **`AssistantPanel.vue`**:
  - A lock button beside Send opens an inline form, as its own `<form>` so Enter adds the secret
    instead of sending the message. The form has a name field and a masked value field with a
    show/hide toggle, plus the line "the AI sees a placeholder, never the secret; kept for 30 minutes".
  - Adding inserts `[🔒 NAME]` into the draft at the cursor. At send the draft is expanded, so the
    message carries `{{secret:…}}`.
  - The warning shows live, checked against the expanded draft so a placeholder never sets it off. The
    first Send of such a draft is held and the warning says "press Send again to send it as it is";
    it also offers the lock button.
- **`AssistantSecretText.vue`** (new) draws handles in user bubbles and in assistant text as lock chips
  with the name, or «سکرت»/"secret" when this pane did not hold it, for example after a reload. Text
  only, no HTML.

**Tests:** `assistant-secrets.test.mjs`, 15 tests.
- handle pattern and splitting, including the global-regex `lastIndex` trap;
- the warning, both for what it catches (13 phrasings) and what it ignores (8, including handles);
- placeholders: insertion at the cursor, distinct names, expand/collapse, nameOf;
- the form: posts once, then forgets; close forgets; validation; a refused post keeps the form open;
- `holdSecret`'s request;
- end to end through the real session: the value is absent from the draft, the sent text, the outbox
  during the send, localStorage and the timeline, and only the handle travels;
- a placeholder in the draft does not set off the warning.

**Browser harness** (fake `POST /assistant/secrets`; its log line withholds the body):
- The lock form posted the value to `/assistant/secrets` only.
- The draft got `… روی [🔒 DB_PASSWORD]`.
- The message request carried `{{secret:58d89208ec2d4f2c9cfa27}}`.
- The user bubble showed a lock chip "DB_PASSWORD".
- localStorage never contained the value.
- «رمز دیتابیس: hunter22 …» raised the warning and the first Send was held. This check found that the
  first version of the Persian pattern needed «رمز» directly before the colon. It now allows a few
  words in between, and a test covers it.

## Commands and output — after both pieces

The final run was on `7517273`, after the rebase onto master `9c8617c`, with `MSBUILDDISABLENODEREUSE=1`,
`-p:UseSharedCompilation=false` and log `/tmp/test-r6-chat-pane.log`.

- **Node:** `node --test src/Harbora.Web/Scripts/tests/*.test.mjs` → `# tests 202  # pass 202  # fail 0`.
  By file: api 33, drawer 20, format 23, plan 15, secrets 15, session 29, stream 15, timeline 40, plus 12
  that were already there.
- **Bundle:** `npm run build`, run alone → `NPM_EXIT=0`,
  `AssistantPanel-*.js 65.57 kB │ gzip: 21.84 kB`, `✓ built in 5.63s`, and the manifest names
  `"Scripts/main.ts"`.

```
BUILD_EXIT=0   28 Warning(s) (48x NU1900 + 8x NU1903 lines, the package-feed warnings only)   0 Error(s)
dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~AssistantPaneHttpTests"
Passed!  - Failed: 0, Passed:    10, Skipped:  0, Total:   10 - Harbora.Tests.dll
dotnet test Harbora.slnx --no-build
EXIT=0
Passed!  - Failed: 0, Passed:   500, Skipped: 17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:    15, Skipped:  0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed:  6951, Skipped:  0, Total: 6951 - Harbora.Tests.dll
dll=2 msb3030=0
```

This branch adds no tests to Harbora.Tests (no Razor change). 6951 is master `9c8617c`'s count,
which has grown with the other agents' landed work since `d20cd0c` (6828).

## Concerns (both pieces)

1. **Not verified against D's server.** `POST /assistant/secrets` and the U endpoints are on D's
   branch, not on master `9c8617c`. Both pieces are coded to the contracts and checked in the harness
   only.
2. **Placeholders are plain text in the textarea, not real chips.** Editing a placeholder by hand
   stops it expanding: it is then sent as literal text, which never contains the value.
3. **After a reload, held secrets show as «سکرت».** The handle → name book lives in memory only.
4. **The warning heuristic is deliberately gentle and incomplete.** A secret with no recognisable key
   or format passes unnoticed.
5. **The handle is the only check on secret use.** Anything sent as a handle is resolved by the server's
   tools (F); the pane does not check that the tool is the right one for that secret.
