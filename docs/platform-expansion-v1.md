# Harbora platform expansion v1

Branch: `feature/harbora-platform-expansion-v1`, **merged into `master`**. Everything below is
shipped behaviour, not a proposal — the sentence saying otherwise was written before the merge and
outlived it.

This document records what was added, what was deliberately not added, and the risks that remain.
It is a record of one phase and is not kept current: the test count and the "not built" list below
were true when the phase closed. Where a later phase has changed one of them, it says so inline.

---

## 1. Ready-app catalogue with versions

### Endpoints and pages

| Path | Purpose |
|---|---|
| `/templates` | Catalogue, now rendering real logos |
| `/templates/{id}` | One template |
| `/templates/{id}/deploy` | Deploy form |

### Data

`AppTemplateVersion` and `AppTemplateAsset` sit beside the existing `AppTemplate`.

The image is pinned by **digest**, not tag. A tag moves, so two people who both installed
"PostgreSQL 16" a month apart are running different software with nothing recording the difference.
`VersionSelection.PinnedImage` deliberately omits the tag from the reference so nobody later
"corrects" it and changes what runs.

Two independent axes:

- **Lifecycle** — Recommended / Stable / PreviousStable / Legacy / Deprecated / Unsupported.
  How old a version is.
- **Publication** — Draft / Published. Whether an operator has decided to offer it.

A registry gaining a tag is not an operator deciding their customers should run it, so nothing is
auto-published.

### Refusals

`VersionSelection.Refuse` is checked again at deploy time, not only when the list is drawn. The list
a page rendered is not a permission — a version can be withdrawn between render and submit, and
somebody with an old link or a scripted call asks for the id directly.

### Choosing a version, and the bug it uncovered

Until this phase **not one of the eight ready apps could be installed, and none of them appeared
in the catalogue.**

Their manifests carried a port, variables, volumes and links — and no `image`. A manifest with no
image, no `"source": "git"` and no managed `service` fails to parse, and a template whose manifest
fails to parse is dropped from the catalogue page by `.Where(i => i is not null)` and refused by the
deploy service. The entries were seeded, sat in the database, and were nowhere on screen. Every
existing test passed: the suite checked digests, logos, licences, lifecycles and the Docker socket,
and never once parsed a manifest it had built.

The manifest now names `repository:tag` so it parses and so the catalogue can describe the app; the
chosen version's digest replaces it at deploy time.

That fixed the catalogue and left a second fault underneath it: **every pinned digest was invented**.
`sha256:0f8b1c2d3e4f5a6b…` is a hex pattern typed by hand — right length, right alphabet, right
prefix, so every test passed, the catalogue rendered and the deploy form offered the app, and the
pull would have failed with "manifest unknown" on a page that had already promised it. Found by
querying the real registries from the server after deploying, not by any test. All eleven digests
were re-resolved against the registry (and one MinIO tag corrected — `RELEASE.2024-10-13` is not a
tag that exists; the real one carries a timestamp). `No_digest_is_a_hand_written_placeholder` now
looks for structure a hash does not have, at several lags, because the placeholders interleaved two
counters and a check on neighbouring characters saw nothing wrong. Both are asserted, including that they name the
same version — a page showing one version while deploying another is worse than either alone.

The selector itself is on the deploy form: offerable versions best-first, upgrade notes and migration
warnings for whichever is selected, and the exact image that will be pulled shown in the resource
panel. `App.TemplateVersionId` records what was installed, because a digest answers "what is running"
but not "which of our versions is that" — and without it nobody can find who is on the release being
deprecated.

A template with versions where none is offerable is refused rather than falling back to the
manifest's tag: the operator published versions precisely so that tag would stop being what customers
get. A template with **no** versions still deploys exactly as before.

### Architecture

`VersionSelection.RunsOn` was written in an earlier phase and had nothing to compare against — the
control plane never learned what a node runs, so the check never fired. `HostInfo.Architecture` is
now reported by the Docker adapter, normalised (`x86_64` → `amd64`) because Docker uses kernel names
and image manifests use Go's, and stored on `Server`.

Null means unknown and filters nothing. Defaulting to amd64 would refuse arm64 images that would have
run. A report that omits the field does not erase what an earlier one recorded — `ReportedFact.Keep`
— or the value flickers and a deployment is refused or allowed depending on which tick ran last.

### Discovering newer versions

`/admin/templates`, behind `platform.manage`. A daily job asks the public registries whether newer
tags exist and records what it finds as **drafts**.

**Off unless an operator turns it on** (`templates.registry_discovery`). It makes outbound requests
from this server to a third party, spending their anonymous rate limit — that is a decision about
somebody else's infrastructure and not a default. Off means no request at all, not a request whose
result is discarded, and a test asserts the registry is never called.

**Nothing is ever published automatically.** A tag appearing upstream is not an operator deciding
their customers should run it. Discovered versions arrive as Draft and Stable — never Recommended,
because exactly one version per template may be recommended and which one is a judgement about
customers, not about tags.

A registry is mostly not releases: `latest`, `main`, commit hashes, release candidates, and the same
release published as `17`, `17.1` and `17.1-alpine`. So a tag becomes a candidate only if it is a run
of digits, is not a pre-release, has the **same shape** as the newest version already stored — same
depth, same variant — and is strictly newer than all of them. At most five per template per run: a
job that adds two hundred rows the first time it runs is a job somebody turns off, after which
nothing is discovered again.

A tag whose digest cannot be resolved is not stored. A version without a digest is refused at deploy
time anyway, so the row would look like an option and fail every time it was chosen.

The registry host comes from a stored template field and is then called by our own server, so it is
checked against an allowlist — `docker.io`, `ghcr.io`, `quay.io`, `registry.k8s.io`,
`mcr.microsoft.com`. So is the token realm a registry names in its own 401, or a challenge response
becomes an instruction telling our server where to send requests next.

**The page is what makes the job worth having.** A job writing draft rows nobody can see is a job
that appears to work and changes nothing. The page lists every version drafts-first, publishes and
withdraws, sets lifecycle (moving Recommended off whichever version held it), and flags a template
where nothing is offered — which looks identical to a working one on the catalogue page until
somebody tries.

### Logos

22 SVG marks in `wwwroot/img/apps`, generated by `scripts/generate-app-logos.py`. Stored here rather
than hotlinked: a hotlink tells a third party who is looking at the panel, and breaks when they move
the file. Each asset records its source and licence, because "where did this come from" is asked
long after whoever added it has gone.

---

## 2. External database access

### Data

`DatabaseAccessGrant`, `DatabaseAccessAudit`.

The **grant row is the permission**. The credential and tunnel are derived from it and torn down
with it. The reverse design — where the credential is the permission — leaves a working password
behind every time a cleanup step is missed, and the miss is invisible until somebody uses it months
later.

### Rules

- Windows: 15m / 1h / 6h / 24h, plus custom, bounded at both ends.
- Extension is capped in count **and** cumulatively, so "temporary" cannot become permanent by
  repetition.
- An unparseable allowlist entry never matches. A typo must lock the customer out — which they
  report immediately — rather than open the database, which nobody reports.
- The sweeper ticks every minute. A fifteen-minute grant closed twenty minutes late was open a third
  longer than the person was told.

### Credentials

PBKDF2, per-credential salt, constant-time compare. The password is returned once and never stored,
so a leaked copy of Harbora's own database is not a list of live logins into customer databases.

### Node boundary

`INodeAgentClient` is narrow and carries no plan, permission, TTL or billing logic — those are
control-plane decisions. `FakeNodeAgentClient` stands in until the real agent ships. It refuses
duplicate logins the way a real database would, and **warns on every call**, so an unconfigured
production deployment cannot quietly report tunnels it never made.

It also answers `IsSimulated`, and that single answer is what closes the worst hole this feature
could have had. Without it the page would issue a username, a password and a connection string
pointing at `gateway.invalid` — Harbora's records showing a healthy active grant while the customer
gets a name-resolution error and reports a broken database. The default on the interface is `false`,
so a real agent needs no change to answer correctly and a client that forgets to answer is treated as
real rather than quietly disabling the feature.

### The page

`/databases/{id}/access`, behind `databases.manage` for anything that changes.

Every action **returns the page instead of redirecting**. Redirect-after-post would have to carry the
password through TempData, and TempData here is cookie-backed — that would write a live database
password into the customer's cookie jar, where it outlives the page they were told to copy it from.

`ExternalAccessAvailability` is asked again when the form is submitted, not only when it was drawn:
the database may have stopped, or the agent gone away, in the minutes since somebody opened it. The
simulated-agent refusal is checked first, because a person told "start the database" would start it,
try again, and still get nothing.

Revoking is the one action that works even with a simulated agent. A grant that cannot be closed is
worse than one that was never issued.

Closed grants stay listed. "Who opened this database in March, and for how long" is a question that
gets asked, and a list showing only what is open cannot answer it. Every row says whose it is, when
it ends, how many extensions it has left, and — in warning colour — when its allowlist is empty and
it can be reached from anywhere.

---

## 3. Simple and Advanced modes

Simple is a smaller view of one panel, not a second panel. Specialist destinations are hidden from
the sidebar and their routes stay live, so a bookmark or a runbook link still works.

- The preference is stored on the **account**, not in local storage: a choice made on a laptop
  should hold on a phone.
- An explicit choice always beats every default, or a platform default nobody can see quietly
  overrules a person's setting the next day.
- Owners and admins who have never chosen get Advanced — the specialist controls are their everyday
  tools.
- The migration writes Advanced onto every **existing** account rather than leaving it null.
  Otherwise they meet a reduced interface on upgrade, which reads as "features were removed".

### Inside a page

Simple mode **folds; it never removes**. Specialist blocks become a collapsed disclosure with a
label saying what is inside, and every control stays in the markup one click away. A form that
quietly drops fields between modes is one where the settings a person gets depend on a preference
they set weeks ago, with nothing on screen saying so.

`PanelSections.StartsOpen` makes the decision once, and the load-bearing half of it is not about the
mode: **any rejected form opens every specialist block**. A block folded over a field the server just
complained about is a form reporting an error about a control the person cannot see, and no amount of
re-reading the page will show it to them. All blocks open rather than only the offending one —
mapping a model-state key to the block that holds it is a mapping that goes stale the first time
somebody moves a field, and being wrong means the error is invisible.

Two places use it today: the application form's runtime and build settings, and the version picker on
the deploy form. The database form was left alone — it has no specialist block worth folding, and
inventing one to demonstrate the feature would make that page worse.

Folding takes away the choice, not the fact: with the version picker collapsed, the page still states
in plain text which version will install. A test asserts that, another asserts every advanced field
is still rendered, and a third sweeps the views so no folding block can decide for itself whether to
open.

---

## 4–5. AI as a service

### Endpoints

| Method | Path |
|---|---|
| GET | `/v1/models` |
| POST | `/v1/chat/completions` |
| POST | `/v1/responses` |
| POST | `/v1/embeddings` |

OpenAI-shaped so existing client libraries work unchanged. Authentication is
`Authorization: Bearer har_…`.

### The customer never holds a provider token

That is the point of the gateway. Revoking a Harbora key really revokes access; a provider token
handed to a customer keeps working wherever it was pasted and keeps billing whoever owns it.
Requests are made server-side, so the provider sees Harbora's infrastructure rather than the
customer.

### Routing

Health-aware weighted least-load. Priority first (an operator's preference must not be overruled by
load), then weight (more headroom carries proportionally more), then in-flight load (a burst is
shared, not queued). Weight zero is a last-resort marker treated as infinitely loaded rather than
dividing by zero.

Circuit opens after five consecutive failures and lets **one** request through after a two-minute
cooldown, so recovered capacity is not lost for ever.

### Failure handling

The distinction that matters is credential versus request. Retrying a bad request across every
credential burns all of them and returns the same error more slowly.

Nothing already streamed to the customer or already charged for is retried — they have seen part of
an answer, and a second attempt duplicates or contradicts it.

### Rate limits

`RequestsPerMinute`, `TokensPerMinute`, `RequestsPerDay` and `ConcurrentRequests` were stored, priced
and shown on the plan page for two phases while nothing enforced them. A limit that exists everywhere
except in the code path is worse than no limit: the page promises sixty requests a minute, the
customer sends six thousand, and the operator finds out from the provider invoice.

**Sliding windows, not fixed ones.** A fixed minute lets a caller send the whole allowance at
11:59:59 and the whole next allowance at 12:00:00 — twice the limit, one second apart — and it passes
every test written against a fixed window. A test sends the burst one second before the boundary
specifically to catch that.

**Longest window first.** A caller who has exhausted both the day and the minute and is told to retry
in sixty seconds will retry in sixty seconds, fail, and read the gateway as broken rather than as
limited. Concurrency is checked last and reports one second, because it clears as soon as a request
in progress finishes.

**A limit of zero blocks.** An administrator who clears the field breaks their customers, who say so
within the hour; one who accidentally removes every limit is told by the provider a month later. The
token limit is the exception — the request limits already bound it, and a plan meaning to leave it
off would otherwise be unusable.

**Counted on the way in.** A limiter that counts finished requests lets a caller open a thousand at
once: none has finished, so none is counted. Tokens are attached to that same event afterwards, not
added as a second one, since adding would count every request twice and halve the real allowance.

Every refusal is a 429 with `Retry-After`, never 402 or 403: client libraries retry a 429 with
backoff and give up on the others, and a limit that reads as "you are not entitled" is escalated as a
billing problem.

**In memory, per process.** Stated plainly rather than hidden behind an abstraction: run two control
planes and each enforces the limits separately, so a tenant can send twice the allowance. Harbora
runs one today, and only `AiRateLimiter` changes when that stops being true. It does not replace the
period quotas — those are durable and survive a restart; these do not, and a restart forgives the
last minute of traffic, which is the right trade for a limit whose job is smoothing bursts rather
than counting money.

### Metering and privacy

`AiUsageRecord` holds **no prompt and no response**. Storing them would make it the most sensitive
table Harbora has: every customer's data in one place, retained as long as billing records, readable
by anyone with database access. A test asserts the type has no such column.

If content logging is ever wanted it must be a separate opt-in feature with its own retention and
consent — not a column added quietly here.

Client disconnects mid-stream are recorded and still charged, because the provider charged us for
what was produced.

### SSRF

A provider base URL is typed by an administrator and then called by our server. HTTPS only;
loopback, private ranges, link-local and `.internal` are refused. `169.254.169.254` — cloud
metadata — is the highest-value target and is covered, as are private IPv6 ranges.

---

### Administration

`/admin/ai`, behind `platform.manage`. Providers, their tokens, the model catalogue, plans and the
plan→model mapping.

A token is **write-only**. It can be added and it can be replaced; it can never be read back. A form
that renders the current token so it can be "edited" puts it in the browser cache, in the next screen
recording and in every support screenshot it appears in — and the field looks like a convenience,
which is why a test asserts the view never mentions `EncryptedToken` and the controller never calls
`Unprotect`.

Rotation clears the failure count, the failure reason and the rate-limit parking, because the
failures belonged to the old token. Left in place, the administrator's fix appears to change nothing:
the new token stays out of rotation behind the old one's open circuit.

The plan→model form replaces the whole set. An add-only form cannot take a model back off a plan, so
a mistake there would be permanent from the interface — and an empty submission has to mean "none",
since the browser sends no field at all for a checkbox group with nothing ticked.

The base URL is validated where it is **stored**, not only where it is called. Stored, it becomes a
request our own server makes on somebody else's instruction.

Health is read from what the router wrote, never probed on render: a page that probes reports the
health of the page load rather than of the traffic.

In the sidebar it is a separate destination from `/ai` — administering the service is a different job
from using it — capability-gated and Advanced-only, with the route live for anyone in Simple mode who
follows a link.

---

## Migrations

All additive, all with a working `Down`.

| Migration | Adds |
|---|---|
| `ReadyAppVersioning` | `AppTemplateVersions`, `AppTemplateAssets` |
| `DatabaseExternalAccess` | `DatabaseAccessGrants`, `DatabaseAccessAudits` |
| `PanelModePreference` | `Users.PanelMode`, backfilled to Advanced |
| `TemplateVersionPinning` | `Apps.TemplateVersionId`, `Servers.Architecture` |

No migration was needed for registry discovery: `AppTemplateVersion.DiscoveredAt` already existed and
had never been written by anything but the seeder.
| `AiCore` | providers, credentials, models, plans, plan-models, subscriptions, keys |
| `AiUsageMetering` | `AiUsageRecords` |

---

## Not built, and why

**Docker Workspace ships as a draft and cannot be deployed.** A workspace template that mounts
`/var/run/docker.sock` hands the node to whoever deploys it. The rootless runtime is not built, so no
template was shipped rather than an unsafe one. A test asserts no manifest mounts that socket.

**The TCP gateway does not exist.** Only its contract, and a fake that returns a placeholder host.
Production database tunnelling is not real until the node agent ships.

**The gateway has not been exercised against a real provider.** Without a provider key it is testable
only up to the network boundary. The same is true of `ContainerRegistryClient`: the token dance and
the digest header are written to the OCI distribution spec and covered by tests up to the HTTP
boundary, but no run in this environment has spoken to Docker Hub.

> **Still true, and now said on the product rather than only here.** Nothing has since made a
> request from this codebase to a live model provider, so the last hop remains the one thing no test
> covers. Rather than leave a full user-facing surface with no indication of that, the AI
> destination is **labelled *Preview* on its own page and hidden from the sidebar in Simple mode**
> (`NavigationMap`, `Views/Ai/Index.cshtml`). It is not disabled and no configuration flag was
> added: the routes answer in both modes, the gateway is untouched, and both marks come off together
> once one live round-trip has been made and recorded — **HARBORA-0054**, whose acceptance is
> exactly that. `AiAdminPageTests` holds the gate and the label so neither can be removed by
> accident.
>
> `ContainerRegistryClient` is the narrower case and has moved: the ready-app digests were
> re-resolved against the real registries from a server (`6ef8c2c`), which is a person doing it by
> hand rather than a test doing it, and it proved the client's answers are the registries' answers.

---

## Testing

1,550 tests. Mutation testing was run on every rule where a wrong answer is silent — version
selection and resolution, the ready-app catalogue, host architecture, database access policy,
credential routing, failure classification, pricing, usage parsing, plan access, API keys and the
administration controller, external access availability, grant scoping, the gateway's rate limits and
registry discovery and Simple-mode folding — for 167 mutants, all caught. The `scripts/mutate-*.py`
files hold the last six runs: changes that compile, leave the
screen looking correct, and each break something nobody would notice.

`UiBaselineTests` guards the approved interface: design tokens present in both themes, every view's
tags balanced (an extra `</div>` closes the layout early and pushes content outside it; `<details>`
joined the list when Simple mode started folding blocks, because an unbalanced disclosure swallows
the rest of a form into a collapsed section and reads as "those settings were removed"), the retired
colour ramp absent, and every filled accent control carrying an explicit text colour.
