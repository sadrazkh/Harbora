# Reaching a volume from outside the panel

**Date:** 2026-08-16 · **Status:** approved, ready for an implementation plan

Sub-project **D4**, the last piece of volume management and the last of the ten improvements.
D1 merged as `378ecfe`; D2 and D3 were withdrawn because they already existed.

---

## The request, and what searching properly turned up

The ask was to reach a volume from outside — to work against its files with ordinary tools rather
than through a web page.

**This one is genuinely absent**, and that claim is made carefully, because the previous spec in this
series claimed the same thing about browsing and upload and was wrong. The method that missed it then
was searching for the name the feature *would* have had. So this time the search was by what the
thing would *do*: mount, one-off, tar, expose, tunnel, gateway.

What that found:

- **SFTP exists only as a backup destination** — `IBackupStorage`, the `SftpBackupDestination`
  migration. It is where archives are *sent*, not a way into a customer's data. Confirmed, not
  assumed.
- **No SFTP, WebDAV or FTP service** anywhere under `Infrastructure/Services` or `Infrastructure/Storage`.
- **But the pattern for temporary external access is already here and is well made**, and it is the
  thing to copy rather than invent alongside.

---

## The precedent this must follow rather than parallel

`AdminerService` gives somebody a throwaway web tool onto a managed database. `AdminerSession`
(`src/Harbora.Infrastructure/Services/AdminerSession.cs`) states the rule that makes it safe to
offer at all:

```csharp
public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
public static bool Expired(DateTimeOffset startedAt, DateTimeOffset now) => now - startedAt >= Lifetime;
```

and a sweeper (`AdminerService.cs:186`) retires anything past it. There is also a `Supports(type)`
gate, so the button is never drawn for an engine the tool cannot speak to — the panel's standing rule
that a control which cannot work should not be offered.

**D4 is that shape, applied to a volume:** a time-bounded, self-retiring way in, offered only where it
can actually work, and reachable through the app that owns the volume.

**Reuse the session vocabulary rather than restating it.** A second notion of "how long is a temporary
thing allowed to live" would drift from the first, and the two would answer differently on the same
question. If the lifetime should differ for a volume, that is a value, not a second mechanism.

---

## The three decisions this spec cannot make alone

These are the owner's, and the plan must not begin before they are answered. Each changes what gets
built, not merely how.

**1. What protocol.** SFTP is what people expect and what every file tool speaks. WebDAV mounts as a
drive on Windows and macOS without extra software. A time-limited HTTPS download link is the smallest
thing that would satisfy "get that file out", and D2/D3's existing screen may already cover enough of
that to make D4 narrower than it looks.

**2. How somebody authenticates**, and this is the part where the honest answer may be "not yet". The
panel does not hand out credentials today, and inventing a credential store for one feature is how a
platform grows a second identity system. What already exists that could be borrowed must be
established before anything is designed.

**3. How it is reached.** The panel is behind Traefik with certificates. SFTP is not HTTP and does not
route through it, so a port has to be opened somewhere and that is an operator decision with a real
cost — the kind that should be a configured, deliberate opt-in rather than something a customer
button turns on.

---

## What is settled regardless of those answers

**Time-bounded and self-retiring**, on `AdminerSession`'s model. An access that outlives the person's
attention is the failure mode; the sweeper is not an optimisation.

**Reached through the app that owns the volume**, never through a volume name in a request. D1 does
this, and the cross-tenant defect fixed in `6b0f91a` was exactly the opposite shape.

**Offered only where it can work.** If a remote node cannot support it — and the node channel has
already proved narrower than expected twice, with no inspect verb and a one-off that dropped its
output until `6593235` — the control says so as a permanent condition rather than failing when
pressed.

**Fails closed.** The rule `ValidateDirectory` states and D2's spec repeated: with nothing configured,
nothing is reachable. The default must not be "any path".

---

## Testing

- An access expires and is retired without anybody pressing anything.
- It is refused for an app in another workspace.
- With nothing configured, no access can be created — and the screen says why rather than offering a
  button that fails.
- Whatever the protocol answer is, a path outside the volume is not reachable through it.

**On assertions that pass for the wrong reason.** The panel renders **Persian by default**; assert on
`data-` attributes and on the request handed to the engine. And an expiry test that passes because
nothing was ever created proves nothing — assert the thing existed first.

---

## What D4 is not

Browsing, upload and download — those exist at `apps/{id}/data` · per-volume backup, which D1 shipped ·
a general file server; this is one volume, for a bounded time, reached through its app.
