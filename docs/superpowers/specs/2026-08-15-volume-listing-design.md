# Seeing what is in a volume

**Date:** 2026-08-15 · **Status:** approved, ready for an implementation plan

Sub-project **D2**, the second of four in volume management. D1 merged as `378ecfe`.

---

## The request, and the one thing exploring found

The ask was to browse a volume from the panel: what is in there, how big, how old.

**Nothing lists a volume's contents today** — D's decomposition established that, and it holds. But
the mechanism to reach inside one is not missing: `IDockerEngine.RunOneOffAsync(DockerOneOffRequest)`
(`src/Harbora.Application/Abstractions/IDockerEngine.cs:57`) runs a throwaway container, and
`DockerOneOffRequest` carries mounts. It is the same primitive the backup stager uses to read a
volume and `AdminerService` uses to offer a database tool.

**So D2 is a listing built on an existing primitive, not a new way into the host.** That distinction
is the whole safety story: nothing here gains filesystem access the platform did not already have.

---

## Architecture

**A one-off container with the volume mounted read-only, which lists and exits.**

**Read-only, and that is a design decision rather than a default.** D2 does not write. D3 does, and it
is a separate sub-project precisely so that the screen people reach for first cannot damage anything.
The mount is `ReadOnly: true` and the test says so — a mount that could write, in a feature that never
writes, is a capability nobody asked for sitting where somebody will later find it convenient.

**The listing is one directory at a time, not a recursive walk.** A volume can hold a million files.
A recursive listing is a request that never returns on somebody's node_modules, and the honest shape
is the one every file browser already uses: this directory, with a way down.

**Paths are resolved inside the container, never composed in C# from user input.** The path the person
navigates to is passed as an argument to the listing command, and the mount root is what bounds it.

---

## Failing closed, in this sub-project's own words

`BackupTargetResolver.ValidateDirectory` states the rule this inherits, and its reason is worth
repeating rather than referencing:

> Fails closed: with no roots configured, no directory can be backed up. The alternative default —
> any absolute path — would mean that enabling the feature quietly grants the ability to read
> `/etc`, or the panel's own data directory including its master key, and to download the result.

For D2 the equivalent is: **a path that leaves the mount root produces nothing, not a listing of
somewhere else.** `..` segments, absolute paths and symlinks that point outward are all the same
answer — refused, and the screen says the path is not in this volume rather than showing an empty
directory, which would read as "this folder is empty".

**And the volume is reached through its app**, never through a name in the request — the rule D1
already follows, and the shape of the cross-tenant defect fixed in `6b0f91a`.

This is a correctness requirement about which bytes the product reads on somebody's behalf. Security
review remains out of scope by the owner's standing instruction.

---

## What the screen shows

A tab or panel on the app's Volumes surface: entries with name, size, and modified time, a way into a
directory, and a way back up that cannot go above the root.

**An empty volume says it is empty.** A volume the panel could not read says that instead, with the
reason — the rule B3 settled for health and uptime and E for backup contents. The two are different
facts and this project has spent nine sub-projects making sure they look different.

**A volume on a remote node** may not be listable if the one-off primitive does not reach there. Find
out during the plan's first task rather than assuming, the way sub-project B3 found that
`NodeWorkloadEngine` had no inspect verb — and if it does not, say so on the screen as a permanent
condition rather than an error.

---

## Testing

- A listing shows the files that are in the volume, from a seeded fixture rather than a constant.
- `..`, an absolute path and a symlink pointing outside the mount each produce a refusal, and the
  refusal is distinguishable from an empty directory.
- The mount is read-only — asserted on the request the engine was given.
- An app in another workspace cannot list its volumes.
- A volume the panel cannot reach says so, and does not render as empty.

**On assertions that pass for the wrong reason.** The panel renders **Persian by default**, so assert
on `data-` attributes and on the request handed to the engine. And "the page contains the file name"
is satisfied by a breadcrumb — assert on the listing rows.

---

## What D2 is not

Writing anything — upload and download are D3, deliberately after this · external access, which is
D4 · a host file manager; this is one volume at a time, reached through the app that owns it ·
previewing file contents, which is a bigger question than listing and belongs with D3 if anywhere.
