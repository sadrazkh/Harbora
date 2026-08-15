# One app name, one app — and one container that belongs to one tenant

**Date:** 2026-08-15 · **Status:** approved, ready for implementation

Fixes the cross-workspace container-retirement defect found while reviewing the `private-app-address`
branch. Recorded as `task_7fac94ac`.

---

## The defect

After a deployment, old containers are retired. The path, verified end to end:

1. `DeploymentPipeline.RetireOldContainersAsync` (`DeploymentPipeline.cs:1161`) calls
   `docker.ListContainersAsync(DeploymentPlanning.AppLabel, ct)`. That filter matches on the
   **existence** of the `harbora.app` label, so it returns every Harbora-managed container on the
   host — across every workspace.
2. `DeploymentPlanning.ContainersToRetire` (`DeploymentPlanning.cs:37-45`) keeps those whose
   `harbora.app` label **equals the slug** and whose name is not in `keepContainerNames`, and returns
   their ids for removal.
3. `App.Slug` is unique **only per workspace** (`HarboraDbContext.cs:239`).

So on a single-host multi-tenant install, deploying workspace A's `api` force-removes workspace B's
live `api` container. `DeploymentPlanning.CurrentContainerId` (`:52`) matches the same way and would
pick a stranger's container.

The names that make this reachable are the common ones — `api`, `web`, `app`. It is pre-existing; the
private-address work did not introduce it.

---

## Two changes, and the owner asked for both

### 1. The container name carries the workspace

`harbora-{slug}-{number}` becomes a name that cannot collide across tenants. This is the fix that
actually closes the defect, and it is invisible to customers.

**Legacy containers do not carry the new shape**, and what retirement does with them is the decision
that makes or breaks this. Treating "does not match the new pattern" as *mine* reintroduces the bug
exactly. Treating it as *not mine* strands every container running today, which then never gets
cleaned up and holds its ports and volumes for ever.

**So retirement matches on a workspace label, not on the name.** A container gets
`harbora.workspace = {id}` at creation, and retirement requires it to match. A container with **no**
workspace label is retired only when its `harbora.app` label matches **and** no other workspace owns
an app with that slug — which, after change 2, is always true. That is the one narrow bridge, and it
closes by itself as containers redeploy.

### 2. The app slug is unique across the platform

The create form refuses a name another workspace already holds, and a unique index enforces it.

**This is the customer-visible half, and it has a cost:** the second person who wants an app called
`api` cannot have one. The owner chose it knowingly. It also makes change 1's legacy bridge safe,
which is why they go together rather than either alone.

**No existing data blocks it.** Checked against production before writing this: 3 apps, 3 distinct
slugs. The migration adds the index without a rename step, and it must **fail loudly** rather than
silently renaming anything if that ever stops being true on another install.

---

## What must not break

- **A deploy must never remove a container it does not own.** This is the whole point; the test says
  so in those words.
- **Existing containers must still be retired.** A fix that strands every container deployed before
  today trades one leak for another.
- **The refusal must be a good message.** "That name is taken" on a platform-wide namespace is
  confusing unless it says so — the person can see their own workspace and there is nothing called
  `api` in it.

---

## Testing

- One workspace's deployment leaves another workspace's identically-slugged container **running**.
  `tests/Harbora.Tests/CrossTenantIsolationTests.cs` is where this belongs.
- A container with no workspace label is still retired when nothing else claims that slug.
- `CurrentContainerId` picks this app's container, not a stranger's.
- Creating an app with a slug held by another workspace is refused, with a message that explains the
  namespace is platform-wide.
- The unique index exists and the migration is one index, no rename.

**Prove the isolation test bites** by reverting the scoping and watching it fail. A test that passes
because the fixture only ever had one workspace proves nothing.

---

## What this is not

Renaming existing containers — they age out through ordinary redeployment · a per-workspace slug
namespace with prefixed display names, which was the alternative and was not chosen · changing how
managed services are named, which already use `harbora-svc-{slug}` and are out of scope here.
