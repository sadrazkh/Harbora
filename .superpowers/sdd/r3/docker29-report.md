# Docker.DotNet → Docker.DotNet.Enhanced 3.131.1 migration report

## What changed and why

`Docker.DotNet 3.125.15` (abandoned 2022) was replaced with `Docker.DotNet.Enhanced 3.131.1` (the
Testcontainers team's maintained fork) in both:

- `src/Harbora.Infrastructure/Harbora.Infrastructure.csproj`
- `src/Harbora.NodeAgent/Harbora.NodeAgent.csproj`

`Harbora.Agent` and `Harbora.Cli`/`Harbora.Web`/etc. get the package transitively via
`ProjectReference` (SDK-style projects flow `PackageReference`s through project references), so
their `.csproj` files did not need a direct edit — confirmed by inspection of
`Harbora.Agent.csproj` (no direct `Docker.DotNet` reference) and by the full-solution build below.

`tests/Harbora.Postgres.Tests` already pulled this exact fork in transitively via
`Testcontainers.PostgreSql 4.11.0`; its own csproj comment already documented that, unchanged.

## How the fork's actual API shape was determined

There is no Docker daemon and no internet access to a public NuGet feed configured as default in
this environment, but `nuget.org` is a registered source and the package was already restored in
the local NuGet cache (`~/.nuget/packages/docker.dotnet.enhanced/3.131.1`). I:

1. Wrote a throwaway reflection console app (outside the repo, in a sibling scratch folder) that
   loads `Docker.DotNet.dll` from the NuGet cache and dumps every public type, and every
   method/property signature of named types, via `System.Reflection`.
2. Installed `ilspycmd` (a .NET IL decompiler, `dotnet tool install -g ilspycmd`, resolved from
   `nuget.org`) and decompiled specific methods to C# to see actual implementation behaviour where
   reflection alone couldn't answer a question (notably: what HTTP endpoint a method actually calls).

Every API difference below was confirmed this way, not guessed.

## Every call site changed

### `src/Harbora.Infrastructure/Docker/DockerEngine.cs`

- Added `using VolumeInfo = Harbora.Application.Abstractions.VolumeInfo;` — the fork's
  `Docker.DotNet.Models` now ships its own `VolumeInfo` type, which collides by bare name with
  Harbora's own `Harbora.Application.Abstractions.VolumeInfo` (used by `ListVolumesAsync`'s return
  type and constructor call). The alias resolves the ambiguity with no change to `ListVolumesAsync`'s
  body — same pattern already used in `DockerContainerRuntime.cs` for `NetworkSpec`.
- `BuildImageFromTarAsync`'s progress handler: `m.Stream ?? m.Status ?? m.ErrorMessage` →
  `m.Stream ?? m.Status ?? m.Error?.Message` (`JSONMessage.ErrorMessage` no longer exists).
- `DescribesBuildFailure(JSONMessage message)`: was
  `!string.IsNullOrWhiteSpace(message.ErrorMessage) || !string.IsNullOrWhiteSpace(message.Error?.Message)`,
  now `!string.IsNullOrWhiteSpace(message.Error?.Message)` — the free-text field is gone, the
  structured field is the only one left, and it is still populated by the daemon. **This is the
  build-failure-detection guard the task called out by name; behaviour (a failed build is never
  reported as a success) is preserved, just through the surviving field.**
- `BuildFailureMessage`: `failure.Error?.Message ?? failure.ErrorMessage ?? "..."` →
  `failure.Error?.Message ?? "..."`.
- `PullImageAsync`'s progress handler: `m.Status ?? m.ProgressMessage ?? m.ErrorMessage` →
  `m.Status ?? m.Error?.Message`; and `lastError = m.Error?.Message ?? m.ErrorMessage;` →
  `lastError = m.Error?.Message;` (`JSONMessage.ProgressMessage` is also gone).
- `ExecAsync` (interactive terminal, used by the web terminal feature):
  - `client.Exec.ExecCreateContainerAsync(...)` → `client.Exec.CreateContainerExecAsync(...)`.
  - `ContainerExecCreateParameters.Tty` → `.TTY` (renamed, different casing) — and the initial
    terminal size is now set via a new `ConsoleSize` property on this same parameters object.
  - `client.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: true, ct)` →
    `client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters { TTY = true, ConsoleSize = size }, ct)`.
  - **Removed** the separate `client.Exec.ResizeContainerExecTtyAsync(...)` call that used to run
    immediately after start to set the initial size — no longer needed since `ConsoleSize` now
    travels on Create/Start directly. See "Known regression" below for what this means for a LIVE
    resize (mid-session, not the initial one).
  - `new DockerContainerExec(client, exec.ID, stream)` → `new DockerContainerExec(stream)` — see
    `DockerContainerExec.cs` below for why `client`/`execId` were dropped from that type entirely.
- Two identical `client.Containers.GetContainerLogsAsync(containerId, tty: false, parameters, ct)`
  call sites (`GetLogsAsync`, `GetLogsSinceAsync`) → `client.Containers.GetContainerLogsAsync(containerId, parameters, ct)`
  — the non-streaming overload dropped the `tty:` parameter entirely; both call sites already passed
  `tty: false`, so nothing about the stdout/stderr demux they read changes.
- `StreamLogsAsync`: `GetContainerLogsAsync(containerId, parameters, ct, new Progress<string>(sink.Report))`
  → `GetContainerLogsAsync(containerId, parameters, new Progress<string>(sink.Report), ct)` — this
  streaming overload swapped the order of its trailing `IProgress<string>`/`CancellationToken`
  parameters.
- `RunOneOffAsync`: same progress/cancellationToken swap as `StreamLogsAsync`, on the
  `if (log is not null) await client.Containers.GetContainerLogsAsync(...)` call.

### `src/Harbora.Infrastructure/Docker/DockerContainerExec.cs`

- Removed the `client` (`IDockerClient`) and `execId` (`string`) primary-constructor parameters
  entirely — both were used **only** by the old `ResizeAsync`'s network call, and once that call has
  no replacement (see below), keeping either parameter unused would produce a new `CS9113`
  ("parameter is unread") compiler warning under this codebase's primary-constructor style. Verified
  this is a real, current warning by reproducing it in the scratch project before deciding to remove
  the parameters rather than silence the warning with a discard.
- `ResizeAsync(uint columns, uint rows, CancellationToken ct)`: was a real network call
  (`client.Exec.ResizeContainerExecTtyAsync(...)`) wrapped in try/catch; is now
  `public Task ResizeAsync(...) => Task.CompletedTask;` — an intentional, documented no-op. See
  "Known regression" below.

### `src/Harbora.Infrastructure/Docker/DockerContainerConfigFileWriter.cs`

- `ReadFileAsync` and `ListDirectoryAsync`: `new GetArchiveFromContainerParameters { Path = ... }` →
  `new ContainerPathStatParameters { Path = ... }` — `GetArchiveFromContainerParameters` is gone;
  `GetArchiveFromContainerAsync`'s second parameter is now `ContainerPathStatParameters` (confirmed
  via reflection on `IContainerOperations`), which only has a `Path` property — no behavioural change,
  `response.Stream` is read exactly as before (`ContainerArchiveResponse.Stream` is unchanged).
- `WriteFileAsync`: `new ContainerPathStatParameters { Path = "/", AllowOverwriteDirWithFile = false }`
  passed to `ExtractArchiveToContainerAsync` → `new CopyToContainerParameters { Path = "/", AllowOverwriteDirWithFile = false }`
  — `ExtractArchiveToContainerAsync`'s second parameter type changed from
  `ContainerPathStatParameters` to the new `CopyToContainerParameters`, which has the same `Path`
  and `AllowOverwriteDirWithFile` fields (plus a new, unused-here `CopyUIDGID`), so the same two
  values carry over unchanged.

### `src/Harbora.NodeAgent/Runtime/DockerContainerRuntime.cs`

- `PullImageAsync`'s progress handler: `m.ErrorMessage ?? m.Status ?? m.ProgressMessage` →
  `m.Error?.Message ?? m.Status`.
- `MapHealthCheck`: return type `HealthConfig?` → `HealthcheckConfig?`, and `new HealthConfig {...}`
  → `new HealthcheckConfig {...}`. The fields this uses (`Interval`/`Timeout` as `TimeSpan`,
  `StartPeriod`/`Retries` as `long`) are unchanged in shape — confirmed by reflection — so no other
  line in this method needed editing; this is a pure rename.
- `PublishedPorts(IList<Port>? ports)` → `PublishedPorts(IList<PortSummary>? ports)` — `Port` is gone,
  replaced by `PortSummary`. `PrivatePort`/`PublicPort` are now `ushort` instead of the old wider
  integer type; the existing `(int)port.PrivatePort` / `port.PublicPort > 0` code compiles unchanged
  against the narrower type.
- `GetLogsAsync`: dropped the `tty: false` argument from `GetContainerLogsAsync`, same as
  `DockerEngine.cs` above.
- `StreamLogsAsync`: swapped the trailing `progress`/`cancellationToken` argument order, same as
  `DockerEngine.cs` above.
- `RunOneOffAsync`: swapped the trailing `progress`/`cancellationToken` argument order on its
  `GetContainerLogsAsync` call, same pattern.
- `ExecAsync` (non-interactive one-shot exec, used by the credential-rotation/database path):
  `client.Exec.ExecCreateContainerAsync(...)` → `client.Exec.CreateContainerExecAsync(...)`;
  `client.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, ct)` →
  `client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters { TTY = false }, ct)`.
  Also fixed a **new compiler warning** this exposed: `ContainerExecInspectResponse.ExitCode` is now
  `long?` (was non-nullable `long`), so `(int)inspect.ExitCode` produced `CS8629` ("nullable value
  type may be null"); changed to `(int)(inspect.ExitCode ?? -1)` with a comment explaining the `-1`
  is a defensive fallback for an exit code that cannot actually be null at that point (the output
  stream has already reached EOF by the time this reads it).
- `WriteTmpfsFilesAsync`: `ContainerPathStatParameters { Path = "/", AllowOverwriteDirWithFile = false }`
  → `CopyToContainerParameters { Path = "/", AllowOverwriteDirWithFile = false }` on
  `ExtractArchiveToContainerAsync`, same as `DockerContainerConfigFileWriter.cs` above.

### `tests/Harbora.Tests/DockerEngineBuildFailureTests.cs`

- Updated the class doc comment (dropped the `ErrorMessage` mention).
- `A_message_carrying_ErrorMessage_describes_a_failure` — this exact scenario is no longer
  constructible (`JSONMessage.ErrorMessage` doesn't exist). Rather than deleting the test (the task's
  6211 baseline must not drop), **repurposed** it into
  `A_message_carrying_a_failure_alongside_ordinary_stream_output_still_describes_a_failure`: a new,
  genuinely additional case — a message that carries both `Stream` (ordinary build output) and
  `Error.Message` at once, which is realistic (the failing message in a real build stream usually
  also carries other fields) and wasn't covered before. Same assertion shape, same test count.
- `Blank_error_fields_do_not_count_as_a_failure`: dropped the `ErrorMessage = "   "` field from the
  constructed message (property no longer exists); kept `Error = new JSONError { Message = "" }`.
- `The_failure_message_names_the_failing_step_and_quotes_the_daemons_own_detail` and
  `A_failure_with_no_step_seen_yet_still_names_the_image_and_the_daemons_detail`: both constructed
  `new JSONMessage { ErrorMessage = "..." }` → `new JSONMessage { Error = new JSONError { Message = "..." } }`.

### `tests/Harbora.Tests/DockerEngineInspectMappingTests.cs`

- `ContainerState` → `State` everywhere (8 occurrences: the `Response(...)` helper's parameter type
  and every `new ContainerState { ... }` construction) — `ContainerState` was renamed `State` in the
  fork. No collision with any other `State` type in Harbora (checked).
- `new Config { Image = ... }` → `new ContainerConfig { Image = ... }` — `ContainerInspectResponse.Config`
  is now typed `ContainerConfig`, not `Config`.

### `tests/Harbora.NodeAgent.Tests/DockerIntegrationTests.cs`

No changes needed — this file only calls `DockerContainerRuntime`'s own public methods and
`DockerClientConfiguration`/`IDockerClient`/`DockerApiException`, none of which changed shape. It
compiles unchanged; its `[DockerFact]`-gated tests skip on this machine (no daemon), same as before.

### `tests/Harbora.Postgres.Tests`, `src/Harbora.Agent/Program.cs`, `src/Harbora.Infrastructure/{ServerEngineFactory,DockerTcpGateway,ImageDigestResolver,RemoteDockerEngine,CapturingProgress,DependencyInjection}.cs`

No changes needed — verified each only uses types/members whose shape didn't change (`IDockerClient`,
`DockerClientConfiguration`, `DockerApiException`, `DockerContainerNotFoundException`,
`DockerImageNotFoundException`, `ImageInspectResponse.RepoDigests`), or (for `RemoteDockerEngine.cs`)
uses `VolumeInfo` without a `Docker.DotNet.Models` import in scope, so no collision exists there.

## Known regression — unverified, flag for live-server testing

**Live terminal resize (mid-session, after a shell is already open) is now a no-op.**

`Docker.DotNet.Enhanced 3.131.1`'s `IExecOperations` has exactly three methods
(`CreateContainerExecAsync`, `StartContainerExecAsync`, `InspectContainerExecAsync`) — confirmed by
decompiling the shipped assembly with `ilspycmd`. There is no method anywhere in this client version
that calls `POST /exec/{id}/resize`. `IContainerOperations.ResizeContainerTtyAsync` looks similar but
its decompiled body only ever posts to `/containers/{id}/resize` — a different endpoint that would
404 against an exec ID rather than a container one. `IDockerClient`'s HTTP-request-building methods
are all `internal`, so there is no way to call the daemon's exec-resize endpoint directly from
Harbora's code either. The Docker Engine API itself still has this endpoint (nothing about Docker 29
removed it); this specific client version just never wraps it.

Effect: `DockerContainerExec.ResizeAsync` (used when a user resizes their browser window during an
active web-terminal session — `src/Harbora.Infrastructure/Docker/DockerContainerExec.cs`) is now a
documented no-op. The terminal still opens at the correct initial size (set via the new
`ConsoleSize` field on `ContainerExecCreateParameters`/`ContainerExecStartParameters` in
`DockerEngine.ExecAsync`), but a session already open will not redraw at a new size if the browser
window changes — it keeps running at whatever size it opened with. This is the same fallback the old
code already used for a *failed* resize; it now applies to *every* resize instead of an occasional
rejected one, so it degrades safely (no exception, no crashed session) but silently loses the
feature. **This needs a decision from the owner**: live with the regression, pin an older/different
Docker.DotNet fork just for this, or accept a future PR against the exec-resize gap upstream.

## Call sites changed blind (no live daemon to verify against)

Every line above that touches an actual Docker.DotNet call was changed without a live daemon to test
against, per the environment constraint. In rough order of how much I'd want eyes on them on the live
server:

1. **`DockerEngine.ExecAsync` / `DockerContainerRuntime.ExecAsync`** — the exec create/start rename
   and the `TTY`/`ConsoleSize` reshuffle. This is the biggest structural change in the whole
   migration; verify an interactive terminal session (`ExecAsync` in `DockerEngine.cs`) actually
   opens a working, correctly-sized shell, and that the non-interactive one-shot exec
   (`DockerContainerRuntime.ExecAsync`, used by the credential-rotation/database path) still returns
   the right stdout/stderr/exit code.
2. **`GetContainerLogsAsync` argument-order swaps** (4 call sites: `DockerEngine.StreamLogsAsync`,
   `DockerEngine.RunOneOffAsync`, `DockerContainerRuntime.StreamLogsAsync`,
   `DockerContainerRuntime.RunOneOffAsync`) and the **dropped `tty:` argument** (2 call sites:
   `DockerEngine.GetLogsAsync`/`GetLogsSinceAsync`, plus `DockerContainerRuntime.GetLogsAsync`) — a
   parameter-order mistake here would compile fine (both parameters are reference types the compiler
   won't distinguish by accident, since I used named types correctly, but it's still new surface).
   Verify live deployment logs stream correctly and a log snapshot/tail still returns real content.
3. **`BuildImageFromTarAsync`'s failure detection** (`DescribesBuildFailure`/`BuildFailureMessage`) —
   this is the exact mechanism that stops a failed build silently reporting success, which is why
   this migration exists in the first place. It's unit-tested against constructed `JSONMessage`s
   (green), but never against a real failing build on Docker 29. **This is the single highest-value
   thing to verify on the live server**: trigger a deployment with a Dockerfile `RUN` step that fails
   and confirm the deployment reports the failure with the right step/detail, not a confusing
   downstream "No such image".
4. **`GetArchiveFromContainerAsync`/`ExtractArchiveToContainerAsync`** (config-file read/write,
   `DockerContainerConfigFileWriter.cs`) — already carried an "unverified without a live daemon" note
   in its own doc comment before this migration; still true now, plus the parameter type changes on
   top.
5. **`ContainerExecCreateParameters.ConsoleSize`/`ContainerExecStartParameters.ConsoleSize`** — new
   fields this migration started using for the terminal's initial size. Unverified whether the
   daemon actually honours a size supplied this way (vs. the old separate resize call) the same way.

## Build / test verification

```
export MSBUILDDISABLENODEREUSE=1
dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false   # Build succeeded, 0 Errors, 0 new CS warnings (only pre-existing NU1900/NU1903 network/vuln-feed warnings)
dotnet test Harbora.slnx --nologo -v q -p:UseSharedCompilation=false
```

- `Harbora.Tests.dll`: **6211 total** (baseline preserved exactly), 6210 passed, **1 failed**:
  `Harbora.Tests.BacklogStatusTests.Every_non_open_item_names_its_evidence`. This is a pre-existing,
  unrelated failure — it checks that every non-`open` entry in `docs/product-audit/backlog.json`
  carries a `statusEvidence` field, and six entries (`HARBORA-0055`, `HARBORA-0057`, `HARBORA-0062`,
  `HARBORA-0064`, `HARBORA-0065`, `HARBORA-0066`) were marked `"done"` without one in commit `b74b9ea`
  ("Close the six backlog items this round landed") — the commit this branch already started from,
  before any of this migration's work. Confirmed via `git status`/`git log` that I never touched
  `docs/product-audit/backlog.json`. Not fixed — out of scope for a Docker client library migration.
- `Harbora.NodeAgent.Tests.dll`: 517 total, 500 passed, 17 skipped (the `[DockerFact]`-gated
  integration tests in `DockerIntegrationTests.cs` — no Docker daemon on this machine, same as
  before this migration).
- `Harbora.Postgres.Tests.dll`: 96 total, all skipped (needs Docker/Testcontainers — none here).
- `Harbora.NodeIngress.Tests.dll`: 15 total, all passed.

## Security review

Not performed — explicitly out of scope per the task.
