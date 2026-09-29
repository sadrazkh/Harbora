# A — build honesty: report

Status: DONE_WITH_CONCERNS (one concern about the remote-agent path, below; the brief's scope
forbids touching the files it lives in).

Commits (branch `r5-build-honesty`, owner as sole author, no trailer):

- `34006a6` — image-exists backstop, cache-restore retry, predicate, fakes, tests
- `8170a52` — the same backstop proven on the Unix-socket transport

Test run: `dotnet test Harbora.slnx`, exit 0, log `/tmp/test-r5-build-honesty.log`.
`Harbora.Tests.dll`: **6321 passed, 0 failed** (baseline 6286, +35). `Harbora.NodeAgent.Tests` 500
passed / 17 skipped (needs Docker), `Harbora.NodeIngress.Tests` 15. No new compiler warnings (the only
warnings are the existing NU1900/NU1903 feed and package advisories).

## What changed

**`DockerEngine.BuildImageFromTarAsync`** (edits confined to that method):

- After the stream ends with no detected failure, `ImageExistsAsync(imageTag, ct)` is called. It sits
  after the transport/typed `if`/`else`, so the socket path, the typed path and the agent (which hosts
  this class) all get it. If the image is missing it throws `DockerBuildException`:
  `Build of <image> ended without an error, but the image does not exist. The last step reported was
  <Step line>. Last output: <last 6 lines joined by " | ">` (each line cut at 300 chars).
- `DescribesBuildFailure` is untouched and still runs first; a failure the daemon did report never
  reaches the image check.
- Progress is now reported through `InlineProgress<JSONMessage>` instead of `Progress<JSONMessage>`.
  `Progress<T>` posts to the thread pool, so the final messages could be handled after the build call
  returned. That is a pre-existing race in the failure detection itself: with the old type a test that
  reports an error message as the last message of the stream failed on its first run. The new check
  quotes the last lines, so it needs them to have been handled.

**`BuildCache.IsCacheRestoreFailure`** (two overloads, `Exception?` and `string?`): the one predicate.
Walks the exception chain (inner exceptions and `AggregateException`) and matches on message text only,
never type. Doc comment quotes the daemon text and explains why text is the only signal.

**`DeploymentPipeline.BuildFromSourceAsync`**: the single-app build is wrapped in
`catch (Exception ex) when (cachePlan.CacheFrom is { Count: > 0 } && BuildCache.IsCacheRestoreFailure(ex))`.
It logs one line (`Build cache: the daemon could not restore the cached image, so the build is being
repeated once without it.`) and builds again with `request with { CacheFrom = null }`. The retry is
outside the catch, so whatever it throws is the deployment's failure and there is no third build. No
retry when no cache was used, when the failure is any other text, or for a forced cold rebuild (its
`CacheFrom` is already null). The compose path passes `CacheFrom: null` and is unchanged.

**Fakes:** `FakeDockerEngine.ScriptBuilds(params Exception?[])` (fail-first-succeed-second: one entry
per call, exception fails it, `null` succeeds). New `Fakes/ScriptedDockerClient.cs`: a
`DispatchProxy`-based stand-in for `IDockerClient` (typed build call reporting scripted messages,
scripted image inspect) and `FakeUnixBuildDaemon`, a scripted HTTP responder on a real Unix socket.
There is no `FakeDockerEngine` "build returns but no image" switch: the backstop lives in the real
engine, so a fake that reproduced it would only test the fake.

## The daemon text the predicate keys on

`failed to restore cached image` (case-insensitive substring). Full form on the live server:

```
failed to restore cached image from "sha256:…" to sha256:…: failed to create cache image: …
```

Only the fixed leading phrase is matched; the ids and the reason after it vary per occurrence.

## Tests (35 new)

- `DockerEngineBuildImageExistsTests` (11): stream with no error but no image fails naming image + last
  step + daemon's last line, and not "No such image"; last lines present under repeated runs; only the
  last few lines quoted; a very long line is cut; no step seen; nothing printed; success unaffected; a
  reported failure keeps its own message and never reaches the image check; an inspect error other than
  not-found surfaces; and the two main scenarios again through the Unix-socket transport
  (`TypedBuildCalls == 0` asserts the socket path really ran).
- `DeploymentPipelineBuildRetryTests` (8): retried once with `CacheFrom: null` and only that field
  differs; not retried with no cache; not retried on a forced rebuild; ordinary failure not retried;
  retry that also fails leaves the retry's error, two builds, one "Deployment failed" line; same
  failure twice is still one retry; failure from a remote agent (`HttpRequestException`) is retried.
- `BuildCacheRestoreFailureTests` (16): bare text, inside a build-failure message, inside the no-image
  message, `HttpRequestException` (remote shape), inner-exception only, `AggregateException`, after
  `FailureText.Describe` joins with `→`, upper case; seven negatives (npm ci, `No such image`,
  `failed to create cache image` alone, an unrelated "restore", empty) and null.

Mutation check: reverting to `Progress<JSONMessage>` made
`A_failure_the_daemon_did_report_keeps_its_own_message...` fail, so the inline reporting is covered.

## Proven only against fakes

- Everything about the daemon. There is no Docker here. That Docker 29 streams the cache-restore error
  as a progress line with no error message comes from the live deployment history (Loomi #12 log line
  67), not from anything run here. The tests prove what this engine does for "stream with no error,
  image absent", not that a real daemon produces that.
- `ImageExistsAsync` is proven against a scripted `InspectImageAsync` (found / `DockerImageNotFoundException`
  / 500), not a real inspect. The 404 mapping in `ImageExistsAsync` pre-dates this work.
- The Unix-socket tests drive the real `DockerBuildTransport` against a scripted responder on a real
  socket. They passed on Windows here; not run on Linux (CI lane). The responder parses the request
  head and drains a `Content-Length` or chunked body, then answers with the lines it was given.
- The pipeline retry is proven against `FakeDockerEngine`: the decision and the request it makes, not
  that a real cache-free rebuild then succeeds.
- Not run: `DockerIntegrationTests` in `Harbora.NodeAgent.Tests` (17 skipped without Docker).

## Concern: the remote-agent path does not carry the daemon text today

The brief assumes the daemon's message survives the agent-to-panel hop. Reading the code, it does not
for a build that fails after output has started:

- `Harbora.Agent/Program.cs` `/agent/build` streams lines with `WriterProgress` (flush per line), then
  `BuildImageFromTarAsync` throws `DockerBuildException` inside the endpoint. The 200 and the headers
  are already sent, so Kestrel aborts the connection; there is no body carrying the message and no
  marker line (unlike `PullErrorMarker` for pulls).
- `RemoteDockerEngine.BuildImageAsync` calls `client.PostAsync(url, content, ct)` with the default
  `ResponseContentRead`, so the whole response is buffered inside that call. The abort surfaces as an
  `HttpRequestException` ("response ended prematurely") before `StreamLines` reads anything. The
  panel's stored error is that, with none of the daemon's text and none of the buffered log lines.

Consequences: the no-image backstop still fails a build on the agent, so a remote deployment no longer
carries on to "No such image" — but its stored reason is the truncated-response error, not the message
this change builds. And the cache-restore retry cannot fire for a build that ran on a remote node,
because the predicate has no text to match. The predicate itself is tested against the
`HttpRequestException`-with-text shape; the wire just never produces it today.

Fixing that means an in-band failure line from the agent (like `PullErrorMarker`) plus reading it in
`RemoteDockerEngine.BuildImageAsync`, which touches `Harbora.Agent/Program.cs` and `RemoteDockerEngine.cs`.
Both are outside this brief's scope and are where the other two agents are working this round, so I
left them alone. It is a small follow-up once those merge.

## Smaller notes

- The same `Progress<T>` race also applies to `PullImageAsync` (same pattern, its own
  `Progress<JSONMessage>` at the top of that method). Not touched: outside `BuildImageFromTarAsync`.
- `ImageExistsAsync` cannot tell a fresh image from a stale one under the same tag. Tags carry the
  deployment number and the retry runs after a failed first attempt, so a stale image under the
  tag is not expected; noted for completeness.
