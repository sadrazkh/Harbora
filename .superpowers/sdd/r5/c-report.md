# C — Live resize of an open web terminal: report

Status: **DONE** (real resize against a live daemon is unverified here; see the end)

Branch `r5-exec-resize`. Commits, owner's name alone, no trailer:

- `0f8008f` Bring back live resize of an open web terminal
- `1b86513` Terminal resize: send in order, newest wins

Full run (`dotnet test Harbora.slnx`, exit 0, log `/tmp/test-r5-exec-resize.log`):

| Assembly | Passed | Skipped |
|---|---|---|
| Harbora.Tests.dll | **6307** (baseline 6286, +21) | 0 |
| Harbora.NodeAgent.Tests.dll | 500 | 17 |
| Harbora.NodeIngress.Tests.dll | 15 | 0 |

`Harbora.Tests.dll` line present (grep count 2), no `MSB3030`, no `warning CS/CA/xUnit`. The
21 new tests were also run 6 times in a row with `--no-build`, all green.

## What changed

- `DockerBuildTransport.cs`: the Unix-socket connection setup (the `SocketsHttpHandler` with its
  `ConnectCallback`) is extracted, unchanged, into `internal static CreateSocketHandler(Uri endpoint)`.
  `BuildAsync` calls it and then sets `PooledConnectionLifetime = InfiniteTimeSpan` itself, so a
  build connects and lives exactly as before. `Handles`, `Query`, the build request and the
  response loop are untouched. The existing `DockerBuildTransport` tests pass untouched.
- `DockerContainerExec.cs`: now receives the exec id, the engine endpoint, the API version and a
  logger. `ResizeAsync` posts `POST /v{apiVersion}/exec/{id}/resize?h={rows}&w={columns}` through
  `CreateSocketHandler`, gated on `DockerBuildTransport.Handles`. For a named pipe or TCP daemon it
  returns at once; the doc comment says why for that case specifically.
- `DockerEngine.cs`: inside `ExecAsync` only — passes `exec.ID`, `client.Configuration.EndpointBaseUri`,
  `BuildApiVersion` (the existing private const, not moved or re-declared) and `logger`, and one
  comment there is updated. `IDockerEngine` untouched.
- `tests/Harbora.Tests/DockerExecResizeTests.cs`: 21 tests (below).

## What was shared from DockerBuildTransport, and how

Only the connection: the Unix-socket `ConnectCallback` handler, via `CreateSocketHandler`. It is
deliberately just the connection. Lifetimes and timeouts stay with the caller because they differ
(a build must outlive any deadline; a resize gets 5 s). The API version is shared the other way:
`DockerEngine.BuildApiVersion` is passed in, so the direct calls and the typed client cannot end up
on different API surfaces. `DockerBuildTransport.Handles` decides which endpoints resize applies to.

## Contract, as tested

- Request is a pure function (`DockerContainerExec.ResizeRequest`): method POST, path, query
  exactly as the brief specifies; API version is a parameter, not hard-coded.
- Sizes go through `TerminalAccess.Size` (no bounds repeated). A `uint` above `int.MaxValue`
  saturates to the top of the range instead of wrapping negative and clamping to the bottom.
- Non-Unix endpoint (npipe, tcp, http, null): does nothing, does not throw, logs nothing (proving
  no attempt was made).
- Failure never throws: unreachable socket, daemon answering 404, daemon that never answers (times
  out, warning logged), token cancelled before start, and session cancelled while the request is in
  flight (silent). Non-cancellation failures are logged as warnings and swallowed.
- Where the socket matters, a listener on a real Unix socket stands in for the daemon and records
  what arrives (the tests run on the Windows dev box and will on Linux CI).

## One addition beyond the brief, and why

`Terminal.vue` sends a resize on every `ResizeObserver` callback with no debounce, and
`TerminalController.ApplyResize` fires `_ = exec.ResizeAsync(...)` without awaiting. A window drag is
therefore a burst of concurrent resizes; sent over separate connections, whichever reached the
daemon last (not whichever was sent last) would be the size the shell kept — the very symptom this
fixes. So resizes go one at a time in call order, a resize overtaken by a newer one while waiting is
dropped, and the timeout starts when a resize gets its turn (a `SemaphoreSlim` plus a sequence
number, about ten lines). Tested deterministically by holding the fake daemon's first reply.

## Unverified without a daemon

- That a live Docker 29 daemon accepts `POST /v1.41/exec/{id}/resize?h=&w=` over its socket and the
  shell actually redraws. The request is pinned to the documented route and the socket path is
  exercised against a stand-in, but no test here touched a real daemon.
- The success status code the daemon returns (any 2xx is treated as success; the stand-in answers 201).
- That API 1.41 is still accepted for this route by the live daemon (the same version `/build` is
  pinned to; the live daemon reported a minimum of 1.40 in the earlier `/build` work).
- Named-pipe (Windows dev) and TCP daemons: resize is a deliberate no-op there, so a terminal on
  those keeps the size it opened with.

## Notes

- The controller still clamps via `TerminalAccess.Size` before calling `ResizeAsync`; the exec
  clamps again through the same function (idempotent), so it stays correct for any other caller.
- `DockerBuildTransport` keeps its name though it now also serves resize; renaming would touch
  tests and files outside this brief's scope.
