# F: launch the stacks and databases people actually bring, for real, and fix what breaks

Read `.superpowers/sdd/r9/CONSTRAINTS.md` first, then `.superpowers/sdd/r8/p-brief.md`,
`.superpowers/sdd/r8/p-report.md`, `deploy/launch-proof.sh`, `deploy/proof-fixtures/README.md` and
`.github/workflows/launch-proof.yml`.

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r9-f`, branch `r9/f`. Report: `.superpowers/sdd/r9/f-report.md`.

## Where things stand

The proof installs Harbora from nothing on a throwaway runner and launches real projects on real Docker,
Traefik and databases. It passes on master (run 42031668273) with twelve fixtures: a Node app that really
queries Postgres, a crashing app and one with a wrong database host that must not end green, a Python
worker with Redis, a two-apps folder, Vite, a Create React App shape, Node and Python build variables,
PHP with MySQL, real Laravel with its migration, and two frontend failures. In round eight this run found
eight real bugs that no unit test had.

Never launched for real: .NET, Go, pnpm, yarn, a real Next.js with server rendering, Gatsby, Vue CLI;
MariaDB, MongoDB, RabbitMQ, NATS, Meilisearch; a zip whose content sits in one wrapper folder;
`harbora launch --git`; a retry after a failure; a panel restart in the middle of a launch. MongoDB,
RabbitMQ and NATS have no readiness wait and no connection check in the product at all, and say so rather
than passing.

## The goal

The owner wants a customer's app to just work. Extend the proof so that the stacks and engines people
actually bring are launched for real, and fix what breaks. In order of worth:

1. .NET, Go, Next.js with server rendering, pnpm, yarn.
2. MariaDB, MongoDB, RabbitMQ, Meilisearch and NATS, each with an app that really talks to it, and for the
   three engines that have neither, a readiness wait and a real connection check in the product, not only
   in the script.
3. The wrapper-folder zip, `--git` against a repository the runner serves itself, a retry after a forced
   failure, a panel restart in the middle.

Stop when what remains costs more than it proves, and say exactly what is left.

## How a run happens

There is no Docker here; the only way to run the proof is the workflow, which starts on a push to a branch
named `proof/**`. **You may push to exactly one remote branch: `proof/r9`**, with
`git push origin HEAD:proof/r9` (force is allowed on that branch only). Never push anything else: not
master, not a tag, not another branch. This is the single exception to "never push" in this round, and the
repository is public, so what you push is published: owner's identity on every commit, and nothing that
names a server or holds a credential.

A new push cancels the older run for the branch. A run takes twenty minutes or more, so batch your changes,
and make the script's failure annotations say enough to diagnose from one run (round eight: the reason has
to be the last line of what is shown).

Read results with no login, from the public API. `gh` is not installed.

- runs for a commit: `https://api.github.com/repos/sadrazkh/Harbora/actions/runs?head_sha=<sha>`
- jobs of a run: `https://api.github.com/repos/sadrazkh/Harbora/actions/runs/<run id>/jobs`
- what failed and why: `https://api.github.com/repos/sadrazkh/Harbora/check-runs/<job id>/annotations`

The unauthenticated limit is 60 requests an hour for this whole machine, and the coordinator shares it.
Poll no more often than every five minutes, from a background command with a loop (a foreground `sleep`
is blocked), and fetch annotations once per finished run.

If the whole proof grows past about forty minutes, splitting the fixtures across parallel jobs is yours
to decide; each job gets a host of its own.

## Fixing what breaks

Each product fix is minimal, has a unit test that fails without it, and is listed in the report with what
the real run showed. Other agents own the areas a fix is likely to land in: database provisioning and its
reconciling (B), Launch and Git code (C), the upload filter (A), cron and build directories (D),
verification after a deploy (E). Fix there only what the real run proves broken, keep it small, and name
it in the report so the merge is easy.

## Finishing

The report lists, precisely, what is now proven by a run and what is not, with the address of the last
green run on your final commit. Leave `proof/r9` on the remote; the coordinator removes it.
