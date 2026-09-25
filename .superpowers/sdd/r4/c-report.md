# C — Report: menu-open scroll lock

## Status: DONE

## Commit

- `edd81ed` — "Lock page scroll behind the mobile slide-over menu"
  (branch `r4-menu-scroll`, committed as `sadra zadeh khameneh <1986.aandrii@gmail.com>`, no
  co-author trailer, per CONSTRAINTS.md)

## The real scroller

Confirmed by reading the layout, not assumed: `body` never scrolls in this app (`<body
class="h-full">`, `<html class="h-full">`). The element that actually scrolls the page content is
the inner `<div class="flex min-h-0 flex-1 flex-col overflow-y-auto 2xl:flex-row">` in
`src/Harbora.Web/Views/Shared/_Layout.cshtml` (the flex child that wraps `<main>` and the optional
right rail). I gave it `id="content-scroll"` so the script can target it directly. Setting
`overflow: hidden` on `body` alone — the trap the brief warned about — would have compiled, looked
plausible, and done nothing, since `body` was never the scrolling box to begin with.

## What changed

- `src/Harbora.Web/Scripts/mobileSidebar.js` (new): the slide-over's behaviour, extracted out of
  the inline `<script>` in `_Layout.cshtml` so it's an importable module.
  - `createMobileSidebar({ sidebar, backdrop, scroller, links })` is the pure, DOM-agnostic core:
    open/close, lock/restore `scroller.style.overflow` (saving whatever the original value was —
    including `''` — and restoring exactly that, never a hard-coded default), close on backdrop
    click, close on any sidebar link click, close on `Escape`.
  - `initMobileSidebar(doc)` wires the real `#sidebar` / `#backdrop` / `#content-scroll` /
    `[data-toggle-sidebar]` elements and is what the app actually calls.
- `src/Harbora.Web/Scripts/main.ts`: imports `initMobileSidebar` and calls it once, replacing the
  old inline IIFE (theme block and form-loading block untouched, as scoped).
- `src/Harbora.Web/Views/Shared/_Layout.cshtml`: added `id="content-scroll"` to the real scrolling
  div; removed the old inline mobile-sidebar IIFE (now just a comment pointing at the module).
- `src/Harbora.Web/Views/Shared/Design/_Sidebar.cshtml`: added Tailwind's `overscroll-contain` to
  the sidebar's own scrollable `<nav>`, so flicking to the end of a long menu no longer chains into
  scrolling the page behind it. Verified the compiled CSS actually contains
  `overscroll-contain{overscroll-behavior:contain}` after `npm run build` (Tailwind's content glob
  already scans `./Views/**/*.cshtml`, so it isn't purged).
- `src/Harbora.Web/Scripts/tests/mobile-sidebar.test.mjs` (new): `node --test` coverage against
  plain fake objects (no DOM/jsdom needed) for: opening locks the scroll; closing restores it;
  closing via a sidebar link restores it; the backdrop closing restores it; Escape closes and
  restores; a non-Escape key is a no-op; Escape while already closed is a no-op; and — the specific
  case the brief called out — the restored value is the *original* value, not a plausible default
  (tested with originals of `'scroll'`, `''`, and `'overlay'`, none of which are the locked value
  `'hidden'` or a typical default like `'auto'`).
- `.github/workflows/ci.yml`: the "Test deployment log recovery" step named
  `deployment-logs.test.mjs` explicitly, so the new test file would have existed and never run.
  Changed it to `node --test src/Harbora.Web/Scripts/tests/*.test.mjs` (renamed the step "Run the
  Scripts/tests suite"). I deliberately used a glob rather than a bare directory path
  (`.../tests/`): I verified locally on this Node version (22.13.0) that `node --test <directory>`
  does **not** discover files — it falls through to the CJS loader trying to `require()` the
  directory itself and fails with `ERR_UNKNOWN_FILE_EXTENSION`/`MODULE_NOT_FOUND`. The glob form
  was verified to correctly pick up both `deployment-logs.test.mjs` and the new
  `mobile-sidebar.test.mjs`.

## Verification

### node --test (run directly against the glob CI now uses)

```
$ node --test src/Harbora.Web/Scripts/tests/*.test.mjs
TAP version 13
# Subtest: backfill reconciles repeated live messages without removing legitimate repetitions
ok 1 - ...
# Subtest: failed initial request preserves logs and schedules recovery
ok 2 - ...
# Subtest: unmount prevents further polling
ok 3 - ...
# Subtest: completed deployments receive bounded trailing-log checks
ok 4 - ...
# Subtest: opening the sidebar locks the page scroll
ok 5 - ...
# Subtest: closing restores the original overflow value, not a hard-coded default
ok 6 - ...
# Subtest: the original value round-trips even when it was the empty string
ok 7 - ...
# Subtest: closing via a sidebar link restores the scroll
ok 8 - ...
# Subtest: the backdrop click closes and restores the scroll
ok 9 - ...
# Subtest: Escape closes the open sidebar and restores the scroll
ok 10 - ...
# Subtest: Escape while already closed is a no-op
ok 11 - ...
# Subtest: a non-Escape key does not close the sidebar
ok 12 - ...
1..12
# tests 12
# suites 0
# pass 12
# fail 0
# cancelled 0
# skipped 0
# todo 0
```

All 12 tests pass — 4 pre-existing (`deployment-logs.test.mjs`) + 8 new (`mobile-sidebar.test.mjs`).

### npm run build (run separately, not while dotnet test was running)

`cd src/Harbora.Web && npm ci && npm run build` → **succeeded**, exit 0.

```
✓ 1838 modules transformed.
wwwroot/build/manifest.json                     1.96 kB
wwwroot/build/assets/main-CWoJAiE5.css        129.48 kB
...
✓ built in 14.75s
```

Confirmed afterward: `wwwroot/build/manifest.json` exists and contains the key `"Scripts/main.ts"`
(the same check CI's "manifest Razor reads must exist" step makes), and the compiled CSS contains
`overscroll-contain{overscroll-behavior:contain}`.

### dotnet test (run separately, after the npm build had already finished — never concurrently)

```
export MSBUILDDISABLENODEREUSE=1
dotnet test Harbora.slnx --nologo -v q -p:UseSharedCompilation=false > /tmp/t.log 2>&1
EXIT=0
```

```
Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517, Duration: 31 s - Harbora.NodeAgent.Tests.dll (net10.0)
Skipped! - Failed:     0, Passed:     0, Skipped:    96, Total:    96, Duration: 69 ms - Harbora.Postgres.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 52 s - Harbora.NodeIngress.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:  6224, Skipped:     0, Total:  6224, Duration: 1 m 41 s - Harbora.Tests.dll (net10.0)
```

- **Harbora.Tests.dll: 6224 passed, 0 failed** — matches the baseline of 6224 exactly (no drop).
- `grep -c "Harbora.Tests.dll" /tmp/t.log` → `2` (not 0 — confirms the suite actually ran, guarding
  against the MSB3030/silent-skip failure mode CONSTRAINTS.md describes).
- Postgres.Tests: all 96 skipped — this machine has no Postgres/Docker, so per CONSTRAINTS.md this
  is reported as **unverified locally**, not as passing. It was not touched by this change anyway
  (out of scope: `MonitoringController`, Monitoring views, `deploy/`, `DocumentationDriftTests` —
  none of which this fix touches).

## Scope

Touched only the mobile slide-over: `_Layout.cshtml`'s toggle logic and the new `content-scroll`
id, the sidebar's scrollable `<nav>`, `main.ts`'s wiring, and the CI test-discovery line. Did not
touch the desktop static layout, `sidebar-collapsed` / `data-collapse-sidebar` (the unrelated
collapse feature), `MonitoringController`, Monitoring views, `deploy/`, or `DocumentationDriftTests`.
