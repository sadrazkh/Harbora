import { test } from 'node:test';
import assert from 'node:assert/strict';
import { copyText, createCopyFeedback } from '../islands/assistantCopy.js';

// Copying an answer or a code block: the Clipboard API where it is allowed, a hidden textarea where it
// is not, an honest «کپی شد» / «کپی نشد» afterwards, and the icon going back after a moment.

/** A document just big enough for the textarea fallback, recording what happened to the element. */
function fakeDocument({ execCommand = () => true, activeElement = { focused: 0, focus() { this.focused++; } } } = {}) {
  const log = { appended: [], removed: 0, selected: 0, commands: [] };
  const doc = {
    activeElement,
    log,
    body: { appendChild(el) { log.appended.push(el.value); } },
    createElement(tag) {
      assert.equal(tag, 'textarea');
      const attrs = {};
      return {
        value: '', style: {}, attrs,
        setAttribute: (k, v) => { attrs[k] = v; },
        select: () => { log.selected++; },
        setSelectionRange: () => {},
        remove: () => { log.removed++; },
      };
    },
    execCommand(command) { log.commands.push(command); return execCommand(); },
  };
  return doc;
}

test('the Clipboard API gets the text exactly', async () => {
  const written = [];
  const ok = await copyText('hello **world**\n', { clipboard: { writeText: async (t) => { written.push(t); } }, doc: fakeDocument() });
  assert.equal(ok, true);
  assert.deepEqual(written, ['hello **world**\n']);
});

test('without the Clipboard API (plain http, an old browser) a hidden textarea does the copying', async () => {
  const doc = fakeDocument();
  const ok = await copyText('docker ps', { clipboard: undefined, doc });
  assert.equal(ok, true);
  assert.deepEqual(doc.log.appended, ['docker ps']);
  assert.deepEqual(doc.log.commands, ['copy']);
  assert.equal(doc.log.selected, 1);
  assert.equal(doc.log.removed, 1, 'the element does not stay in the page');
  assert.equal(doc.activeElement.focused, 1, 'and the keyboard focus goes back to where it was');
});

test('a refused Clipboard write falls back to the textarea', async () => {
  const doc = fakeDocument();
  const ok = await copyText('x', { clipboard: { writeText: async () => { throw new DOMException('denied', 'NotAllowedError'); } }, doc });
  assert.equal(ok, true);
  assert.deepEqual(doc.log.commands, ['copy']);
});

test('when nothing can copy, it says so rather than pretending', async () => {
  const refused = fakeDocument({ execCommand: () => false });
  assert.equal(await copyText('x', { clipboard: undefined, doc: refused }), false);
  assert.equal(refused.log.removed, 1);

  const throwing = fakeDocument({ execCommand: () => { throw new Error('no'); } });
  assert.equal(await copyText('x', { clipboard: { writeText: async () => { throw new Error('no'); } }, doc: throwing }), false);
  assert.equal(throwing.log.removed, 1, 'the element is removed even when the command throws');

  assert.equal(await copyText('x', { clipboard: undefined, doc: undefined }), false, 'no document at all');
  assert.equal(await copyText('x', { clipboard: undefined, doc: { body: null } }), false);
});

test('nothing to copy is not a copy', async () => {
  let touched = 0;
  const clipboard = { writeText: async () => { touched++; } };
  assert.equal(await copyText('', { clipboard }), false);
  assert.equal(await copyText(null, { clipboard }), false);
  assert.equal(await copyText(42, { clipboard }), false);
  assert.equal(touched, 0);
});

// ---- what the button shows afterwards ------------------------------------------------------------------

function fakeTimers() {
  const queue = [];
  let id = 0;
  return {
    queue,
    setTimeout(fn, ms) { const handle = ++id; queue.push({ handle, fn, ms }); return handle; },
    clearTimeout(handle) { const at = queue.findIndex((t) => t.handle === handle); if (at >= 0) queue.splice(at, 1); },
  };
}

test('after a copy the button reads «copied» for about a second and a half, then goes back', async () => {
  const timers = fakeTimers();
  const copied = [];
  const feedback = createCopyFeedback({ copy: async (t) => { copied.push(t); return true; }, timers });
  assert.equal(feedback.statusOf('message'), 'idle');

  assert.equal(await feedback.copy('message', 'the answer'), true);
  assert.deepEqual(copied, ['the answer']);
  assert.equal(feedback.statusOf('message'), 'copied');
  assert.equal(feedback.statusOf('code-1'), 'idle', 'only the button that was pressed');
  assert.equal(timers.queue.length, 1);
  assert.equal(timers.queue[0].ms, 1500);

  timers.queue[0].fn();
  assert.equal(feedback.statusOf('message'), 'idle');
  assert.equal(feedback.state.status, 'idle');
});

test('a copy that did not work says «couldn\'t copy», on that button', async () => {
  const feedback = createCopyFeedback({ copy: async () => false, timers: fakeTimers() });
  assert.equal(await feedback.copy('code-2', 'x'), false);
  assert.equal(feedback.statusOf('code-2'), 'failed');
  assert.equal(feedback.state.status, 'failed');
});

test('a copy that throws is a failed copy, not an unhandled rejection', async () => {
  const feedback = createCopyFeedback({ copy: async () => { throw new Error('boom'); }, timers: fakeTimers() });
  assert.equal(await feedback.copy('message', 'x'), false);
  assert.equal(feedback.statusOf('message'), 'failed');
});

test('pressing another button takes over, and the first one\'s timer cannot reset the second', async () => {
  const timers = fakeTimers();
  const feedback = createCopyFeedback({ copy: async () => true, timers });
  await feedback.copy('message', 'a');
  await feedback.copy('code-0', 'b');
  assert.equal(feedback.statusOf('message'), 'idle');
  assert.equal(feedback.statusOf('code-0'), 'copied');
  assert.equal(timers.queue.length, 1, 'one timer, the latest');
});

test('nothing to copy changes nothing', async () => {
  const feedback = createCopyFeedback({ copy: async () => true, timers: fakeTimers() });
  assert.equal(await feedback.copy('message', null), false);
  assert.equal(feedback.state.status, 'idle');
});

test('the state goes through the component\'s reactive wrapper, and disposing leaves no timer running', async () => {
  const timers = fakeTimers();
  const wrapped = [];
  const feedback = createCopyFeedback({ copy: async () => true, timers, wrap: (state) => { wrapped.push(state); return state; } });
  assert.equal(wrapped.length, 1);
  assert.equal(feedback.state, wrapped[0]);

  await feedback.copy('message', 'a');
  feedback.dispose();
  assert.equal(timers.queue.length, 0);

  // A copy that finishes after the component is gone shows nothing and leaves no timer behind.
  let release;
  const slow = createCopyFeedback({ copy: () => new Promise((resolve) => { release = resolve; }), timers });
  const pending = slow.copy('message', 'a');
  slow.dispose();
  release(true);
  await pending;
  assert.equal(slow.state.status, 'idle');
  assert.equal(timers.queue.length, 0);
});
