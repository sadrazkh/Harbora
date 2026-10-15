import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createAssistantApi } from '../islands/assistantApi.js';
import { createAssistantSession } from '../islands/assistantSession.js';
import { createTimeline } from '../islands/assistantTimeline.js';
import { createAssistantPrefs } from '../assistantDrawer.js';

// Round seven P3: the Stop button. Pressing it is an intent the session remembers until the turn it is
// meant for has ended — not one request made once. `timeline.stopping` is what the button shows
// («در حال توقف…»); it stays true after the request is answered and goes false only when the turn is over.
//
// The server side (POST /assistant/conversations/{id}/stop → AgentStopView) is played by a fake whose
// answers the test scripts. The `stopped` stream event itself is drawn by the timeline, not here.

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
const assistantMessage = (text) => ({ id: 'a1', role: 'assistant', text, toolName: null, createdAt: '2026-10-15T10:00:05Z' });

/**
 * A session against a scripted server.
 *   stops         the answers to POST …/stop, in order: `true`/`false` (→ `{ stopped }`) or an Error (→ rejects);
 *                 once they are used up a request stays pending, to be answered through `gate.stop`
 *   holdCreate    the create-conversation round trip waits for `gate.create()`
 *   createFails   the server has no create endpoint (the legacy path: the id comes with `started`)
 *   holdStarted   the stream does not open with `started` until `gate.emit('started', …)`
 *   stop          a function replacing the stop endpoint altogether
 */
function setup({ views = [], stops = [], holdCreate = false, createFails = false, holdStarted = false, stop } = {}) {
  const prefs = createAssistantPrefs(fakeStorage());
  const timeline = createTimeline();
  const timers = fakeTimers();
  const calls = [];
  const queue = [...views];
  const replies = [...stops];
  const gate = { stop: null, create: null, emit: null, end: null };
  const api = {
    createConversation() {
      calls.push({ op: 'create' });
      if (createFails) return Promise.reject(Object.assign(new Error('no endpoint'), { status: 404 }));
      const created = { id: CONVO, title: '', updatedAt: '2026-10-15T10:00:00Z' };
      if (!holdCreate) return Promise.resolve(created);
      return new Promise((resolve) => { gate.create = () => resolve(created); });
    },
    send(args) {
      calls.push({ op: 'send', id: args.conversationId });
      gate.emit = (name, data) => args.onEvent(name, data);
      if (!holdStarted) args.onEvent('started', { conversationId: args.conversationId ?? CONVO });
      return new Promise((resolve) => { gate.end = () => resolve({ completed: true }); });
    },
    async conversation(id) { calls.push({ op: 'get', id }); return queue.shift() ?? view(); },
    stop(id) {
      calls.push({ op: 'stop', id });
      if (stop) return stop(id);
      if (replies.length > 0) {
        const reply = replies.shift();
        return reply instanceof Error ? Promise.reject(reply) : Promise.resolve({ stopped: reply });
      }
      return new Promise((resolve, reject) => { gate.stop = { resolve, reject }; });
    },
  };
  const session = createAssistantSession({ api, prefs, timeline, timers, now: () => Date.parse('2026-10-15T10:00:00Z'), pollIntervalMs: 2500 });
  const stopCalls = () => calls.filter((c) => c.op === 'stop').length;
  return { session, timeline, timers, calls, gate, prefs, stopCalls };
}

/** A turn streaming in this tab, held open. */
async function streaming(s, text = 'ساخت دیتابیس') {
  s.session.send(text); // not awaited: the turn stays open until the test ends it
  await settle();
  assert.equal(s.timeline.streaming, true);
}

/** A conversation this tab only follows: opened mid-answer, no stream here. */
async function following(s) {
  await s.session.open(CONVO);
  assert.equal(s.timeline.resuming, true);
  assert.equal(s.timeline.streaming, false);
}

/** The turn ends the way it does: `done`, then the stream closes. */
async function finishTurn(s) {
  s.gate.emit('done', { conversationId: CONVO, remainingToday: 5 });
  s.gate.end();
  await settle();
}

// ---- the stopping state ------------------------------------------------------------------------------

test('Stop asks the server to stop this conversation, once, and the button says «stopping» until the turn is over', async () => {
  const s = setup();
  await streaming(s);
  assert.equal(s.timeline.stopping, false);

  const pending = s.session.stop();
  assert.equal(s.timeline.stopping, true, 'from the press');
  await settle();
  assert.deepEqual(s.calls.filter((c) => c.op === 'stop'), [{ op: 'stop', id: CONVO }]);
  s.gate.stop.resolve({ stopped: true });
  assert.deepEqual(await pending, { stopped: true });
  assert.equal(s.timeline.stopping, true, 'the request being answered does not end it: the turn is still going');
  assert.equal(s.timeline.notice, null, 'a stop that worked says nothing of its own — the `stopped` event is drawn by the timeline');

  await finishTurn(s);
  assert.equal(s.timeline.streaming, false);
  assert.equal(s.timeline.stopping, false, 'the turn is over');
});

test('pressing Stop again, in flight or after the answer, is the same intent and never a second request', async () => {
  const s = setup();
  await streaming(s);
  const first = s.session.stop();
  const second = s.session.stop();
  await settle();
  assert.equal(s.stopCalls(), 1);
  s.gate.stop.resolve({ stopped: true });
  assert.deepEqual(await Promise.all([first, second]), [{ stopped: true }, { stopped: true }]);

  const third = s.session.stop();
  await settle();
  assert.equal(s.stopCalls(), 1, 'the answered intent stands until the turn ends');
  assert.deepEqual(await third, { stopped: true });
});

test('the stopping state is cleared by `done`, and a late answer then asks for nothing more', async () => {
  const s = setup();
  await streaming(s);
  const pending = s.session.stop();
  await settle();
  s.gate.emit('done', { conversationId: CONVO, remainingToday: 5 });
  assert.equal(s.timeline.stopping, false);
  s.gate.stop.resolve({ stopped: false });
  await pending;
  await settle();
  assert.equal(s.stopCalls(), 1);
  assert.equal(s.timers.queue.length, 0, 'no retry is scheduled for a turn that is over');
  s.gate.end();
});

test('the stopping state is cleared by an `error` event, and by a stream that just ends', async () => {
  const errored = setup();
  await streaming(errored);
  errored.session.stop();
  await settle();
  errored.gate.emit('error', { messageEn: 'The assistant failed.', messageFa: 'دستیار ناموفق بود.' });
  assert.equal(errored.timeline.stopping, false);
  errored.gate.end();

  const cut = setup();
  await streaming(cut);
  cut.session.stop();
  await settle();
  cut.gate.end(); // no `done`: the connection simply closed
  await settle();
  assert.equal(cut.timeline.streaming, false);
  assert.equal(cut.timeline.stopping, false);
});

// ---- no conversation id yet --------------------------------------------------------------------------

test('Stop pressed during the create round trip waits for the id, then goes — behind the message request', async () => {
  const s = setup({ holdCreate: true, holdStarted: true, stops: [false, true] });
  s.session.send('ساخت دیتابیس');
  await settle();
  assert.equal(s.timeline.streaming, true);
  assert.equal(s.timeline.conversationId, null);

  const pending = s.session.stop();
  await settle();
  assert.equal(s.timeline.stopping, true);
  assert.equal(s.stopCalls(), 0, 'there is nothing to ask about yet');

  s.gate.create();
  await settle();
  assert.deepEqual(s.calls.map((c) => c.op), ['create', 'send', 'stop'], 'the id is known — before `started` — and the stop goes out, behind the message');
  assert.deepEqual(s.calls.at(-1), { op: 'stop', id: CONVO });

  s.gate.emit('started', { conversationId: CONVO });
  await settle();
  assert.equal(s.stopCalls(), 2, '"nothing to stop" before `started`: asked again when it arrives');
  assert.deepEqual(await pending, { stopped: true });
  await finishTurn(s);
  assert.equal(s.timeline.stopping, false);
});

test('Stop pressed during the create round trip also goes when only `started` follows', async () => {
  const s = setup({ holdCreate: true, stops: [true] });
  s.session.send('ساخت دیتابیس');
  await settle();
  const pending = s.session.stop();
  s.gate.create();
  assert.deepEqual(await pending, { stopped: true });
  assert.equal(s.stopCalls(), 1, 'one request, however many of the triggers (the id, `started`) there were');
});

test('on the legacy path (no create endpoint) the stop waits for `started`, which brings the id', async () => {
  const s = setup({ createFails: true, holdStarted: true, stops: [true] });
  s.session.send('hello');
  await settle();
  assert.equal(s.timeline.conversationId, null);
  const pending = s.session.stop();
  await settle();
  assert.equal(s.stopCalls(), 0);

  s.gate.emit('started', { conversationId: CONVO });
  await settle();
  assert.deepEqual(s.calls.at(-1), { op: 'stop', id: CONVO });
  assert.deepEqual(await pending, { stopped: true });
});

// ---- { stopped: false } while this tab's stream is open -----------------------------------------------

test('"nothing to stop" before `started` is retried at once when `started` arrives, not on a timer', async () => {
  const s = setup({ holdStarted: true, stops: [false, true] });
  s.session.send('hi');
  await settle();
  const pending = s.session.stop();
  await settle();
  assert.equal(s.stopCalls(), 1, 'asked as soon as the id was known');
  assert.equal(s.timers.queue.length, 0, 'and not asked again on a timer: the turn has not been registered yet');
  assert.equal(s.timeline.stopping, true);

  s.gate.emit('started', { conversationId: CONVO });
  await settle();
  assert.equal(s.stopCalls(), 2, 'asked again the moment `started` arrived');
  assert.deepEqual(await pending, { stopped: true });
  assert.equal(s.timers.queue.length, 0);
});

test('`started` arriving while the first request is out makes the answer "false" an immediate retry', async () => {
  const s = setup({ holdStarted: true });
  s.session.send('hi');
  await settle();
  const pending = s.session.stop();
  await settle();
  assert.equal(s.stopCalls(), 1);
  s.gate.emit('started', { conversationId: CONVO }); // the first request is still in flight
  await settle();
  assert.equal(s.stopCalls(), 1, 'never two requests at once');
  s.gate.stop.resolve({ stopped: false });
  await settle();
  assert.equal(s.stopCalls(), 2, 'its "false" came from before `started`: asked again now');
  s.gate.stop.resolve({ stopped: true });
  assert.deepEqual(await pending, { stopped: true });
});

test('after `started`, "nothing to stop" is retried about every 500 ms until it stops', async () => {
  const s = setup({ stops: [false, false, true] });
  await streaming(s);
  const pending = s.session.stop();
  await settle();
  assert.equal(s.stopCalls(), 1);
  assert.deepEqual(s.timers.queue.map((t) => t.ms), [500]);

  await s.timers.fire(500);
  assert.equal(s.stopCalls(), 2);
  assert.deepEqual(s.timers.queue.map((t) => t.ms), [500]);
  await s.timers.fire(500);
  assert.equal(s.stopCalls(), 3);
  assert.deepEqual(await pending, { stopped: true });
  assert.equal(s.timers.queue.length, 0, 'stopped: nothing more to ask');
  assert.equal(s.timeline.stopping, true, 'and the stopping state waits for the turn to end');
});

test('a Stop that never takes asks six times at most, then waits for the turn to end by itself', async () => {
  const s = setup({ stops: [false, false, false, false, false, false, false, false] });
  await streaming(s);
  const pending = s.session.stop();
  await settle();
  for (let i = 0; i < 5; i++) await s.timers.fire(500);
  assert.equal(s.stopCalls(), 6);
  assert.equal(s.timers.queue.length, 0, 'no seventh');
  assert.deepEqual(await pending, { stopped: false });
  assert.equal(s.timeline.stopping, true, 'still «stopping»: only the end of the turn closes it');
  assert.equal(s.timeline.notice, null);

  await finishTurn(s);
  assert.equal(s.timeline.stopping, false);
  assert.equal(s.stopCalls(), 6);
});

test('no second request goes out while a retry is in flight', async () => {
  const s = setup({ stops: [false] }); // the first answer is "false"; the retry then stays pending
  await streaming(s);
  const pending = s.session.stop();
  await settle();
  await s.timers.fire(500);
  assert.equal(s.stopCalls(), 2);
  s.session.stop();
  s.session.stop();
  await settle();
  assert.equal(s.stopCalls(), 2, 'pressing again while the retry is out asks nothing');
  s.gate.stop.resolve({ stopped: true });
  assert.deepEqual(await pending, { stopped: true });
});

test('no retry once the turn has ended — a waiting timer is cancelled, a late answer is ignored', async () => {
  const waiting = setup({ stops: [false] });
  await streaming(waiting);
  waiting.session.stop();
  await settle();
  assert.deepEqual(waiting.timers.queue.map((t) => t.ms), [500]);
  await finishTurn(waiting);
  assert.equal(waiting.timers.queue.length, 0, 'the retry timer went with the turn');
  assert.equal(waiting.stopCalls(), 1);

  const late = setup();
  await streaming(late);
  late.session.stop();
  await settle();
  await finishTurn(late); // the turn ends while the request is still out
  late.gate.stop.resolve({ stopped: false });
  await settle();
  assert.equal(late.stopCalls(), 1);
  assert.equal(late.timers.queue.length, 0);
});

test('no retry once the person has moved to another conversation', async () => {
  const startedNew = setup({ stops: [false] });
  await streaming(startedNew);
  const pending = startedNew.session.stop();
  await settle();
  assert.deepEqual(startedNew.timers.queue.map((t) => t.ms), [500]);
  startedNew.session.startNew();
  assert.equal(startedNew.timeline.stopping, false);
  assert.equal(startedNew.timers.queue.length, 0);
  assert.deepEqual(await pending, { stopped: false });
  assert.equal(startedNew.stopCalls(), 1);

  const opened = setup({ views: [view({ messages: [userMessage('hi')] })] });
  await streaming(opened);
  opened.session.stop();
  await settle();
  await opened.session.open(OTHER);
  assert.equal(opened.timeline.stopping, false);
  assert.equal(opened.timers.queue.filter((t) => t.ms === 500).length, 0);
  assert.equal(opened.stopCalls(), 1);
});

// ---- failures ----------------------------------------------------------------------------------------

test('an error becomes the composer\'s notice, in both languages, ends «stopping», and the next press asks again', async () => {
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
  assert.equal(s.timeline.stopping, false, 'the button is Stop again');
  assert.equal(s.timers.queue.length, 0, 'an error is not retried behind the person\'s back');

  const again = s.session.stop();
  await settle();
  assert.equal(s.stopCalls(), 2, 'a failure does not lock the button');
  s.gate.stop.resolve({ stopped: true });
  await again;
});

test('an error with no words of its own still says something', async () => {
  const s = setup({ stops: [new Error('socket hang up')] });
  await streaming(s);
  await s.session.stop();
  assert.match(s.timeline.notice.messageEn, /socket hang up/);
  assert.match(s.timeline.notice.messageFa, /socket hang up/);
});

test('a stop that fails at once does not leave the button locked', async () => {
  let attempt = 0;
  const s = setup({ stop: () => { if (++attempt === 1) throw new Error('sync failure'); return Promise.resolve({ stopped: true }); } });
  await streaming(s);
  const failed = await s.session.stop();
  assert.equal(failed.stopped, false);
  assert.ok(failed.error);
  assert.match(s.timeline.notice.messageEn, /sync failure/);
  assert.equal(s.timeline.stopping, false);
  assert.deepEqual(await s.session.stop(), { stopped: true }, 'the next press asks again');
  assert.equal(s.stopCalls(), 2);
});

test('an error that arrives after the person moved to another conversation is not shown there', async () => {
  const s = setup();
  await streaming(s);
  const failed = s.session.stop();
  await settle();
  s.session.startNew();
  s.gate.stop.reject(new Error('late'));
  assert.deepEqual(await failed, { stopped: false });
  await settle();
  assert.equal(s.timeline.notice, null);
});

test('with nothing to stop there is no request at all, and nothing says «stopping»', async () => {
  const idle = setup();
  assert.deepEqual(await idle.session.stop(), { stopped: false }, 'no conversation yet');
  await idle.session.open(CONVO);
  assert.deepEqual(await idle.session.stop(), { stopped: false }, 'a conversation that is not being answered');
  assert.equal(idle.stopCalls(), 0);
  assert.equal(idle.timeline.stopping, false);
});

// ---- a conversation only followed after a reload -----------------------------------------------------

test('when this tab only follows the answer: one request, then the conversation is read again promptly', async () => {
  const finished = view({ messages: [userMessage('hi'), assistantMessage('part of it')] });
  const s = setup({ views: [view({ inProgress: true, messages: [userMessage('hi')] }), finished], stops: [true] });
  await following(s);
  assert.equal(s.timers.queue.map((t) => t.ms).join(), '2500', 'the poller waits for its next tick');

  const pending = s.session.stop();
  assert.equal(s.timeline.stopping, true);
  assert.deepEqual(await pending, { stopped: true });
  assert.equal(s.timeline.stopping, true, 'until the re-read finds the turn over');

  const soon = s.timers.queue.find((t) => t.ms !== 2500);
  assert.ok(soon, 'a re-read was scheduled');
  assert.ok(soon.ms < 2500, `sooner than the next poll, not ${soon.ms} ms`);
  const readsBefore = s.calls.filter((c) => c.op === 'get').length;
  await s.timers.fire(soon.ms);
  assert.equal(s.calls.filter((c) => c.op === 'get').length, readsBefore + 1, 'it read the conversation');
  assert.equal(s.timeline.resuming, false, 'and, finding it finished, no longer waits');
  assert.equal(s.timeline.stopping, false);
  assert.equal(s.session.polling, false);
  assert.equal(s.timeline.items.at(-1).text, 'part of it');
  assert.equal(s.stopCalls(), 1);
});

test('there "nothing to stop" is the truth — it had finished: silent, one request, no retries', async () => {
  const s = setup({ views: [view({ inProgress: true, messages: [userMessage('hi')] }), view({ messages: [userMessage('hi')] })], stops: [false] });
  await following(s);
  assert.deepEqual(await s.session.stop(), { stopped: false });
  assert.equal(s.timeline.notice, null);
  assert.deepEqual(s.timers.queue.map((t) => t.ms).sort((a, b) => a - b).filter((ms) => ms === 500), [], 'no 500 ms retry');
  await s.timers.fire(s.timers.queue.find((t) => t.ms !== 2500).ms);
  assert.equal(s.timeline.resuming, false);
  assert.equal(s.timeline.stopping, false);
  assert.equal(s.stopCalls(), 1);
});

test('"nothing to stop" followed by a conversation that is still being answered is not left «stopping»', async () => {
  const s = setup({ views: [view({ inProgress: true }), view({ inProgress: true })], stops: [false] });
  await following(s);
  await s.session.stop();
  await s.timers.fire(s.timers.queue.find((t) => t.ms !== 2500).ms);
  assert.equal(s.timeline.resuming, true, 'it is being answered after all — perhaps by a new turn from another tab');
  assert.equal(s.timeline.stopping, false, 'so the button is Stop again');
});

test('a stop that was taken but whose turn is still going stays «stopping» through the re-read', async () => {
  const s = setup({ views: [view({ inProgress: true }), view({ inProgress: true })], stops: [true] });
  await following(s);
  await s.session.stop();
  await s.timers.fire(s.timers.queue.find((t) => t.ms !== 2500).ms);
  assert.equal(s.timeline.resuming, true);
  assert.equal(s.timeline.stopping, true);
});

test('leaving a followed conversation — a new one, or another opened — ends «stopping»', async () => {
  const fresh = setup({ views: [view({ inProgress: true })], stops: [true] });
  await following(fresh);
  await fresh.session.stop();
  assert.equal(fresh.timeline.stopping, true);
  fresh.session.startNew();
  assert.equal(fresh.timeline.stopping, false);
  const readsBefore = fresh.calls.filter((c) => c.op === 'get').length;
  for (const timer of [...fresh.timers.queue]) { timer.fn(); await settle(); } // whatever is still waiting does nothing
  assert.equal(fresh.calls.filter((c) => c.op === 'get').length, readsBefore, 'no read of the conversation that was left');
  assert.equal(fresh.timeline.items.length, 0);

  const other = setup({ views: [view({ inProgress: true }), view({ messages: [userMessage('hi')] })], stops: [true] });
  await following(other);
  await other.session.stop();
  await other.session.open(OTHER);
  assert.equal(other.timeline.stopping, false);
});

test('a turn streaming in this tab is not re-read: its own stream carries the ending', async () => {
  const s = setup({ stops: [true] });
  await streaming(s);
  await s.session.stop();
  assert.equal(s.timers.queue.length, 0);
  assert.equal(s.calls.filter((c) => c.op === 'get').length, 0);
});

test('a failed stop of a followed conversation does not re-read, and is not «stopping»', async () => {
  const s = setup({ views: [view({ inProgress: true, messages: [userMessage('hi')] })], stops: [new Error('down')] });
  await following(s);
  await s.session.stop();
  assert.deepEqual(s.timers.queue.map((t) => t.ms), [2500], 'only the poller\'s own tick');
  assert.equal(s.timeline.stopping, false);
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

const panelSource = () => readFileSync(new URL('../islands/AssistantPanel.vue', import.meta.url), 'utf8');

test('the composer: Stop is a button of its own, Send is the submit button, and Enter reaches only Send', () => {
  const source = panelSource();
  const stop = /<button[^>]*data-assistant-stop[^>]*>/s.exec(source)?.[0];
  assert.ok(stop, 'the Stop button exists');
  assert.ok(/\bv-if="answering"/.test(stop), 'it is there while an answer is coming');
  assert.ok(/\btype="button"/.test(stop) && !/type="submit"/.test(stop), 'pressing Enter in a form can never click it');
  assert.ok(/t\('Stop the answer', 'توقف پاسخ'\)/.test(stop), 'its name is «توقف پاسخ» / "Stop the answer"');
  assert.ok(source.includes("t('Stop', 'توقف')"), 'its word is «توقف» / "Stop"');

  const send = /<button[^>]*data-assistant-send[^>]*>/s.exec(source)?.[0];
  assert.ok(send && /\bv-else\b/.test(send) && /type="submit"/.test(send), 'Send is the other branch of the same place');

  const keyHandler = /function onComposerKey[\s\S]*?\n\}/.exec(source)?.[0] ?? '';
  assert.ok(keyHandler.includes('send()'), 'Enter sends');
  assert.equal(/stop/i.test(keyHandler), false, 'Enter never stops');
  // send() itself refuses while an answer is coming, which is what makes Enter a no-op then.
  assert.ok(/const canSend = computed\(\(\) => !answering\.value/.test(source));
});

test('while stopping the button says «در حال توقف…» / "Stopping…", spins, and cannot be pressed — but keeps the focus', () => {
  const source = panelSource();
  const stop = /<button[^>]*data-assistant-stop[^>]*>[\s\S]*?<\/button>/.exec(source)?.[0] ?? '';
  assert.ok(source.includes("const stopping = computed(() => timeline.stopping === true)"), 'the state is the session\'s, not the island\'s');
  assert.ok(/t\('Stopping…', 'در حال توقف…'\)/.test(stop), 'the word');
  assert.ok(/LoaderCircle/.test(stop) && /:spin="stopping"/.test(stop), 'the spinner');
  // aria-disabled rather than disabled: a focused button that becomes disabled drops the keyboard
  // focus in some browsers, and the person would be left nowhere.
  assert.ok(/:aria-disabled="stopping \? 'true' : undefined"/.test(stop), 'announced as unavailable');
  assert.equal(/:disabled=/.test(stop), false, 'but not natively disabled');
  const handler = /function stopAnswer\(\)[\s\S]*?\n\}/.exec(source)?.[0] ?? '';
  assert.ok(/if \(stopping\.value\) return;/.test(handler), 'a press while stopping does nothing');
  assert.ok(/session\.stop\(\)/.test(handler));
});

test('focus goes to the composer only once the answer is over — never while Stop or Stopping is on screen', () => {
  const source = panelSource();
  const handler = /function stopAnswer\(\)[\s\S]*?\n\}/.exec(source)?.[0] ?? '';
  assert.equal(/focusComposer/.test(handler), false, 'pressing Stop moves nothing');
  const watcher = /watch\(answering[\s\S]*?\n\}\);/.exec(source)?.[0] ?? '';
  assert.ok(watcher, 'it waits for `answering`');
  assert.ok(/if \(!now && stopPressed\)/.test(watcher) && /focusComposer\(\)/.test(watcher), 'and moves the focus then, when Stop was pressed');
});
