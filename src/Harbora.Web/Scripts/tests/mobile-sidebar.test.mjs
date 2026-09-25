import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createMobileSidebar } from '../mobileSidebar.js';

// Plain fakes, not a DOM: `createMobileSidebar` only needs `classList.toggle/contains` and a
// mutable `style.overflow`, so the test exercises exactly the locking/restoring contract without
// pulling in jsdom or emulating a real element.
function fakeToggleable(initialClasses = []) {
  const classes = new Set(initialClasses);
  return {
    classList: {
      contains: (c) => classes.has(c),
      toggle: (c, force) => {
        const want = force === undefined ? !classes.has(c) : force;
        if (want) classes.add(c); else classes.delete(c);
        return want;
      },
    },
  };
}

function fakeBackdrop(initialClasses = ['hidden']) {
  const el = fakeToggleable(initialClasses);
  const listeners = [];
  el.addEventListener = (type, fn) => { if (type === 'click') listeners.push(fn); };
  el.click = () => listeners.forEach((fn) => fn());
  return el;
}

function fakeLink() {
  const listeners = [];
  return {
    addEventListener: (type, fn) => { if (type === 'click') listeners.push(fn); },
    click: () => listeners.forEach((fn) => fn()),
  };
}

function fakeScroller(overflow) {
  return { style: { overflow } };
}

test('opening the sidebar locks the page scroll', () => {
  const sidebar = fakeToggleable();
  const backdrop = fakeBackdrop();
  const scroller = fakeScroller('auto');
  const sut = createMobileSidebar({ sidebar, backdrop, scroller });

  sut.open();

  assert.equal(sut.isOpen(), true);
  assert.equal(backdrop.classList.contains('hidden'), false);
  assert.equal(scroller.style.overflow, 'hidden');
});

test('closing restores the original overflow value, not a hard-coded default', () => {
  const sidebar = fakeToggleable();
  const backdrop = fakeBackdrop();
  // Deliberately not 'auto' and not '': a fix that restores a plausible-looking default instead of
  // the real original value would pass a naive test but fail this one.
  const scroller = fakeScroller('scroll');
  const sut = createMobileSidebar({ sidebar, backdrop, scroller });

  sut.open();
  sut.close();

  assert.equal(sut.isOpen(), false);
  assert.equal(backdrop.classList.contains('hidden'), true);
  assert.equal(scroller.style.overflow, 'scroll');
});

test('the original value round-trips even when it was the empty string', () => {
  const sidebar = fakeToggleable();
  const backdrop = fakeBackdrop();
  const scroller = fakeScroller('');
  const sut = createMobileSidebar({ sidebar, backdrop, scroller });

  sut.open();
  assert.equal(scroller.style.overflow, 'hidden');
  sut.close();

  assert.equal(scroller.style.overflow, '');
});

test('closing via a sidebar link restores the scroll', () => {
  const sidebar = fakeToggleable();
  const backdrop = fakeBackdrop();
  const scroller = fakeScroller('auto');
  const link = fakeLink();
  const sut = createMobileSidebar({ sidebar, backdrop, scroller, links: [link] });

  sut.open();
  link.click();

  assert.equal(sut.isOpen(), false);
  assert.equal(backdrop.classList.contains('hidden'), true);
  assert.equal(scroller.style.overflow, 'auto');
});

test('the backdrop click closes and restores the scroll', () => {
  const sidebar = fakeToggleable();
  const backdrop = fakeBackdrop();
  const scroller = fakeScroller('auto');
  const sut = createMobileSidebar({ sidebar, backdrop, scroller });

  sut.open();
  backdrop.click();

  assert.equal(sut.isOpen(), false);
  assert.equal(scroller.style.overflow, 'auto');
});

test('Escape closes the open sidebar and restores the scroll', () => {
  const sidebar = fakeToggleable();
  const backdrop = fakeBackdrop();
  const scroller = fakeScroller('overlay'); // an unusual original value on purpose
  const sut = createMobileSidebar({ sidebar, backdrop, scroller });

  sut.open();
  sut.handleKeydown({ key: 'Escape' });

  assert.equal(sut.isOpen(), false);
  assert.equal(scroller.style.overflow, 'overlay');
});

test('Escape while already closed is a no-op', () => {
  const sidebar = fakeToggleable();
  const backdrop = fakeBackdrop();
  const scroller = fakeScroller('auto');
  const sut = createMobileSidebar({ sidebar, backdrop, scroller });

  sut.handleKeydown({ key: 'Escape' });

  assert.equal(sut.isOpen(), false);
  assert.equal(scroller.style.overflow, 'auto');
});

test('a non-Escape key does not close the sidebar', () => {
  const sidebar = fakeToggleable();
  const backdrop = fakeBackdrop();
  const scroller = fakeScroller('auto');
  const sut = createMobileSidebar({ sidebar, backdrop, scroller });

  sut.open();
  sut.handleKeydown({ key: 'Enter' });

  assert.equal(sut.isOpen(), true);
  assert.equal(scroller.style.overflow, 'hidden');
});
