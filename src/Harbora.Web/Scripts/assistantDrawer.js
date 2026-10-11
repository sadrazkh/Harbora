// The assistant drawer: open/close, the topbar toggle, Ctrl/Cmd+J, Escape, and remembering
// open/closed and the current conversation across page loads.
//
// The drawer itself is Razor (`_Layout.cshtml`, `#assistant-panel`, rendered only when the
// workspace is entitled to the assistant). The Vue island inside it is a chunk of its own, loaded
// the first time the drawer opens — most page views never open it, and those should not pay for it.
//
// `createAssistantDrawer` and `createAssistantPrefs` are the testable core, taking element-like and
// Storage-like objects so `node --test` can drive them with small fakes (as mobileSidebar.js does).
// `initAssistantDrawer` is the wiring `main.ts` calls.

export const STORAGE_OPEN = 'harbora-assistant-open';
export const STORAGE_CONVERSATION = 'harbora-assistant-conversation';

/**
 * localStorage, made safe to call: in a private window or with a full quota the Storage methods
 * throw, and a preference that cannot be saved must not take the drawer down with it.
 *
 * @param {Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> | undefined | null} storage
 */
export function createAssistantPrefs(storage) {
  const attempt = (fn, fallback) => {
    try { return fn(); } catch { return fallback; }
  };
  return {
    isOpen: () => attempt(() => storage?.getItem(STORAGE_OPEN) === '1', false),
    setOpen: (open) => attempt(() => storage?.setItem(STORAGE_OPEN, open ? '1' : '0')),
    conversationId: () => attempt(() => storage?.getItem(STORAGE_CONVERSATION) || null, null),
    setConversationId: (id) => attempt(() => (id
      ? storage?.setItem(STORAGE_CONVERSATION, id)
      : storage?.removeItem(STORAGE_CONVERSATION))),
  };
}

/**
 * @param {{
 *   panel: { classList: { toggle(cls: string, force?: boolean): void }, setAttribute(name: string, value: string): void },
 *   toggles?: Array<{ setAttribute(name: string, value: string): void }>,
 *   prefs: ReturnType<typeof createAssistantPrefs>,
 *   onOpen?: () => void,
 * }} deps
 */
export function createAssistantDrawer({ panel, toggles = [], prefs, onOpen }) {
  let open = false;

  function paint() {
    panel.classList.toggle('is-open', open);
    panel.setAttribute('aria-hidden', open ? 'false' : 'true');
    for (const toggle of toggles) toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
  }

  function set(next) {
    if (next === open) return;
    open = next;
    paint();
    prefs.setOpen(open);
    if (open) onOpen?.();
  }

  return {
    open: () => set(true),
    close: () => set(false),
    toggle: () => set(!open),
    isOpen: () => open,
    /** Paints the remembered state on page load; opening it there counts as an open. */
    restore() {
      open = prefs.isOpen();
      paint();
      if (open) onOpen?.();
    },
  };
}

/**
 * Ctrl+J (Cmd+J on a Mac). Matched on the physical key as well as the character, because on a
 * Persian layout the J key types "ت" and a shortcut that only works in English is half a shortcut.
 */
export function isAssistantShortcut(event) {
  if (!(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey) return false;
  return event.code === 'KeyJ' || String(event.key ?? '').toLowerCase() === 'j';
}

/**
 * Wires the drawer rendered in `_Layout.cshtml`.
 *
 * @param {HTMLElement} panel  `#assistant-panel`
 * @param {(host: HTMLElement, options: { close: () => void, focus: boolean }) => Promise<void>} mountIsland
 *   imports and mounts the Vue island into `host`; called on the first open (again only if that failed).
 */
export function initAssistantDrawer(panel, mountIsland) {
  const toggles = Array.from(document.querySelectorAll('[data-assistant-toggle]'));
  const host = panel.querySelector('[data-assistant-host]');
  const topbarToggle = toggles.find((t) => !panel.contains(t));
  let lastToggle = null;
  let mounted = null;
  // Set only around an open the person asked for. A drawer restored open on page load must not
  // take the keyboard focus away from the page they just navigated to.
  let byPerson = false;

  function close() {
    drawer.close();
    // Back to where the person was, not to the top of the page.
    (lastToggle && !panel.contains(lastToggle) ? lastToggle : topbarToggle)?.focus?.();
  }

  function onOpen() {
    const focus = byPerson;
    if (!mounted) {
      mounted = mountIsland(host ?? panel, { close, focus }).catch(() => {
        // The chunk did not load (offline, a bad deploy). Say so in the drawer instead of leaving
        // "Loading…" up for ever, and let the next open try again.
        panel.querySelector('[data-assistant-loading]')?.setAttribute('hidden', '');
        panel.querySelector('[data-assistant-load-failed]')?.removeAttribute('hidden');
        mounted = null;
      });
      return;
    }
    // Already mounted: the island refreshes its status (and focuses the composer) on this signal.
    mounted.then(() => document.dispatchEvent(new CustomEvent('harbora:assistant-opened', { detail: { focus } })));
  }

  const drawer = createAssistantDrawer({
    panel,
    toggles,
    prefs: createAssistantPrefs(globalThis.localStorage),
    onOpen,
  });

  function toggleByPerson() {
    byPerson = true;
    try { drawer.toggle(); } finally { byPerson = false; }
    // Closed with the keyboard focus still inside it: a hidden element must not keep the focus.
    if (!drawer.isOpen() && panel.contains(document.activeElement)) topbarToggle?.focus?.();
  }

  for (const toggle of toggles) {
    toggle.addEventListener('click', () => {
      lastToggle = toggle;
      toggleByPerson();
    });
  }

  document.addEventListener('keydown', (event) => {
    if (isAssistantShortcut(event)) {
      event.preventDefault();
      toggleByPerson();
      return;
    }
    if (event.key === 'Escape' && drawer.isOpen() && panel.contains(document.activeElement)) close();
  });

  drawer.restore();
  return drawer;
}
