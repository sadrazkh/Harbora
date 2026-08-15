# The guide is written. It just is not in the product.

**Date:** 2026-08-15 · **Status:** approved, ready for an implementation plan

Sub-project **G**. A `f92c1cd`…`6b0f91a` precede it.

---

## The request, and what exploring it changed — again

The ask was a Learning Centre: guides with screenshots, a Help button on every page, worked routing
examples.

**Most of it is written.** `docs/tutorial/` holds nine chapters — first steps, projects and
environments, applications, databases and brokers, storage, networking, operations, AI,
administration — plus a README, 61 images and an `annotations.json`.

None of it is reachable from the panel. A customer looking at a screen has no way to get from that
screen to the page that explains it, and the material was written for exactly that moment.

**So G is mostly a routing and surfacing job, not a writing job.** That is the fifth time this
programme has found the capability already present and unreachable, and it is the same shape each
time.

---

## A blocker that must be settled before a single image is served

**31 of the 61 images in `docs/tutorial/img/` are raw captures**, not `*.annotated.png`.

This project has a standing rule that only annotated captures belong in the repository, and the
reason is recorded: **the raw panel screenshots leaked a webhook secret once.** Raw captures are
whole-screen shots of a working panel, so they carry whatever was on screen.

Serving them from inside the product would take a repository problem and turn it into a published
one, on a page whose whole purpose is that customers look at it.

**So step one of G is not a feature.** It is:

1. Establish which of the 31 raw files are still referenced by a chapter.
2. Decide, per file, whether an annotated version exists to use instead.
3. Remove from the repository the raw captures that should never have been committed, and re-take
   what the guides genuinely need in a form that is safe to publish.
4. Only then serve images at all.

**And the Learning Centre must serve only `*.annotated.png`, enforced by a test rather than by
discipline.** A rule that lives in somebody's memory is the rule that gets broken by the next person
adding a chapter — this programme has now watched that happen five times with four different rules.

**This is a data-handling decision, not a security review**, which remains out of scope. What is
being decided is which files the product publishes.

---

## Architecture, once the images are safe

**The chapters stay as markdown in `docs/tutorial/` and are rendered by the panel.** Not copied into
views, not rewritten as Razor. One source, so a chapter edited for the docs site is the chapter the
panel shows, and the drift that makes documentation untrustworthy cannot start.

**A Help control on every page, and it is context-aware.** A Help button that opens a table of
contents is a link to a filing cabinet. It should open the chapter for the screen the person is on —
the applications chapter from an app page, the networking chapter from domains. Sub-project A made
each tab a real route, which is what makes that mapping possible.

**A page with no mapped chapter says so and offers the index**, rather than opening something
unrelated. An unhelpful Help button is worse than a missing one, because it costs a click to learn
it is unhelpful.

**The routing worked examples belong in the networking chapter**, not in a new place. That was the
original decomposition's decision and it still holds: what was asked for is a guide and an example,
not a new capability.

---

## Screenshots go last, deliberately

The original decomposition put G last because its screenshots go stale the moment A–F change the
screens they document. A–F have now changed: the app page was split into tabs, and it gained an
address block, a private address, a specifics card, a usage window and a rollback-depth marker.

**Several of the 30 annotated images are already wrong.** So the order within G is:

1. The image safety pass above.
2. The chapter rendering and the Help control — these do not depend on any image being current.
3. Re-taking the captures the changes invalidated, last.

Steps 1 and 2 deliver the thing that was asked for. Step 3 is upkeep and can follow.

---

## Testing

- Every chapter in `docs/tutorial/` is reachable from the Learning Centre — a census, reading the
  directory rather than a hand-kept list, following `DetailTabCensusTests` and `AppAddressCensusTests`.
- Every image the panel serves matches `*.annotated.png`. **This is the test that matters most**, and
  it must be provable by adding a raw file and watching it fail.
- Every mapped screen's Help control points at a chapter that exists.
- A screen with no mapping opens the index and says so, rather than 404ing or opening chapter one.
- The renderer does not execute markup embedded in a chapter.

**On assertions that pass for the wrong reason.** The panel renders **Persian by default**, so assert
on route fragments, `data-` attributes and file names. A page-wide check for `.annotated.png` would be
satisfied by one correct image among ten wrong ones — assert over the whole set the panel would serve.

---

## What G is not

New capability — the routing guide documents what exists · a docs site · translating the nine
chapters, which are already written in the language they are written in · replacing `README.md` or
the operator runbook, which serve different readers.
