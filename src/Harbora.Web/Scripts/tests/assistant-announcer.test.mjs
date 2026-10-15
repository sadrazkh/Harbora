import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createAnnouncer, ANNOUNCER } from '../islands/assistantAnnouncer.js';
import { copyNotice } from '../islands/assistantCopy.js';

// «کپی شد» / «کپی نشد» are said by one announcer for the whole pane — a single polite live region
// outside the conversation log — and not by a role="status" element in every message.

function fakeTimers() {
  const queue = [];
  let id = 0;
  return {
    queue,
    setTimeout(fn, ms) { const handle = ++id; queue.push({ handle, fn, ms }); return handle; },
    clearTimeout(handle) { const at = queue.findIndex((t) => t.handle === handle); if (at >= 0) queue.splice(at, 1); },
  };
}

test('a line is said, stays for about a second and a half, and then the region is empty again', () => {
  const timers = fakeTimers();
  const announcer = createAnnouncer({ timers });
  assert.equal(announcer.state.text, '');
  announcer.say('کپی شد');
  assert.equal(announcer.state.text, 'کپی شد');
  assert.equal(timers.queue.length, 1);
  assert.equal(timers.queue[0].ms, 1500);
  timers.queue[0].fn();
  assert.equal(announcer.state.text, '');
});

test('a later line replaces an earlier one, and the first one\'s timer cannot empty the second', () => {
  const timers = fakeTimers();
  const announcer = createAnnouncer({ timers });
  announcer.say('Copied');
  announcer.say("Couldn't copy");
  assert.equal(announcer.state.text, "Couldn't copy");
  assert.equal(timers.queue.length, 1, 'one timer, the latest');
});

test('the same line twice within the hold is said again: emptied, then set on the next tick', () => {
  const timers = fakeTimers();
  const announcer = createAnnouncer({ timers });
  announcer.say('کپی شد');
  assert.equal(announcer.state.text, 'کپی شد');
  announcer.say('کپی شد'); // pressed again while it is still showing
  assert.equal(announcer.state.text, '', 'cleared, so that the next value is a change');
  assert.deepEqual(timers.queue.map((t) => t.ms), [0], 'set on the next tick, and not before; the old hold timer is gone');
  timers.queue.shift().fn();
  assert.equal(announcer.state.text, 'کپی شد');
  assert.deepEqual(timers.queue.map((t) => t.ms), [1500], 'and held again from here');
  timers.queue.shift().fn();
  assert.equal(announcer.state.text, '');
});

test('a different line, or the same one after the hold, needs no tick of its own', () => {
  const timers = fakeTimers();
  const announcer = createAnnouncer({ timers });
  announcer.say('Copied');
  announcer.say("Couldn't copy");
  assert.equal(announcer.state.text, "Couldn't copy", 'a change already');
  timers.queue.shift().fn(); // the hold ends
  announcer.say("Couldn't copy");
  assert.equal(announcer.state.text, "Couldn't copy", 'the region was empty, so this is a change too');
});

test('disposing while a repeat is waiting for its tick says nothing', () => {
  const timers = fakeTimers();
  const announcer = createAnnouncer({ timers });
  announcer.say('Copied');
  announcer.say('Copied');
  announcer.dispose();
  assert.equal(timers.queue.length, 0);
  assert.equal(announcer.state.text, '');
});

test('saying nothing says nothing', () => {
  const timers = fakeTimers();
  const announcer = createAnnouncer({ timers });
  announcer.say('Copied');
  announcer.say('');
  announcer.say(undefined);
  assert.equal(announcer.state.text, '');
  assert.equal(timers.queue.length, 0);
});

test('the state goes through the component\'s reactive wrapper, and disposing empties it and leaves no timer', () => {
  const timers = fakeTimers();
  const wrapped = [];
  const announcer = createAnnouncer({ timers, wrap: (state) => { wrapped.push(state); return state; } });
  assert.equal(announcer.state, wrapped[0]);
  announcer.say('Copied');
  announcer.dispose();
  assert.equal(announcer.state.text, '');
  assert.equal(timers.queue.length, 0);
});

test('the words for a copy, in both languages', () => {
  assert.equal(copyNotice(true, true), 'کپی شد');
  assert.equal(copyNotice(false, true), 'کپی نشد');
  assert.equal(copyNotice(true, false), 'Copied');
  assert.equal(copyNotice(false, false), "Couldn't copy");
});

test('the announcer is a key the panel provides and the message text injects', () => {
  assert.equal(typeof ANNOUNCER, 'symbol');
  const islands = new URL('../islands/', import.meta.url);
  const text = readFileSync(new URL('AssistantText.vue', islands), 'utf8');
  const panel = readFileSync(new URL('AssistantPanel.vue', islands), 'utf8');
  assert.ok(/inject\(ANNOUNCER, null\)/.test(text), 'AssistantText injects it');
  assert.ok(/provide\(ANNOUNCER, announcer\)/.test(panel), 'the panel provides it');
});

test('one live region in the pane, outside the log — and none in a message', () => {
  const islands = new URL('../islands/', import.meta.url);
  const text = readFileSync(new URL('AssistantText.vue', islands), 'utf8');
  assert.equal(/role="status"|aria-live/.test(text), false, 'a message text draws no live region of its own');

  const panel = readFileSync(new URL('AssistantPanel.vue', islands), 'utf8');
  const regions = [...panel.matchAll(/<p[^>]*data-assistant-announce[^>]*>/g)];
  assert.equal(regions.length, 1, 'exactly one announcer');
  assert.ok(/role="status"/.test(regions[0][0]) && /aria-live="polite"/.test(regions[0][0]) && /sr-only/.test(regions[0][0]));
  // The log is the element with data-assistant-log; it ends before the thinking status line. The announcer
  // is after that line, so it is not inside the log (and not inside the aria-busy region).
  const logAt = panel.indexOf('data-assistant-log');
  const thinkingAt = panel.indexOf('data-assistant-thinking-status');
  const announceAt = panel.indexOf('data-assistant-announce');
  assert.ok(logAt > 0 && thinkingAt > logAt && announceAt > thinkingAt, 'after the log and its status line');
  assert.ok(announceAt < panel.indexOf('<form class="border-t border-line p-3"'), 'before the composer');
});
