# D — the inspector: report

## What was built

`SourceInspector.Inspect(root)` is real now. It is pure over the given directory, uses only the BCL, and `Harbora.Shared` still references nothing. The records in `SourceInspection.cs` are unchanged; only the stub class moved out of that file, into `SourceInspector.cs`.

Files, all under `src/Harbora.Shared/Inspection/`:

| File | Does |
|---|---|
| `SourceInspector.cs` | Entry point and the never-throw guard |
| `SourceTree.cs` | All disk access: the 1 MiB limit, no links, binary reads as nothing, the bounded project search |
| `RootLocator.cs` | Wrapper-folder descent, one app or several |
| `StackDetector.cs` | Dockerfile, then the `Buildpacks.Detect` order, then compose, plus the stack notes |
| `ProjectFacts.cs`, `JsonManifests.cs`, `PythonManifests.cs`, `GoManifest.cs`, `DotNetManifest.cs` | Read each language's project files |
| `ServiceTable.cs` | The brief's table, row per service |
| `ServiceDetector.cs` | Libraries, Prisma, compose images, example-file names, with evidence |
| `AttachEnvNames.cs` (public) | The mirror of `ServiceCatalog.AttachEnv` |
| `EnvAliases.cs`, `ExampleEnvFiles.cs`, `EnvVarClassifier.cs` | Parsing and the six rules |
| `KindDetector.cs`, `PortDetector.cs`, `ReleaseCommands.cs` | Web or worker, port, migration command |
| `InspectionNotes.cs` | Every note, in both languages |

Tests: `tests/Harbora.Tests/SourceInspectorTests.cs`, `SourceInspectorServiceTests.cs`, `SourceInspectorEnvTests.cs`, `SourceInspectorParityTests.cs`.

## The runs

Commands were the ones in CONSTRAINTS, with `MSBUILDDISABLENODEREUSE=1` and `-p:UseSharedCompilation=false`.

- **Build:** `dotnet build Harbora.slnx --no-restore`: exit 0, 0 errors, 2 warnings. Every warning line in the log is `NU1903` for `SSH.NET` in `tests/Harbora.Postgres.Tests`, a restore warning about a package this work does not touch. No compiler warnings. The full suite below ran on exactly the code at the head of this branch; only this report was committed after it.
- **Full suite:** `dotnet test Harbora.slnx --no-build`: exit 0.
  - `Harbora.Tests.dll`: Passed 8016, Failed 0, Skipped 0 (baseline 7429, plus 587 inspector tests).
  - `Harbora.NodeAgent.Tests.dll`: Passed 500, Skipped 17, Failed 0.
  - `Harbora.NodeIngress.Tests.dll`: Passed 15, Failed 0.
  - `Harbora.Postgres.Tests.dll`: Skipped 115, Passed 0. No Postgres here, and nothing of mine is in that lane.
  - `grep -c Harbora.Tests.dll` printed 2.
- **Parity tests can fail:** one build with three deliberate faults, then `SourceInspectorParityTests`: 42 total, 39 passed, 3 failed, one per fault.
  - PHP checked ahead of Go: `Every_combination_of_marker_files_detects_the_same_stack` failed.
  - `MEILI_PORT` removed from the mirror: `The_attach_names_are_exactly_what_an_attachment_sets(Meilisearch)` failed.
  - `vendor` skipped in the project search: `A_project_file_is_looked_for_exactly_where_the_pipeline_looks(vendor/lib/Lib.csproj)` failed.
  - All faults were then reverted with `git checkout`, the files touched, and the solution rebuilt before the full run above.
- **A real tree:** a throwaway console (since deleted) ran the inspector over this repository: 141 ms. It answered Dockerfile, Web, port 8080, and Postgres, Redis and RabbitMQ as certain. The Postgres evidence included `src/Harbora.Data/Harbora.Data.csproj`, reached by following project references. It ran before the last small edits.
- **Front-end node tests:** not run. No front-end file changed.

## Unverified

- **File links:** `An_example_file_that_is_a_link_is_not_followed_even_to_somewhere_inside_the_tree` returns early on this machine, because creating a file symlink needs administrator rights here. It passes without proving anything until it runs on CI. The folder-link tests are real here: they use a junction and assert the link exists.
- **Linux:** nothing was run on Linux, which is where the panel runs. Parity on the project chosen among several rests on directory listing order being stable between two reads there.
- **The time tests:** the `A_file_built_to_be_slow_is_not` cases were not shown to fail against the old patterns. My mutation of the old Procfile pattern did not apply, and I did not retry it. That those patterns were quadratic is reasoning, not a measurement.
- **The named-pipe guard:** an empty-length file is not opened, because opening a pipe blocks for ever. No test covers it. `SourceArchive` already refuses such entries for tar; the zip path is S's to add.
- **The roles against S's table:** I cannot see `AttachEnvRoles` from this branch. After integration, check that each role I emit resolves to a key: Meilisearch `Url` and `Password`; Redis, RabbitMq and Nats `Url`; Postgres, MySql, MariaDb and MongoDb every role. A role resolving to null would drop the alias with no error.

## Contracts I think are wrong, or need a decision

1. **`RootCandidates` loses the answer.** The list holds folder names only, and S's brief deletes the working tree in `finally`. Once someone picks a folder, nothing has read it, so the proposal would have no services, variables or port for the app they chose. The record has no room for a per-candidate result, and I did not change it. The cheapest fix is on S's side: while the tree still exists, call `SourceInspector.Inspect(Path.Combine(dir, candidate))` for each candidate and store the results beside the inspection. That result's `RootDirectory` is relative to the candidate, so prefix it. **This one will bite: the launch would succeed with no database.**
2. **`ServiceEvidence.File` says "relative to the inspected root".** I made it, and `DetectedEnvVar.SourceFile`, relative to the *chosen* root, because the brief says everything else is judged from there and its own example is `("package.json", …)`. So a `repo-main` zip reports `package.json`, not `repo-main/package.json`. The doc comment should say so; I left the contract file alone.
3. **`Port`'s doc says an example file outranks the stack's default.** Static is the exception: see reading 4 below.

## Readings of the brief worth a second look

1. **The project search ignores the skip list.** The brief asks for both the skip list and exact `FindCsproj` parity, and they conflict for a project under `vendor/` or `dist/`. `FindCsproj` leaves out only `bin` and `obj`, so with `go.mod` plus `vendor/x/x.csproj` the pipeline really builds .NET. I followed the pipeline, because saying Go would be false. `.git` is the one addition. Everything else, including app candidates, uses the skip list.
2. **"The root holds something" means directly.** A file in the folder counts, and so do `.sln`, `.slnx`, `global.json`, `Directory.Build.props` and `Directory.Packages.props`. A nested `.csproj` alone does not. So `backend/` (a csproj) with `frontend/` (package.json) is several apps. In that case `Stack` stays `DotNet`, because that is what a build from the root would do, which keeps `Stack` equal to `Buildpacks.Detect` in every result, including several-apps. With no nested csproj, several-apps gives `Unknown`, and `no-stack-recognised` is left out because it would be false. Folders named `test(s)`, `docs`, `example(s)` and the like are dropped when something else remains. A `src/` plus `tests/` layout therefore gets root `src` rather than a question with one answer.
3. **Python under the buildpack proposes the framework's port** (Flask 5000; uvicorn, FastAPI, gunicorn 8000), else 8000. The brief says framework defaults only where the buildpack does not set `PORT`, and it does set it here. But what runs is `python app.py`, and neither `app.run()` nor `uvicorn.run()` reads `PORT`. The proposal is what `PORT` gets set to, so this number agrees whether or not the code reads it, and a fixed number would not. **This departs from the letter of the brief; the alternative is one constant in `PortDetector`.**
4. **Static is always 80, even with `PORT=` in the example.** The generated nginx image never reads a port, so any other number would be a failing health check.
5. **Not every table hit is certain.** `devDependencies`, `optionalDependencies`, `peerDependencies` and `require-dev` give `Certain = false`, despite "all of those are certain". The brief also says weak evidence is not certain, and a test helper should not tick a paid database. The SQLite and SQL Server notes count only what the running app has.
6. **`REDIS_URL`, `AMQP_URL`, `NATS_URL` are certain even when bare**, since the brief names them outright. Any other bare name is a guess.
7. **A release command also needs a stack whose image has the tool.** A Laravel repo with a `package.json` builds as Node, which has no PHP, so `artisan` there gives null rather than a release that fails the deploy.
8. **A Procfile `web:` line leaves `StartCommand` null**, as the brief says. Only `worker:` sets it.
9. **`bullmq` alone stays Web.** I read "bullmq worker scripts" as needing a worker script, a `main` naming a worker, or a `worker.js`-style file.

## Added beyond the brief

- **Notes:** `stack-ambiguous`, `manifest-unreadable`, `tree-too-large`, `source-unreadable`.
- **Evidence:** at most 8 reasons per service.
- **Root search:** at most 100 folders examined.
- **Aliases:** a few more, such as `MEILISEARCH_API_KEY`, `MARIADB_*`, `MYSQL_URL`, `MONGO_DB`. Without them the screen would ask a person for a key Harbora generated.
- **Engines:** Prisma `sqlserver` and Laravel `sqlsrv` give the SQL Server note. `DATABASE_URL=sqlite:…` gives the SQLite note.
- **Dockerfile:** `Dockerfile.harbora` counts as a Dockerfile, as it does in the pipeline.
- **Not added:** `ext-pdo_pgsql` is missing from the brief's table beside `ext-pdo_mysql`. I kept to the table; it is one string in `ServiceTable` if wanted.

## Things in the pipeline this surfaced (not changed)

- **`package.json` wins detection.** A Laravel, Flask or ASP.NET app with a frontend's `package.json` and no Dockerfile builds as Node and cannot start. The inspector agrees, as it must, and says so with `stack-ambiguous`. This is the most likely wall a real customer hits on `/launch`, so it may deserve a decision of its own.
- **`FindCsproj` walks into `vendor/` and `node_modules/`**, ahead of `go.mod` and `composer.json`. It also filters on the full path, so a checkout path containing `/bin/` or `/obj/` would find no project at all.
- **The .NET buildpack uses SDK and runtime 9.0.** I would expect a `net10.0` project to fail at build. I read this in the Dockerfile template and did not run it.
