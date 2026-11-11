# A: no real env file leaves a developer's machine, and a ready CI example

Read `.superpowers/sdd/r9/CONSTRAINTS.md` first.

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r9-a`, branch `r9/a`. Report: `.superpowers/sdd/r9/a-report.md`.

## 1. Env files

What the code does today:

- `src/Harbora.Cli/SourcePacker.cs:23-33`: `AlwaysExclude` names `.env` and `.env.local` and no other env
  file. So `harbora deploy` and `harbora launch` upload `.env.production`, `.env.staging` and the like
  whenever no ignore file names them, which is exactly the case of a folder that was never in Git.
- `src/Harbora.Infrastructure/Deployments/SourceArchive.cs:293-296`: the server's filter for a zip sent
  from a browser knows the same two names.
- `src/Harbora.Shared/Inspection/ExampleEnvFiles.cs:13`: the inspector learns a project's variable names
  from `.env.example`, `.env.sample`, `.env.template`, `.env.dist`, `env.example`, `example.env`,
  `sample.env`. These must keep being uploaded, or Launch stops knowing what to ask.

The goal: a file holding real values does not leave the machine by default, on either path, and the person
is told. One rule, in one place both the CLI and the panel can reach, so the two cannot drift.

What makes this more than a two-line change, and is yours to settle by reading:

- Vite, Next.js and Create React App read `.env.production` at build time, and some projects commit one
  on purpose with public values. Leaving it out silently would change what such a build produces. So a
  file that is left out is named in the output of the command that left it out, with what to do: put the
  values in the app's variables (round eight added build variables with public prefixes: see
  `PublicBuildVariables` and `Buildpacks.ArgLines`), or opt the file back in.
- There must be a way to opt a file back in, so nobody is stuck. Read how `.dockerignore` and
  `.gitignore` are honoured today (`LoadIgnorePatterns`, `MatchingIgnorePattern`) and whether a negation
  already reaches the always-excluded names; use what exists rather than invent a second mechanism.
- A Git deploy clones the repository, so a committed `.env.production` is there. That difference is fine,
  but say it where the guide explains what is uploaded.
- `harbora doctor` (`src/Harbora.Cli/Doctor.cs:384-400`) already talks about `.env.local`; it should say
  what will be left out before a deploy does.

The guides state the list in words: `README.md:323-326` and the same block in `README.fa.md`,
`docs/cli-deploy.md` (around lines 99 and 136), `docs/tutorial/12-cli-deploy.md`, `docs/tutorial/15-launch.md`.
`Views/Launch/Index.cshtml` promises it for a browser zip; if the sentence spells names out, change those
words and nothing else in that file, which belongs to agent C.

## 2. A CI example a person can copy

There is none: neither `docs/cli-deploy.md` nor `docs/tutorial/12-cli-deploy.md` shows one. The README says
`harbora login --token ... --server ...` is the non-interactive form and that `--yes` is implied in CI.

The goal: a person copies one file into their repository, sets two secrets, and every push deploys. A
GitHub Actions workflow and a GitLab CI job, under `examples/` (next to `examples/harbora.yml`), and one
section in `docs/cli-deploy.md` and in the Persian `docs/tutorial/12-cli-deploy.md` (that chapter is shown
inside the panel; `LearningLibraryTests` and `LearningCensusTests` hold it to rules).

Every command and flag in the example must exist in `src/Harbora.Cli`, and the section must say where in
the panel the token comes from; read the code for both rather than trust the README. A test that holds the
example to the CLI's registered commands, in the manner of `LaunchChapterTests`, keeps it true later. The
workflow cannot be run from here: say so in the report.

## Owned and not

Yours: `SourcePacker.cs`, the env checks in `Doctor.cs`, the filter rule in `SourceArchive.cs`, a shared rule
in `Harbora.Shared`, the guide sections above, `examples/`.

Agent G is adding workspace selection to the CLI: `Commands.cs`, `ApiClient.cs`, `HarboraConfig.cs`,
`ProjectConfig.cs` are G's, and G adds its own section to the same two guides. Your CI example should work
for the personal workspace as the CLI is today; leave a one-line placeholder comment in the example where a
workspace would be named, and say so in the report so the coordinator can join the two.
