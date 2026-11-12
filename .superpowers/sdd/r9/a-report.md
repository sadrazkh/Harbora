# Agent A report: no real env file leaves a developer's machine, and a CI example to copy

Branch `r9/a`, worktree `../Harbora-r9-a`, base `e84a288`. Commits: `e84a288..HEAD` (three: the env rule,
the CI example with the guides, this report). Nothing pushed.

## What was built, and where

### 1. Env files

**One rule, in `Harbora.Shared`, reached by the CLI and the panel alike.**

- `src/Harbora.Shared/EnvFiles.cs` (new). An env file holds real values when its name is `.env` or
  `.env.<anything>` and it is not an example. Examples are the seven names the inspector reads
  (`ExampleNames`, which `Inspection/ExampleEnvFiles.cs` now takes from here so the two cannot drift)
  plus any `.env.<name>` whose last part is `example`, `sample`, `template` or `dist` (`.env.local.example`,
  which Next.js's own examples ship). `.env` and `.env.local` are `NeverUploaded`: no `!` line brings them
  back. `StaysHome(path, ignoreEntries)` is the whole decision; `IsLetThrough` reads `!` lines.
- `src/Harbora.Shared/IgnoreFile.cs` (new). `.dockerignore`, else `.gitignore`, never both (what the CLI
  always did), and the line normaliser both sides use. A `!` line now keeps its `!` instead of being
  dropped; it means something only for an env file.

**The CLI** (`src/Harbora.Cli/SourcePacker.cs`). `.env`/`.env.local` stay in `AlwaysExclude` (spread from
`EnvFiles.NeverUploaded`, and kept as a folder name because a `.env/` folder is usually a Python virtualenv).
After the built-in and root-only rules, a `!` line naming an env file lets it through (and outranks the
patterns around it), else `EnvFiles.StaysHome` keeps it back with the reason `built-in rule '.env.*'`.
`ExcludedEntry` gained `EnvFile` (true only for a file kept back as an env file, not for one inside
`node_modules`). `MatchingIgnorePattern` skips `!` entries, so no other negation changes meaning.
`SurveyEnvFiles` gives doctor the same decision without packing.

**Told, on every path.**
- After a push, `harbora deploy` and `harbora launch` both print, through `ReportExcluded`
  (`src/Harbora.Cli/EnvFileNotice.cs`, new):
  `! Kept on this machine: .env, .env.production — env files hold real values. Set what the app needs as
  its variables in the panel; a public name such as VITE_ or NEXT_PUBLIC_ reaches a generated build by
  itself.` and, when one could be sent, `To upload .env.production anyway, add "!.env.production" to
  .dockerignore, or under ignore: in harbora.yml when there is no .dockerignore. .env and .env.local never go.`
  No value is ever printed.
- `harbora doctor` (`Doctor.CheckEnvFiles`, called from the full report only): silent with no env file;
  Ok naming `.env`/`.env.local` when only those stay; Warn when another stays (there is a decision in it);
  says which a `!` line sends and that whoever reads the build can read it. The upload check's
  `Dockerfile COPY` failure for a kept env file now says what to do instead of telling the DriveUnion story.
- A browser zip (`src/Harbora.Infrastructure/Deployments/SourceArchive.cs`): the filter calls the same
  `EnvFiles.StaysHome`, with the `!` lines of the zip's own `.dockerignore`, else `.gitignore`, at the
  project root (after the wrapper is taken off; read with a 64 KB ceiling). `Result.EnvFilesLeftOut`
  lists what was left out, and `DescribeEnvFilesLeftOut` is the deploy-log line, which the pipeline
  writes after unpacking (`⚠ Left out .env.production: ...`). A tar is untouched, as before: the CLI chose.

### 2. CI example

- `examples/ci/github-actions.yml` and `examples/ci/gitlab-ci.yml` (new). Each installs the latest CLI
  with `deploy/install-cli.sh` and runs `harbora deploy --push --yes --server "$HARBORA_SERVER" --token
  "$HARBORA_TOKEN"`. Comments say where to copy it, where the token comes from (account menu → Settings →
  API Tokens → Create, read from `_Topbar.cshtml` and `Settings/Index.cshtml`), which two secrets to set,
  and where the app slug comes from. GitHub: push to `main`, `concurrency` group. GitLab: default branch,
  `debian:bookworm-slim` (the release is `linux-x64`, a glibc binary, so not Alpine), `resource_group`.
- `docs/cli-deploy.md`: "CI, with no interactive login" became "CI: deploy on every push", with the steps,
  both files without comments, and what each part does. The old example used `--no-follow` and said the
  exit code gates a pipeline; with `--no-follow` it is `0` once queued, so that sentence was untrue and is gone.
- `docs/tutorial/12-cli-deploy.md`: the same section in Persian, with the panel's Persian labels.
- `tests/Harbora.Tests/CiExampleTests.cs` (new): each file parses as YAML with a deploy in it; every
  `harbora` command in it is registered in `Program.cs` and every flag is declared on that command's
  settings type (read by reflection, not from a list); both guides contain each file's non-comment lines
  exactly; the secrets the file reads are the ones it and the guides name; the labels the guides quote
  are on the screens; the installer URL is this repository's and the release is built for glibc.

### Guides

`README.md` (the "What never leaves your machine" block, the doctor line, a pointer to `examples/ci/`),
`docs/cli-deploy.md` (launch flag table, the zip sentence, the doctor list, a new **Env files** part of
"What is never uploaded", the third-party `tar` example now also excludes `.env.*`, a troubleshooting row),
`docs/tutorial/12-cli-deploy.md` (doctor bullet, a "فایل‌های env" subsection, the CI section),
`docs/tutorial/15-launch.md` (one sentence).

## Decisions the owner might have made differently

1. **What counts as an env file.** `.env` and `.env.<anything>`, not `<name>.env` (`production.env`,
   `docker/db.env`) and not `.envrc`. Reason: that is the family the brief names and the frameworks use;
   `example.env`/`sample.env` are examples by the inspector's list. A compose `env_file:` such as `db.env`
   is accepted and ignored by `ComposeFile` (line 193), so leaving those alone breaks nothing here, but
   such a file can hold secrets and still goes.
2. **Examples by suffix too.** `.env.local.example`, `.env.production.sample` go. The inspector still
   reads only its seven names.
3. **The way back in is a `!` line**, in the ignore file the upload already reads or in `harbora.yml`'s
   `ignore:` list, because both mechanisms exist and the negation was simply dropped (`LoadIgnorePatterns`,
   old line 131). No new key, no flag. `ProjectConfig.cs` (G's) is untouched: a quoted `- "!.env.production"`
   already reaches the list.
4. **`.env` and `.env.local` can never be let through**, because `docs/product-audit/19-do-not-change-list.md`
   item 25 says `.env` is never uploaded and `env pull` writes decrypted secrets to `.env.local`. A Vite
   project that keeps public values in `.env` itself must put them on the app.
5. **A `!` line naming an env file outranks the lines around it.** So `.env*` then `!.env.example` now
   sends the example; before, the negation was dropped and the example was excluded, so `harbora launch`
   from such a folder had no variable names to read. Other `!` lines still do nothing, as before.
6. **The notice's advice prefers `.dockerignore`** when there is one (Docker reads it too, so a `.env*`
   line there would keep a file the CLI sent out of the build), else `harbora.yml`, never "create a
   `.dockerignore`", which would silently replace the `.gitignore` the upload reads.
7. **A zip reads the project's own `.dockerignore`/`.gitignore` `!` lines; `harbora.yml` is not read for a zip.**
   An env file beside a zip's wrapper folder is outside the project and is never let through, so it cannot
   change whether the wrapper is taken off.
8. **`--branch` and `--tar` are not filtered.** `--branch` uploads `git archive` output, the committed
   content a Git deploy would also build; `--tar` is the person's own archive. Both are said in the guide.
9. **Doctor: a separate check in the full report, not in the deploy preflight**, so a deploy says it once,
   after packing, rather than twice. Warn only when a file that could be sent stays home.
10. **New refusal:** a Dockerfile that `COPY`s `.env.production` used to build and now fails the deploy's
    preflight before upload, with what to do. This is the consequence of the safer default.
11. **The push notice prints on every push that kept an env file, including a project with only `.env`**
    (one extra line). The brief asked that what is left out be named.
12. **The deploy-log line is English only**, like every other pipeline log line.
13. **CI: `--push`, follow the logs, explicit `--yes`, two secrets** (`HARBORA_SERVER`, `HARBORA_TOKEN`).
    `--push` deploys exactly the commit the job checked out, for an app with or without a Git remote;
    following makes a failed deploy fail the job. The CLI version is not pinned: the installer only takes
    `releases/latest`, and I had no verified release tag to write in.
14. **The guides show the examples without comments** and the test holds them identical, so the Persian
    chapter, which the panel shows, is copyable without the repository.

## Runs on the final commit

On `90feb05` (the last code commit; the report commits after it change nothing that builds), in this
worktree, nothing else running in it:

```
export MSBUILDDISABLENODEREUSE=1
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false   # BUILD_EXIT=0
dotnet test Harbora.slnx --no-build --nologo -v q --blame-hang --blame-hang-timeout 6m --blame-hang-dump-type none   # EXIT=0
```

- Build: 0 errors, 2 warnings, both `NU1903` advisories on `SSH.NET` 2025.1.0 in
  `Harbora.Postgres.Tests.csproj`, a package this work does not touch. No compiler warnings.
- `Harbora.Tests.dll`: **9544 passed**, 0 failed, 0 skipped (baseline 9457; +87 new).
- `Harbora.NodeAgent.Tests.dll`: 500 passed, 17 skipped. `Harbora.NodeIngress.Tests.dll`: 15 passed.
  `Harbora.Postgres.Tests.dll`: 117 skipped (no Postgres credentials here), as at baseline.
- `grep -c "Harbora.Tests.dll"` on the log: 2.
- Front-end tests not run: nothing under `Scripts/` changed.

Earlier, on the way: the filtered run of my own classes (EnvFiles, SourcePacker, SourceArchive,
SourceArchiveZip, CliDoctor, CliEnvFileNotice, CliLaunchEndToEnd, LaunchChapter) gave 359 passed; and
CiExample, DocumentationDrift, Learning*, LaunchChapter and LaunchHttp gave 282 passed.

## Unverified

- **Neither workflow has been run.** Only GitHub and GitLab can run them. Not verified: that
  `install-cli.sh` puts `harbora` on the GitHub runner's PATH (it uses `/usr/local/bin`, with `sudo` when
  needed), that `debian:bookworm-slim` plus `curl ca-certificates` is enough for the self-contained binary,
  that GitLab accepts the token as a masked variable.
- **No Docker here.** That Docker honours `!.env.production` in `.dockerignore` at build time is Docker's
  documented behaviour, not something run. The deploy-log line is tested as a sentence and its two call
  sites in `DeploymentPipeline.cs` compile; no pipeline run on a real host wrote it.
- Not checked on Linux: the `.env*` file enumeration in `SurveyEnvFiles` and the case-insensitive rule on a
  case-sensitive file system (the rule compares ignoring case on purpose).

## Outside my area

- `src/Harbora.Cli/Commands.cs` (G): two lines. `checks.AddRange(Doctor.CheckEnvFiles(dir, config));`
  after `CheckEnvLocal` in `DoctorCommand.RunAsync`, and `EnvFileNotice.Print(excluded);` in
  `DeployCommand.ReportExcluded`.
- `src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs` (D's build and checkout methods): one log
  line after unpacking, in `MaterialiseSourceAsync` and in `BuildFromUploadAsync`; the first also keeps the
  extraction result it used to discard.
- `src/Harbora.Web/Views/Launch/Index.cshtml` (C): only the drop-zone words, both languages.
- `docs/tutorial/15-launch.md` (C): one sentence.
- `src/Harbora.Shared/Inspection/ExampleEnvFiles.cs`: its private list now points at `EnvFiles.ExampleNames`.
- `tests/Harbora.Tests/CliLaunchEndToEndTests.cs`: the existing env test also writes `.env.production` and
  asserts the notice.
- **Workspace placeholder for the coordinator and G:** both examples carry the comment
  `# Workspace: the token owner's personal one. (Placeholder: a team workspace is named on this command.)`
  right above the `harbora deploy` line. Once G's flag exists, put it on that command and in the two
  guides' copies; `CiExampleTests` will then demand the flag be declared on `DeployCommand.Settings` and the
  guides' copies match the files.

## Facts in the brief that were wrong

- `README.fa.md` has no copy of the excluded-files block; it sends readers to the English README for the
  CLI. Nothing there needed changing.
- The other line numbers held.

## Defects found and left

- **`/build` is not root-anchored.** `docs/cli-deploy.md` (line ~483) and chapter 12 (line ~130) advise
  writing `/build` instead of `build` to scope an ignore entry to the root, but the loader trims slashes at
  both ends (`IgnoreFile.Entries`, as `LoadIgnorePatterns` did before), so `/build` matches a nested
  `Scripts/build/` exactly as `build` does. Found by reading the code; not run.
- `docs/cli-deploy.md` "How the mode is chosen when you pass no flags" omits the rule that an app with no
  Git remote on the server is uploaded even from a Git checkout (`DeployPlan.Decide`, `serverCanPull is false`).
- `examples/harbora.yml` still advertises `env:` and `domains:`, which the parser ignores (known R-37).
- `Views/Apps/Create.cshtml` says ".git, node_modules and .env are excluded": still true, so left.
- Launch from a zip: the inspection never says which env files the zip had, because `LaunchService.InspectAsync`
  (C) discards the extraction result. The deploy log says it later; the confirm page could say it first.
- Bash heredocs in this tool collapse `\\` to `\`; that is how one C# literal broke during this work.
  Worth knowing for any agent writing files through Bash here.
