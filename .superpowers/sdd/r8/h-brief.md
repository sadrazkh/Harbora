# H — Help: a customer reads one page and is done

Read `.superpowers/sdd/r8/CONSTRAINTS.md` and `docs/superpowers/specs/2026-11-04-launch-design.md` first, then the four reports in `.superpowers/sdd/r8/` (`d-report.md`, `s-report.md`, `v-report.md`, `u-report.md`). They say what actually exists, which is what you write about. The design says what was meant.

The owner: «بزرگترین مشکل اینه که سازگاری و یادگرفتن کار کردن با این پروژه برای مشتری سخته». The whole round exists because this product takes learning. So the guide must be the shortest page in the library, and what it teaches is that there is almost nothing to learn.

## What to write

**1. A tutorial chapter, `docs/tutorial/15-launch.md`.** Read `docs/tutorial/README.md`, chapters `12-cli-deploy.md` and `14-python-bot.md`, and `LearningCensusTests`, and follow their shape, language and front matter exactly. Persian is the default; if chapters are bilingual, write both.

Its content, in this order:
- Two sentences on what it is: give it your code, and the database, the connection, the deployment and the checks are done for you.
- The steps as a person sees them: `/launch`, Git address or zip, the screen that says what was found, the one button, the timeline, the address.
- **What it looks at, and what it does not.** A table of what is read (`package.json`, `requirements.txt`, `go.mod`, `composer.json`, `.csproj`, a `Dockerfile`, `docker-compose`, `Procfile`, `.env.example`, …) and what it concludes from each. Read `SourceInspector` for the real list: never invent one.
- **What "connected" means.** The variables an attachment sets, the aliases written for other spellings like `DB_HOST` and `MONGO_URI`, and the one thing asked of the app: read those variables and listen on `0.0.0.0`. A small example for Node and one for Python. This is the one place a person learns something, so make it good.
- **Reading the checks.** Each check by name in plain words: what passed means, what failed means, what to do. Be exact about what the `service:` check proves: that the database answers on this app's network with the instance's credentials, not that the app itself connected. A `Skipped` check is not a pass.
- **When it says it cannot.** What each honest failure means, and what to do: several apps in one repository, a Compose project from an upload, a private repository, too little balance, an app that crashes after starting.
- **The same from a terminal**: `harbora launch`, `--yes`, `--set`, and the exit codes (0, 1, 2) for CI. C is writing this in parallel from `.superpowers/sdd/r8/c-brief.md`: use that brief's flags and exit codes. They will be checked when the two are merged.
- **Afterwards**: `harbora deploy` for the next version from a folder, a Git app's auto-deploy, the upload form on an uploaded app's page. And the manual form is still there.

Do not describe anything as verified that the reports say is not. Do not mention rounds, agents, or internal names in customer docs.

**2. `HelpMap` and `LearningLibrary`.** The Help control on `/launch` must open this chapter. Follow `LearningCensusTests`.

**3. Both READMEs, `README.md` and `README.fa.md`.** A customer's first look should show this first. Put it at the top of the "get started" part, a short block with the two ways (the page and the command) in the voice of the README as it stands. Read `DocumentationDriftTests` before touching anything: it pins exact bullet shapes and wording. C adds the `harbora launch` line in "Every command", so do not add a second one. Update the feature list if there is one and it would now be wrong to leave this out. No screenshots unless they are `*.annotated.png` made by the project's own means; raw panel captures hold secrets, and a sentence is fine.

**4. `docs/tutorial/01-first-steps.md`.** If a beginner is sent down the manual path first, point them at this chapter first. Change as little as possible.

**5. Anything else that now lies.** Search the docs for sentences this round has made wrong: "you must create the database first", say. Fix what you find, and list it in your report. Ignore `docs/overhaul`, `docs/superpowers`, `docs/product-audit`: those are history.

Do not touch `deploy/live-host-proof.sh`. A separate agent is handling a real proof.

## Check it

`dotnet test` on `DocumentationDriftTests`, `LearningCensusTests`, and whichever other documentation censuses exist, then the full `Harbora.slnx` suite once on your last commit, with `Harbora.Tests.dll` confirmed in the output. Read your chapter once as a customer who has never seen the panel, and cut whatever they did not need.

## Worktree

`E:/Cash.Net/source/repos/sadrazkh/Harbora-r8-h`, branch `r8/h`. Report: `.superpowers/sdd/r8/h-report.md`. Commit with `git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit`, with no Co-Authored-By trailer.
