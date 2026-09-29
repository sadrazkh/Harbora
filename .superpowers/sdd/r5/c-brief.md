# C — Bring back resizing an open web terminal

## The regression

Migrating to `Docker.DotNet.Enhanced 3.131.1` for Docker 29 support removed the exec-resize call:
`IExecOperations` in that fork has no `ResizeContainerExecTtyAsync`, the request-building methods on
`IDockerClient` are internal, and `IContainerOperations.ResizeContainerTtyAsync` posts to
`/containers/{id}/resize`, which is a different endpoint that 404s for an exec id.

So `src/Harbora.Infrastructure/Docker/DockerContainerExec.cs`'s `ResizeAsync` became a documented
no-op. A web terminal still **opens** at the right size — `DockerEngine.ExecAsync` now sends
`ConsoleSize` on the exec create and start calls — but resizing the browser window while a session is
open no longer reaches the shell, so full-screen programs draw over the wrong area.

The Docker Engine API still has the route: `POST /exec/{id}/resize?h={rows}&w={columns}`.

## There is already a way to call the daemon directly — reuse it

`src/Harbora.Infrastructure/Docker/DockerBuildTransport.cs` moved `/build` off the bundled client onto
`SocketsHttpHandler` with a Unix-socket `ConnectCallback`, pinned to the same API version the rest of the
client uses (`DockerEngine.BuildApiVersion`), and only for the endpoints `DockerBuildTransport.Handles`
accepts. Read it fully. Do the resize the same way: reuse its socket-connection setup rather than
writing a second one — extract the shared piece if needed, without changing how `/build` behaves.

## Build

- `DockerContainerExec` gets what it needs to resize: the exec id and the engine endpoint (it currently
  receives only the stream). Thread them from `DockerEngine.ExecAsync`.
- `ResizeAsync` posts the resize over the Unix socket for endpoints the transport handles. For any other
  endpoint — a named pipe on a Windows dev machine, a TCP daemon — it stays a no-op, and the doc comment
  says why for that case specifically.
- **A resize that fails must never end the session.** That was the original contract, stated in the
  code this replaced: a wrongly-drawn screen, not a lost one. Swallow and log failures; never let a
  cancellation or a socket error escape into the session.
- Sizes are clamped the same way `Terminals.TerminalAccess.Size` already clamps them for the initial
  size. Call it rather than repeating the bounds.

## Tests

- The request built for a resize — method, path, query — is exactly
  `POST /v{BuildApiVersion}/exec/{id}/resize?h={rows}&w={columns}`, as a pure function tested without
  a daemon.
- Sizes are clamped through `TerminalAccess.Size`.
- A resize against a non-Unix endpoint does nothing and does not throw.
- A resize whose request fails does not throw.
- `/build` behaviour is unchanged (existing `DockerBuildTransport` tests still pass untouched).

## Scope

Only `DockerContainerExec.cs`, the `ExecAsync` method of `DockerEngine.cs`, `DockerBuildTransport.cs`
(to share, not change, its connection setup), and tests. Two parallel agents are editing other methods
of `DockerEngine.cs` and one is adding members to `IDockerEngine` — **do not add members to
`IDockerEngine`** and keep your `DockerEngine.cs` edits inside `ExecAsync`.

No Docker here: the real resize is unverified locally — say so.

## Verify

Per `.superpowers/sdd/r5/CONSTRAINTS.md`. Baseline 6286. Log to a branch-named file.
