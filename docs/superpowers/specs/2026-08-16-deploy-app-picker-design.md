# A deploy that shows you the apps, and a slug the server actually recognises

**Date:** 2026-08-16 · **Status:** approved, ready for an implementation plan

`harbora deploy Kousar-kolie` failed twice in a row against `platform.irnetfree.info` with

```
Error: Error while copying content to a stream.
```

after packing 311 files and announcing a 3.1 MB upload. Nothing about that message names the problem.
This spec covers the defect behind it, the two ways the CLI made it invisible, and the CapRover-style
app picker the owner asked for in the same breath.

---

## What actually happened

The app's slug on the server is `kousar-kolie`. The owner typed `Kousar-kolie`.

The CLI matched the two. `Commands.cs` finds the app in the list it fetched from `GET /apps` with
`a.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)` — so the app was found, `canServerPull`
was read correctly, and the deploy mode was chosen correctly. But the match assigns nothing back:
`slug` stays as the user typed it, and it is the typed string that goes into the upload URL.

The server does not match the two. `ApiV1Controller.DeployArchive` looks the app up with
`a.Slug == slug`, which on PostgreSQL is an ordinal comparison. Probed directly:

```
POST /api/v1/apps/Kousar-kolie/deploy/archive  →  404 {"error":"App not found."}
POST /api/v1/apps/kousar-kolie/deploy/archive  →  400 {"error":"Send the project as a gzipped tar…"}
```

So the request was a 404 both times. The owner never saw it, for two separate reasons.

**The error was masked in flight.** `DeployArchive` returns `NotFound` before it reads a byte of the
body, and the connection is torn down while the CLI is still writing 3.1 MB into it. The write fails,
`HttpContent.CopyToAsync` wraps whatever the transport threw in `HttpRequestException` with the
generic text *"Error while copying content to a stream."*, and the real 404 — response body and all —
is discarded. `PostFileAsync` sends no `Expect: 100-continue`, so the server had no way to refuse
before the body was on the wire.

**The bad name was then made permanent.** The first run wrote `app: Kousar-kolie` into
`harbora.yml`, and `RememberApp` never overwrites an existing config. Every later `harbora deploy` in
that folder — with or without an argument — reproduces the same 404 forever, with no way out short of
editing the file by hand.

That last part is what makes this worth more than a one-line patch. A typo that a case-insensitive
match quietly tolerated became a permanent, self-reinforcing, undiagnosable failure.

---

## The four changes

### 1. The server's slug is the only slug

Wherever `Commands.cs` resolves a `RemoteApp` — from the picker, from a case-insensitive match, from
a single-app account — it assigns `slug = app.Slug` immediately afterwards. From that point the typed
string is not used again.

This is the root-cause fix. `GET /apps` is the only place the truth about a slug lives, and the CLI
already had it in hand; it simply preferred what the user typed. Every other change here is about the
failure being invisible, not about the failure happening.

### 2. `harbora deploy` shows the apps, the way CapRover does

The picker becomes the default rather than a fallback for the "no name anywhere" case.

- **Interactive, no `--yes`:** the list is shown. The app named by `--app`, then `harbora.yml`, then
  the stored project slug, is moved to the **top** of the list and labelled `(current)`. Spectre's
  `SelectionPrompt` cannot pre-highlight a choice, so position carries that meaning instead; pressing
  Enter accepts the current app, which is the common case.
- **`--yes` / `-y`, or no terminal (CI, pipes, redirected input):** no prompt at all. The resolved
  name is used exactly as it is today, and today's error messages for "no app named" and "no such
  app" stay as they are. `Interactive.IsAvailable` already draws this line correctly and is reused.
- **An account with exactly one app** is selected without a prompt. A one-item menu asks a question
  with no answer.
- **An account with no apps** keeps today's message pointing at the panel.

`--yes` is new on the deploy command and means only "do not ask me anything"; it grants no other
permission and changes no deploy semantics.

### 3. Choosing a different app offers to update `harbora.yml`

When the chosen slug differs from what the folder's config holds — including differing only in case,
which is exactly the situation that caused this bug — the CLI asks once:

```
harbora.yml says Kousar-kolie. Update it to kousar-kolie? [y/n]
```

On yes, **only the `app:` line is rewritten.** The file is read, the single line whose key parses as
`app` or `name` is replaced, and everything else is written back untouched. A project that has grown
`dockerfile:`, `context:`, `ignore:` or `dockerfileLines:` entries does not lose them to a config the
CLI regenerated from two fields. If the file has no `app:` line at all, one is appended.

On no, the deploy proceeds with the chosen app and the file is left alone — the next run will ask
again, which is the honest behaviour for an answer the user declined to make permanent.

`RememberApp` keeps its current job unchanged: writing a fresh `harbora.yml` when the folder has
none. The new path is a separate method for the folder that already has one, so the "never
overwrite" rule stays true of the thing it was written to protect.

### 4. A server error stops pretending to be a stream error

`ApiClient.PostFileAsync` sets `request.Headers.ExpectContinue = true`. The server then gets its
chance to answer 404, 403 or 413 before the body is sent, and `HttpClient` returns that response for
`ReadAsync` to turn into the real message.

`ExpectContinue` is a courtesy, not a guarantee — a proxy may drop the header, and the server may
still reject mid-body. So the send is also wrapped: an `HttpRequestException` raised while copying
the request content is rethrown with text that names the upload and the endpoint instead of the
stream, and preserves the inner exception. The goal is not to make every failure legible; it is that
no failure is ever again reported as a fact about a stream when it is a fact about an app name.

---

## Testing

The selection rules move out of `ExecuteAsync` into a pure function so they can be tested without a
terminal or a server — the same treatment, and for the same reason, that `DeployPlan.Decide` already
gets: these are the rules users will argue with.

```
AppChoice.Resolve(typedSlug, apps, interactive, yes) → (RemoteApp? Current, bool NeedsPrompt, string? Error)
```

`Current` is the app the typed name resolved to, or null when nothing matched. When `NeedsPrompt` is
true the caller shows the list with `Current` first and labelled, and takes the answer; when it is
false, `Current` is the app to deploy and `Error` — set only when `Current` is null — is the message
to print before giving up.

Tests, in `tests/Harbora.Tests`:

1. **Regression, and the reason this spec exists.** A typed slug of `Kousar-kolie` against an app
   whose slug is `kousar-kolie` produces an upload to `apps/kousar-kolie/deploy/archive`. Asserted
   against the request URI seen by a stub `HttpMessageHandler` — `ApiClient`'s handler-injecting
   constructor exists for exactly this.
2. `--yes` and a non-interactive console each suppress the prompt; neither changes which app is
   chosen.
3. The current app is first in the offered order, and is the only one labelled.
4. No apps, and an unknown name with `--yes`, keep today's errors.
5. Rewriting `app:` preserves `dockerfile:`, `ignore:` and `dockerfileLines:` in the same file, and
   an absent `app:` line is appended rather than silently dropped.
6. `ExpectContinue` is set on the archive upload request.

The prompts themselves are not tested; `Interactive` stays a thin shell over Spectre, and everything
worth asserting is in the function it calls.

---

## What this does not do

**The server keeps comparing slugs ordinally.** Making `DeployArchive` case-insensitive would hide
the class of bug rather than fix it, and slug lookup is a hot, indexed path on every deploy. The CLI
now sends the canonical slug; a hand-written `curl` with the wrong case still gets an honest 404.

**No slug normalisation on app creation** is added here. Whether the panel should lowercase a slug as
it is created is a separate question about existing data, and nothing in this failure depended on it.

**Nothing is fixed for the owner until the CLI is rebuilt.** The failing binary is the installed
`0.2.0` at `C:\Users\sadra\AppData\Local\Harbora\harbora`, not the checkout. The implementation plan
ends with rebuilding and reinstalling it, and with one real `harbora deploy` against
`platform.irnetfree.info` — the deploy that has failed twice is the acceptance test.
