# A price that belongs to a server, and a figure a customer can plan a month around

**Date:** 2026-08-16 · **Status:** approved, ready for an implementation plan

The owner asked for three things. They are three projects, not one, and this spec covers only the
first. The other two are named at the foot of this document so the order is on the record.

1. **This spec.** The payment surfaces show an estimated monthly figure beside the hourly one; the
   size chooser becomes clean selectable cards rather than a dropdown, with each option's details
   written on it; and a price can differ from one server to the next, the way a cloud offers
   general-purpose and memory-optimised machines side by side.
2. **The app overview gets the full page**, the way the databases section already has it.
3. **The backups section is completed** — delete, upload, and real management of destinations and
   schedules.

---

## What is here already, and what that decides

The hourly meter is good and this must not weaken it. Three of its rules are load-bearing and every
decision below is shaped by them.

**A rate is `long?` and null is not zero.** `InstanceSize.RunningRatePerHourMinor` is null when nobody
has priced the tier and zero when somebody priced it at nothing.
`BillingRates.ForWorkload` returns `long?` so the compiler will not let a caller spend an unset rate
as a number, and `BillingTick` writes no ledger line for one — because a line of zero takes the
resource's slot in the unique index for the hour, and the corrected pass after somebody sets the price
would then collide with it and be discarded as "already charged".

**Every state is resolved from its own column.** Pricing a tier for a running workload does not vouch
for it stopped. That is written down in `InstanceSize` and it is why the resolver below keeps the two
apart rather than falling back from one to the other.

**A withdrawal is not a repricing.** `Plan.IsEnabled` takes a plan off the list offered to new
tenants and changes nothing for anybody already on it — "withdrawing is refused while anybody is on
it". The new per-server offer follows that precedent exactly.

The scheduling target is `Server`, not `Node`. `App.ServerId` and `ManagedService.ServerId` point at a
`Server` row; a `Node` links to one through `Node.ServerId` when it is a placement target. `Server`
already carries `Pool`, and `Plan.NodePool` and `Plan.AllowedSizeKeys` already say which servers and
which tiers a tenant may use — so the chooser needs no new field to know what to offer whom.

The `/servers` page announces itself as the deprecated HTTP agent and points at `/nodes`. Prices are
therefore **stored on `Server`**, because that is what the meter reads, and **edited from the node
page and `/plans`**, because those are the surfaces that are not deprecated.

---

## The model

### `InstanceSize.Family`

A string, defaulting to `general`, seeded as one of `general`, `cpu`, `memory`, `storage`.

A family belongs to the **tier**, not to the server. A `memory` tier is memory-heavy wherever it runs;
a server either offers it or does not. Putting the label on the server instead would create a second
source of truth that the offers could contradict — a box badged "memory-optimised" while offering only
general tiers — and nothing would report the disagreement.

A string rather than an enum because the provider can already add custom sizes, and a family nobody
anticipated must not be a migration. **A family this code does not recognise renders under its own raw
key rather than being hidden.** A priced tier that disappears from the chooser is capacity a customer
cannot buy and an operator cannot see they are not selling.

The keys, their bilingual labels and their display order live in one pure helper,
`InstanceSizeFamily`, beside the `InstanceSizeLabel` and `InstanceSizeKey` helpers that exist for the
same reason: the label was being built in four pickers, each with its own string.

### `ServerInstanceOffer`

A new entity. `ServerId`, `InstanceSizeKey`, `RunningRatePerHourMinor long?`,
`StoppedRatePerHourMinor long?`, `IsOffered bool`, with a unique index on
`(ServerId, InstanceSizeKey)`.

Keyed by the size's **key** rather than its id, for the reason the key is not editable: an app and a
managed service store the key, and the meter already builds its size dictionary from it.

Four states, and they must stay distinguishable:

| State | Meaning |
|---|---|
| no row | this server offers this tier at the global rate |
| row, rate null | offered, at the global rate |
| row, rate set | offered, at this rate |
| row, `IsOffered = false` | not offered to anything new |

The absent row and the null rate mean the same thing on purpose. A provider who has never opened the
pricing matrix has every server offering every tier at the global price, which is what the platform
does today — so switching this feature on changes nobody's bill.

**A price change applies immediately to everything on that server.** The meter resolves the rate live,
the way it already does for `InstanceSize`. The alternative — the immutable snapshot
`MailDomain.RatePerHourMinor` uses — was considered and rejected by the owner: it is fairer to an
existing tenant but costs two columns on `App` and two on `ManagedService`, and it means a provider
correcting a typo cannot correct the bills it produced.

---

## Resolving a rate

A pure class, no database, for the reason `BillingRates` and `PlanOverage` are pure: the money
arithmetic is then provable without a container.

```
ForWorkload(size, offer, state) ->
    offer's rate for that state, if set
    else the size's rate for that state, if set
    else null
```

Three rules that are not obvious and each of which has a test named after it:

**Each state still falls back on its own column.** An offer that prices `running` and leaves `stopped`
blank inherits the *global stopped* rate, never its own running rate. Crossing them would charge a
stopped workload the running price on exactly the servers where somebody was careful enough to price
one state and not the other.

**Null at every level stays null.** An unpriced tier on a server is not a free tier on that server.
It is not charged for the hour, and it is reported — once, with a key of
`unpriced-size:{serverId}:{key}:{state}`, so a forgotten price on a popular tier on one node is one
legible line rather than one per workload sitting on it. That is the de-duplication rule `Pass`
already enforces, extended by one field.

**`IsOffered = false` does not remove the price.** An app already running on a withdrawn tier is
charged at its proper rate; withdrawal only stops new placement. Reading a withdrawal as an unpriced
tier would stop billing for everything already on it — silently, and in the platform's favour — which
is the shape of failure the whole nullable-rate design exists to prevent.

`BillingTick` reads the offers once per pass, as it already reads every size once, into a dictionary
keyed by `(ServerId, InstanceSizeKey)`, and passes the app's or service's `ServerId` in. Nothing about
the pass's structure changes: the unknown counter, the withheld plan minimum, and the idempotence all
work on the resolved rate exactly as before.

---

## The monthly estimate

A pure helper. Hourly minor units × **730**, where 730 is 365 × 24 ÷ 12 — an average month, stated in
the file rather than left as a magic number.

Always rendered with `≈` and the word *estimate*. A month is 28 to 31 days, and the multiplication
also ignores that a workload will be stopped for some of it; a figure that presents itself as exact is
the figure a customer later disputes.

**No estimate is printed for a null rate.** Multiplying an unset price by 730 produces a very
convincing zero, which is the one thing the nullable rate columns exist to stop.

Guarded against overflow. This project compiles unchecked, so a nonsense rate multiplied by 730 would
wrap to a large negative and print a monthly *credit* rather than throwing.

---

## The chooser

One partial, `Design/_SizePicker.cshtml`, and one view model. Four consumers today: the application
create form, the app resize control, the database resize control, and the template deploy form. Those
are the same four places `InstanceSizeLabel` was written for, each of which had built its own line.

Three steps, in the shape a cloud console uses:

**The server.** A card each: name, the family badges derived from the tiers it offers, and its free
capacity. A server that is offline, or too full for anything, is **disabled with the reason written
on it** rather than merely dimmed or dropped. Filtered by `Plan.NodePool`, so a tenant is not shown a
pool they cannot be placed in.

**The family.** A tab strip built from the families the chosen server actually offers.

**The size.** A card each: vCPU, memory, disk, the exact hourly price, and the estimate beneath it. A
tier the chosen server cannot fit, or that `Plan.AllowedSizeKeys` excludes, is disabled with its
reason. Capacity is asked of the existing `SchedulerService.CheckAsync`, not recomputed.

The whole thing is radio inputs and works with no JavaScript, with every family visible; the script
only filters. The chooser replaces a `<select>` on all four forms, so a form that submitted
`instanceSizeKey` goes on submitting `instanceSizeKey` — plus `serverId`, which the create form
already posts.

---

## Pricing it, as the provider

A matrix per server: a row per tier, an offered checkbox, and running and stopped rate boxes using
the `MinorUnits.Box` convention every other money box on the panel uses.

**This is the one real trap in the feature.** An empty money box means "not priced" everywhere else on
`/plans`, and here it means "inherit the global rate". Those are opposite, and a note underneath will
not save somebody scanning a grid. So the inherited figure is shown **inside the box as its
placeholder** — `0.02 (global)` — which states the meaning at the point of confusion rather than
below it. The copy says it too.

Reachable from the node detail page and from `/plans`. Not from `/servers`, which is deprecated and
should not grow a feature.

---

## The two payment screens

**`/billing`.** The "what each thing cost" table becomes a card per resource: the name, kind, instance
size, server and running/stopped hours at the head; the lines that make it up in the body; the total
and the estimated monthly run-rate at the edge. The reconciliation sentence — payments plus signed
adjustments minus costs equals the balance movement — stays, because it is the only thing on the page
that proves the page adds up. The ledger's own sign handling is untouched: costs are still flipped to
positive once, at render.

**`/plans`.** The offered plans become proper cards carrying the hourly minimum and its estimate. The
instance-size list is grouped by family and gains a per-server price column where an override exists.
The `<details>` accordions that hide each plan's edit form become one open card per plan — the owner's
objection was specifically that a payment surface should not be an accordion, and this is the
accordion they meant.

---

## Tests

The pure classes get unit tests, which is how this codebase proves money arithmetic:

- rate precedence, including a state inheriting the global column while its sibling is overridden
- null at each level staying null, and zero staying a real zero
- a withdrawn tier still being charged for what is already on it
- 730, a null rate producing no estimate, and the overflow guard
- an unrecognised family rendering rather than vanishing

`BillingTick` gets cases for two servers at two prices inside one workspace, and for the unpriced
report naming the server once.

The pages get HTTP tests beside the existing `BillingPageHttpTests`.

The migration is generated against a **full build**. A migration built with `--no-build` captures the
model from a stale assembly; `MigrationConsistencyTests` is what catches it, and the point is not to
need it to.

---

## What this spec deliberately does not do

- **No new column on `Server` for what it is optimised for.** Derived from the tiers it offers.
- **No rate snapshot on `App` or `ManagedService`.** The owner chose live pricing.
- **No second notion of a month.** One helper, one constant, every surface.
- **Nothing on `/servers`.** It is deprecated and says so.
