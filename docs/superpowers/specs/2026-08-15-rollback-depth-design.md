# How far back you can actually roll back

**Date:** 2026-08-15 · **Status:** approved, ready for implementation

Sub-project **F**. B1 `f92c1cd`, B2 `964e39e`, B3 `3f150d2`, C `9fe3a7c`.

---

## The request, and what the code already had

The ask was "images should have a maximum". Sub-project A's decomposition already flagged the answer
and it holds: **the maximum exists.** `HarboraRuntimeOptions.ImageRetentionCount`
(`src/Harbora.Infrastructure/Deployments/HarboraRuntimeOptions.cs:47`) defaults to 5, and both the
pipeline (`DeploymentPipeline.cs:1130`) and the disk-cleanup service
(`DiskCleanupService.cs:195`) prune against it through the same `DeploymentPlanning.ImagesToPrune`.

Its own docstring says what it really is:

> How many rollback-eligible deployments keep their build image after a successful deploy. This is
> the real depth of "instant rollback": beyond it, an artifact rollback is impossible and the user
> must redeploy from source.

**No screen shows it.** So the Deployments tab offers a Rollback link beside every deployment in the
list, and for the older ones that link cannot do what it says — the image is gone and the rollback
will be refused. The refusal is correct and already handled (`AppsController.Rollback` re-checks and
lands the reason on the Deployments tab), but the person only finds out by pressing it.

**So F is not "add a maximum". It is: make the maximum visible where it changes what somebody does.**

---

## Architecture

The Deployments tab marks which entries can still be rolled back to instantly and which cannot, and
says why the line falls where it does.

**Derived, not stored.** Whether a deployment still has its image is already computable from the same
inputs `ImagesToPrune` uses. A second source of truth would drift from the pruner, and the drift
would show as a Rollback link that lies in the opposite direction — offered where it will fail.

**The number is shown once, in words, not as a bare setting.** "The last 5 deployments can be rolled
back to instantly; older ones must be redeployed from source" tells somebody what to do. A field
reading `ImageRetentionCount: 5` does not.

**Not settable from the panel in this sub-project.** It is a platform-wide runtime option read from
configuration, and the two callers use it per-app; a per-workspace override is a real feature with a
quota and a billing question attached, not a text box. Making it visible is what closes the reported
gap — someone who wants a different depth can already set it, and now knows what it means.

---

## The failure this removes

A Rollback link that cannot work is the promise-without-a-feature this project has spent five
sub-projects removing. Either the link goes, or it says plainly that this one needs a redeploy.

**The link stays and is marked**, rather than disappearing. Rolling back to an older deployment is
still possible — it just costs a rebuild — and hiding the control would remove a capability rather
than describing it. The same reasoning as do-not-change item 23.

---

## Testing

- A deployment inside the retention depth is offered instant rollback.
- One outside it is marked as needing a redeploy, and the marker is on that row rather than on the
  page.
- The boundary follows `ImageRetentionCount` rather than a literal 5: a test with a different
  configured value moves the line.
- The count of rollback-eligible entries matches what `ImagesToPrune` would keep — the two must not
  be able to disagree.

**On assertions that pass for the wrong reason.** The panel renders **Persian by default**, so assert
on `data-` attributes. And a page-wide assertion would be satisfied by any row — assert per row.

---

## What F is not

A per-workspace or per-app retention setting · changing the default · a disk-usage view, which
`DiskCleanupService` already has its own surface for · touching the pruning logic itself, which works.
