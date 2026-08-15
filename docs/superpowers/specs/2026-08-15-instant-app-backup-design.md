# Back this app up, now

**Date:** 2026-08-15 · **Status:** approved, ready for an implementation plan

Sub-project **E**. A `f92c1cd`…`04a3a1e` precede it.

---

## The request, and the correction that has to come first

The ask was: back up or restore an app right now, from its own page.

**I told the owner twice that this needed a data-model change** — that `BackupTargetType` had no
application member, so E would open the schema. **That was wrong, and it was wrong because I repeated
sub-project A's decomposition note without checking it.**

`src/Modules/Backup/Harbora.Modules.Backup.Contracts/BackupEnums.cs:58` declares `Application = 0` as
the **first** member of the enum. And it is not a declaration with nothing behind it:
`BackupTargetResolver.Validate` (`:91`) routes it to `ValidateApplication`, and `AcquireAsync` (`:107`)
routes it to `applications.StageAsync`. The application target is wired end to end.

Reading what it stages: it `Include`s `a.Volumes` and `a.EnvironmentVariables`, writes volume data
under a `volumes/` directory one per volume, and **refuses rather than silently skipping** a volume
whose name the daemon would reject — with a comment saying why: *"finding that out during a restore
is too late."*

**So E is the ninth time this programme has found the capability already present.** What is missing is
narrower than the request implied, and the spec's first job is to find out exactly how narrow.

---

## What is actually missing

**Task 1 of the plan is a verification task, not a build task.** Establish, by reading and by test,
which of these three an application backup captures today:

| | Status from this exploration |
|---|---|
| Volumes | **Confirmed captured** — `Include(a => a.Volumes)`, staged one directory per volume |
| Environment variables | **Confirmed captured** — `Include(a => a.EnvironmentVariables)` |
| Image reference | **Not confirmed either way.** Nothing in what was read mentions it |

The owner has decided the answer should be **all three**: the data, the config, and the image
reference, so that a restore has something to restore *onto*. If the image reference is absent, adding
it is E's real content. If it is present, E is only the button.

**Do not design past that answer.** This spec has already been wrong once by assuming instead of
checking.

**And the button is missing regardless.** The request was "instant" — from the app's own page, not
from the backup centre with a target type to choose and a GUID to paste.

---

## Two questions the all-three decision forces, and neither may be left to a default

### The image reference may no longer resolve

A registry prunes; a tag moves. A backup that names an image nobody can pull is a backup that restores
into nothing, and the moment to say so is when somebody is looking at the restore screen — not after
they have pressed it.

**Reuse sub-project F's language rather than inventing a second vocabulary for the same fact.** F
already faced this for deployments: it marks rows that can still be rolled back to instantly and
distinguishes them from rows that need a redeploy from source, deriving the answer from the pruner's
own rule so the two cannot disagree. A restore whose image is gone is the same situation wearing
different clothes, and calling it something else would leave one product with two names for one idea.

### An app backup carries secrets by definition

`EnvironmentVariable` has an `IsSecret` flag. Capturing environment variables therefore means
capturing secret values — that is not an edge case, it is the ordinary path.

Two things must be decided out loud rather than falling out of whatever the serializer does:

1. **What the backup file holds.** The platform already has an encryption path for backups; this must
   state that secret values travel inside it and never beside it.
2. **What a restore into a *different* workspace does.** Restoring an app into another tenant, which
   the panel can express, would otherwise hand one customer's secrets to another. Whatever the rule
   is — refuse, or restore with secrets blanked and say so — the spec that ships must contain it as a
   sentence, not as an implementation detail somebody infers later.

This is a data-handling decision, not a security review, which remains out of scope by the owner's
standing instruction. What is being decided is what the product writes into a file and where that
file may be restored.

---

## Where it appears

A control on the app's own page — the Overview tab, beside the address and specifics blocks that
sub-projects B1, B2 and B3 built there — that takes a backup now and lists what this app already has.

**A cron or release-task app has no volumes and no running container**, and the control must say what
it would capture rather than offering an action that produces an empty archive. The same rule B3
settled for health and uptime: say what is known, and say when there is nothing.

---

## Testing

- An application backup contains this app's volumes and its environment variables — asserted against
  what was staged, not against the code path having run.
- A secret variable's value does not appear in plaintext in the artefact.
- A restore whose image reference no longer resolves is reported before it is attempted, in F's
  vocabulary.
- A restore into another workspace follows whatever rule Task 1 settles, and the test names that rule.
- An app with no volumes gets an honest description of what a backup would contain, not an empty
  archive presented as a success.

**On assertions that pass for the wrong reason.** The panel renders **Persian by default**, so assert
on `data-` attributes and on staged content. And "the archive contains the app slug" is satisfied by a
file name — assert on what was actually staged.

---

## What E is not

Volume browsing and upload (sub-project D) · scheduling, which backup policies already do · changing
the backup module's storage or encryption · a new target type; `Application` exists and is wired.
