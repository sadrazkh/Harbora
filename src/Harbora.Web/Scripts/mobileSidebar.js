// Mobile slide-over sidebar: open/close, locking the page scroll while it is open, and Escape.
//
// The element that actually scrolls the page is NOT `body` (see `_Layout.cshtml`: `<body
// class="h-full">`) — it is the inner content div marked `id="content-scroll"`, the flex child
// with `overflow-y-auto` that holds `<main>`. Locking `body` instead looks like a fix and does
// nothing, because `body` never scrolls in the first place.
//
// `createMobileSidebar` is the testable core: it takes plain element-like objects (anything with
// `classList.toggle/contains` and, for the scroller, a mutable `style.overflow`) rather than
// looking them up itself, so `node --test` can exercise the locking/restoring behaviour with
// small fakes and no DOM. `initMobileSidebar` is the thin wiring layer that looks up the real
// elements this app renders and is what `main.ts` actually calls.

/**
 * @param {{
 *   sidebar: { classList: { toggle(cls: string, force?: boolean): void, contains(cls: string): boolean } },
 *   backdrop: { classList: { toggle(cls: string, force?: boolean): void }, addEventListener(type: string, fn: Function): void },
 *   scroller: { style: { overflow: string } },
 *   links?: Array<{ addEventListener(type: string, fn: Function): void }>,
 * }} deps
 */
export function createMobileSidebar({ sidebar, backdrop, scroller, links = [] }) {
  // `null` (not `''`) means "not currently locked", so a scroller whose original inline value
  // really was the empty string still round-trips correctly instead of being confused with "no
  // lock in effect".
  let savedOverflow = null;

  function lockScroll() {
    if (savedOverflow !== null) return; // already locked; do not clobber the saved original
    savedOverflow = scroller.style.overflow;
    scroller.style.overflow = 'hidden';
  }

  function unlockScroll() {
    if (savedOverflow === null) return;
    scroller.style.overflow = savedOverflow; // whatever it actually was, never a hard-coded default
    savedOverflow = null;
  }

  function isOpen() {
    return sidebar.classList.contains('is-open');
  }

  function setOpen(open) {
    sidebar.classList.toggle('is-open', open);
    backdrop.classList.toggle('hidden', !open);
    if (open) lockScroll();
    else unlockScroll();
  }

  function handleKeydown(event) {
    if (event.key === 'Escape' && isOpen()) setOpen(false);
  }

  backdrop.addEventListener('click', () => setOpen(false));
  for (const link of links) {
    link.addEventListener('click', () => setOpen(false));
  }

  return {
    open: () => setOpen(true),
    close: () => setOpen(false),
    toggle: () => setOpen(!isOpen()),
    isOpen,
    handleKeydown,
  };
}

/**
 * Wires the real sidebar/backdrop/toggle/content-scroll elements this app renders on every page
 * and returns the controller above. Returns null on pages that somehow lack one of the elements
 * (there are none today — the layout always renders them) rather than throwing during boot.
 * @param {Document} doc
 */
export function initMobileSidebar(doc = document) {
  const sidebar = doc.getElementById('sidebar');
  const backdrop = doc.getElementById('backdrop');
  const scroller = doc.getElementById('content-scroll');
  if (!sidebar || !backdrop || !scroller) return null;

  const links = Array.from(sidebar.querySelectorAll('a'));
  const controller = createMobileSidebar({ sidebar, backdrop, scroller, links });

  doc.querySelector('[data-toggle-sidebar]')?.addEventListener('click', () => controller.toggle());
  doc.addEventListener('keydown', (event) => controller.handleKeydown(event));

  return controller;
}
