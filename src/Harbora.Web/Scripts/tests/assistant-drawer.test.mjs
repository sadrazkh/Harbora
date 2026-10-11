import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  createAssistantDrawer, createAssistantPrefs, isAssistantShortcut,
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
