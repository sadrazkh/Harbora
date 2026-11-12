# Proof fixtures

The apps `deploy/launch-proof.sh` launches on a throwaway host. Each one is what a customer's project
looks like when it reaches `harbora launch`, so none of them has a Dockerfile, and none has been bent to
make a check pass: if one of them is reported wrongly, the report is the bug.

Nothing here is shipped or built into the panel's image.

## What is not in the folder

Lockfiles are the ones the tool itself wrote: `npm install --package-lock-only` for `nextjs-ssr`, pnpm
12 for `pnpm-mongo`, yarn 1.22 for `yarn-mariadb`. Nobody typed a hash.

`go-nats` has no `go.sum`. It cannot be written by hand, and it was written on a machine with no Go, so
the proof runs `go mod tidy` in a Go container before it launches the folder, the way it lets composer
make its Laravel project. `go.mod` pins the one direct dependency, so what tidy adds is the same every
time.

`git-node` and `git-build` are pushed to a Git server the proof starts on its own host, and launched from
there with `harbora launch --git`. The proof writes `git-node/marker.txt` before each push.

`tests/Harbora.Tests/ProofFixtureTests.cs` checks, in seconds, what the proof takes for granted about
each folder: the stack it is read as and the services `--yes` will create for it. A new fixture needs a
row there.
