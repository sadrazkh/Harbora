# Feature entitlements, and the first feature that needs them

Branch: `feature/entitlements-and-functions`, cut from `master` at `7cd873c`.

Two things, in this order. First a way to say **who a feature is for** — shown to everybody, usable
by the people the owner names. Then the first feature that is sold that way: **Functions**, code
written in the panel that runs without a repository, a Dockerfile or a `git push`.

---

## 1. Why

Harbora has exactly two ways to withhold something today, and neither is the one a provider selling
plans actually needs:

- **Capability** (`Capabilities.PlatformManage` …) — a role question. The sidebar *hides* what a
  role cannot reach, on the stated principle that "a sidebar that lists a locked door is a sidebar
  people learn to distrust" (`NavigationMap`, line 112).
- **Feature flag** (`Features:Sync`, `Features:Backup`) — a config-file boolean, platform-wide. Both
  modules deliberately contribute *no* sidebar entry when off, for the same reason.

Neither can express "every customer sees that Functions exists; the two who pay for it can use it".
That sentence is the product decision behind a paid tier, and it is a **third** axis: not who you
are, and not what this server has switched on, but what this workspace is entitled to.

So the hidden-not-disabled rule changes — for entitlements only, and on purpose. A capability the
caller does not have stays hidden, because there is nothing to sell them; an entitlement they do not
have is shown locked, because the whole point is that they can see it and ask for it. The precedent
for the locked control already exists in the codebase, in `Views/Templates/Deploy.cshtml`:

> Disabled rather than hidden: the person came here to deploy and needs to see that the button
> exists and why it will not work. The server refuses this case anyway — a disabled button is a
> courtesy, never the check.

That last clause is the rule this whole design is built on. **Every lock is enforced server-side.**
The grey control is decoration over a refusal that would happen anyway.

---

## 2. Entitlements

### 2.1 The catalogue

Features are a hard-coded catalogue (`Harbora.Domain.Features.PlatformFeatures`), not rows an
operator invents. A feature key only means something because code reads it, exactly like
`Capabilities`. The catalogue entry carries the bilingual name, the one-line pitch shown on the
locked page, and the **default state** for a workspace nobody has decided about.

### 2.2 The three states

| State | Sidebar | Controls | Server |
|---|---|---|---|
| `Enabled` | normal | normal | allowed |
| `Locked` | greyed, lock icon, links to the locked page | greyed, `disabled` | **refused** |
| `Hidden` | absent | absent | **refused** |

`Hidden` exists for the feature an operator does not sell at all and does not want advertised;
`Locked` is the one this work is for. A fourth value, `Inherit`, exists only on stored grants and
never as an answer.

### 2.3 Resolution

Two scopes, resolved in order, last non-`Inherit` wins:

1. **catalogue default** — what the product ships with,
2. **plan grant** — `FeatureGrant` with `Scope = Plan`, keyed to the workspace's plan,
3. **workspace override** — `FeatureGrant` with `Scope = Workspace`.

The resolution itself is a pure static function (`FeatureAccess.Resolve`) over three nullable
states, so the interesting behaviour is testable without a database. The verdict carries *which*
level decided it, because "why can this customer not use it" is the question the owner actually
asks.

Two facts the storage has to respect, both of them scars from this codebase:

- `FeatureGrant` gets **no global workspace query filter**. It is platform configuration read by
  sessionless work; a tenant filter here would make a background invoker read an empty table and
  report success (`tenant-filter-kills-sessionless-work`).
- The state enum is persisted as an int. Values are frozen and appended, like every other enum in
  `Domain/Common/Enums.cs`.

### 2.4 Enforcement

`IFeatureGate.EvaluateAsync(workspaceId, key)` is the single answer everything else asks.

- **Controllers**: `[RequireFeature(PlatformFeatures.Functions)]` — a filter that turns anything but
  `Enabled` into a redirect to `/features/{key}` for a page request, and `403` with a JSON reason
  for an API/AJAX one.
- **Views**: an injected `FeatureBadge` helper renders the greyed control and its reason, so the
  lock looks the same everywhere and nobody hand-rolls an `opacity-50`.
- **Sidebar**: `NavItem` gains a `Feature` key. `Locked` renders greyed with a lock; `Hidden` is
  filtered out exactly like a missing capability.

### 2.5 Who sets it

**Platform → Features** (`Capabilities.PlatformManage`): a grid of features × plans, and below it
the per-workspace exceptions with the reason and who set it. The same control appears on a tenant's
own page, because that is where an operator is standing when a customer asks.

---

## 3. Functions

### 3.1 Shape

The Azure model, chosen by the owner: a **Function App** is one container hosting **many
functions**. It is billed as one nano-sized app-hour, so twenty ten-line functions cost one
resource, not twenty.

A Function App *is* an `App` — `SourceType = InlineCode` (appended, value 7) with
`FunctionRuntime` naming the language. That is not a shortcut; it is what makes deploy history,
rollback, live logs, env vars, domains, quotas, metering, the scheduler and per-tenant network
isolation work on day one instead of being re-implemented. Each function is a `FunctionDefinition`
row: name, trigger, route/cron/event key, and the code itself.

### 3.2 Runtimes

Three host images, one contract. Publishing generates a complete build context — host files, the
user's code, and a `Dockerfile.harbora` — and hands it to the existing
`DeploymentPipeline.BuildFromSourceAsync`. No new build machinery.

| Runtime | Host | User writes |
|---|---|---|
| C# | ASP.NET minimal API, compiled by `dotnet publish` in the build | a `static class Function` with `Run(FnRequest, FnContext)` |
| JavaScript | Node 22, no dependencies | `export default async function (req, ctx)` |
| Python | 3.12 stdlib `ThreadingHTTPServer`, no pip | `def run(req, ctx)` |

The generated dispatcher names each function explicitly — no reflection, no directory scanning — so
a mistake is a **compile error in the build log**, at deploy time, which is exactly where Azure puts
it too. No user code runs on the panel; the panel only ever writes text files.

### 3.3 Triggers

All three arrive at the same door: `POST /__harbora/invoke/{function}` with a per-app secret. One
runtime contract, three ways of knocking.

- **HTTP** — Traefik routes the app's domain to the host, which dispatches on the function's route.
- **Cron** — a scheduler reusing the existing `CronSchedule` parser calls the host over the private
  network. The panel keeps the schedule; the host stays a plain server.
- **Event** — `IFunctionEventBus.PublishAsync` from the places that already know something happened:
  deployment finished, alerts raised (`AlertEvent` covers crashes, backup failure, thresholds, low
  balance), git webhooks, workspace/member changes.

Every invocation writes a `FunctionInvocation` row — trigger, status, duration, error — because a
function that silently stops firing is the failure mode this feature would otherwise have.

### 3.4 What is deliberately not built

- **No scale-to-zero, no per-request billing.** The host runs like any other app and is metered by
  the hour, which is what `docs/overhaul/03-feature-matrix.md` already decided for a self-hosted
  single-VPS platform. Cold-start-per-request would need a router that starts containers, and that
  is a different project.
- **No package installs.** Each host ships what its base image has. A function needing npm or NuGet
  is an ordinary app, and the panel says so.
- **No secrets in code.** Functions read `ctx.env`, which is the app's existing environment
  variables, decrypted at container start like every other app's.

---

## 4. Verification

- Resolution, catalogue integrity, and grant precedence: unit tests, no database.
- Generated project text for all three runtimes: golden-ish assertions on the produced files —
  these run on a machine with no Docker, which is the machine this is written on.
- Trigger routing and subscription matching: unit tests over the pure matcher.
- Controller/filter behaviour: the existing web test patterns.
- End to end (a real container being built and hit) is a **server** step, not a local one, and is
  written up as such rather than claimed.
