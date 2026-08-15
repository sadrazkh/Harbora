# One app can reach another by a name that survives a deploy

**Date:** 2026-08-15 · **Status:** approved, ready for an implementation plan

Sub-project **B2**. B1 (the public address guarantee) merged as `f92c1cd`.

---

## The request, and what exploring it changed

The ask was a stable private address so one app can call another without going out to the internet
and back.

**The mechanism already exists, is already correct, and is already used — by one of the two code
paths that start containers.**

`DockerRunRequest.NetworkAliases` (`IDockerEngine.cs:125`) carries extra DNS names for a container.
`DockerEngine` applies them (`:149`), and so does the node agent on a remote host
(`DockerContainerRuntime.cs:261`). Its docstring states this exact problem:

> a service written to connect to `db:5432` must resolve `db`, not the versioned container name that
> lets old and new coexist during a cutover.

`DeploymentPipeline` starts containers in two places:

| Path | Aliases |
|---|---|
| Compose stack (`:846`, aliases built at `:829`) | `{service.Name}` and `{service.Name}-{number}` |
| Single container (`:426`) — the ordinary app | **none** |

So an ordinary app is reachable only as `harbora-{slug}-{number}` (`DeploymentPlanning.cs:19`), and
the deployment number in that name changes on every deploy. The name a caller hard-codes today stops
resolving the next time the callee ships.

**App→managed-database already works**, which is why this gap was easy to miss: a managed service has
a stable `ContainerName` and apps reach it by that inside the environment network. Only app→app is
missing.

---

## Architecture: give the single-container path what the compose path already has

The app's container is created with the network alias `{slug}`. Nothing new is built — one of two
existing call sites starts passing an argument the other already passes.

`http://{slug}:{ContainerPort}` then resolves from any app in the same environment.

**One alias, not two.** The compose path also registers `{name}-{number}` to disambiguate across
stacks. An app does not need it: `harbora-{slug}-{number}` **is** the container name and already
resolves. A second versioned alias would be a synonym for something that already works.

---

## Reach stops at the environment, and does so by construction

`DeploymentPipeline.cs:285` states the existing rule — each environment is a private network "so
staging cannot reach production's". The alias is applied on the **creation** network, and
`ConnectNetworkAsync` (`IDockerEngine.cs:46`) takes no aliases, so the extra networks a container is
attached to afterwards do not carry it.

That means the boundary holds because of how the mechanism works, not because of a rule somebody has
to remember. Worth stating explicitly: if `ConnectNetworkAsync` ever gains an aliases parameter, this
containment is what a caller would be quietly removing.

---

## The cutover window is correct as it stands

During a deploy the old and new containers both answer to `{slug}` until the old one is removed —
which happens only after the new one is healthy and traffic has switched
(`DeploymentPlanning.cs:24-27`).

This is the behaviour to want. If only the new container answered, there would be a window in which
it was not yet healthy and the internal name resolved to nothing at all. Both containers are working
versions of the same app; briefly reaching the previous one is a smaller problem than briefly
reaching none.

---

## The collision, which is the half of this worth the care

Docker resolves an alias to every container that registers it and balances between them. So if an app
is slugged `db`, and a **different** app in the same environment runs a compose stack with a service
named `db`, both answer to `db`.

The consequence is not a broken link. It is an app connecting to **the wrong database**, intermittently,
with nothing reporting a problem. That is worse than having no private address at all.

**This is not new.** Two compose stacks in one environment can already collide on a service name
today. What B2 changes is that ordinary apps join the same pool, so the surface grows.

**The rule: an alias that would be ambiguous is not registered, and the app's page says why.**

**Where the answer comes from.** Not the database — `ComposeService` (`ComposeFile.cs:4`) is parsed
from the repository at deploy time and never persisted, so there is no stored list to consult. The
authority is the containers themselves: the compose path labels each one
`harbora.compose.service = {name}` (`DeploymentPipeline.cs:836`), and `ListContainersAsync` takes a
label filter (`IDockerEngine.cs:41`). Reading the running containers on the network asks the thing
that actually holds the name rather than a mirror that can drift out of date.

**Not a refusal to deploy.** A name clash must not stop an app shipping. The deployment proceeds, the
alias is skipped, and the app is still reachable at `harbora-{slug}-{number}` and at its public
address. Only the convenience is withheld, and the page says so rather than showing an address that
would sometimes reach somebody else.

---

## Where it appears

Beside the public address on Overview, in the block B1 built.

A service with no inbound traffic still gets one: `ServicePlan.JoinsInternalNetwork`
(`ServicePlan.cs:36`) is true for everything except `ReleaseTask`, and a worker with a metrics port
its siblings scrape is the case that rule was written for. So a worker can have a private address and
no public one, and its page shows exactly that.

A `ReleaseTask` gets neither, and says so.

---

## Testing

- The single-container run request carries the alias; the compose path's own aliases are unchanged.
- A `ReleaseTask` is started with no alias.
- A slug that collides with a compose service label already on the network does **not** register the
  alias, and the reason reaches the page.
- A collision does not fail the deployment.
- Overview shows the private address with the app's own `ContainerPort`, not a hard-coded 80.

**On assertions that pass for the wrong reason.** The recurring defect here is a check that reports
success for work it never did. The panel renders **Persian by default** in tests, so assert on
`data-` attributes, route fragments or the alias string itself. And an assertion that the run request
"contains the slug" is satisfied by the container name, which contains the slug too — assert on the
`NetworkAliases` collection specifically.

---

## What B2 is not

Environment-variable injection. The owner chose display-only: the address is shown, copyable, and the
customer puts it where they need it. Injecting a variable per neighbour would fill an app's
environment with entries it did not ask for and change it whenever an unrelated app is created.

Also not: cross-environment reach · an attach flow like the managed-database one · pod specifics (B3)
· anything about the public address, which B1 settled.

---

## Risk

The alias is applied at container creation, on the deployment path that every app uses. A mistake
here does not degrade a page — it fails a deploy. The change is one argument to one existing call,
and the compose path twenty lines away is the worked example of it being correct.

The second risk is the collision check calling out to Docker on every deployment. It is one
`ListContainersAsync` against a label filter, on a path that already makes several Docker calls, and
a failure to answer must skip the alias rather than fail the deploy.
