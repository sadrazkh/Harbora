import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createAssistantApi } from '../islands/assistantApi.js';
import { createAssistantSession } from '../islands/assistantSession.js';
import { createTimeline } from '../islands/assistantTimeline.js';
import { createAssistantPrefs } from '../assistantDrawer.js';

// Round seven P3: the Stop button. session.stop() asks the server (POST /assistant/conversations/{id}/stop,
// AgentStopView) to end the turn in progress. The `stopped` event it causes is drawn by the timeline;
// what is pinned here is the asking: once, with the right id, quietly when there was nothing to stop,
// loudly when the asking failed, and a prompt re-read when this tab only follows the answer.

const CONVO = '0199d0c4-6a1e-7000-8000-0000000000c1';
const OTHER = '0199d0c4-6a1e-7000-8000-0000000000c2';

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

function fakeStorage() {
  const data = {};
  return {
    getItem: (k) => (k in data ? data[k] : null),
    setItem: (k, v) => { data[k] = String(v); },
    removeItem: (k) => { delete data[k]; },
  };
}

function fakeTimers() {
  const queue = [];
  let id = 0;
  return {
    queue,
    setTimeout(fn, ms) { const handle = ++id; queue.push({ handle, fn, ms }); return handle; },
    clearTimeout(handle) { const at = queue.findIndex((t) => t.handle === handle); if (at >= 0) queue.splice(at, 1); },
    /** Runs the timer that was set for `ms`, and lets its promise chain settle. */
    async fire(ms) {
      const at = queue.findIndex((t) => t.ms === ms);
      assert.ok(at >= 0, `a timer for ${ms} ms was expected; there are ${queue.map((t) => t.ms).join(', ') || 'none'}`);
      const [timer] = queue.splice(at, 1);
      timer.fn();
      await settle();
    },
  };
}

const view = ({ inProgress = false, messages = [] } = {}) => ({ id: CONVO, title: 'shop', messages, pendingPlan: null, inProgress });
const userMessage = (text) => ({ id: 'u1', role: 'user', text, toolName: null, createdAt: '2026-10-15T10:00:00Z' });

/** `stopCalls` resolve when the test says so: `gate.stop` is the pending api.stop. */
function setup({ views = [] } = {}) {
  const prefs = createAssistantPrefs(fakeStorage());
  const timeline = createTimeline();
  const timers = fakeTimers();
  const calls = [];
  const queue = [...views];
  const gate = { stop: null, send: null };
  const api = {
    async createConversation() { return { id: CONVO, title: '', updatedAt: '2026-10-15T10:00:00Z' }; },
    send(args) {
      calls.push({ op: 'send', id: args.conversationId });
      args.onEvent('started', { conversationId: args.conversationId });
      return new Promise((resolve) => { gate.send = () => resolve({ completed: true }); });
    },
    async conversation(id) { calls.push({ op: 'get', id }); return queue.shift() ?? view(); },
    stop(id) {
      calls.push({ op: 'stop', id });
      return new Promise((resolve, reject) => { gate.stop = { resolve, reject }; });
    },
  };
  const session = createAssistantSession({ api, prefs, timeline, timers, now: () => Date.parse('2026-10-15T10:00:00Z'), pollIntervalMs: 2500 });
  return { session, timeline, timers, calls, gate, prefs };
}

/** A turn streaming in this tab, with the stream held open. */
async function streaming(s) {
  s.session.send('ساخت دیتابیس');
  await settle();
  assert.equal(s.timeline.streaming, true);
  assert.equal(s.timeline.conversationId, CONVO);
}

/** A conversation this tab only follows: opened mid-answer, no stream here. */
async function following(s) {
  await s.session.open(CONVO);
  assert.equal(s.timeline.resuming, true);
  assert.equal(s.timeline.streaming, false);
}

test('Stop asks the server to stop this conversation, once', async () => {
  const s = setup();
  await streaming(s);
  const pending = s.session.stop();
  await settle();
  assert.deepEqual(s.calls.filter((c) => c.op === 'stop'), [{ op: 'stop', id: CONVO }]);
  s.gate.stop.resolve({ stopped: true });
  assert.deepEqual(await pending, { stopped: true });
  assert.equal(s.timeline.notice, null, 'a stop that worked says nothing of its own — the `stopped` event is drawn by the timeline');
});

test('pressing Stop again while the first is in flight is the same request, not a second', async () => {
  const s = setup();
  await streaming(s);
  const first = s.session.stop();
  const second = s.session.stop();
  await settle();
  assert.equal(s.calls.filter((c) => c.op === 'stop').length, 1);
  s.gate.stop.resolve({ stopped: true });
  assert.deepEqual(await Promise.all([first, second]), [{ stopped: true }, { stopped: true }]);
});

test('once it has answered, Stop can be pressed again', async () => {
  const s = setup();
  await streaming(s);
  const first = s.session.stop();
  await settle();
  s.gate.stop.resolve({ stopped: false });
  await first;
  const again = s.session.stop();
  await settle();
  assert.equal(s.calls.filter((c) => c.op === 'stop').length, 2);
  s.gate.stop.resolve({ stopped: true });
  await again;
});

test('{ stopped: false } — it had already finished — is silent', async () => {
  const s = setup();
  await streaming(s);
  const pending = s.session.stop();
  await settle();
  s.gate.stop.resolve({ stopped: false });
  const outcome = await pending;
  assert.deepEqual(outcome, { stopped: false });
  assert.equal(outcome.error, undefined);
  assert.equal(s.timeline.notice, null);
});

test('an error becomes the composer\'s notice, in both languages, and the next press works', async () => {
  const s = setup();
  await streaming(s);
  const failed = s.session.stop();
  await settle();
  s.gate.stop.reject(Object.assign(new Error('Could not reach the panel.'), {
    status: 0, messageEn: 'Could not reach the panel.', messageFa: 'ارتباط با پنل برقرار نشد.',
  }));
  const outcome = await failed;
  assert.equal(outcome.stopped, false);
  assert.ok(outcome.error);
  assert.equal(s.timeline.notice.messageEn, 'Could not reach the panel.');
  assert.equal(s.timeline.notice.messageFa, 'ارتباط با پنل برقرار نشد.');

  const again = s.session.stop();
  await settle();
  assert.equal(s.calls.filter((c) => c.op === 'stop').length, 2, 'a failure does not lock the button');
  s.gate.stop.resolve({ stopped: true });
  await again;
});

test('an error with no words of its own still says something', async () => {
  const s = setup();
  await streaming(s);
  const failed = s.session.stop();
  await settle();
  s.gate.stop.reject(new Error('socket hang up'));
  await failed;
  assert.match(s.timeline.notice.messageEn, /socket hang up/);
  assert.match(s.timeline.notice.messageFa, /socket hang up/);
});

test('an error that arrives after the person moved to another conversation is not shown there', async () => {
  const s = setup({ views: [view({ messages: [userMessage('hi')] })] });
  await streaming(s);
  const failed = s.session.stop();
  await settle();
  s.session.startNew();
  s.gate.stop.reject(new Error('late'));
  await failed;
  assert.equal(s.timeline.notice, null);
});

test('with nothing to stop there is no request at all', async () => {
  const idle = setup();
  assert.deepEqual(await idle.session.stop(), { stopped: false }, 'no conversation yet');
  await idle.session.open(CONVO);
  assert.deepEqual(await idle.session.stop(), { stopped: false }, 'a conversation that is not being answered');
  assert.equal(idle.calls.filter((c) => c.op === 'stop').length, 0);
});

test('when this tab only follows the answer, the conversation is read again promptly after Stop', async () => {
  const finished = view({ messages: [userMessage('hi'), { id: 'a1', role: 'assistant', text: 'part of it', toolName: null, createdAt: '2026-10-15T10:00:05Z' }] });
  const s = setup({ views: [view({ inProgress: true, messages: [userMessage('hi')] }), finished] });
  await following(s);
  assert.equal(s.timers.queue.map((t) => t.ms).join(), '2500', 'the poller waits for its next tick');

  const pending = s.session.stop();
  await settle();
  s.gate.stop.resolve({ stopped: true });
  assert.deepEqual(await pending, { stopped: true });

  const soon = s.timers.queue.find((t) => t.ms !== 2500);
  assert.ok(soon, 'a re-read was scheduled');
  assert.ok(soon.ms < 2500, `sooner than the next poll, not ${soon.ms} ms`);
  const readsBefore = s.calls.filter((c) => c.op === 'get').length;
  await s.timers.fire(soon.ms);
  assert.equal(s.calls.filter((c) => c.op === 'get').length, readsBefore + 1, 'it read the conversation');
  assert.equal(s.timeline.resuming, false, 'and, finding it finished, no longer waits');
  assert.equal(s.session.polling, false);
  assert.equal(s.timeline.items.at(-1).text, 'part of it');
});

test('a stop that found the answer already finished reads the conversation too, but says nothing', async () => {
  const s = setup({ views: [view({ inProgress: true, messages: [userMessage('hi')] }), view({ messages: [userMessage('hi')] })] });
  await following(s);
  const pending = s.session.stop();
  await settle();
  s.gate.stop.resolve({ stopped: false });
  await pending;
  assert.equal(s.timeline.notice, null);
  await s.timers.fire(s.timers.queue.find((t) => t.ms !== 2500).ms);
  assert.equal(s.timeline.resuming, false);
});

test('a turn streaming in this tab is not re-read: its own stream carries the ending', async () => {
  const s = setup();
  await streaming(s);
  const pending = s.session.stop();
  await settle();
  s.gate.stop.resolve({ stopped: true });
  await pending;
  assert.equal(s.timers.queue.length, 0);
  assert.equal(s.calls.filter((c) => c.op === 'get').length, 0);
});

test('a failed stop does not re-read', async () => {
  const s = setup({ views: [view({ inProgress: true, messages: [userMessage('hi')] })] });
  await following(s);
  const pending = s.session.stop();
  await settle();
  s.gate.stop.reject(new Error('down'));
  await pending;
  assert.deepEqual(s.timers.queue.map((t) => t.ms), [2500], 'only the poller\'s own tick');
});

// ---- the HTTP call -------------------------------------------------------------------------------------

function recordingFetch(respond) {
  const calls = [];
  return { calls, fetch: async (url, init) => { calls.push({ url, init }); return respond(url, init); } };
}
const json = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });

test('stop is POST /assistant/conversations/{id}/stop with the antiforgery header, and answers { stopped }', async () => {
  const { calls, fetch } = recordingFetch(() => json({ stopped: true }));
  const api = createAssistantApi({ fetch, csrf: 'token-1' });
  assert.deepEqual(await api.stop(CONVO), { stopped: true });
  assert.equal(calls[0].url, `/assistant/conversations/${CONVO}/stop`);
  assert.equal(calls[0].init.method, 'POST');
  assert.equal(calls[0].init.headers['X-CSRF-TOKEN'], 'token-1');
  assert.equal(calls[0].init.headers['X-Requested-With'], 'XMLHttpRequest');
  assert.equal(calls[0].init.body, undefined);
});

test('a refused stop carries the server\'s words', async () => {
  const { fetch } = recordingFetch(() => json({ error: 'not_found', messageEn: 'No such conversation.', messageFa: 'گفت‌وگو پیدا نشد.' }, 404));
  const api = createAssistantApi({ fetch, csrf: 'x' });
  await assert.rejects(api.stop(CONVO), (error) => {
    assert.equal(error.status, 404);
    assert.equal(error.code, 'not_found');
    assert.equal(error.messageEn, 'No such conversation.');
    assert.equal(error.messageFa, 'گفت‌وگو پیدا نشد.');
    return true;
  });
});

test('the id is escaped into the path, not trusted', async () => {
  const { calls, fetch } = recordingFetch(() => json({ stopped: false }));
  await createAssistantApi({ fetch, csrf: 'x' }).stop('a/b?c');
  assert.equal(calls[0].url, '/assistant/conversations/a%2Fb%3Fc/stop');
});

// ---- the markup ----------------------------------------------------------------------------------------

test('the composer: Stop is a button of its own, Send is the submit button, and Enter reaches only Send', () => {
  const source = readFileSync(new URL('../islands/AssistantPanel.vue', import.meta.url), 'utf8');
  const stop = /<button[^>]*data-assistant-stop[^>]*>/s.exec(source)?.[0];
  assert.ok(stop, 'the Stop button exists');
  assert.ok(/\bv-if="answering"/.test(stop), 'it is there while an answer is coming');
  assert.ok(/\btype="button"/.test(stop) && !/type="submit"/.test(stop), 'pressing Enter in a form can never click it');
  assert.ok(/:disabled="stopping"/.test(stop), 'and cannot be pressed twice while it waits');
  assert.ok(/aria-label="[^"]*'Stop the answer', 'توقف پاسخ'/.test(stop) || /t\('Stop the answer', 'توقف پاسخ'\)/.test(stop));
  assert.ok(source.includes("t('Stop', 'توقف')"), 'its word is «توقف» / "Stop"');

  const send = /<button[^>]*data-assistant-send[^>]*>/s.exec(source)?.[0];
  assert.ok(send && /\bv-else\b/.test(send) && /type="submit"/.test(send), 'Send is the other branch of the same place');

  const keyHandler = /function onComposerKey[\s\S]*?\n\}/.exec(source)?.[0] ?? '';
  assert.ok(keyHandler.includes('send()'), 'Enter sends');
  assert.equal(/stop/i.test(keyHandler), false, 'Enter never stops');
  // send() itself refuses while an answer is coming, which is what makes Enter a no-op then.
  assert.ok(/const canSend = computed\(\(\) => !answering\.value/.test(source));
});
