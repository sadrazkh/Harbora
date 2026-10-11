import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  createAssistantDrawer, createAssistantPrefs, isAssistantShortcut, initAssistantDrawer,
  STORAGE_OPEN, STORAGE_CONVERSATION,
} from '../assistantDrawer.js';

// Plain fakes, not a DOM — the same approach as mobile-sidebar.test.mjs: the drawer only needs
// classList.toggle/contains and setAttribute, and the prefs only a Storage-shaped object.

function fakeElement() {
  const classes = new Set();
  const attrs = {};
  return {
    attrs,
    classList: {
      contains: (c) => classes.has(c),
      toggle: (c, force) => {
        const want = force === undefined ? !classes.has(c) : force;
        if (want) classes.add(c); else classes.delete(c);
        return want;
      },
    },
    setAttribute: (name, value) => { attrs[name] = String(value); },
  };
}

function fakeStorage(initial = {}) {
  const data = { ...initial };
  return {
    data,
    getItem: (k) => (k in data ? data[k] : null),
    setItem: (k, v) => { data[k] = String(v); },
    removeItem: (k) => { delete data[k]; },
  };
}

const throwingStorage = {
  getItem() { throw new Error('SecurityError'); },
  setItem() { throw new Error('QuotaExceededError'); },
  removeItem() { throw new Error('SecurityError'); },
};

function setup(stored = {}) {
  const panel = fakeElement();
  const toggles = [fakeElement(), fakeElement()];
  const storage = fakeStorage(stored);
  const opened = [];
  const drawer = createAssistantDrawer({ panel, toggles, prefs: createAssistantPrefs(storage), onOpen: () => opened.push(true) });
  return { panel, toggles, storage, opened, drawer };
}

test('opening shows the drawer, marks every toggle expanded and remembers it', () => {
  const { panel, toggles, storage, opened, drawer } = setup();
  drawer.open();
  assert.equal(drawer.isOpen(), true);
  assert.equal(panel.classList.contains('is-open'), true);
  assert.equal(panel.attrs['aria-hidden'], 'false');
  assert.deepEqual(toggles.map((t) => t.attrs['aria-expanded']), ['true', 'true']);
  assert.equal(storage.data[STORAGE_OPEN], '1');
  assert.equal(opened.length, 1);
});

test('closing hides it again and remembers that too', () => {
  const { panel, toggles, storage, drawer } = setup();
  drawer.open();
  drawer.close();
  assert.equal(panel.classList.contains('is-open'), false);
  assert.equal(panel.attrs['aria-hidden'], 'true');
  assert.deepEqual(toggles.map((t) => t.attrs['aria-expanded']), ['false', 'false']);
  assert.equal(storage.data[STORAGE_OPEN], '0');
});

test('toggle flips, and opening an open drawer does not announce a second open', () => {
  const { opened, drawer } = setup();
  drawer.toggle();
  drawer.open();
  assert.equal(drawer.isOpen(), true);
  assert.equal(opened.length, 1);
  drawer.toggle();
  assert.equal(drawer.isOpen(), false);
});

test('restore reopens a drawer that was open on the last page, and announces it', () => {
  const { panel, opened, drawer } = setup({ [STORAGE_OPEN]: '1' });
  drawer.restore();
  assert.equal(drawer.isOpen(), true);
  assert.equal(panel.classList.contains('is-open'), true);
  assert.equal(opened.length, 1);
});

test('restore leaves a closed drawer closed, painted closed, without loading anything', () => {
  const { panel, toggles, opened, drawer } = setup({ [STORAGE_OPEN]: '0' });
  drawer.restore();
  assert.equal(drawer.isOpen(), false);
  assert.equal(panel.attrs['aria-hidden'], 'true');
  assert.equal(toggles[0].attrs['aria-expanded'], 'false');
  assert.equal(opened.length, 0);
});

test('the conversation id round-trips and clearing it removes the key', () => {
  const storage = fakeStorage();
  const prefs = createAssistantPrefs(storage);
  assert.equal(prefs.conversationId(), null);
  prefs.setConversationId('0199d0c4-6a1e-7000-8000-000000000001');
  assert.equal(prefs.conversationId(), '0199d0c4-6a1e-7000-8000-000000000001');
  prefs.setConversationId(null);
  assert.equal(STORAGE_CONVERSATION in storage.data, false);
});

test('storage that throws (private mode, a full quota) never breaks the drawer', () => {
  const prefs = createAssistantPrefs(throwingStorage);
  assert.equal(prefs.isOpen(), false);
  assert.equal(prefs.conversationId(), null);
  assert.doesNotThrow(() => prefs.setOpen(true));
  assert.doesNotThrow(() => prefs.setConversationId('x'));
  const drawer = createAssistantDrawer({ panel: fakeElement(), toggles: [], prefs });
  assert.doesNotThrow(() => { drawer.restore(); drawer.open(); drawer.close(); });
  assert.equal(createAssistantPrefs(undefined).isOpen(), false);
});

test('Ctrl+J and Cmd+J toggle the assistant; plain J and other chords do not', () => {
  assert.equal(isAssistantShortcut({ ctrlKey: true, key: 'j', code: 'KeyJ' }), true);
  assert.equal(isAssistantShortcut({ metaKey: true, key: 'J', code: 'KeyJ' }), true);
  assert.equal(isAssistantShortcut({ key: 'j', code: 'KeyJ' }), false);
  assert.equal(isAssistantShortcut({ ctrlKey: true, shiftKey: true, key: 'J', code: 'KeyJ' }), false);
  assert.equal(isAssistantShortcut({ ctrlKey: true, altKey: true, key: 'j', code: 'KeyJ' }), false);
  assert.equal(isAssistantShortcut({ ctrlKey: true, key: 'k', code: 'KeyK' }), false);
});

test('the shortcut works on a Persian keyboard layout, where the J key types "ت"', () => {
  assert.equal(isAssistantShortcut({ ctrlKey: true, key: 'ت', code: 'KeyJ' }), true);
});

// ---- the wiring: initAssistantDrawer against a fake document --------------------------------------
//
// Just enough DOM for the drawer's own wiring: elements with listeners, attributes, focus and
// containment, and a document that tracks the focused element and the events dispatched on it.

function wiring({ stored = {}, compact = false, mount } = {}) {
  const toggles = [];
  const doc = {
    activeElement: null,
    listeners: {},
    dispatched: [],
    addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); },
    dispatchEvent(event) { this.dispatched.push(event); return true; },
    querySelectorAll: (selector) => (selector === '[data-assistant-toggle]' ? toggles : []),
  };

  function element(name, parent = null) {
    const listeners = {};
    const attrs = {};
    const classes = new Set();
    const el = {
      name, parent, attrs, listeners,
      classList: {
        contains: (c) => classes.has(c),
        toggle: (c, force) => {
          const want = force === undefined ? !classes.has(c) : force;
          if (want) classes.add(c); else classes.delete(c);
          return want;
        },
      },
      setAttribute: (k, v) => { attrs[k] = String(v); },
      removeAttribute: (k) => { delete attrs[k]; },
      addEventListener: (type, fn) => { (listeners[type] ??= []).push(fn); },
      focus: () => { doc.activeElement = el; },
      contains: (other) => { for (let n = other; n; n = n.parent) if (n === el) return true; return false; },
      closest: (selector) => { for (let n = el; n; n = n.parent) if (n.matches?.(selector)) return n; return null; },
      click: (init = {}) => { for (const fn of listeners.click ?? []) fn({ target: el, button: 0, defaultPrevented: false, ...init }); },
    };
    return el;
  }

  const panel = element('panel');
  const host = element('host', panel);
  const loading = element('loading', host);
  const failed = element('failed', host);
  failed.attrs.hidden = '';
  const composer = element('composer', host);
  const link = element('link', host);
  link.matches = (selector) => selector === 'a[href]';
  link.target = '';
  panel.querySelector = (selector) => ({
    '[data-assistant-host]': host, '[data-assistant-loading]': loading, '[data-assistant-load-failed]': failed,
  })[selector] ?? null;
  // A click on the link bubbles to the panel's listener, the way the real DOM delivers it.
  link.click = (init = {}) => {
    for (const fn of panel.listeners.click ?? []) fn({ target: link, button: 0, defaultPrevented: false, ...init });
  };

  const topbar = element('topbar-toggle');
  toggles.push(topbar);
  const storage = fakeStorage(stored);
  const mounts = [];
  const mountIsland = mount ?? (async () => {});
  const drawer = initAssistantDrawer(panel, (into, options) => { mounts.push({ into, options }); return mountIsland(into, options); }, {
    document: doc, storage, matchMedia: () => ({ matches: compact }),
  });
  const key = (init) => {
    const event = { preventDefault() { event.prevented = true; }, ...init };
    for (const fn of doc.listeners.keydown ?? []) fn(event);
    return event;
  };
  return { doc, panel, host, loading, failed, composer, link, topbar, storage, drawer, mounts, key };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

test('the first open mounts the island once, focused when the person asked for it', async () => {
  const w = wiring();
  w.topbar.click();
  await settle();
  assert.equal(w.mounts.length, 1);
  assert.equal(w.mounts[0].into, w.host);
  assert.equal(w.mounts[0].options.focus, true);
  w.topbar.click();
  w.topbar.click();
  await settle();
  assert.equal(w.mounts.length, 1, 'opened again, not mounted again');
  assert.equal(w.doc.dispatched.at(-1).type, 'harbora:assistant-opened');
  assert.equal(w.doc.dispatched.at(-1).detail.focus, true);
});

test('a drawer restored open on page load does not take the focus', async () => {
  const w = wiring({ stored: { [STORAGE_OPEN]: '1' } });
  await settle();
  assert.equal(w.drawer.isOpen(), true);
  assert.equal(w.mounts[0].options.focus, false);
});

test('Escape with the focus inside closes the drawer and puts the focus back on the toggle', () => {
  const w = wiring();
  w.topbar.click();
  w.composer.focus();
  w.key({ key: 'Escape' });
  assert.equal(w.drawer.isOpen(), false);
  assert.equal(w.doc.activeElement, w.topbar);
});

test('Escape with the focus outside the drawer leaves it open', () => {
  const w = wiring();
  w.topbar.click();
  w.doc.activeElement = { name: 'something on the page' };
  w.key({ key: 'Escape' });
  assert.equal(w.drawer.isOpen(), true);
});

test('Ctrl+J closing the drawer from inside it returns the focus to the toggle', () => {
  const w = wiring();
  const opened = w.key({ key: 'j', code: 'KeyJ', ctrlKey: true });
  assert.equal(opened.prevented, true);
  assert.equal(w.drawer.isOpen(), true);
  w.composer.focus();
  w.key({ key: 'j', code: 'KeyJ', ctrlKey: true });
  assert.equal(w.drawer.isOpen(), false);
  assert.equal(w.doc.activeElement, w.topbar);
});

test('a chunk that fails to load says so, and the next open tries again', async () => {
  let attempt = 0;
  const w = wiring({ mount: async () => { if (++attempt === 1) throw new Error('chunk failed'); } });
  w.topbar.click();
  await settle();
  assert.equal('hidden' in w.loading.attrs, true, 'the loading line goes');
  assert.equal('hidden' in w.failed.attrs, false, 'the failure line shows');
  w.topbar.click(); // close
  w.topbar.click(); // open again
  assert.equal('hidden' in w.loading.attrs, false, 'loading again while it retries');
  assert.equal('hidden' in w.failed.attrs, true);
  await settle();
  assert.equal(attempt, 2, 'retried');
  assert.equal(w.mounts.length, 2);
});

test('on a phone a drawer left open is not reopened on the next page', () => {
  const w = wiring({ stored: { [STORAGE_OPEN]: '1' }, compact: true });
  assert.equal(w.drawer.isOpen(), false);
  assert.equal(w.panel.attrs['aria-hidden'], 'true');
  assert.equal(w.mounts.length, 0, 'nothing loaded for a drawer nobody opened');
});

test('on a phone following a link in the drawer closes it and remembers it closed', () => {
  const w = wiring({ compact: true });
  w.topbar.click();
  assert.equal(w.drawer.isOpen(), true);
  w.link.click();
  assert.equal(w.drawer.isOpen(), false);
  assert.equal(w.storage.data[STORAGE_OPEN], '0');
});

test('on a wide screen a followed link leaves the drawer open beside the page', () => {
  const w = wiring({ compact: false });
  w.topbar.click();
  w.link.click();
  assert.equal(w.drawer.isOpen(), true);
  assert.equal(w.storage.data[STORAGE_OPEN], '1');
});

test('a link opened elsewhere (Ctrl+click, middle click) does not close the drawer even on a phone', () => {
  const w = wiring({ compact: true });
  w.topbar.click();
  w.link.click({ ctrlKey: true });
  w.link.click({ button: 1 });
  assert.equal(w.drawer.isOpen(), true);
});
