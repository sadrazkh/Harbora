# U-C report — stream the model's text (client)

Branch `r6/chat-client` (reset to master `d20cd0c`), worktree `Harbora-r6-c`. Status: **DONE** (concerns at the end).

Task: `u-brief.md` section "C — stream the model's text (client)". Scope kept to the client and its tests; the contract
(`AgentChatContracts.cs`, `AgentViews.cs`) is untouched.

## What changed

| File | Change |
|---|---|
| `src/Harbora.Infrastructure/Agent/OpenAiCompatibleAgentChatClient.cs` | now `partial`; the 2-arg `CompleteAsync` delegates to the new 3-arg public overload; the whole-reply round trip, the request message, the timeout/unreachable failures and the answer assembly were lifted into helpers (`SendWholeAsync`, `NewMessage`, `TimedOut`, `Unreachable`, `Reported`, `Answer`) so both paths share them |
| `src/Harbora.Infrastructure/Agent/OpenAiCompatibleAgentChatClient.Streaming.cs` (new) | `StreamAsync`, `TryStreamAsync`, `ReadStreamAsync`, `StreamedAnswer` (the chunk accumulator), `RefusesStreaming`, `internal TimeSpan StreamIdleTimeout` (45 s) |
| `tests/Harbora.Tests/AgentChatClientStreamingTests.cs` (new) | 46 tests |
| `tests/Harbora.Tests/Fakes/SseScript.cs` (new) | scripted server-sent-events body: pieces cut at chosen byte offsets, pauses, a hang, a dying connection; chunk builders (`Chunk`, `UsageChunk`, `ToolFragment`, `Comment`, `Done`) |
| `tests/Harbora.Tests/AgentChatClientTests.cs` | harness members made `internal` for reuse (`Rig`, `Build` + a `streamIdle` argument, `Ask`, `RestartApp`, `ShouldFailInBothLanguages`, `PassthroughProtector`); no test changed |

No DI change (the class is already registered; the new overload is picked up through the interface — a test proves the interface call reaches it and not the default).

## Behaviour

**Entry point.** `CompleteAsync(request, Func<string, Task>? onTextDelta, ct)`. `onTextDelta == null` is exactly the old whole-reply call (no `stream` field on the wire). With a callback: availability, URL and body are built as before, then a copy of the body gets `stream: true` and `stream_options: { include_usage: true }`. The copy matters: the fallback below re-sends the plain body.

**Reading.** `HttpCompletionOption.ResponseHeadersRead`, then a `StreamReader` line by line. Lines are grouped into events by a blank line (a last event with no trailing blank line is still read); `data:` payloads are joined; lines starting with `:` (OpenRouter's `: OPENROUTER PROCESSING`) and every other field (`event:`, `id:`, `retry:`) are ignored; `\r\n` and a missing space after `data:` are fine; a payload that is not JSON is skipped (if it was part of the answer, the missing finish says so). `data: [DONE]` ends the read.

**Per chunk.** `choices[0].delta.content` (string, or an array of text parts) -> appended and passed to the callback; `delta.tool_calls[]` accumulated by `index` (id and name kept once — a name repeated on every fragment is not doubled; argument fragments concatenated; an object-shaped `arguments` is kept as raw JSON); `finish_reason`, the latest `model`, and `usage` (the usage-only last chunk has `choices: []`). Tool calls with no `index` are told apart by `id`, or extend the last call; a gateway that numbers every parallel call 0 is told apart by `id`.

**Result.** `StreamedAnswer.Build()` ends in the same `Answer(...)` the whole-reply parser uses, with the same fallbacks (a missing call id becomes `call_<n>`, blank arguments become `{}`, null `Usage` when the provider sent none, and the same "neither text nor a tool call" failure). A test builds the same answer both ways and asserts `BeEquivalentTo` for a text answer and for a tool call.

**Failures (values, bilingual):**

| Situation | English text |
|---|---|
| non-2xx before the stream | same as today (`refused the request (<code>)…`, 429 -> rate-limit text) |
| `error` object/string at the top of a chunk, or inside the choice | `The AI provider reported an error: <message>` |
| `finish_reason: "error"` with no message | `The AI provider stopped the answer with an error (finish reason: error).` |
| stream ends with neither `[DONE]` nor a finish reason | `The AI provider's answer was cut off before it finished.` |
| connection dies mid-stream | `The AI provider's connection dropped before the answer finished: <reason>` |
| silent for 45 s | `The AI provider stopped sending the answer for 45 seconds.` |
| overall 90 s | `The AI provider did not answer within 90 seconds.` |

**Timeouts.** The overall `RequestTimeout` (90 s) bounds the whole stream. A separate idle timer (`StreamIdleTimeout`, 45 s) is re-armed before every read and by every line, **comments included** — a keep-alive is a sign of life; a stream that only ever sends comments is still cut by the 90 s. Once a finish reason has been seen, a silence, a timeout or a dropped connection is not a failure: the answer is whole, only the usage line can be missing, and it is returned. A finish reason plus usage ends the read at once (some providers hold the connection open after the last chunk).

**Fallback.** A non-2xx before the stream starts that is a 4xx (not 401, 403, 408, 429) and whose body mentions "stream" (`Streaming is not supported…`, `Unrecognized request argument supplied: stream_options`) -> the plain request is sent once; its result is returned, and its text is handed to the callback in one piece. If the plain request also fails, that failure is returned (two requests, no loop). A 200 that is `application/json` (the provider ignored `stream`) is read as the whole answer, with its text given to the callback once; no retry.

**Callback.** Awaited per piece, in order. If it throws, the exception propagates (a bug or a gone client is the caller's to handle, not a provider failure). The caller's cancellation propagates as `OperationCanceledException`, as before.

## Tests (`AgentChatClientStreamingTests`, 46)

Request shape (stream + usage, otherwise identical to the whole request, same URL/key/OpenRouter headers; no callback = no streaming; interface dispatch); deltas in order with the body cut every 5 bytes, inside a Persian letter, and uncut; usage from the last chunk; comments, other fields, CRLF, `data:` with no space, a last event with no blank line; finish without `[DONE]` and `[DONE]` without finish; response equals the whole response (text, tool call); tool calls from fragments by index (+ text before them), repeated names, all-index-0 parallel calls, no index, no id, no arguments, object arguments; mid-stream error (object, string, choice-level, `finish_reason: error`) — earlier deltas were shown and the result is a failure; truncated / empty / comment-only / junk-only streams; empty answer; connection reset mid-stream, and after the finish; idle timeout, keep-alives counted as life, overall budget with comments only, silence after the finish, finish+usage ends the read early; non-2xx / 429 / 401-mentioning-stream / unreachable before the stream (one request, no callback); fallback (once, with the plain body, text delivered once; `stream_options` rejection; failing twice; a 4xx that is not about streaming); JSON reply to a streaming request; caller cancellation; throwing callback; unavailable agent.

Plus one over a **real loopback socket** (`HttpClient` against a Kestrel endpoint inside the test; no provider is involved): the server writes the first event, flushes, and waits until the client's callback has seen it before sending the rest. A client that buffered the body would deadlock that wait. I checked it bites: switching the client to `ResponseContentRead` made this test fail.

Mutation checks (reverted afterwards with `git checkout`): removing the truncation check, the finished-answer rule in the catch blocks, the name de-duplication, the fallback rule (never falls back), and the finish+usage early exit failed 10 tests between them.

## Commands and output

```
export MSBUILDDISABLENODEREUSE=1
dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false   -> BUILD_EXIT=0; only NU1900/NU1903 NuGet warnings (no compiler warnings)
dotnet test  Harbora.slnx --nologo -v q --no-build                       -> EXIT=0
Passed!  - Failed:     0, Passed:  6874, Skipped:     0, Total:  6874 - Harbora.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll
Skipped! - Failed:     0, Passed:     0, Skipped:   108, Total:   108 - Harbora.Postgres.Tests.dll
grep -c "Harbora.Tests.dll" /tmp/test-r6-chat-client.log -> 2 (present)
```

`Harbora.Tests` 6874 = 6828 (master, per the ledger) + 46. Logs: `/tmp/test-r6-chat-client.log{,.build,.exit}`.

## Not verified / concerns

1. **Never run against a real provider** (as required). What a real OpenRouter / free model sends is unverified: whether `stream_options` is accepted by every route, how a particular model chunks tool calls, whether `: OPENROUTER PROCESSING` precedes the first byte of headers or the first event. The loopback test shows incremental delivery works through a real `HttpClient`; it cannot show a provider's behaviour.
2. **The 90 s ceiling now also bounds a streaming answer** (per the brief). A long answer from a slow free model can be cut at 90 s even while text is still arriving; the idle timer is not the limit, `RequestTimeout` is. If that bites in practice, raise `RequestTimeout` for the streamed path only.
3. **A provider that refuses streaming costs an extra request on every model call** (a fresh client per scope, nothing remembers the refusal). Correct but wasteful on a 1000-requests/day free tier; caching "this base URL + model does not stream" would be a small follow-up if it ever matters.
4. **For agent D:** text that precedes a tool call ("On it.") is streamed through the callback like any other text, because it cannot be known to be a non-final answer until the stream ends; the *returned* response is what says whether the call also carries tool calls. The brief's "deltas only for the final answer of a model call" therefore has to be decided by D from the response (or by buffering), not by this client. The callback is awaited inline, so a slow callback slows the read.
5. If the callback throws, `CompleteAsync` throws (it is not a provider failure). D's `emit` should swallow a gone client itself, as the brief already says.
6. `Harbora.Postgres.Tests` skipped (108, no Postgres here); nothing in this change touches the database.

---

## Fixes after review

Commit `d6da4bd`. Review found two Important issues and four minors; all are fixed, each with a named test.

**Important 1 — the callback's own exceptions were reported as provider failures.** The callback ran inside the `try` whose
handlers turn an `OperationCanceledException` into "stopped sending for 45 seconds" and an `IOException` into "the AI
provider's connection dropped" — exactly what a closed browser tab produces. Now every call to `onTextDelta` goes through
`Deliver(...)`, which wraps whatever it throws in a private `CallbackFault`; no handler filter can match that type, and the
outer `catch (CallbackFault)` rethrows the original exception with `ExceptionDispatchInfo` (same instance, same type). Covered at
all three call sites: the streamed read, the provider-ignored-`stream` JSON reply, and the fallback (which was already outside the
`try`).
Tests (each runs for OCE, `TaskCanceledException`, `IOException`, `HttpRequestException`, `TimeoutException`, `InvalidOperationException`):
`Whatever_the_callback_throws_reaches_the_caller_as_itself_and_never_as_a_provider_failure` (also asserts the stream is
abandoned after the first fault), `A_callback_that_fails_asynchronously_is_the_same`,
`A_callback_fault_is_also_the_callers_when_the_provider_answered_with_plain_json_or_the_fallback_ran`.

**Important 2 — the fallback heuristic matched "upstream" and quoted requests.** `RefusesStreaming` now parses the body and
looks for the word `(?<![a-z_])stream` (case-insensitive) only in the error text: `ErrorDetail(...)` (message plus
`metadata.raw`), else a bare top-level `message` or `detail` (vLLM- and FastAPI-style bodies), else — only when the body is not JSON
at all — the plain text itself. A JSON body with no error text (for example one that just echoes `"stream":true`) never falls back.
Test: `Only_an_error_that_talks_about_streaming_as_a_word_makes_a_4xx_a_refusal_to_stream` (12 cases): must NOT fall back —
`Upstream provider returned error`, `downstream service`, an error plus a quoted `"stream":true` request, a bare echo
`{"stream":true,...}`, `metadata.raw: "upstream said no"`, an HTML 400 mentioning "upstream"; must fall back — `Streaming is not
supported`, `stream_options` rejected, `{"object":"error","message":"stream mode …"}`, `{"detail":"Streaming responses …"}`,
`metadata.raw: "stream=true is not allowed"`, plain text `streaming is not supported`. (`ErrorDetail` keeps 300 characters, so a
refusal whose only mention of streaming is past that point would not fall back; none of the real shapes seen does that.)

**Minors**
- *Parallel calls on one wire index.* Tool calls are now keyed by creation order, with a map of the newest call per wire index, so an
  id-less argument fragment goes to the newest call under its index, and a repeated id goes back to the call it names. Tests:
  `Fragments_with_no_id_go_to_the_newest_call_under_their_wire_index` (fails on the old code), `A_repeated_id_under_a_shared_wire_index_returns_to_the_call_it_names`.
- *Callback time is not silence.* The idle timer used to be armed before the read and keep running through the callback; a read
  that then needed the network failed at once with an already-cancelled token. It is now a fresh timer created inside `NextLineAsync`
  around the read alone. Test: `The_time_the_callback_takes_is_never_counted_as_the_providers_silence` (callback takes 350 ms against a
  150 ms limit; fails on the old code). To make the fake faithful the scripted stream now throws on a read asked for with an
  already-cancelled token, as a socket stream does.
- *Fallback shares the call's budget.* `SendWholeAsync` takes an optional `within`; `StreamAsync` passes `RequestTimeout` minus the
  time the refused attempt took (and returns a timeout if none is left). Test: `The_fallback_gets_what_is_left_of_the_budget_not_a_second_one`
  (1 s budget, refusal after 0.6 s, hanging retry: the call ends in about 1 s, asserted under 1.4 s; fails on the old code).
- *A cancellation that is neither our budget nor our silence timer is a timeout.* The silence verdict is now only given by
  `NextLineAsync` when its own timer fired and the overall budget did not; every other cancellation reaching `TryStreamAsync` is
  `TimedOut`. Test: `A_cancellation_that_is_neither_our_budget_nor_our_silence_timer_is_a_timeout_not_a_silence` (fails on the old code).

Checked against the old code: with the fixed tests and the previous `src/` restored, 21 of the 81 streaming tests failed (the callback
faults, the five false-positive fallbacks, the parallel-call fragments, the stray cancellation, the shared budget); after the
silence-timer test was made to need a second network read it failed there too. Then `git stash pop` brought the fixes back.

**Output**
```
dotnet build Harbora.slnx -p:UseSharedCompilation=false -> BUILD_EXIT=0 (NU1900/NU1903 NuGet warnings only; no compiler warnings)
dotnet test  Harbora.slnx --no-build                    -> EXIT=0
Passed!  - Failed:     0, Passed:  6909, Skipped:     0, Total:  6909 - Harbora.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll
Skipped! - Failed:     0, Passed:     0, Skipped:   108, Total:   108 - Harbora.Postgres.Tests.dll
```
`Harbora.Tests` 6909 = 6874 + 35 new; `AgentChatClientStreamingTests` is now 81 tests (was 46). Logs `/tmp/test-r6-chat-client.log{,.build,.exit}`.

**Concerns that remain:** unchanged from the first report (never run against a real provider; the 90 s ceiling also bounds a long
answer; a refusing provider costs an extra request per model call). One new note for D: because a callback fault is now the caller's
exception, `emit` must swallow a gone browser itself, or the turn's model call is abandoned by that exception — which is what the
brief already asks of it.
