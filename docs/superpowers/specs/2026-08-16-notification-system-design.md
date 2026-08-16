# One attempt, and the record of it is a column the next success erases

**Date:** 2026-08-16 · **Status:** decomposition proposed; five sub-projects, none ready for a plan
until the owner answers §7

Phase 9 of `docs/product-audit/17-implementation-roadmap.md` — backlog items **0036** (notification
system core) and **0037** (preferences, digest, quiet hours, weekly report). The requirements
document is `docs/product-audit/09-notification-system-plan.md`; its §6 is the acceptance bundle this
phase is judged against.

---

## 1. What exploring changed

The audit predates Phases 1 and 2, PAYG, ten sub-projects and the whole of Phase 6, and ten times in
this programme a capability the plan assumed missing already existed. The most recent miss was
volume browsing, absent only because the search was for `BrowseVolume` when the code was called
`AppData`. So the search here was by verb — *notify, deliver, send, queue, dispatch, retry, throttle,
dedup, prefer, mute, subscribe, digest, aggregate* — and by behaviour rather than by the name a
notification system would have had.

Four things came back that change the shape of the phase.

**First, and it decides the phase's priority: a notification can be lost, silently, today.** §3 sets
this out with evidence. Phase 6's spec could open by saying an alert fires end to end, and that was
true — what was not asked then is what happens when the channel says no. The answer is that the
message is gone, the only trace is one column on the rule, and the next successful send to that same
rule erases it. This is the codebase's defining defect in its purest form, and Phase 9 is therefore a
repair, not a feature.

**Second, the durable queue this plan is built on already exists and already retries — but it would
not retry the failure the audit names.** `JobExecutionPolicy.IsRetryable` accepts four transport
exception types (`JobExecutionPolicy.cs:111-125`), and `NotificationService` converts every non-2xx
HTTP response into an `InvalidOperationException` (`NotificationService.cs:211`). R-13's worked
example — "a transient 502 from Discord permanently loses a critical alert" — is exactly the case
that a naive `JobKind.NotificationDelivery` would still not retry. §7 Q4.

**Third, `AlertIncident` is most of doc 09's `NotificationEvent`, under a different name.** M4 shipped
a durable row carrying workspace, condition, subject, severity, title, body, opened-at, closed-at and
a closed reason (`AlertIncident.cs:17-57`), deduplicated on `(WorkspaceId, Condition, SubjectRef)` and
refreshed in place while open (`IncidentService.cs:37-45`). Doc 09 §4.1 asks for an entity with id,
type, severity, scope refs, payload, dedup key and created-at. Building a parallel `NotificationEvent`
beside it would be this codebase growing a second identity system for the same fact. §7 Q1.

**Fourth, `User.PreferredCulture` has existed all along** (`User.cs:25`, defaulting to `fa`), and
public registration with email verification shipped too (`User.EmailVerifiedAt:51`,
`AccountController.cs:363-385`). Doc 09's catalog lists EmailVerification as "future — no signup flow
yet"; that is stale. What is missing is not the field but any background sender that reads it —
`BillingTick.cs:1037-1047` states the position in as many words: a timer has no request culture and
its destination is a channel rather than a person, "so the honest answer is to say it twice".

---

## 2. What actually exists

The most valuable section of this document. Cited so the plan does not re-derive it.

### The seven things Phase 9 is asked for

| Capability | State | Evidence |
|---|---|---|
| **Delivery** — a message reaching a channel | **Built, four channels, and hardened** | `NotificationService.DispatchSafe:134-166`; Telegram `:215`, Discord `:226`, Webhook `:237`, Email `:255`. A non-2xx is a verdict rather than discarded (`EnsureAcceptedAsync:196-213`); targets read case-insensitively after every channel silently failed for as long as notifications existed (`:31-38`); outbound URLs guarded (`:249-253`) |
| Delivery, the platform's own mail | **Built** | `PlatformMailer.SendAsync:78-90`. Config lives in the `Settings` table, not `appsettings` (`:36-69`); unconfigured **throws** (`:81-82`); a password that will not decrypt is downgraded to "not configured" (`:52-59`). No queue, no persistence, no retry — one `SmtpClient.SendMailAsync` |
| **Retry** — for a notification | **Absent** | `DispatchSafe` is one attempt inside a 10-second budget (`NotificationOptions.cs:17`, applied `NotificationService.cs:139-140`). No loop, no re-queue, no second attempt anywhere in the file |
| Retry — the machinery to do it with | **Built, and unused by this subsystem** | `Job.Attempts:88`, `Job.NextAttemptAt:105`; claim honours the backoff in SQL (`JobClaimQuery.cs:38-39`); the retry decision is `JobWorker.cs:260-277`; policy `MaxAttemptsFor:77-104` (**default 1**), `IsRetryable:111-125`, `BackoffFor:131-138` (1 min → 5 min → 30 min, then holds). **`JobKind` has eleven members and none of them is a notification** (`Job.cs:6-32`) |
| **Deduplication** | **Built three times, for three conditions, by three unrelated mechanisms** | In-memory `AlertThrottle.cs:16-36`, singleton at `DependencyInjection.cs:311`, whose sole key is `disk:{serverId}` (`MetricsCollector.cs:453`) · persisted per-rule `Alert.ThresholdFiredAt:53` via `ThresholdRule.MayRepeat:76-84` · balance-keyed `Wallet.LowBalanceWarnedAtBalanceMinor:67` (`BillingTick.cs:898-906`). A crash is deduplicated by a fourth thing entirely — the app status machine only raises on a transition (`MetricsCollector.cs:389`) |
| Deduplication for the other events | **Absent** | Nothing dedups a failed deploy or a failed backup, which is correct — each is a distinct attempt. Nothing dedups SSL either, which is **not** correct: `CertificateWatcher` runs on a 24-hour timer with a 2-minute startup delay (`:29,:32`) and holds no persisted marker, so a panel restarted twice in a day emails twice about the same certificate. Doc 09 §6 asks for at most one per host per day; that criterion fails today |
| **Per-user preferences** | **Absent — and there is no per-user anything** | No preference entity, no `UserSettings`, no notification DbSet (`HarboraDbContext.cs:40-150`). `Alert` is keyed to a workspace (`Alert.cs:8`) and its five toggles (`:17-23`) are the workspace's, not a person's. There is no `UserId` on `Alert`. `LowBalance` has no toggle at all and answers true for every rule, deliberately (`NotificationService.cs:111-129`) |
| Preferences the user does have | **Built, and unread by any sender** | `User.PreferredCulture:25`, `PanelMode:32`, `ShowQuickStart:40`, `ShowOverview:43` — all UI, all request-path only |
| **Digests** | **Absent, in every form** | No scheduled aggregation-and-send exists. Of the periodic services, `BillingScheduler.cs:18` queues work, `UpdateCheckService.cs:12-27` writes a banner, `DataRetentionSweeper.cs:89` deletes, `BackupDeliveryService.cs:34` sends **one message per finished artifact**, `CertificateWatcher.cs:90` **one message per breaching host**. `MetricRollups` aggregates, but only to draw charts |
| **Quiet hours** | **Absent, and the prerequisite is missing too** | Nothing suppresses by time of day. `TimeZoneInfo` appears nowhere in application code; the platform is UTC throughout. The only clock-hour setting in the codebase is `RetentionOptions.SweepHourUtc:100` |
| **Weekly report** | **Absent** | Nothing periodic composes a summary for a person. `BillingRun` is the nearest artefact and it is an operator row on `/billing-runs`, never mailed |

### What the delivery record actually is

| Part | State | Evidence |
|---|---|---|
| A row per attempted message | **Absent** | Every `DbSet` in `HarboraDbContext.cs:40-150` was enumerated. There is no outbox, no delivery log, no attempt table for anything human-facing |
| What is written instead | **Two columns on the rule, overwritten** | `Alert.LastAttemptAt:56`, `Alert.LastError:62`, written by `RecordAttemptAsync:172-184`, truncated to 400 chars (`:186-187`). **`LastError` is set to `null` on success** (`:177`) — a channel that failed a critical alert this morning and carried a test message this afternoon shows nothing wrong |
| The same shape, again | **Built for backup artifacts** | `BackupDelivery.LastAttemptAt:40`/`.LastError:46`, `BackupDeliveryService.RecordAsync:188-200`. One attempt, 10-minute timeout, never retried |
| Where a failure is visible | **Built, for rules only** | The alerts list renders `LastError` (`Views/Monitoring/Index.cshtml:254-277`); the dashboard's Attention panel counts broken channels (`AttentionService.cs:58-65`); the Test button returns the server's own words (`AlertsController.cs:177-179`) |
| Where a failure is **not** visible | — | Every background raiser. `NotifyAsync` returns an `int` and its own doc says the number counts rules the message was *handed to*, not rules that took it (`NotificationService.cs:42-47`) |
| Transactional email | **No trace at all** | Password reset `AccountController.cs:179` (catch `:184-192`), verification `:372`, workspace invite `WorkspacesController.cs:157`, platform user invite `UsersController.cs:189`, SMTP test `AdminSettingsController.cs:171`. None is retried; none writes a row; the reset path deliberately tells the user "a link is on its way" whether or not it went (`:185-186`, anti-enumeration) |

### What Phase 6's M4 shipped, and precisely where it stopped

M4's own spec says: *"Not in M4: deduplication across channels, retry, digests, or a notification
centre. Those are Phase 9."* Confirmed, and the line falls here:

| Part | State | Evidence |
|---|---|---|
| A durable row for a thing that fired | **Built** | `AlertIncident.cs:17-57`; table `20260816183419_AlertIncidents.cs`; DbSet `HarboraDbContext.cs:107` |
| Opened, refreshed, resolved, acknowledged, expired | **Built** | `IncidentService.OpenAsync:33`, `ResolveAsync:65`, `AcknowledgeAsync:82`, `ExpireStaleAsync:101`, driven from `MetricsCollector.cs:51` at `IncidentAutoExpireDays = 14` (`MonitoringOptions.cs:93`) |
| Opened for all six conditions | **Built** | `MetricsCollector.cs:204,401,436`, `CertificateWatcher.cs:88`, `DeploymentPipeline.cs:623`, `BackupEngine.cs`/`BackupJobHandlers.cs`, `BillingTick.cs:919` |
| The incident survives a delivery failure | **Built, deliberately** | `DeploymentPipeline.cs:623-630` opens the incident and saves it **before** the notify, and the notify itself is wrapped in `TellSomebody` (`:170-178`, called `:644-647`) which logs and swallows |
| A timeline | **Built** | `Views/Monitoring/Index.cshtml:173-222`; `MonitoringController.cs:157` — newest 20, no filter, no pagination |
| A bell badge | **Built — per workspace** | `OpenIncidentsViewComponent.cs:22-23` counts open incidents for `currentUser.WorkspaceId`. **Every member of a workspace sees the same number, and one person acknowledging clears it for everyone.** There is no read state and no per-user row anywhere |
| Incident retention | **Absent** | `RetentionOptions.cs` carries seven knobs (`:37-86`); none is for incidents. M4 added a table that grows without bound — the R-14 shape, in a table three days old |
| `AlertEvent.ThresholdBreached = 6` | **Still inert, still loaded** | `Enums.cs:236`; no arm in `Matches` (`NotificationService.cs:122-132`), so it falls to `_ => false`. M4 routed incidents through `IncidentService`, not `NotifyAsync`, so the trap the Phase 6 spec flagged has not sprung — and it will the moment anything routes it through the notification path |

---

## 3. Can a notification be lost? Yes — three ways, and one of them is the ordinary case

This was the question worth answering before anything else, and unlike Phase 6 the answer is the bad
one. Phase 9's priority follows from it.

**Way one — the channel refuses, and the message is gone.** `DispatchSafe` makes one attempt inside a
ten-second budget. A timeout, a 502, a DNS blip, a revoked Discord hook: all land in the same catch
(`NotificationService.cs:153-162`), all become a `NotificationResult.Failed`, and none is ever tried
again. What survives is `Alert.LastError` — 400 characters, on the rule, describing *the last attempt
on that rule*, not this message. Send a test to the same rule an hour later and the record of the lost
critical alert is set to `null` (`:177`). Nothing anywhere records that a specific event failed to
reach a specific destination.

**Way two — nobody is a recipient, and only billing has ever noticed.** Nothing seeds an `Alert` row;
the alerts form is the only thing that creates one. A workspace that has never visited that form
matches zero rules, so `NotifyAsync` runs its dispatch loop zero times, returns `0`, throws nothing
and writes nothing. Every caller but one discards that number. The exception is the low-balance path,
and its comment is the clearest statement of this defect anyone has written in this repository
(`BillingTick.cs:925-943`):

```csharp
// Nobody could have received it, which until this line was the one way a warning could be
// recorded as delivered while reaching nobody at all.
```

That fix is one event's, in one file. Deploy failures, crashes, disk warnings, expiring certificates
and failed backups still go quietly nowhere for any workspace without a rule.

**Way three — the deploy path swallows the attempt entirely.** `TellSomebody`
(`DeploymentPipeline.cs:170-178`) exists for a good reason: a failing SignalR hub must not cost a
deployment its failure record. But it means that for the single most common critical event, even
`DispatchSafe`'s own bookkeeping can be lost without the caller knowing.

**What is *not* lost, and this is M4's real gift to Phase 9.** Since `d0bab9a`, all six conditions
write an `AlertIncident` before or independently of the notify, and that row is durable, survives a
restart, and appears on `/monitoring` and in the bell count. So the *fact* is retained even when the
*message* is not. Phase 9 does not have to invent durability for the event. It has to invent it for
the delivery — and for the person, since an incident is addressed to a workspace and nobody in
particular.

---

## 4. What is genuinely missing

Stated plainly, before it is decomposed.

1. **There is no such thing as a failed delivery you can look at.** Not for a notification, not for a
   password reset, not for an invitation. The platform's memory of trying to tell somebody something
   is one overwritten column, and only on the two paths that have a rule row to overwrite.
2. **Nothing is ever attempted twice**, in a codebase that already owns a durable queue that would do
   it — and whose retry predicate would, as written, decline the exact failure this is meant to fix.
3. **A notification is addressed to a channel, never to a person.** Users have an email address and a
   preferred culture and neither is ever used to tell them anything except a password reset they asked
   for. The default state of a new workspace is that nobody hears anything.
4. **The bell is a workspace's number, not a reader's.** There is no unread, no read, no per-user row,
   so "have I seen this" is unanswerable and one colleague's acknowledgement silences the badge for
   everybody.
5. **Everything raised in the background is English**, in a panel that renders Persian by default,
   with a per-user culture field sitting unread. The one exception says both languages at once because
   it had no way to choose (`BillingTick.cs:1069-1077`).
6. **Every message is a plain-text string built at the raise site** (`MetricsCollector.cs:402,437`,
   `CertificateAlert.cs:35-41`). `MailMessage` is constructed without `IsBodyHtml`
   (`PlatformMailer.cs:88`, `NotificationService.cs:273`), so all platform mail is plain text. There
   are no templates of any kind, and `SharedResource` is a `Harbora.Web` type that
   `Harbora.Infrastructure` cannot reach.
7. **Deduplication is four mechanisms with four shapes, one of them a dictionary a restart empties**,
   and the event whose acceptance criterion names deduplication explicitly — SSL, once per host per
   day — is the one with none.
8. **No digest, no quiet hours, no report, and no time zone to hang quiet hours on.**

---

## 5. Decomposition

Five sub-projects, each independently mergeable, each worth shipping alone. N1–N4 are item 0036;
N5 is item 0037.

| | Sub-project | What it delivers | Schema |
|---|---|---|---|
| **N1** | **A delivery that survives a refusal** | One durable row per (message × destination) with status, attempts and last error; the send moves onto the existing job queue; three attempts with backoff; a delivery log a person can read | one table |
| **N2** | **Say it once, across a restart** | A persisted dedup key with a window, replacing `AlertThrottle`; SSL once per host per day for real; the four existing suppression mechanisms named and reconciled | one column or small table |
| **N3** | **Told a person, not a channel** | Recipients resolved from workspace membership; a per-user notification row with a read state; the bell counts unread; a `/notifications` page | one table |
| **N4** | **In the reader's own language** | Templates per event × culture, HTML with a text alternative, chosen by the recipient's `PreferredCulture`; raise sites stop building strings | none |
| **N5** | **Noise control** | Per-user preference matrix, quiet hours, hourly/daily digest, weekly report | two tables |

### The order, and why

**N1 first, and it is not a close call.** It is the only one that closes §3, and §3 is the reason this
phase is P1 rather than a nicety. It is also the substrate: every later sub-project writes into the
row N1 creates — N2 puts a dedup key on it, N3 gives it a recipient, N4 changes what is rendered into
it, N5 changes whether it is sent now or at seven o'clock. Building any of them first means building
onto a delivery that still drops messages.

**Its honest limit, stated so nobody is surprised.** N1 makes today's deliveries reliable. It does not
make them *reach anybody* — a workspace with no alert rule still has nothing to deliver to, now
dependably. That is N3's, and if the owner would rather close way-two of §3 before way-one, N1 and N3
swap. The argument for N1 first is that a recipient model whose deliveries still evaporate is the
worse of the two half-finished states.

**N2 second, and it is the smallest.** It touches no UI and one background service. It is placed after
N1 only because a dedup key on a delivery row is one column, whereas a dedup key on nothing is a new
table that N1 would then have to reconcile with. If the owner wants a quick merge, this is the one —
and it retires `AlertThrottle`, whose own doc comment (`AlertThrottle.cs:13-14`) has been documenting
its own limitation since it was written.

**N3 third, and it is the largest.** Recipients, a per-user table, a read state, a page, and a bell
that changes meaning. It also has to decide what happens to M4's timeline, which is a question about
product rather than code (§7 Q1). It is third because it is the one whose design mistakes are written
into a table before anybody notices, and because both N1 and N2 make it cheaper: a per-user row that
is delivered reliably and only once is a much smaller thing to reason about than one that is not.

**N4 fourth.** It cannot precede N3 honestly: a template is rendered in *somebody's* culture, and
until N3 there is no somebody — only a Telegram group, which has no language. Shipping it after N3
also means the in-app rows N3 writes with today's inline English are not retro-rendered, which is
correct: a row records what was said at the time.

**N5 last**, which is where the backlog already puts it (0037 depends on 0036). A preference is
per-user-per-event, so it needs N3's user; a digest is a template, so it needs N4's; and quiet hours
need a time zone the platform does not have (§7 Q5). It is also the only one nobody misses until the
others are noisy enough to be worth muting.

### N1 — a delivery that survives a refusal

A row per (message × destination): status `Pending`/`Sent`/`Failed`/`Suppressed`, attempts, last
error, destination reference. `DispatchSafe`'s body becomes the job's body rather than the caller's
inline await, under a new `JobKind`.

**Reuse the queue, and reuse the channel senders.** The four executors are correct and were hardened
at real cost — case-insensitive targets, verdict-from-status-code, SSRF guard. Nothing here rewrites
them; N1 changes who calls them and what is written down afterwards.

**The retry predicate is the trap.** `IsRetryable` will decline `InvalidOperationException`, which is
what a 502 becomes, and `MaxAttemptsFor` defaults to `1`. Whichever answer §7 Q4 gets, a plan that
adds a `JobKind` and assumes it inherits three attempts has built nothing.

**One thing to be careful of that is easy to miss.** `RecordAttemptAsync` calls
`db.SaveChangesAsync` on a context the caller is also using (`NotificationService.cs:178`), so the
bookkeeping commits whatever else the caller had pending. Enqueuing a delivery must not inherit that
habit — a raiser's half-built unit of work must not be committed by the act of queuing a message.

**Not in N1:** recipients, preferences, templates, or a second UI. The delivery log lives beside the
alert rules on `/monitoring`, where `LastError` is already rendered.

### N2 — say it once, across a restart

A persisted key with a window — `ssl:{host}:{yyyy-mm-dd}`, `disk:{server}:{hour}` — replacing the
in-memory dictionary.

**Four mechanisms exist and they are not four copies of one idea.** The disk throttle is time-keyed
and volatile; `Alert.ThresholdFiredAt` is time-keyed and durable and also serves as the threshold's
firing state; `Wallet.LowBalanceWarnedAtBalanceMinor` is keyed on a *balance* rather than a clock, and
its rationale (`Wallet.cs:48-66`) explains why a time window would be wrong for it; the crash path is
deduplicated by the app status machine, which is not a notification concern at all. Collapsing all
four would be a bug dressed as tidying. N2 replaces the first, leaves the third and fourth alone, and
decides deliberately whether the second becomes a dedup key or stays firing state.

**Not in N2:** cross-channel deduplication of the same event to different destinations. One message
per destination is correct; N2 is about not raising the same message twice.

### N3 — told a person, not a channel

Recipients resolved from `WorkspaceMember`, a per-user notification row with `ReadAt`, a bell counting
unread, a `/notifications` page with the filter and pagination idioms the audit page already uses.

**In-app is the sink that cannot fail**, and that is the point of it: a workspace with no channel
configured stops being a workspace nobody can reach. This is the fix for §3 way two, generalised from
the one place billing solved it.

**The bell changes meaning**, from "conditions open in this workspace" to "things this person has not
read", and those are different numbers with different lifecycles. What happens to M4's badge and
timeline is §7 Q1 and cannot be settled here.

**Not in N3:** preferences. Everyone in a workspace gets the in-app copy of everything the workspace
is told, and N5 is where that becomes tunable. Shipping N3 with preferences half-built would produce
the state this project keeps producing — a checkbox that looks configured and is inert.

### N4 — in the reader's own language

`Templates/Notifications/{EventType}.{culture}` producing HTML and a text alternative, chosen by
`User.PreferredCulture`, defaulting `fa` as the field already does.

**The raise sites stop composing prose.** `MetricsCollector.cs:437` currently passes a finished English
sentence; it should pass what happened and what it happened to. That is the change that makes every
other language possible, and it is most of the work.

**`Harbora.Infrastructure` cannot see `SharedResource` today** — localization is wired in
`Harbora.Web` only (`Program.cs:81,88`). Whether that becomes a project reference, a moved resource
assembly, or a rendering step that happens in Web is a real design question the plan must answer,
though not one the owner needs to.

**Not in N4:** per-workspace branding. Doc 09 §4.2 wants the template lookup to be indirect so
branding is later a data change; keeping the indirection is in scope, using it is not.

### N5 — noise control

The preference matrix (user × event type × channel, absent row meaning the default), quiet hours,
the digest job, the weekly report.

**Critical events are re-routable, not mutable.** Doc 09 §3's C/O split is the right rule and the
`LowBalance` reasoning at `NotificationService.cs:111-120` is the same argument already made in this
codebase: a customer may choose where the last warning before suspension goes, not whether it exists.

**Quiet hours are blocked on a decision, not on code** — there is no time zone anywhere in this
platform (§7 Q5).

**Not in N5:** maintenance announcements and delivery-overview dashboards, which the roadmap puts in
Phase 13.

---

## 6. What is settled regardless of the open questions

- **Nothing is rebuilt that works.** The four channel executors, `PlatformMailer`, `IncidentService`
  and the job queue are all kept. Phase 9 changes who calls them and what is written down.
- **The in-app copy is written even when every channel fails**, and a channel that has no
  configuration degrades to `Suppressed` with a reason rather than throwing (doc 09 §6).
- **Every new table gets a retention knob in `RetentionOptions` in the sub-project that adds it** —
  and `AlertIncident`, which M4 shipped without one, gets its own. This platform has already paid for
  R-14 once.
- **Any new `AlertEvent` member gets an arm in `Matches` on the same day**, and `ThresholdBreached`
  gets closed by whichever sub-project first routes an incident through the notification path.
  `NotificationService.cs:104-108` warns about this in writing; the warning is a year of hindsight and
  should be honoured rather than re-learned.
- **Retention values follow doc 14**: events 90 days, in-app rows 180 days, deliveries 90 days.
- **BYO SMTP only.** Doc 09 §5's explicit non-dependency holds: Phase 9 does not wait for Phase 10.

---

## 7. The five decisions this spec cannot make

Each changes what gets built, not merely how. They are the owner's.

**Q1 — Is an incident the event, or does Phase 9 add a second event table beside it?**
`AlertIncident` already carries workspace, condition, subject, severity, title, body, opened, closed
and a closed reason, deduplicated on exactly the triple doc 09 wants a dedup key for.
*(a) Reuse it as the event.* One table for "something happened", with recipients and deliveries
hanging off it. Cheapest, no duplication, and the timeline and the notification centre are two reads
of one truth. Cost: half of doc 09's catalog — `RoleChanged`, `DeploySucceeded`, `TokenCreated`,
`RestoreCompleted` — are not conditions and have no meaningful open/close, so the entity either grows
a "born closed" mode or those events look permanently resolved. It also makes every notifiable thing a
*workspace* thing, which `PasswordReset` is not.
*(b) A separate `NotificationEvent` per doc 09 §4.1, with `AlertIncident` continuing as the condition
lifecycle and linking to it.* Faithful to the audit, and each entity keeps one job. Cost: two tables
recording overlapping facts, two retention policies, and the standing risk that a raiser writes one
and forgets the other — the failure mode `NotificationService.cs:61-67` already names for function
events.
*(c) Extend `AlertIncident` with a nullable lifecycle so a non-condition event is a row that opens and
closes in the same instant.* Middle path; costs a slightly dishonest column and a rename, since
"incident" would then mean "anything worth telling someone".
Everything in N3 follows from this answer, and so does what the bell counts.

**Q2 — Who is a recipient, by default, of a workspace event?**
There is no answer in the code to copy: today the answer is "whoever configured a channel", which is
usually nobody.
*(a) Every member of the workspace.* Nobody is ever missed, which is the whole point of N3. Cost: a
Viewer on a twelve-person team gets an in-app row for every failed deploy, and until N5 ships there is
no way to stop it.
*(b) Doc 09 §3's role defaults — workspace Admins plus the acting user, Developers get their own
actions' outcomes, Viewers get nothing.* Correct-feeling and matches the audit. Cost: it is a routing
matrix, in N3, before there is any preference UI to correct it with when it guesses wrong; and "the
acting user" does not exist for anything a background service raises.
*(c) Admins only.* Smallest and least wrong; costs the developer who broke the deploy not being told
about it.

**Q3 — Does the outbox cover transactional email, or only notifications?**
Password reset, verification, and both invitations are today fire-and-forget with no record
(`AccountController.cs:179,372`, `WorkspacesController.cs:157`, `UsersController.cs:189`).
*(a) Leave them alone.* Zero risk to a login flow. Cost: the platform still cannot answer "was that
reset email sent", which is the single most common support question any panel gets.
*(b) Fold them into the outbox and queue them.* One rail, one log, retries for free. Cost: a reset
email becomes asynchronous, so "check your inbox" is now a promise about a queue; the anti-enumeration
behaviour at `:185-186` must be preserved exactly; and the worker's boot gate now stands between a
locked-out user and their link.
*(c) Fold in the record but not the queue* — send inline as today, write a delivery row either way.
Keeps the latency, gains the log, gains no retry.

**Q4 — Where does the notification retry policy live?**
`IsRetryable` accepts four transport exception types; a Discord 502 is an `InvalidOperationException`
from `EnsureAcceptedAsync:212`; `MaxAttemptsFor` defaults to 1.
*(a) Widen `JobExecutionPolicy`* — a `NotificationDelivery` arm in `MaxAttemptsFor`, and a retryable
exception type raised by the channel senders for a 5xx (a 404 must stay terminal; retrying a revoked
webhook three times helps nobody). Costs a change to a shared policy every job kind reads, in a file
whose comments argue carefully against retrying things that fail identically.
*(b) The delivery row carries its own attempts and next-attempt-at, and the job body owns the
decision.* Leaves the shared policy untouched and lets notifications have a different curve from
deployments. Costs a second retry mechanism in a codebase that just built its first.
*(c) Use the queue as-is and accept that only timeouts and connection failures retry.* Cheapest, and
it leaves R-13's own worked example unfixed, which is a strange place for this phase to land.

**Q5 — Quiet hours need a time zone the platform does not have.**
No `TimeZoneInfo` exists in application code; everything is UTC; the panel's default culture is
Persian, and Iran is UTC+3:30, so UTC quiet hours are wrong by half an hour on top of three.
*(a) A per-user IANA time zone on `User`.* Correct, and the only answer that works for a platform with
users in more than one place. Costs a picker, a stored value, and the ICU/tzdata question on the
container image.
*(b) One installation-wide time zone setting*, beside the SMTP settings in the `Settings` table.
Much smaller, right for a single-country installation, wrong the day it is not one.
*(c) Quiet hours are expressed as a UTC window and labelled as such.* No new concept; asks every user
to do arithmetic, which means the feature is used wrongly or not at all.
This decides whether N5 is one sub-project or one sub-project plus a schema change to `User`.

---

## 8. Testing

Each sub-project states its own; five rules apply across the phase.

- **A test that a message was delivered must first assert there was somewhere to deliver it.** The
  Phase 6 equivalent was a delivery test that passed because `Matches` returned false and `NotifyAsync`
  returned zero. Here it is a retry test that passes because nothing was ever enqueued.
- **A background sender's test must run without a session and still find rows.** Every regression of
  this kind in this codebase has looked like a clean pass over an empty set. `IgnoreQueryFilters` in
  the code under test is not evidence; a fixture with two workspaces and an assertion that both were
  reached is.
- **Assert on `data-` attributes, not on visible text.** The panel renders Persian by default. The
  incident timeline already exposes `data-incident-open` and `data-incident-closed-reason`
  (`Views/Monitoring/Index.cshtml:200-201`) and is the model to follow.
- **Assert on what was handed to the channel, not on what the channel returned.** A fake responder
  that can answer 502 then 200 already exists (`tests/Harbora.Tests/NotificationDeliveryTests.cs:27-40`)
  and is the right seam for every retry test in N1.
- **Time-dependent behaviour uses the fake clock**, never a real delay: backoff, dedup windows, quiet
  hours boundaries, digest grouping.

Behaviours worth naming now: a channel that answers 502 three times leaves a row reading
`Failed: <reason>` and the in-app copy still exists · the same 404 is not retried at all · a workspace
with no alert rule still has somebody who was told · a restart between two certificate passes does not
produce two emails for one host · a Persian-preference user receives Persian for an event raised by a
timer · removing the SMTP settings turns email deliveries into `Suppressed(no-smtp)` and never an
exception · one member reading a notification does not mark it read for the rest of the workspace ·
a quiet-hours window that crosses midnight behaves at both ends.

---

## 9. What Phase 9 is not

The customer email service — a tenant's *application* sending mail — which is Phase 10 and doc 10,
and whose Phase-1 does not gate this one · the platform mail-server product that already exists
(`Infrastructure/Mail/MailPlatformService.cs`, `StalwartClient.cs`, `MailServers`/`MailDomains`/
`MailMailboxes` at `HarboraDbContext.cs:73-75`): that provisions mailboxes for tenants and is not an
outbox, and it should not be mistaken for one · maintenance announcements and delivery-overview
dashboards, which the roadmap puts in Phase 13 · a managed relay, which is Phase 14 · SMS, push and
mobile, which nothing in the audit asks for · rebuilding the four channel senders or `IncidentService`,
both of which work · **security review**, out of scope by the owner's standing instruction: what is
being settled here is whether a message that was meant to arrive did.
