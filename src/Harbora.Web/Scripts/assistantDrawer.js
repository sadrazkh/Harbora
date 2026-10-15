// The assistant drawer: open/close, the topbar toggle, Ctrl/Cmd+J, Escape, its width (normal or wide),
// and remembering open/closed, wide/normal and the current conversation across page loads.
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
export const STORAGE_OUTBOX = 'harbora-assistant-outbox';
export const STORAGE_WIDE = 'harbora-assistant-wide';

// The wide button lives in the island's header, but the width belongs to the drawer around it (a class
// on #assistant-panel, see app.css). They talk by events on the document, like open and close do: the
// island asks, the drawer decides and says what the width now is — also once the island has mounted,
// since the drawer may have been painted wide before there was an island to hear it.
export const EVENT_TOGGLE_WIDE = 'harbora:assistant-toggle-wide';
export const EVENT_WIDTH = 'harbora:assistant-width';

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
  const outboxKey = (conversationId) => conversationId || 'new';
  // Every entry worth keeping, by key. A malformed value, or the single entry an earlier version of
  // the pane stored, is read the forgiving way rather than losing the words in it.
  const readOutbox = () => {
    const value = JSON.parse(storage?.getItem(STORAGE_OUTBOX) ?? 'null');
    if (!value || typeof value !== 'object') return {};
    if (typeof value.text === 'string') return { [outboxKey(value.conversationId)]: value };
    return Object.fromEntries(Object.entries(value).filter(([, e]) => e && typeof e.text === 'string' && e.text));
  };
  return {
    isOpen: () => attempt(() => storage?.getItem(STORAGE_OPEN) === '1', false),
    setOpen: (open) => attempt(() => storage?.setItem(STORAGE_OPEN, open ? '1' : '0')),
    /** The wide drawer (about 40 rem) instead of the normal one. Off until somebody turns it on. */
    isWide: () => attempt(() => storage?.getItem(STORAGE_WIDE) === '1', false),
    setWide: (wide) => attempt(() => storage?.setItem(STORAGE_WIDE, wide ? '1' : '0')),
    conversationId: () => attempt(() => storage?.getItem(STORAGE_CONVERSATION) || null, null),
    setConversationId: (id) => attempt(() => (id
      ? storage?.setItem(STORAGE_CONVERSATION, id)
      : storage?.removeItem(STORAGE_CONVERSATION))),
    /**
     * Messages on their way, one per conversation: `{ conversationId, text, at }`, kept from the
     * moment one is sent until the server says it has it (`started`). A reload in between finds it
     * here, so the words are not lost even when the request never got as far as being saved. Keyed by
     * conversation (`new` for one not created yet), so a message waiting for one conversation is
     * never overwritten by a message sent in another.
     */
    outbox: (conversationId) => attempt(() => readOutbox()[outboxKey(conversationId)] ?? null, null),
    setOutbox: (entry) => attempt(() => {
      const all = readOutbox();
      all[outboxKey(entry.conversationId)] = entry;
      storage?.setItem(STORAGE_OUTBOX, JSON.stringify(all));
    }),
    /** Moves the entry waiting under `new` to the conversation that was just created for it. */
    moveOutbox: (conversationId) => attempt(() => {
      const all = readOutbox();
      const entry = all[outboxKey(null)];
      if (!entry) return;
      delete all[outboxKey(null)];
      all[outboxKey(conversationId)] = { ...entry, conversationId };
      storage?.setItem(STORAGE_OUTBOX, JSON.stringify(all));
    }),
    clearOutbox: (conversationId) => attempt(() => {
      const all = readOutbox();
      delete all[outboxKey(conversationId)];
      if (Object.keys(all).length) storage?.setItem(STORAGE_OUTBOX, JSON.stringify(all));
      else storage?.removeItem(STORAGE_OUTBOX);
    }),
  };
}

/**
 * @param {{
 *   panel: { classList: { toggle(cls: string, force?: boolean): void }, setAttribute(name: string, value: string): void },
 *   toggles?: Array<{ setAttribute(name: string, value: string): void }>,
 *   prefs: ReturnType<typeof createAssistantPrefs>,
 *   onOpen?: () => void,
 *   onClose?: () => void,
 *   onWidth?: (wide: boolean) => void,
 *   isCompact?: () => boolean,
 * }} deps
 *   `isCompact`: whether the drawer is the whole screen right now (a phone; below 640 px).
 */
export function createAssistantDrawer({ panel, toggles = [], prefs, onOpen, onClose, onWidth, isCompact = () => false }) {
  let open = false;
  let wide = false;

  function paint() {
    panel.classList.toggle('is-open', open);
    panel.setAttribute('aria-hidden', open ? 'false' : 'true');
    panel.classList.toggle('is-wide', wide);
    for (const toggle of toggles) toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
  }

  function set(next) {
    if (next === open) return;
    open = next;
    paint();
    prefs.setOpen(open);
    if (open) onOpen?.();
    else onClose?.();
  }

  const drawer = {
    open: () => set(true),
    close: () => set(false),
    toggle: () => set(!open),
    isOpen: () => open,
    isWide: () => wide,
    /**
     * Wide or normal, remembered. Kept as a setting even where the button is hidden (a phone, where the
     * drawer is the whole screen already), so the choice is still there on a wider screen.
     */
    setWide(next) {
      if (next === wide) return;
      wide = next;
      paint();
      prefs.setWide(wide);
      onWidth?.(wide);
    },
    toggleWide: () => drawer.setWide(!wide),
    /**
     * Paints the remembered state on page load; opening it there counts as an open. Not on a phone:
     * there the drawer is the whole screen, and reopening it on every load would put it over every
     * page the person goes to — including the one a result card's link just took them to.
     */
    restore() {
      wide = prefs.isWide();
      open = prefs.isOpen() && !isCompact();
      paint();
      if (open) onOpen?.();
    },
    /**
     * A link inside the drawer was followed. On a phone the drawer closes, and stays closed, so the
     * page the link leads to is what the person sees. On a wide screen it stays open beside it.
     */
    followedLink() {
      if (isCompact()) set(false);
    },
  };
  return drawer;
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
 * @param {{ document?: Document, storage?: Storage, matchMedia?: (query: string) => { matches: boolean } }} [env]
 *   the browser globals, injectable so `node --test` can drive the wiring with fakes.
 */
export function initAssistantDrawer(panel, mountIsland, env = {}) {
  const doc = env.document ?? globalThis.document;
  const storage = 'storage' in env ? env.storage : globalThis.localStorage;
  const matchMedia = env.matchMedia ?? globalThis.matchMedia?.bind(globalThis);
  const isCompact = () => matchMedia?.('(max-width: 639px)')?.matches === true;

  const toggles = Array.from(doc.querySelectorAll('[data-assistant-toggle]'));
  const host = panel.querySelector('[data-assistant-host]');
  const topbarToggle = toggles.find((t) => !panel.contains(t));
  let lastToggle = null;
  let mounted = null;
  // Set only around an open the person asked for. A drawer restored open on page load must not
  // take the keyboard focus away from the page they just navigated to.
  let byPerson = false;

  function returnFocus() {
    // Back to where the person was, not to the top of the page.
    (lastToggle && !panel.contains(lastToggle) ? lastToggle : topbarToggle)?.focus?.();
  }

  function close() {
    drawer.close();
    returnFocus();
  }

  function onOpen() {
    const focus = byPerson;
    if (!mounted) {
      panel.querySelector('[data-assistant-loading]')?.removeAttribute('hidden');
      panel.querySelector('[data-assistant-load-failed]')?.setAttribute('hidden', '');
      mounted = mountIsland(host ?? panel, { close, focus }).then(announceWidth).catch(() => {
        // The chunk did not load (offline, a bad deploy). Say so in the drawer instead of leaving
        // "Loading…" up for ever, and let the next open try again.
        panel.querySelector('[data-assistant-loading]')?.setAttribute('hidden', '');
        panel.querySelector('[data-assistant-load-failed]')?.removeAttribute('hidden');
        mounted = null;
      });
      return;
    }
    // Already mounted: the island refreshes its status (and focuses the composer) on this signal.
    mounted.then(() => doc.dispatchEvent(new CustomEvent('harbora:assistant-opened', { detail: { focus } })));
  }

  // Tells the island, which draws the wide button, what the width is — now, and whenever it changes.
  function announceWidth() {
    doc.dispatchEvent(new CustomEvent(EVENT_WIDTH, { detail: { wide: drawer.isWide() } }));
  }

  // Closing only hides the pane; the island hears it so it can stop re-reading a conversation that
  // is still being answered (and start again on the next open).
  function onClose() {
    mounted?.then(() => doc.dispatchEvent(new CustomEvent('harbora:assistant-closed')));
  }

  const drawer = createAssistantDrawer({
    panel,
    toggles,
    prefs: createAssistantPrefs(storage),
    onOpen,
    onClose,
    onWidth: announceWidth,
    isCompact,
  });

  function toggleByPerson() {
    byPerson = true;
    try { drawer.toggle(); } finally { byPerson = false; }
    // Closed with the keyboard focus still inside it: a hidden element must not keep the focus.
    if (!drawer.isOpen() && panel.contains(doc.activeElement)) returnFocus();
  }

  for (const toggle of toggles) {
    toggle.addEventListener('click', () => {
      lastToggle = toggle;
      toggleByPerson();
    });
  }

  // The island's wide button.
  doc.addEventListener(EVENT_TOGGLE_WIDE, () => drawer.toggleWide());

  // A plain left click on a link in the drawer (a result card's "open the database"): on a phone,
  // close before the navigation, so the next page is not covered. Modified clicks open elsewhere.
  panel.addEventListener('click', (event) => {
    const link = event.target?.closest?.('a[href]');
    if (!link || event.defaultPrevented || event.button > 0) return;
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || link.target === '_blank') return;
    drawer.followedLink();
  });

  doc.addEventListener('keydown', (event) => {
    if (isAssistantShortcut(event)) {
      event.preventDefault();
      toggleByPerson();
      return;
    }
    if (event.key === 'Escape' && drawer.isOpen() && panel.contains(doc.activeElement)) close();
  });

  drawer.restore();
  return drawer;
}
