# The alerts do fire. Nobody can turn one off.

**Date:** 2026-08-16 · **Status:** decomposition proposed; four sub-projects, none ready for a plan
until the owner answers §6

Phase 6 of `docs/product-audit/17-implementation-roadmap.md` — backlog items **0029**, **0030**,
**0031**.

---

## 1. What exploring changed

The audit was written before Phases 1 and 2 and before ten sub-projects landed, and nine times in
this programme a capability the plan assumed missing turned out to already exist. So the search here
was by verb rather than by expected noun — *evaluate, threshold, breach, fire, notify, throttle,
resolve, acknowledge, incident, uptime, restart* — because the last spec in this series claimed
volume browsing did not exist and was wrong, and the reason it was wrong is that it searched for
`BrowseVolume` when the code was called `AppData`.

Three things came back that change the shape of the phase.

**First: an alert can fire, end to end, today.** Every one of the six event kinds has something that
raises it and a channel that delivers it. That is set out with evidence in §3, and it matters because
it is the opposite of this codebase's defining defect. Phase 6 is not an emergency repair of a
capability the panel pretends to have; it is finishing work on a mechanism that works.

**Second: 0030 is more than half built, and the missing half is not the node contract.** The exact
two figures item 0030 asks for — `StartedAt` and `RestartCount` — are already on the node wire, on a
verb that is already allowlisted and already implemented by the agent. The gap the previous
exploration recorded (`NodeWorkloadEngine.InspectAsync` returns null because the allowlist has no
inspect verb) is real, but it is a gap in the *full* `ContainerDetail` shape, which only the app
Overview card needs. Uptime and restart collection does not have to wait for it. §4 and §6 Q5.

**Third: 0029 is the one item the audit describes exactly.** Rules cannot be edited or switched off;
`IsEnabled = true` is written into the constructor call and never touched again. The audit's framing
survives contact with the code, which after nine reversals is worth saying explicitly rather than
assuming.

---

## 2. What actually exists

The most valuable section of this document. Cited so the plan does not re-derive it.

### Item 0029 — alert management: edit/toggle, validation, configurable thresholds

| Part | State | Evidence |
|---|---|---|
| Create a rule | **Built** | `AlertsController.cs:27-71` — every field, all four channels, target encrypted at `:52` |
| Delete a rule | **Built** | `AlertsController.cs:89-96`, scoped by `WorkspaceId` |
| Send a test | **Built**, and it can fail | `AlertsController.cs:73-87`; `NotificationService.SendTestAsync:77` |
| Delivery outcome shown on the rule | **Built** | `Alert.LastAttemptAt/LastError` (`Alert.cs:49,55`), written at `NotificationService.cs:157`, rendered at `Views/Monitoring/Index.cshtml:147-170` |
| Capability gate | **Built** | `Capabilities.AlertsManage` (`Capabilities.cs:18`), on all three actions |
| **Edit a rule** | **Absent** | No `Edit` action on `AlertsController`; no `Views/Alerts/` directory at all |
| **Enable / disable a rule** | **Absent** | `IsEnabled = true` hardcoded, `AlertsController.cs:67`. The column exists and is honoured by every reader — nothing can write it |
| Threshold half stored all-or-nothing | **Built** | `AlertsController.cs:62-64`, with a comment at `:59-61` naming this exact failure mode |
| **Threshold half validated as a unit** | **Absent** | The triple is silently discarded when incomplete. The rule saves, the redirect succeeds, and the user is told nothing |
| **Threshold half visible on an existing rule** | **Absent** | The list renders name, channel and severity only (`Index.cshtml:152`). A rule that watches an app looks identical to one that does not |
| **Disk ratio configurable** | **Absent** | `DiskWarnRatio = 0.85` const, `MetricsCollector.cs:23`; `DiskAlertInterval = 1h`, `:26` |
| **Backup staleness configurable** | **Absent, and it is three different numbers** | 48 h at `MonitoringController.cs:132-134`; `VerificationSchedule.StaleAfter = 7 days` at `:19`; `StorageMeasurer.StaleAfter = 24 h` at `:28` |
| **Repeat window configurable** | **Absent** | `ThresholdRule.RepeatAfter = 1h` (`:75`), `Tolerance = 1min` (`:65`) |
| Options plumbing to hang them on | **Built** | `NotificationOptions` bound at `DependencyInjection.cs:283`; eleven other sections bound the same way. There is no `Monitoring` section |

**One observation that "configurable" does not fix, recorded so the plan does not think it does.**
`MetricsCollector.MaybeDiskAlert:293-298` finds every workspace holding a rule with `OnDiskWarning`
and tells all of them that node *N*'s disk is full — whether or not that workspace has anything on
node *N*. Making the ratio configurable makes the line adjustable; it does not make the audience
right.

### Item 0030 — uptime and restart-count collection

| Part | State | Evidence |
|---|---|---|
| The two figures exist in the panel's own contract | **Built** | `ContainerDetail(… int? RestartCount, DateTimeOffset? StartedAt)`, `IDockerEngine.cs:147-156` — both nullable on purpose |
| The local engine answers | **Built** | `DockerEngine.InspectAsync:252`, mapped at `:269-294` |
| The remote-agent engine answers | **Built** | `RemoteDockerEngine.InspectAsync:139` |
| They are displayed, live, per app | **Built** — sub-project B3 | `Views/Apps/Details.cshtml:709-721`, behind `data-spec-restarts`; unknown renders as unknown at `:723-730` |
| The two figures cross the node wire | **Built, and this corrects the brief** | `WorkloadStatus.StartedAt`/`.RestartCount` (`Inventory.cs:101-102`) and `ContainerStatus.RestartCount` (`:114`), answered by `GetWorkloadStatus` — allowlisted at `NodeCommands.cs:17`, catalogued at `:85` under `workloads:read`, handled at `WorkloadHandlers.cs:160` |
| `IDockerEngine.InspectAsync` for a node app | **Absent, deliberately** | `NodeWorkloadEngine.cs:350-361` returns null and its comment says why: `GetWorkloadStatus` aggregates a workload into a non-nullable `Healthy` with no digest, which is not this contract |
| **Persisting either figure** | **Absent** | `MetricsCollector.CollectServerAsync:194-233` samples `cpu.percent`, `mem.used`, `net.rx`, `net.tx`, `disk.*`, `containers.running`. No lifecycle series exists |
| **Uptime percent** | **Absent** | Nothing computes it. `Views/Monitoring/Index.cshtml:49-51` says so in a comment, and shows nothing rather than a fabricated figure |
| **Restart sparkline** | **Absent** | No series to draw |
| **Restart-rate alert rule** | **Absent** | `AlertMetric` has two members, `CpuPercent` and `MemoryPercent` (`Enums.cs:250-254`) |
| The place a new series has to survive | **Built, and it excludes counters** | `MetricRollups.IsCumulative:103` means "starts with `net.`"; cumulative series are excluded from rollups and therefore die with the raw samples at 24 h (`:106`). §6 Q4 |

### Item 0031 — alert incident lifecycle and timeline

| Part | State | Evidence |
|---|---|---|
| **`AlertIncident`, or any incident row** | **Absent** | Nothing named incident exists under `Harbora.Domain` or `Harbora.Infrastructure.Monitoring`; the word appears in the codebase only in unrelated prose |
| The only firing state that exists | **Built, for one rule kind** | `Alert.ThresholdFiredAt` (`Alert.cs:46`), set at `MetricsCollector.cs:145`, cleared at `:139` |
| The only other suppression state | **Built, in memory** | `AlertThrottle` (`AlertThrottle.cs:16-35`), keyed `disk:{serverId}`. Its own doc comment at `:13-14` says a restart allows one extra alert — deliberate, and it also means there is nothing durable to query |
| **A resolve, for anything** | **Absent** | Nothing writes a closing timestamp for any condition |
| Recovery is already *detected* and thrown away | **Built, then dropped** | `MetricsCollector.cs:278-281`: an app that recovers from `Crashed` writes an info log and tells nobody. This is the cheapest resolve in the phase |
| **Timeline UI on `/monitoring`** | **Absent** | `Views/Monitoring/Index.cshtml` is 257 lines: stat cards, CPU chart, app health, deploys, domains, the alert rule list. No history of anything that fired |
| **Bell badge counting open incidents** | **Absent; the link works** | `_Topbar.cshtml:85` — a bare `<a href="/monitoring#alerts">` with no count. The "bell → real target" half of the roadmap line already shipped |

---

## 3. Can an alert actually fire? Yes — and here is the chain

This was the question worth answering before anything else, because if the answer had been no, Phase
6 would have been urgent rather than routine. It is yes. Each event has a raiser, a match arm and a
delivery path, and each link was checked rather than assumed.

| Event | Raised by | Matched by | Delivered |
|---|---|---|---|
| `DeployFailed` | `DeploymentPipeline.cs:624` | `Matches`, `NotificationService.cs:109` | ✔ |
| `AppCrashed` | `MetricsCollector.cs:275` | `:110` | ✔ |
| `SslExpiring` | `CertificateWatcher.cs:76` | `:111` | ✔ |
| `DiskWarning` | `MetricsCollector.cs:296` | `:112` | ✔ |
| `BackupFailed` | `BackupEngine.cs:155`, `BackupVerifier.cs:63`, `BackupJobHandlers.cs:170` | `:113` | ✔ |
| `LowBalance` | `BillingTick.cs:869` | `:114`, true for every rule by design | ✔ |
| Per-app threshold | `MetricsCollector.cs:150` | bypasses `Matches` — `NotifyRuleAsync` targets one rule by id | ✔ |

**The tenant-filter trap does not bite here, and that was worth confirming rather than assuming**,
because it has bitten this codebase before. `Alert` carries a workspace query filter
(`HarboraDbContext.cs:858`). The collector and the watcher run inside
`scopeFactory.CreateScope()` from a `BackgroundService`, where `IHttpContextAccessor.HttpContext` is
null, and `HttpWorkspaceScope.IsUnscoped` is exactly that test (`HttpWorkspaceScope.cs:19`). So the
unqualified `db.Alerts` reads at `MetricsCollector.cs:293` and the unqualified `db.Apps` read at
`:259` see every tenant, which is what they need. `EvaluateThresholdsAsync` additionally writes
`IgnoreQueryFilters` throughout and says why at `:58-61`; belt and braces, not a contradiction.

**The delivery path itself was hardened by earlier work and should not be rebuilt.** Targets are read
case-insensitively because every channel silently failed for as long as notifications existed
(`NotificationService.cs:30-37`); a non-2xx response is now a verdict rather than discarded
(`:181-198`); the outcome is written back onto the rule (`:157`) and the list admits it (`:147-170`).
Anything Phase 6 adds delivers through this, unchanged.

**One dead thing, recorded because it is a loaded trap rather than a bug today.**
`AlertEvent.ThresholdBreached = 6` (`Enums.cs:232`) is raised by nothing and has no arm in `Matches`,
so it falls to `_ => false`. Threshold breaches reach a channel through the other door,
`NotifyRuleAsync`, which never consults `Matches`. The member is therefore inert — but
`NotificationService.cs:88-93` warns in as many words that an event with no arm here "is delivered to
nobody, raises nothing, throws nothing, and leaves its caller reporting a notification sent". The
moment M4 routes an incident through `NotifyAsync`, that value becomes live and silent. Whichever
sub-project touches it owns closing it.

---

## 4. What is genuinely missing

Stated plainly, before it is decomposed.

1. **A rule you got wrong can only be deleted.** No edit, no disable. Correcting a severity means
   re-entering a Telegram bot token, because the plaintext target is deliberately never returned to
   the UI (`AlertsController.cs:14-15`).
2. **A half-filled threshold is accepted and silently made inert.** The controller already refuses to
   store the incomplete triple; what it does not do is say so. The user gets a rule that is not the
   rule they described.
3. **Four operational numbers are constants**, and the backup-staleness one is three constants that
   disagree.
4. **Nothing records how long an app has been up or how often it has restarted.** Both figures are
   read live, shown on one page, and then discarded. There is no history, therefore no uptime
   percent, no sparkline, and no restart-rate rule.
5. **Nothing that fires has an end.** There is no row anywhere with an opened-at and a resolved-at.
   The disk throttle is an in-memory dictionary that a restart empties; `ThresholdFiredAt` is a single
   nullable column on the rule. "What happened last night" is answerable only from the channel a
   message was sent to — which is precisely what item 0031 exists to fix.
6. **A node-hosted app's Overview permanently says the engine did not answer** (`Details.cshtml:723-730`).
   That sentence is honest and it is also the last one anybody wants to read about their own app.

---

## 5. Decomposition

Four sub-projects, each independently mergeable, each worth shipping alone.

| | Sub-project | What it delivers | Schema |
|---|---|---|---|
| **M1** | **Manage the rules that already exist** | Edit and enable/disable; the list shows the threshold half; a half-filled threshold is refused with a message instead of swallowed | none |
| **M2** | **The numbers become settings** | A `Monitoring` options section for the disk ratio, the disk interval, the threshold repeat window and one backup-staleness figure | none |
| **M3** | **Record uptime and restarts** | A lifecycle series or table, per app, on local and node engines; uptime percent and restart history on the app page; a restart-rate rule | one addition |
| **M4** | **Things that fire also stop firing** | Incidents with an opened-at and a resolved-at, a timeline on `/monitoring`, and a bell badge that counts what is open | one table |

### The order, and why

**M1 first**, because it is the only one whose absence is visible on the screen as an inconsistency: a
list of rules with a delete button and nothing else. It is also the smallest thing that changes
somebody's day. And it is the precondition for M4 being tolerable — an incident list is only bearable
if a noisy rule can be switched off rather than deleted and re-typed.

**M2 second, and it could equally be first.** It is the smallest of the four and touches no UI. The
only argument for keeping it behind M1 is that moving the disk ratio into configuration while the rule
that uses it still cannot be disabled fixes the less annoying half of the same complaint. If the owner
wants a quick merge, this is the one.

**M3 third**, not because anything blocks it — it is independent of M1 and M2 and could go first if
the data is wanted sooner — but because it is the largest and because it is the only one whose design
error is written into a table before anyone notices. A cumulative series that `MetricRollups` averages
produces summaries that mean nothing, and a series `IsCumulative` excludes is deleted at 24 hours
(`MetricRollups.cs:103,106`). Either way the mistake is discovered a month later when somebody asks a
question about last month. §6 Q4 has to be answered first.

**M4 last**, for three reasons that are all real. It is the only one with a new table. It changes what
every raiser in §3 does, so it wants those raisers stable. And its whole value is a list somebody
reads, which means it wants the rules tunable (M1, M2) before the list starts filling.

### M1 — manage the rules that already exist

Edit and toggle on `AlertsController`, under the `AlertsManage` policy the other three actions already
carry, scoped by `WorkspaceId` the way `Delete:94` and `Owns:98` already are. The list gains what a
rule actually watches, so a threshold rule stops looking like an event rule.

The validation rule is the point of the sub-project, and it is one sentence: **a threshold that is
half-filled is refused, not quietly emptied.** The controller comment at `:59-61` already understands
that an inert rule that looks configured is this project's recurring failure; what is missing is
telling the person who typed it.

**Not in M1:** a new page. The rules live in a section of `/monitoring` and the phase does not need to
move them.

### M2 — the numbers become settings

A `MonitoringOptions` section, bound the way the other eleven are (`DependencyInjection.cs:283` is the
nearest model), carrying the disk warn ratio, the disk alert interval, the threshold repeat window,
and backup staleness.

**Backup staleness is the interesting one and it is not a rename.** Three constants exist with three
different meanings — 48 h before the dashboard warns, 7 days before a verification is stale, 24 h
before a storage measurement is. They are not one number wearing three hats, and collapsing them
would be a bug dressed as tidying. M2 names one of them (the dashboard's) and leaves the other two
where they are, with a line in the options doc saying which is which.

**Not in M2:** per-workspace or per-server overrides. One installation-wide figure, changed by an
operator, is the whole of this sub-project.

### M3 — record uptime and restarts

Persist `StartedAt` and `RestartCount` per app, on both engines, and answer "is it healthy and how
often does it crash" over time rather than in this instant.

**Reuse the collector, not a new loop.** `MetricsCollector.CollectServerAsync` already walks every
server through its own engine every 30 seconds and already writes six series; this is a seventh
concern in a pass that is already happening.

**The node half needs no new verb**, per §2: `GetWorkloadStatus` already carries both figures under
`workloads:read`. What it does not carry is the digest and the tri-state health that `ContainerDetail`
exists for — a separate question, §6 Q5.

**What it must not claim.** An app with no collected sample says so; an engine that did not answer
says so. `MetricDisplay` and `AllocationReading` are the existing rule for this and the audit calls
them exemplary (`08 §B1`). An uptime of 100% derived from no samples is the exact lie this codebase
legislates against.

**Not in M3:** response time, error rate, p95. Audit `08 §B3` rules them out until Traefik metrics are
ingested, and nothing here changes that.

### M4 — things that fire also stop firing

An incident row with an opened-at and a resolved-at, opened by whatever raises a condition and closed
by whatever observes it clear; a timeline on `/monitoring`; a bell badge that counts what is open.

**Two closes are already free.** `MetricsCollector.cs:136-140` already recognises a cleared threshold
and nulls `ThresholdFiredAt`. `MetricsCollector.cs:278-281` already recognises an app that recovered
and writes a log line nobody reads. Both are resolve events that exist and are discarded.

**What an incident is, and what closes one that nothing re-evaluates, are the owner's** — §6 Q1 and
Q2. Those two answers decide the table, so M4 cannot begin without them.

**Not in M4:** deduplication across channels, retry, digests, or a notification centre. Those are
Phase 9 (`09`), and `08 §B2.7` says so.

---

## 6. The five decisions this spec cannot make

Each of these changes what gets built, not merely how. They are the owner's.

**Q1 — What is an incident: one per rule, or one per condition?**
*Per (rule, subject)* matches the shapes already in the code — `ThresholdFiredAt` hangs off a rule,
and the throttle key is `disk:{serverId}`. It is the smaller change. Its cost is that one full disk in
a workspace with three rules opens three incidents, and the timeline reads as three things going wrong.
*Per (workspace, condition, subject)* — one incident for "node A's disk is low", however many channels
carried it — reads correctly on a timeline and is what a status page would later want. Its cost is
that it separates the condition from who was told, which means either a second table or a nullable
link, and it makes `Alert.ThresholdFiredAt` redundant state that must be migrated or knowingly
abandoned. Everything else in M4 follows from this answer.

**Q2 — What closes an incident for something that is an event rather than a condition?**
Thresholds, disk and crashes are conditions: the collector re-evaluates them every 30 seconds and
already sees them clear. SSL is a condition re-evaluated daily. But **a failed deploy never recovers**
— the next deploy succeeding is a different fact about a different deployment — and the same is true
of a failed backup.
*(a) Only conditions get incidents; events get a timeline entry born closed.* Honest, smallest, and
the timeline still shows everything that happened. Cost: two kinds of row on one list, and the bell
badge counts only conditions.
*(b) Everything gets an incident, events auto-close after a fixed age.* Cost: an arbitrary number that
will be wrong for somebody, and a badge that decays instead of being cleared by anything real.
*(c) Everything gets an incident, a person acknowledges events.* Cost: a new verb, a permission
question, and — reliably — a list of things nobody ever acknowledged.

**Q3 — Does editing a rule mean retyping its secret?**
The channel target is encrypted and the plaintext is deliberately never returned to the UI
(`AlertsController.cs:14-15`), which is a rule worth keeping.
*(a) Blank means unchanged.* Familiar; the edit form cannot show what is stored, so the field is
always empty and the user has to trust it.
*(b) The target is required on every edit.* Simple and unambiguous; makes "raise this rule's severity"
a reason to go and find a bot token.
*(c) The target has its own action, everything else edits freely.* Clearest; costs two forms.
Small, but it decides M1's shape, so it cannot be left to the plan.

**Q4 — What shape does a restart count take, and can uptime survive a month?**
`MetricRollups.IsCumulative` currently means "starts with `net.`", and cumulative series are excluded
from rollups and therefore deleted at 24 hours (`MetricRollups.cs:103,106`).
*(a) Store `app.restarts` raw and cumulative, widen `IsCumulative`, compute the rate at read time as
`NetworkThroughput` already does for network counters — including recognising a container replacement
as a reset rather than a spike.* Consistent with the existing precedent. Cost: "restarts this month"
is unanswerable, because the raw rows are gone.
*(b) Store a per-tick delta, which rolls up correctly and answers a year.* Cost: a missed collector
tick loses a restart permanently, and the panel would then under-report rather than say it does not
know — the failure this codebase is most careful about.
*(c) A small per-app lifecycle table instead of the metric series, as `08 §B2.3` floats.* Cost: a
third shape for time-series data alongside raw samples and rollups.
The same question governs uptime percent: **a figure over 30 days cannot come from a series that lives
24 hours**, so whichever answer is chosen has to be one that survives the sweeper. This decides M3's
size more than anything else in it.

**Q5 — Does M3 close the node inspect gap, or leave it open?**
M3 needs no node contract change. But if it ships alone, a node-hosted app will have an uptime history
on one screen and "the container engine did not answer" on its Overview (`Details.cshtml:723-730`) —
the same app, two clicks apart.
*(a) Accept the inconsistency; close it in its own sub-project later.* Cheapest, and leaves a visible
oddity.
*(b) Add a real inspect verb to the node contract in M3.* Closes both. Costs a new allowlist entry, an
agent handler, an agent release and the version negotiation that goes with a node fleet that upgrades
on its own schedule.
*(c) Answer `InspectAsync` from `GetWorkloadStatus`, leaving digest and tri-state health null.*
Removes the permanent "did not answer" without inventing anything — every field `ContainerDetail`
cannot honestly fill is already nullable for exactly this reason. Costs nothing on the wire, and
`NodeWorkloadEngine.cs:350-359` argues against it: it was written as a deliberate refusal to force a
mismatched shape. That argument was made about the app-specifics card, before uptime collection was on
the table, and the owner may weigh it differently now.

---

## 7. Testing

Each sub-project states its own, but four rules apply across the phase.

- **A test that a rule fired must first assert the rule existed.** The equivalent trap in D4 was an
  expiry test that passed because nothing had ever been created. Here it is a delivery test that
  passes because `Matches` returned false and `NotifyAsync` returned zero.
- **Assert on `data-` attributes, not on visible text.** The panel renders **Persian by default**;
  `Details.cshtml:718` already exposes `data-spec-restarts` for exactly this reason and is the model.
- **A background evaluator's test must run without a session and still see rows.** Every regression of
  this kind in this codebase has looked like a clean pass over an empty set. `IgnoreQueryFilters` in
  the code under test is not evidence; a fixture with two workspaces and an assertion that both were
  reached is.
- **Assert on the request handed to the engine, not on what the engine returned.** For M3 that means
  the collector asked the node for a status, and what it wrote when the node declined to answer.

Specific behaviours worth naming now: a disabled rule delivers nothing and still appears in the list ·
a half-filled threshold is refused with a message and no row is written · an app with no lifecycle
samples reports unknown rather than 100% · a node-hosted app is not silently recorded as zero restarts
· an incident opened by a condition is closed by that condition clearing, without anybody pressing
anything · a rule in another workspace cannot be edited, toggled or resolved.

---

## 8. What Phase 6 is not

Response time, error rate and p95 — `08 §B3` defers them until Traefik metrics are ingested · log
aggregation and historical search · external uptime probing, which belongs with status pages ·
notification deduplication, retry and digests, which are Phase 9 · the retention sweeper
(`DataRetentionSweeper`, `DependencyInjection.cs:233`) and the `MetricRollups` index
(`HarboraDbContext.cs:372-373`), both of which Phase 2 already shipped and which the roadmap still
lists under this phase · **security review**, which is out of scope by the owner's standing
instruction: what is being settled here is whether an alert is correct and whether it arrives.
