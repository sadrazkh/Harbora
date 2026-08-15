# Getting at what is inside a volume

**Date:** 2026-08-15 · **Status:** decomposition approved; D1 ready for a plan, D2–D4 need their own

Sub-project **D**, the last of the ten. A `f92c1cd`…`c41dd13` precede it.

---

## The request, and why this one really is big

The ask was four things: browse a volume, upload and download files, reach it from outside, and back
up a single volume.

> **CORRECTED 2026-08-16, and the correction is the important part of this document.**
>
> The paragraph that stood here said D was the first of the ten where most of it genuinely does not
> exist. **That was wrong.** `AppDataController` (`src/Harbora.Web/Controllers/AppDataController.cs`,
> routed at `apps/{id:guid}/data`) already offers browse, download, upload, save, delete and
> create-folder over `VolumeFileService`, using the same `RunOneOffAsync` primitive and the same
> `alpine:3.20` helper this spec proposed to introduce. `Views/Apps/_Shell.cshtml:45` already links to
> it from every app tab.
>
> So **D2 and D3 were already built.** The tenth time this programme has found a capability present,
> and the third time the author of a spec was the one who did not check.
>
> **How it was missed, because the method matters more than the fact:** the search was for
> `BrowseVolume` and `FileBrowser` — the names the feature *would* have had. It lives under `AppData`.
> Searching for the name you expect finds only the code you already imagined.

| Part | What exists today |
|---|---|
| Browse | **Already built** — `AppDataController` + `VolumeFileService`, linked from every app tab |
| Upload / download | **Already built** — same controller: `upload`, `download`, `save`, `delete`, `folder` |
| External access | **Nothing for volumes.** SFTP exists only as a backup *destination* — where archives are sent — not as a way into a customer's data |
| Per-volume backup | **Wired.** `BackupTargetType.DockerVolume = 1`, with `ValidateVolume` and `StageVolumeAsync` in `BackupTargetResolver` (`:99`, `:116`) |

---

## Decomposition, because one spec cannot hold this

| | Sub-project | What it delivers |
|---|---|---|
| **D1** | **Back up one volume** — done, merged `378ecfe` | The button for a capability that already works |
| ~~D2~~ | ~~See what is in there~~ | **Withdrawn — already exists.** See the correction above |
| ~~D3~~ | ~~Get files in and out~~ | **Withdrawn — already exists.** Same controller |
| D4 | Reach it from outside | A time-bounded external route to one volume |

**D1 is first and is small**, exactly like sub-project E turned out to be: the target type is wired, so
what is missing is the control on the page. It ships on its own and is worth having on its own.

**D2 before D3, and D3's two halves in that order**, because reading is recoverable and writing is
not. A wrong listing is a confusing screen; a wrong upload overwrites a customer's data with no undo.

**D4 last** because it is the one with a real operational cost to get wrong, and because D2 and D3
answer most of what people actually want — which is usually "what is in there" and "give me that
file", not "mount it on my laptop".

---

## The constraint that governs D2, D3 and D4, written here so each inherits it

Reading and writing inside a customer's volume is a capability with a blast radius, and this codebase
already has the precedent for how to treat that. `BackupTargetResolver.ValidateDirectory` refuses a
directory target unless it sits inside a configured root, and its comment says why in terms worth
reusing:

> Fails closed: with no roots configured, no directory can be backed up. The alternative default —
> any absolute path — would mean that enabling the feature quietly grants the ability to read
> `/etc`, or the panel's own data directory including its master key, and to download the result.

**Every one of D2, D3 and D4 must fail closed the same way**, and each spec must say so in its own
words rather than inheriting it by assumption. A path that escapes the volume is the whole risk of
this sub-project, and it is a correctness requirement, not a security exercise — the owner's standing
instruction keeps security review out of scope, and what is being decided here is which bytes the
product will read and write on somebody's behalf.

**A second constraint, from B2:** a volume belongs to an app, and an app belongs to a workspace. The
cross-tenant defect fixed in `6b0f91a` was exactly a name-based lookup that crossed that boundary.
Every volume operation resolves through the app, and the tenant filter that already covers `App` is
what makes that safe — never through a volume name taken from a request.

---

## D1: back up one volume

**What it is.** The Volumes tab that sub-project A built lists an app's volumes. Each row gains a
"back up now" action, and the tab says when each was last backed up.

**Reuse, do not rebuild.** `BackupTargetType.DockerVolume` is validated and staged already, and
sub-project E has just built the equivalent control for a whole application — including where its
outcome lands so the message is actually seen. Follow what E did rather than inventing a second shape
for the same action.

**What it must not claim.** A volume that has never been backed up says so; it does not show a blank
that reads as a bug. The same rule B3 settled for health and uptime, and E for what a backup contains.

**Testing.** A volume backup contains that volume's data and not a sibling's · a volume with no
backup history says so · the action is refused for an app in another workspace · the outcome lands on
a page that renders it.

---

## What D is not

Anything about managed-database storage, which has its own backup path · changing the backup module's
storage or encryption · a general file manager for the host — D2 and D3 are about one volume at a
time, reached through the app that owns it.
