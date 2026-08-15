# What this app actually is, on the page where somebody asks

**Date:** 2026-08-15 · **Status:** approved, ready for an implementation plan

Sub-project **B3**. B1 (the public address) merged as `f92c1cd`; B2 (the private address) as `964e39e`.

---

## The request, and what exploring it changed

The owner chose four groups of specifics for the app Overview tab: resources and size, origin and
version, placement and network, health and uptime. Overview shows almost none of them today — only
the prebuilt image reference, when there is one.

**The four groups are not remotely the same size of job.**

| Group | Where the data is | Cost |
|---|---|---|
| Resources and size | `InstanceSize` (`CpuCores`, `MemoryBytes`, `DiskBytes`), `App.DesiredReplicas`, `App.ContainerPort`, `App.MemoryLimitBytes`, `App.CpuLimit` | Near zero — all stored |
| Placement and network | `App.ServerId`, project and environment, `DeploymentPlanning.ContainerName`, `NetworkPlan` | Near zero — computed, no Docker call |
| Origin and version | `Deployment.CommitSha`, `CommitMessage`, `CommitAuthor`, `GitRef`, `ImageTag` | Near zero — all stored |
| Health and uptime | Nothing. `ContainerInfo` (`IDockerEngine.cs:129`) carries `State` and a `Status` string and no more | **Most of the work** |

**The correction exploring produced.** The plan going in was to store an image digest on `Deployment`
at build time, which meant a migration. That turned out to be both unnecessary and slightly wrong:
the node agent's own inspect record already carries `ImageDigest`
(`src/Harbora.NodeAgent/Runtime/IContainerRuntime.cs:86`), alongside `RestartCount`, `StartedAt`,
`Healthy` and the network addresses.

Unnecessary, because the digest arrives with everything else once an inspect exists. Slightly wrong,
because a stored digest records what a deployment **intended** to run, and the question this page
answers is what is **running now**. Inspecting the live container is the more truthful answer to the
question actually being asked, and it needs no schema change at all.

**So B3 adds no migration.**

---

## Architecture: one new engine capability, and everything else is already here

`IDockerEngine` gains an inspect that mirrors what the node agent already extracts. The local engine
implements it against `InspectContainerAsync`; the remote engine forwards to the agent, which has
done this work since it was written (`DockerContainerRuntime.cs:167-186`).

Everything else on this page is a read of data the panel already holds.

**Live, not stored — the opposite of B2's choice, for a reason.** B2 recorded its outcome on the app
because a name registered at deploy time stays true until the next deploy. "Up three hours" is true
for one second. A stored uptime is a wrong uptime, so this one is fetched when the page is drawn.

**The cost is one inspect per Overview render**, on a page that already loads several collections. It
is the Overview tab only — the tab split in sub-project A is what makes that affordable, because the
other three tabs do not pay for it.

---

## When Docker does not answer, the page says it does not know

This is the part worth writing down, because getting it wrong is this project's signature defect.

A failed or slow inspect must render **unknown**, not zero and not blank. `RestartCount = 0` means
"it has never restarted" — a real, reassuring, specific claim. Showing it because the inspect
failed is the panel asserting something it has no basis for.

The codebase already holds this discipline and says so out loud. `RuntimeContainerStats`
(`IContainerRuntime.cs:97`) documents it: *"Every figure is nullable and stays null when the runtime
did not report it, so 'not measured' survives the whole way to the screen instead of arriving there
as a zero."* The inspect path inherits that rule rather than inventing a second one.

**A worker with no health check is not unhealthy.** `DockerContainerRuntime.cs:179` is explicit about
this: `Healthy` is null when no health check is configured, because "no health check configured is
not 'unhealthy': it is 'we were not told how to ask'". The page must show those as different things.

---

## Order of work

1. **Resources, placement and network.** Stored data only. No Docker, no migration, no risk.
2. **`InspectAsync` on `IDockerEngine`** — local implementation, remote forwarding, and the node
   agent endpoint if one is not already exposed.
3. **Health, uptime and digest on the page**, including the unknown state.

Step 1 is mergeable on its own. If step 2 proves harder than expected — it touches the node channel —
the page has already gained most of its content.

---

## Testing

- Every figure comes from the fake engine, never from a constant written in the test. A digest is the
  specific trap: a plausible-looking `sha256:…` invented in a test passes everything and is wrong in
  production. The fake supplies it; the test asserts the page shows **what the fake said**.
- Docker not answering renders unknown, and the test asserts the unknown marker rather than the
  absence of a number — absence is also what a blank renders.
- A container with no health check renders as "not checked", distinct from unhealthy.
- Resources come from the app's own `InstanceSize`, so a hard-coded default fails.
- Tenancy: an app in another workspace returns 404, not content.

**On assertions that pass for the wrong reason.** The panel renders **Persian by default** in tests,
so assert on `data-` attributes or on the values themselves. And a page-wide `Contain("sha256:")`
would be satisfied by any digest anywhere on it — assert against the element meant to carry it.

---

## What B3 is not

Usage charts over time (sub-project C) · volume detail (D) · deployment history, which already has
its own tab · anything about either address, which B1 and B2 settled · a metrics store; this reads
the current state and keeps nothing.

**No migration.** The digest comes from inspecting what is running rather than from a column.
