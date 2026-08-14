# Every app that serves HTTP has an address, and the panel can prove it

**Date:** 2026-08-14 · **Status:** approved, ready for an implementation plan

This is sub-project **B1** of the panel improvements. Sub-project A (the detail-page shell) merged as
`2009b81`.

---

## The request, and what exploring it changed

The ask was "a guaranteed internal link per app". Reading the code changed what the work is.

**The capability already exists. It exists four times.** An app can be created through four doors,
and each door decides for itself whether the app gets a hostname:

| Path | Rule it applies |
|---|---|
| `AppsController.Create` (`AppsController.cs:287-299`) | `ServicePlan.HostFor` · checks kind · checks reserved hosts · **skips silently on collision** |
| `TemplateDeploymentService.cs:262-269` | builds `$"{slug}.{root}"` **by hand** · no kind check · no reserved-host check · no collision check |
| `PreviewEnvironmentService.cs:204-214` | `PreviewNaming.Host` — a third rule, keyed on the branch |
| `EnvironmentCloner.cs:230-306` | **assigns nothing.** A cloned app has no address at all |

So whether an app has a URL today depends on which door it came through. That is the defect behind
the request: not a missing feature, but one written three times and forgotten once.

`EnvironmentCloner` reads `a.Domains` in two places, which looks at a glance like it handles them.
It does not — both are `a.Domains.Count`, a number for the quota preview. The copy is built and
added with no `DomainName` on it.

**A second consequence, worth stating separately:** the assignment happens **only at creation**, and
only if `platform.root_domain` (`Setting.cs:37`) was already set at that moment. An operator who sets
the root domain afterwards leaves every existing app permanently without an address, and nothing in
the panel says so.

---

## Decomposition

The owner asked for both a public address and a private one. They are different subsystems and ship
separately:

| | Sub-project | What it delivers |
|---|---|---|
| **B1** | **The guaranteed public address** ← *this spec* | Every HTTP-serving app has a URL, through every door, and the panel shows it |
| B2 | The private address | A stable in-network name so app A can reach app B |
| B3 | Pod specifics | Resources, origin and version, placement, health and uptime on Overview |

**B1 is first** because it is pure panel work — no deployment pipeline, no Docker layer — so it
carries the least risk for the most immediate value.

**B2 is separate** because it changes the deployment pipeline. App→database already works this way:
a managed service has a stable `ContainerName` and apps reach it inside the environment network. App
→app does not, because an app's container is `harbora-{slug}-{number}`
(`DeploymentPlanning.cs:19`) — the deployment number is in the name, so it changes on every deploy.

**B3 is separate** because two of its four groups have no data today. `ContainerInfo`
(`IDockerEngine.cs:129`) carries `State` and a `Status` string but no restart count and no start
timestamp, and `Deployment` stores `ImageTag` with no digest. B3 begins by adding that capability to
both the local and remote engines, which is not panel work at all.

---

## Architecture: one rule, one place

`ServicePlan.HostFor` becomes the **only** code that decides what hostname an app gets, and all four
paths call it. The three parallel implementations are deleted.

The rule, in order: **kind → reserved host → collision → record.**

**One caveat, because "one rule" would otherwise be read too literally.** Branch previews legitimately
need a different *name shape* — `PreviewNaming.Host` keys on the branch, and it should keep doing so,
or two previews of the same app would collide by construction. What that path must stop doing is
deciding for itself about kind, reserved hosts and collisions. So the shared rule takes the candidate
name as an input: the preview path supplies its own, the other three supply `{slug}.{root}`, and all
four get the same four checks applied to it. The name differs; the guarantee does not.

**Why one place rather than four correct copies.** Four copies were the starting condition, and three
of them were already wrong in different ways. A guarantee with four implementations is not a
guarantee; it is a coincidence that has held so far. The census test below is what keeps a fifth door
from opening.

---

## Four decisions, each a judgement rather than a detail

### 1. "Every app" means every app that serves HTTP

`ServicePlan.CanHaveDomains` already restricts this to kinds with public traffic. A Worker, a Cron, a
Private service and a ReleaseTask have no inbound traffic and must not get an address.

Their pages must **say why** — "this service takes no inbound traffic, so it has no address" — rather
than showing an empty slot. An unexplained gap is the promise-without-a-feature that sub-project A
refused twice: once for the app Backups tab, once for the internal-link placeholder the plan
deliberately did not build.

### 2. A collision must make a sound

Today, if `myapp.apps.example.com` is taken, `AppsController.cs:299` skips the insert and the app is
created with no address and no message.

The address gets a short discriminator instead — `myapp-k3f.apps.example.com` — and the person is
told the name was taken and what they got instead. Refusing to create the app over a name clash is
the worst of the three options: it blocks work over something the panel can resolve itself.

### 3. Backfill is a control the operator presses, not something that happens to them

Apps with no address — clones, and anything created before the root domain was set — can be given
one. **This is the only part of B1 that changes live Traefik routing**, so it is an explicit action in
the panel, not an automatic sweep. The operator sees which apps would be affected and what each would
be called, then decides.

The same control is what the operator reaches for after setting the root domain for the first time.
Setting that value does not silently rewrite anything.

An app that already has a custom domain is never touched. That is the property most worth testing:
the failure that would matter here is not "an app got no address" but "an app that had one lost it".

### 4. The address appears on Overview, clickable and copyable

The place sub-project A deliberately left empty, now filled by the thing it was left empty for.

---

## Testing

**The census.** Every path that calls `db.Apps.Add` must have gone through the address rule. This is
the test that stops a fifth door from writing its own version — the exact failure that produced this
sub-project. It follows `DetailTabCensusTests`, which reads route templates off the source rather
than a hand-kept list, for the reason its docstring gives: a hand-kept list is checked by a reviewer
noticing, and a reviewer noticing is the step a real gap slips past.

Then, one test each for the decisions above:

- A collision produces a discriminated address **and** a message naming what happened.
- A Worker gets no address, and its page states the reason.
- The backfill control leaves an app's existing custom domain untouched.
- The backfill control gives an address to an app that has none.
- Setting the root domain changes no existing app by itself.

**On assertions that pass for the wrong reason.** This suite's recurring defect is a check that
reports success for work it never did. The panel renders **Persian by default** in tests, so an
assertion on an English label never matches; assert on route fragments, ids or the exact untranslated
host string. And an assertion that a page "contains the address" can be satisfied by an unrelated
mention elsewhere on that page — assert against the element that is supposed to carry it.

---

## What B1 is not

The private in-network address (B2) · pod specifics (B3) · anything about certificates, which
`DomainName.SslEnabled` and the existing ACME path already handle · the routing guide, which belongs
to sub-project G.

**No change to how custom domains are added.** That form works and is out of scope.

---

## Risk

The backfill writes `DomainName` rows, and a `DomainName` row is what Traefik routes on. The failure
that matters is not a missing address — it is an app that had a working custom domain and stopped
serving on it. Making the backfill an explicit, previewed control rather than an automatic sweep is
the mitigation; the test that an existing custom domain survives is the proof.

The production server carries three apps, so the blast radius is small — but small is not the same as
verified, and the preview is what makes it verified before the button is pressed.
