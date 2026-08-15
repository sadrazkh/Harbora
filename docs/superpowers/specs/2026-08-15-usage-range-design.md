# Usage over a window somebody chooses

**Date:** 2026-08-15 · **Status:** approved, ready for implementation

Sub-project **C**. B1 merged as `f92c1cd`, B2 as `964e39e`, B3 as `3f150d2`.

---

## The request, and why it is now much smaller than it was

The decomposition asked for "an app usage tab level with the database one, with detail". Exploring
found that sub-project A already delivered the first half and overshot it: `Views/Apps/Usage.cshtml`
carries **three** chart islands — `mem.used`, `cpu.percent`, `net.rx` — against the database tab's
two. The app tab is not behind; it is ahead.

**What is actually missing is the window.** `MonitoringController.Metrics`
(`src/Harbora.Web/Controllers/MonitoringController.cs:150`) already accepts `int minutes = 60`. The
chart island (`src/Harbora.Web/Scripts/main.ts:37`) never passes it, and no screen offers a choice.
So every chart in the panel shows the last hour, always, and the question people actually open a
usage page with — *is this normal, and when did it start* — cannot be answered past sixty minutes.

**So C is: a range control, wired through to a parameter that already exists.** Two of the three
pieces are built.

---

## Architecture

A small control on the Usage tab picks the window. The island passes it to the endpoint. The chart
redraws.

**The range lives in the URL**, as `?minutes=`. That makes a window shareable and survives a reload,
which matters for the case this is for: somebody noticing a spike and sending the page to a colleague.
Sub-project A made each tab a real route precisely so things like this could be linked to.

**Three windows: 1 hour, 24 hours, 7 days.** Not a free-form picker. The retention sweeper and the
rollup already decide how far back data goes; offering a window the store cannot fill produces an
empty chart that reads as "the app was idle" rather than "we do not keep that". If a longer window is
wanted later, it arrives with the retention change that makes it truthful.

**Both usage tabs get it** — apps and databases. The island is shared, and a control on one and not
the other is the kind of asymmetry that reads as a bug.

---

## The failure this must not have

An empty chart currently looks identical to a flat-zero chart. Widen the window past what the store
holds and the difference matters: "no data for this period" and "used nothing for this period" are
opposite facts about an app, and the second is a claim.

So a chart with no points for the chosen window says so. This is the same rule B3 settled for
health and uptime, applied to a series rather than a figure.

---

## Testing

- The endpoint honours `minutes`: a request for 24 hours does not return the 60-minute slice.
- The chosen range survives in the URL and comes back selected on reload.
- A window with no stored points renders the no-data state, not a zero line.
- Tenancy is unchanged: `Metrics` already refuses an app the caller cannot see
  (`MonitoringController.cs:169`), and the range must not become a way around that — a test pins it.

**On assertions that pass for the wrong reason.** The panel renders **Persian by default**, so assert
on `data-` attributes or query values. And asserting the page "contains 1440" would be satisfied by
any number anywhere — assert on the control's own markup.

---

## What C is not

New metrics — the three series already collected are the three shown · a metrics store change ·
retention changes · per-container breakdown for compose stacks · anything about cost, which the
billing work already covers.
