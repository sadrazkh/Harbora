import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  createAssistantDrawer, createAssistantPrefs, initAssistantDrawer,
  STORAGE_OPEN, STORAGE_WIDE, EVENT_TOGGLE_WIDE, EVENT_WIDTH,
} from '../assistantDrawer.js';

// Round seven P3: the wide drawer. The width belongs to the drawer — a class on #assistant-panel — and
// is remembered with the other drawer preferences; the island's header button only asks for it and
// shows what it is told.

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

test('the drawer is its normal width until somebody widens it', () => {
  const prefs = createAssistantPrefs(fakeStorage());
  assert.equal(prefs.isWide(), false);
  const panel = fakeElement();
  const drawer = createAssistantDrawer({ panel, prefs });
  drawer.restore();
  assert.equal(drawer.isWide(), false);
  assert.equal(panel.classList.contains('is-wide'), false);
});

test('toggling widens and narrows it, paints the class and remembers the choice', () => {
  const storage = fakeStorage();
  const panel = fakeElement();
  const widths = [];
  const drawer = createAssistantDrawer({ panel, prefs: createAssistantPrefs(storage), onWidth: (w) => widths.push(w) });
  drawer.toggleWide();
  assert.equal(drawer.isWide(), true);
  assert.equal(panel.classList.contains('is-wide'), true);
  assert.equal(storage.data[STORAGE_WIDE], '1');
  drawer.toggleWide();
  assert.equal(drawer.isWide(), false);
  assert.equal(panel.classList.contains('is-wide'), false);
  assert.equal(storage.data[STORAGE_WIDE], '0');
  assert.deepEqual(widths, [true, false]);
});

test('setting the width it already has announces nothing', () => {
  const widths = [];
  const drawer = createAssistantDrawer({ panel: fakeElement(), prefs: createAssistantPrefs(fakeStorage()), onWidth: (w) => widths.push(w) });
  drawer.setWide(false);
  drawer.setWide(true);
  drawer.setWide(true);
  assert.deepEqual(widths, [true]);
});

test('a remembered wide drawer is painted wide on the next page, open or not', () => {
  const closed = fakeElement();
  createAssistantDrawer({ panel: closed, prefs: createAssistantPrefs(fakeStorage({ [STORAGE_WIDE]: '1' })) }).restore();
  assert.equal(closed.classList.contains('is-wide'), true);

  const open = fakeElement();
  const drawer = createAssistantDrawer({ panel: open, prefs: createAssistantPrefs(fakeStorage({ [STORAGE_WIDE]: '1', [STORAGE_OPEN]: '1' })) });
  drawer.restore();
  assert.equal(drawer.isOpen(), true);
  assert.equal(drawer.isWide(), true);
  assert.equal(open.classList.contains('is-wide') && open.classList.contains('is-open'), true);
});

test('the width does not depend on the drawer being open, and closing keeps it', () => {
  const panel = fakeElement();
  const drawer = createAssistantDrawer({ panel, prefs: createAssistantPrefs(fakeStorage()) });
  drawer.open();
  drawer.toggleWide();
  drawer.close();
  assert.equal(panel.classList.contains('is-wide'), true);
  drawer.open();
  assert.equal(panel.classList.contains('is-wide'), true);
});

test('storage that throws never breaks the width: it just is not remembered', () => {
  const prefs = createAssistantPrefs(throwingStorage);
  assert.equal(prefs.isWide(), false);
  assert.doesNotThrow(() => prefs.setWide(true));
  const panel = fakeElement();
  const drawer = createAssistantDrawer({ panel, prefs });
  assert.doesNotThrow(() => { drawer.restore(); drawer.toggleWide(); });
  assert.equal(drawer.isWide(), true, 'it works for this page');
  assert.equal(panel.classList.contains('is-wide'), true);
  assert.equal(createAssistantPrefs(undefined).isWide(), false);
  assert.doesNotThrow(() => createAssistantPrefs(undefined).setWide(true));
});

// ---- the wiring: the island's button talks to the drawer through events on the document ----------------

function wiring({ stored = {}, mount } = {}) {
  const listeners = {};
  const toggles = [];
  const doc = {
    activeElement: null,
    dispatched: [],
    addEventListener(type, fn) { (listeners[type] ??= []).push(fn); },
    dispatchEvent(event) {
      this.dispatched.push(event);
      for (const fn of listeners[event.type] ?? []) fn(event);
      return true;
    },
    querySelectorAll: (selector) => (selector === '[data-assistant-toggle]' ? toggles : []),
  };
  const panel = {
    ...fakeElement(),
    addEventListener() {},
    contains: () => false,
    querySelector: () => null,
  };
  const topbar = { ...fakeElement(), addEventListener(type, fn) { (this.click ??= []).push(fn); }, focus() {} };
  toggles.push(topbar);
  const storage = fakeStorage(stored);
  const drawer = initAssistantDrawer(panel, mount ?? (async () => {}), { document: doc, storage, matchMedia: () => ({ matches: false }) });
  const widthEvents = () => doc.dispatched.filter((e) => e.type === EVENT_WIDTH).map((e) => e.detail.wide);
  return { doc, panel, storage, drawer, topbar, widthEvents, open: () => topbar.click.forEach((fn) => fn()) };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

test('the island\'s toggle event widens the drawer, and the drawer says what the width is now', () => {
  const w = wiring();
  w.doc.dispatchEvent(new CustomEvent(EVENT_TOGGLE_WIDE));
  assert.equal(w.drawer.isWide(), true);
  assert.equal(w.panel.classList.contains('is-wide'), true);
  assert.equal(w.storage.data[STORAGE_WIDE], '1');
  w.doc.dispatchEvent(new CustomEvent(EVENT_TOGGLE_WIDE));
  assert.deepEqual(w.widthEvents(), [true, false]);
  assert.equal(w.panel.classList.contains('is-wide'), false);
});

test('an island that mounts into a drawer already wide is told so', async () => {
  const w = wiring({ stored: { [STORAGE_OPEN]: '1', [STORAGE_WIDE]: '1' } });
  await settle();
  assert.deepEqual(w.widthEvents(), [true], 'announced once the island is there to hear it');
});

test('and one that mounts into a normal drawer is told that too', async () => {
  const w = wiring();
  w.open();
  await settle();
  assert.deepEqual(w.widthEvents(), [false]);
});

test('an island that failed to load is not announced to', async () => {
  const w = wiring({ stored: { [STORAGE_OPEN]: '1' }, mount: async () => { throw new Error('chunk failed'); } });
  await settle();
  assert.deepEqual(w.widthEvents(), []);
});

test('the panel markup: a widen button that is a toggle, hidden on a phone, before History and New', async () => {
  const { readFileSync } = await import('node:fs');
  const source = readFileSync(new URL('../islands/AssistantPanel.vue', import.meta.url), 'utf8');
  const button = /<button[^>]*data-assistant-wide[^>]*>/s.exec(source)?.[0];
  assert.ok(button, 'the widen button exists');
  assert.ok(/:aria-pressed="wide"/.test(button), 'it says whether it is on');
  assert.ok(/\bhidden\b/.test(button) && /\bsm:block\b/.test(button), 'the drawer is full-width on a phone, so it is not shown there');
  assert.ok(source.indexOf('data-assistant-wide') < source.indexOf('data-assistant-history'), 'before History');
  assert.ok(source.indexOf('data-assistant-history') < source.indexOf('data-assistant-new'), 'History before New');
  assert.ok(source.includes('MoveHorizontal'), 'the horizontal-arrows icon');
});

test('the stylesheet: the wide drawer is about 40 rem, never wider than its box, and only from tablet width up', async () => {
  const { readFileSync } = await import('node:fs');
  const css = readFileSync(new URL('../app.css', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
  const rule = /@media \(min-width: 640px\) \{\s*\.assistant-drawer\.is-wide \{ width: min\(40rem, 100%\); \}\s*\}/.exec(css);
  assert.ok(rule, 'the wide rule');
  // The width moves with the same easing as the slide, and not at all for anyone who asked for less motion.
  assert.ok(/\.assistant-drawer \{[^}]*transition:[^;]*width \.2s ease/s.test(css));
  assert.ok(/prefers-reduced-motion: reduce\) \{\s*\.assistant-drawer, html\[dir\] \.assistant-drawer\.is-open \{ transition: none; \}/.test(css));
});
