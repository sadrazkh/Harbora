# A — A build that did not produce its image must not be reported as built

## The evidence, from the live server's own deployment history

Three deployments died on the same daemon error, all on the classic builder with `--cache-from`:

```
failed to restore cached image from "sha256:…" to sha256:…: failed to create cache image: …
```

It surfaced two different ways:

- **DriveUnion #44** — the daemon sent it as an error message; `DockerEngine.DescribesBuildFailure`
  caught it; the deployment failed honestly: *"Build of harbora/driveunion:build-44 failed at Step
  19/23: …"*.
- **Loomi #9 and #12** — the identical error arrived as an ordinary **`stream`** progress line (Loomi
  #12's deployment log, sequence 67). Nothing flagged it. The build call returned normally, the
  pipeline logged *"Starting container …-loomi-12"*, and the deployment failed with
  `Docker API responded with status code=NotFound … No such image: harbora/loomi:build-12`.

`DockerEngine.BuildImageFromTarAsync`'s own doc comment says this exact outcome — *"the failure then
only surfaces two steps later, as a confusing 'No such image'"* — is what its failure tracking prevents.
For this message shape it does not.

Each failed deployment was followed by a retry that succeeded. The classic builder is deprecated in
Docker 29 and its cache-restore path is intermittently broken.

## Two changes

**1. Confirm the tag exists before returning from a build.** After the build stream ends without a
detected failure, call `ImageExistsAsync(imageTag)` — it already exists on `IDockerEngine`, do not add
a new member. If the image is not there, throw `DockerBuildException` with a message that names the
image, the last `Step` line seen, and the last few output lines, so the reason is in the deployment's
stored error instead of a `No such image` two steps later.

Do this inside `DockerEngine.BuildImageFromTarAsync`, after **both** the `DockerBuildTransport` branch
and the typed-call branch, so the socket path, the typed path, and the remote agent (which hosts this
same class) all get it. Keep the existing `DescribesBuildFailure` detection — this is a backstop that
catches whatever shape the daemon's error takes, not a replacement.

Do not try to make `DescribesBuildFailure` recognise the stream-line form by text. Chasing message
shapes is how this gap existed; checking the one fact that matters — does the image exist — closes it
for every shape at once.

**2. Retry once without `--cache-from` when the cache could not be restored.** The owner has said
explicitly they want to keep the build-cache feature, so this must not disable it — it must make its
failure cheap. In `DeploymentPipeline`, at the single-app build call site that passes
`cachePlan.CacheFrom` (around line 1480), if the build fails **and** a cache was used **and** the failure
is a cache-restore failure, log one plain line saying the cache could not be restored and the build is
being repeated without it, then build once more with `CacheFrom: null`.

Put the "is this a cache-restore failure" decision in **one named predicate** next to `BuildCache`,
with a comment quoting the daemon text it keys on and why text is the only signal available. It must
work for an error that came back from a remote agent, where the exception type does not survive HTTP —
so it reads the message, not the type.

Do **not** retry any other build failure. A genuine `RUN npm ci` error must fail once, not twice at
double the wait. Do not retry when no cache was used. Retry at most once.

## Tests

- A build whose stream reports nothing wrong but whose tag does not exist afterwards fails with a
  message naming the image and the last step — not "No such image" from a later call.
- A build that succeeds and whose tag exists is unaffected.
- A cache-restore failure with a cache in use is retried once, and the retry passes `CacheFrom: null`.
- The same failure with no cache in use is not retried.
- An ordinary build failure is not retried.
- A retry that also fails fails the deployment with the retry's error, once.
- The predicate recognises the daemon's text, including when it arrives wrapped by `FailureText` or
  from `RemoteDockerEngine`.

Read `tests/Harbora.Tests/Fakes/FakeDockerEngine.cs` and `tests/Harbora.Tests/Fakes/PipelineHarness.cs`
first; the fakes may need a way to script "build returns but no image" and "fail first, succeed second".

## Scope

Only `DockerEngine.BuildImageFromTarAsync`, the pipeline's single-app build call site, `BuildCache`,
the fakes, and tests. Two parallel agents are editing `DockerEngine.cs` in **other** regions (exec
resize; a new dangling-image method) and one is adding members to `IDockerEngine` — **do not add
members to `IDockerEngine`** and keep your `DockerEngine.cs` edits inside the build method.

No Docker here: say which behaviour is proven only against fakes.

## Verify

Per `.superpowers/sdd/r5/CONSTRAINTS.md`. Baseline 6286. Log to a branch-named file.
