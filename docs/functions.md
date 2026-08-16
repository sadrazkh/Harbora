# Functions

Code written in the panel that runs on your own server. No repository, no Dockerfile, no `git push`.

A **function app** is one container hosting **many functions** — the Azure shape. It is billed as one
app-hour at whatever size you chose, so twenty ten-line functions cost one resource rather than
twenty. Under the surface it is an ordinary Harbora application, which is why it has deploy history,
rollback, live logs, environment variables, a domain, quotas and metering on the day it is created.

---

## Creating one

**Functions → New function app** → name, language (**C# / JavaScript / Python**), size → *Create*.

You get a working `hello` function immediately, because an app with no functions cannot be published
and a create button that lands you on a page whose only button fails is a trap.

Then: edit the code → **Save** → **Publish**.

> **Save is not publish.** Saving stores the code; publishing rebuilds the image and releases it.
> The page says *unpublished changes* until you do, and publishing rebuilds **every** function in the
> app together — one image, one release.

---

## Writing a function

Each language has one entry point. The editor is pre-filled with it; it compiles and runs as it
stands.

### C#

```csharp
public static class Function
{
    public static Task<FnResponse> Run(FnRequest req, FnContext ctx)
    {
        var name = req.Query.TryGetValue("name", out var n) ? n : "world";
        return Task.FromResult(FnResponse.Json(new { hello = name }));
    }
}
```

`FnRequest` — `Method`, `Path`, `Query`, `Headers`, `Body`, and `Json<T>()`.
`FnResponse` — `Text(...)`, `Json(...)`, `Empty(...)`, or the record directly.
`FnContext` — `FunctionName`, `Trigger`, `Env`, `Event`, `Log(...)`.

The file is wrapped in a namespace of its own, so two functions may both declare `Function`. Your
`using` directives are kept.

### JavaScript

```javascript
export default async function (req, ctx) {
  return { hello: req.query.name || 'world' };
}
```

Return a string (200 text), an object (200 JSON), or `{status, headers, body}`. `ctx.env` is the
app's environment; `ctx.log(...)` writes to the app log.

### Python

```python
def run(req, ctx):
    return {'hello': req['query'].get('name', 'world')}
```

`req` is a dict — `method`, `path`, `query`, `headers`, `body`. Return a string, a dict, or a dict
with `status` / `headers` / `body`. `ctx['log'](...)` writes to the app log.

**A mistake is a build error.** The generated host names every function explicitly, so a wrong
signature or a typo fails `Publish` with the compiler's own message in the deploy log — not a 404 at
three in the morning from a function that was never registered.

---

## Triggers

| Trigger | Runs when | Configure |
|---|---|---|
| **HTTP** | someone requests the app's domain | *Route* — blank means the function's own name |
| **Schedule** | a five-field cron expression is due | e.g. `0 3 * * *` |
| **Event** | something happens on the platform | pick from the list |

Two HTTP functions cannot share a route, and the panel refuses the second — the dispatcher matches
the longest route, so a duplicate would silently give every request to one of them. A single HTTP
function also answers `/`, because one function and a 404 on the root reads as broken.

Events available: deployment succeeded / failed, application crashed, backup failed, disk warning,
threshold breached, certificate expiring, low balance, git push, git tag, workspace created /
suspended / resumed, member invited / joined. An event never crosses a workspace boundary.

Schedules and events reach the container over the private network with the app's own invoke secret;
they never go out through the proxy. **Run now** on the editor page uses that same door, so testing
by hand tests what will happen at 03:00 — including the fact that it runs the *published* code, not
what is currently in the editor.

Every call the platform makes itself is recorded on the function's page: trigger, status, duration,
and the error if there was one. Rows are kept for `Retention:FunctionInvocationDays` days (30 by
default).

---

## Secrets and configuration

Use the app's **Environment Variables** (encrypted at rest, like every other app's) and read them
from `ctx.env` / `ctx['env']`. Do not put credentials in the code: the code is stored in the
database in plain text so it can be shown in an editor.

`HARBORA_FN_SECRET` is injected by the platform and cannot be edited — it is what lets the scheduler
prove a call came from the panel.

---

## What functions are not

- **They do not scale to zero, and they are not billed per request.** The host runs like any other
  app and is metered by the hour.
- **They do not install packages.** Each host has what its base image ships (`node:22-alpine`,
  `python:3.12-alpine`, .NET 10). A function that needs npm or NuGet is an ordinary application —
  deploy it from a repository.
- **They are not a sandbox for untrusted code.** Functions in one app share a process, and the
  isolation between customers is the container and the per-tenant network, exactly as for every
  other app.

---

## Who can use them

Functions are an **entitlement**: the platform owner decides which plans and which workspaces have
them, at **Platform → Features**. A workspace without the entitlement still sees Functions in the
sidebar, greyed with a lock, and a page explaining who can switch it on.

Taking the entitlement away stops the schedules and events of code that is **already deployed** — not
just the create page.

---

## Not yet proven

The generator, the routing, the triggers and every refusal are covered by tests. What no test on a
machine without Docker can cover is `docker build` producing the three host images and a container
answering. Until a publish has succeeded on a real server, treat the runtimes as unproven — and if
the first one fails, the build log has the reason.
