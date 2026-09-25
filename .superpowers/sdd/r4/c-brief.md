# C — Opening the menu on a phone leaves the page behind it scrolling

## The bug, as reported

"Sometimes you scroll and the menu and the site get mixed up." On a narrow screen the navigation is a
slide-over; with it open, scrolling moves the site underneath it, so the two appear tangled.

## Why it happens

`src/Harbora.Web/Views/Shared/_Layout.cshtml`, the inline "Mobile slide-over sidebar" block near the
bottom, is three lines:

```js
function open(v) { sidebar.classList.toggle('is-open', v); backdrop.classList.toggle('hidden', !v); }
```

It adds a class and unhides the backdrop. **Nothing locks the scroll.** Three separate consequences:

1. The page keeps scrolling behind the open slide-over.
2. The sidebar's own `<nav class="flex-1 overflow-y-auto …">` (in `Design/_Sidebar.cshtml`) **chains**
   its scroll to the page once it reaches its end, so flicking through a long menu scrolls the site.
3. There is no `Escape` to close it — the only ways out are the backdrop, the toggle, or a link.

Read `app.css`'s `@media (max-width: 767px)` block (around line 138) for how the slide-over is
positioned: `position: fixed`, `z-index: 40`, backdrop at `z-index: 30`. Note that the scrolling
element is **not** `body` — `_Layout.cshtml` has `<body class="h-full">` and the content scroller is an
inner `div … overflow-y-auto`. So a fix that sets `overflow: hidden` on `body` alone will appear to work
and will not actually stop the scroll. Find the element that really scrolls and lock that; confirm which
one it is by reading the layout rather than assuming.

## Build

1. Lock the page scroll while the slide-over is open, and restore it on close — including when a link
   inside the sidebar closes it, and when the backdrop does. Restoring must put back whatever the
   value was, not a hard-coded default.
2. `overscroll-behavior: contain` on the sidebar's scrollable nav, so reaching its end does not scroll
   the page.
3. `Escape` closes it.

Keep it to the slide-over. The desktop layout is static and correct; do not restructure it, and do not
change the sidebar's collapse behaviour (`sidebar-collapsed`, `data-collapse-sidebar`), which is a
different feature.

## The test, and the trap in wiring it

A regression test belongs in `src/Harbora.Web/Scripts/tests/`, beside `deployment-logs.test.mjs`,
run with `node --test`.

**`.github/workflows/ci.yml` line 192 names that one file explicitly:**

```yaml
run: node --test src/Harbora.Web/Scripts/tests/deployment-logs.test.mjs
```

So a new test file there **is never run by CI** unless you also change that line — make it cover the
directory. A test that exists and never runs is worse than no test, because it reads as coverage. This
matters more than the fix: that CI line is itself an instance of the defect class this project is built
around.

The toggle logic currently lives inline in `_Layout.cshtml`, which a `node --test` file cannot import.
Extract just the slide-over behaviour into a module under `src/Harbora.Web/Scripts/` that the layout
loads and the test imports. Do not move the theme block or the form-loading block — only the
slide-over.

Cover: opening locks the scroll; closing restores it; closing via a sidebar link restores it; Escape
closes and restores; the restored value is the original one, not a default.

## Scope boundaries

Do not touch `MonitoringController`, the Monitoring views, `deploy/` or `DocumentationDriftTests` —
parallel agents own those.

## Verify

Per `.superpowers/sdd/r4/CONSTRAINTS.md`. Baseline 6224 in `Harbora.Tests.dll`.

You are changing frontend files, so `npm run build` must succeed — run it **separately**, never while
`dotnet test` is running, or the web project fails and the test project silently does not run. Also run
your node test and paste its output. Report both.
