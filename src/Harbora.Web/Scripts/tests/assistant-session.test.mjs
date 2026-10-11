import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAssistantSession, createResumePoller } from '../islands/assistantSession.js';
import { createTimeline, isThinking } from '../islands/assistantTimeline.js';
import { createAssistantPrefs, STORAGE_CONVERSATION, STORAGE_OUTBOX } from '../assistantDrawer.js';

// The session against a scripted server, storage and clock. What is pinned here is the owner's report
// after the first deploy — a reload mid-answer lost the message — and the three things that fix it:
// the conversation exists (and is remembered) before the message is sent, the words wait in an outbox
// until the server has them, and a conversation still being answered is re-read until it is over.

const CONVO = '0199d0c4-6a1e-7000-8000-0000000000c1';

function fakeStorage(initial = {}) {
  const data = { ...initial };
  return {
    data,
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
    /** Runs the next timer and lets its promise chain settle. */
    async next() { const t = queue.shift(); assert.ok(t, 'a timer was expected'); t.fn(); await settle(); return t; },
  };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

function view({ messages = [], inProgress = false, pendingPlan = null } = {}) {
  return { id: CONVO, title: 'shop', messages, pendingPlan, inProgress };
}
const userMessage = (text, id = 'u1') => ({ id, role: 'user', text, toolName: null, createdAt: '2026-10-11T10:00:00Z' });
const assistantMessage = (text, id = 'a1') => ({ id, role: 'assistant', text, toolName: null, createdAt: '2026-10-11T10:00:05Z' });

function setup({ stored = {}, script = {} } = {}) {
  const storage = fakeStorage(stored);
  const prefs = createAssistantPrefs(storage);
  const timeline = createTimeline();
  const timers = fakeTimers();
  const calls = [];
  const views = [...(script.views ?? [])];
  const api = {
    async createConversation() {
      calls.push({ op: 'create', storedBefore: storage.data[STORAGE_CONVERSATION] ?? null });
      if (script.createFails) throw Object.assign(new Error('no endpoint'), { status: 404 });
      return { id: CONVO, title: '', updatedAt: '2026-10-11T10:00:00Z' };
    },
    async send(args) {
      calls.push({
        op: 'send', conversationId: args.conversationId, text: args.text,
        storedAtSend: storage.data[STORAGE_CONVERSATION] ?? null,
        outboxAtSend: prefs.outbox(args.conversationId),
      });
      if (script.send) return script.send(args, { storage, timeline });
      args.onEvent('started', { conversationId: args.conversationId ?? CONVO });
      args.onEvent('message', { id: 'a9', role: 'assistant', text: 'ok' });
      args.onEvent('done', { conversationId: args.conversationId ?? CONVO, remainingToday: 5 });
      return { completed: true };
    },
    async conversation(id) {
      calls.push({ op: 'get', id });
      const next = views.shift();
      if (next instanceof Error) throw next;
      return next ?? view();
    },
  };
  const session = createAssistantSession({ api, prefs, timeline, timers, now: () => Date.parse('2026-10-11T10:00:00Z') });
  return { storage, prefs, timeline, timers, calls, api, session };
}

test('a new conversation is created and remembered before the first message is sent', async () => {
  const s = setup();
  const result = await s.session.send('یک دیتابیس بساز');
  assert.deepEqual(s.calls.map((c) => c.op), ['create', 'send']);
  assert.equal(s.calls[1].conversationId, CONVO, 'sent to the created conversation, not to "new"');
  assert.equal(s.calls[1].storedAtSend, CONVO, 'the id was in storage before the request went out');
  assert.equal(s.calls[1].outboxAtSend.text, 'یک دیتابیس بساز', 'the words were kept before the request went out');
  assert.equal(s.storage.data[STORAGE_OUTBOX], undefined, 'and dropped once the server said it had them');
  assert.deepEqual(result, { sent: true });
});

test('an existing conversation is sent to directly, without creating another', async () => {
  const s = setup({ script: { views: [view({ messages: [userMessage('hi')] })] } });
  await s.session.open(CONVO);
  await s.session.send('next');
  assert.deepEqual(s.calls.map((c) => c.op), ['get', 'send']);
  assert.equal(s.calls[1].conversationId, CONVO);
});

test('a server without the create endpoint still gets the message, sent to "new"', async () => {
  const s = setup({ script: { createFails: true } });
  await s.session.send('hello');
  assert.deepEqual(s.calls.map((c) => c.op), ['create', 'send']);
  assert.equal(s.calls[1].conversationId, null);
  assert.equal(s.storage.data[STORAGE_CONVERSATION], CONVO, 'remembered from `started`, as before');
});

test('a reload before the server had the message brings the words back in the composer', async () => {
  // The first page: created, remembered, sent — and the page goes before `started` arrives.
  const first = setup({ script: { send: async () => new Promise(() => {}) } });
  first.session.send('ساخت shop-db');
  await settle();
  assert.equal(first.storage.data[STORAGE_CONVERSATION], CONVO);
  assert.ok(first.storage.data[STORAGE_OUTBOX]);

  // The reloaded page: the conversation exists but the message never got saved.
  const second = setup({ stored: { ...first.storage.data }, script: { views: [view({ messages: [] })] } });
  const restored = await second.session.restore();
  assert.equal(second.calls[0].op, 'get');
  assert.equal(restored.restoreDraft, 'ساخت shop-db');
  assert.match(second.timeline.notice.messageEn, /may not have reached/);
  assert.equal(second.storage.data[STORAGE_OUTBOX], undefined);
});

test('a reload after the server saved the message shows it, and nothing comes back to the composer', async () => {
  const stored = {
    [STORAGE_CONVERSATION]: CONVO,
    [STORAGE_OUTBOX]: JSON.stringify({ conversationId: CONVO, text: 'ساخت shop-db', at: Date.parse('2026-10-11T09:59:58Z') }),
  };
  const s = setup({ stored, script: { views: [view({ messages: [userMessage('ساخت shop-db')], inProgress: true })] } });
  const restored = await s.session.restore();
  assert.equal(restored.restoreDraft, null);
  assert.equal(s.timeline.items[0].kind, 'user', 'the person sees their own message at once');
  assert.equal(s.timeline.items[0].text, 'ساخت shop-db');
  assert.equal(s.timeline.notice, null);
});

test('with no conversation remembered, an outbox message still comes back', async () => {
  const stored = { [STORAGE_OUTBOX]: JSON.stringify({ conversationId: null, text: 'hello', at: Date.parse('2026-10-11T09:59:00Z') }) };
  const s = setup({ stored });
  const restored = await s.session.restore();
  assert.equal(restored.restoreDraft, 'hello');
  assert.deepEqual(s.calls, []);
});

test('an outbox older than an hour, or another conversation\'s, is not offered back here', async () => {
  const old = setup({ stored: { [STORAGE_OUTBOX]: JSON.stringify({ conversationId: null, text: 'old', at: Date.parse('2026-10-11T08:00:00Z') }) } });
  assert.equal((await old.session.restore()).restoreDraft, null);
  assert.equal(old.storage.data[STORAGE_OUTBOX], undefined);

  const otherBox = JSON.stringify({ conversationId: '0199d0c4-6a1e-7000-8000-0000000000c9', text: 'elsewhere', at: Date.parse('2026-10-11T09:59:00Z') });
  const other = setup({ stored: { [STORAGE_CONVERSATION]: CONVO, [STORAGE_OUTBOX]: otherBox }, script: { views: [view()] } });
  assert.equal((await other.session.restore()).restoreDraft, null);
  assert.equal(other.storage.data[STORAGE_OUTBOX], otherBox, 'kept for its own conversation');
});

test('a conversation still being answered is re-read until it is over, then drawn as it ended', async () => {
  const s = setup({
    stored: { [STORAGE_CONVERSATION]: CONVO },
    script: {
      views: [
        view({ messages: [userMessage('q')], inProgress: true }),
        view({ messages: [userMessage('q'), assistantMessage('half')], inProgress: true }),
        view({ messages: [userMessage('q'), assistantMessage('the answer')], inProgress: false }),
      ],
    },
  });
  await s.session.restore();
  assert.equal(s.timeline.resuming, true);
  assert.equal(isThinking(s.timeline), true, 'the dots show for a conversation opened mid-answer');
  assert.equal(s.session.polling, true);
  assert.equal(s.timers.queue[0].ms, 2500);

  await s.timers.next();
  assert.equal(s.timeline.items.at(-1).text, 'half');
  assert.equal(s.timeline.resuming, true);

  await s.timers.next();
  assert.equal(s.timeline.items.at(-1).text, 'the answer');
  assert.equal(s.timeline.resuming, false);
  assert.equal(isThinking(s.timeline), false);
  assert.equal(s.session.polling, false, 'stops once the server says it is over');
  assert.equal(s.timers.queue.length, 0);
});

test('a conversation that is not being answered is read once and not polled', async () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: CONVO }, script: { views: [view({ messages: [userMessage('q')] })] } });
  await s.session.restore();
  assert.equal(s.timeline.resuming, false);
  assert.equal(s.session.polling, false);
});

test('closing the drawer stops the re-reading, opening it carries on, and unmounting stops it for good', async () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: CONVO }, script: { views: [view({ inProgress: true }), view({ inProgress: true })] } });
  await s.session.restore();
  assert.equal(s.session.polling, true);
  s.session.pause();
  assert.equal(s.session.polling, false);
  assert.equal(s.timers.queue.length, 0);
  s.session.wake();
  assert.equal(s.session.polling, true);
  s.session.dispose();
  assert.equal(s.session.polling, false);
  assert.equal(s.timers.queue.length, 0);
});

test('a read that comes back after the polling stopped changes nothing', async () => {
  let release;
  const s = setup({ stored: { [STORAGE_CONVERSATION]: CONVO }, script: { views: [view({ messages: [userMessage('q')], inProgress: true })] } });
  await s.session.restore();
  s.api.conversation = () => new Promise((resolve) => { release = resolve; });
  s.timers.queue[0].fn();
  s.session.dispose();
  release(view({ messages: [userMessage('q'), assistantMessage('late')], inProgress: false }));
  await settle();
  assert.equal(s.timeline.items.length, 1, 'nothing drawn from a read nobody is waiting for');
});

test('starting a new conversation stops polling the old one', async () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: CONVO }, script: { views: [view({ inProgress: true })] } });
  await s.session.restore();
  s.session.startNew();
  assert.equal(s.session.polling, false);
  assert.equal(s.timeline.resuming, false);
  assert.equal(s.storage.data[STORAGE_CONVERSATION], undefined);
});

test('"still answering" takes the message back to the composer and waits for the answer in progress', async () => {
  const s = setup({
    script: {
      views: [view({ messages: [userMessage('first')] }), view({ messages: [userMessage('first')], inProgress: true })],
      send: async () => ({ completed: false, busy: true, notice: { messageEn: 'Still answering the previous message.', messageFa: 'هنوز در حال پاسخ است.' } }),
    },
  });
  await s.session.open(CONVO);
  const result = await s.session.send('second');
  assert.deepEqual(result, { sent: false, restoreDraft: 'second' });
  assert.deepEqual(s.timeline.items.map((i) => i.text), ['first'], 'the refused message is not left in the conversation');
  assert.equal(s.timeline.notice.messageEn, 'Still answering the previous message.');
  assert.equal(s.timeline.items.some((i) => i.kind === 'error'), false, 'a notice, not an error');
  assert.equal(s.timeline.resuming, true);
  assert.equal(s.session.polling, true);
  assert.equal(s.storage.data[STORAGE_OUTBOX], undefined);
  assert.deepEqual(await s.session.send('third'), { sent: false }, 'the composer waits while the answer is in progress');
});

test('a message that never reached the server stays visible, marked not sent, and comes back to the composer', async () => {
  const s = setup({
    script: {
      send: async (args) => {
        args.onEvent('error', { messageEn: 'Could not reach the panel.', messageFa: 'x' });
        return { completed: false };
      },
    },
  });
  const result = await s.session.send('hello');
  assert.deepEqual(result, { sent: false, restoreDraft: 'hello' });
  const user = s.timeline.items.find((i) => i.kind === 'user');
  assert.equal(user.unsent, true);
  assert.equal(s.timeline.streaming, false);
  assert.equal(s.storage.data[STORAGE_OUTBOX], undefined);
});

test('the dots follow the turn: on send, gone with the first text, back after a tool, gone at the end', async () => {
  const seen = [];
  const s = setup({
    script: {
      send: async (args, { timeline }) => {
        seen.push(['sent', isThinking(timeline)]);
        args.onEvent('started', { conversationId: CONVO });
        seen.push(['started', isThinking(timeline)]);
        args.onEvent('tool', { name: 'get_app', state: 'running' });
        seen.push(['tool running', isThinking(timeline)]);
        args.onEvent('tool', { name: 'get_app', state: 'done' });
        seen.push(['tool done', isThinking(timeline)]);
        args.onEvent('delta', { text: 'shop ' });
        seen.push(['delta', isThinking(timeline)]);
        args.onEvent('message', { id: 'm', role: 'assistant', text: 'shop is running.' });
        seen.push(['message', isThinking(timeline)]);
        args.onEvent('done', { conversationId: CONVO, remainingToday: 1 });
        return { completed: true };
      },
    },
  });
  await s.session.send('how is shop?');
  assert.deepEqual(seen, [
    ['sent', true], ['started', true], ['tool running', false], ['tool done', true], ['delta', false], ['message', false],
  ]);
  assert.equal(isThinking(s.timeline), false, 'the turn is over');
});

test('the poller retries a failed read a few times and stops when the conversation is gone', async () => {
  const timers = fakeTimers();
  const results = [Object.assign(new Error('offline'), { status: 0 }), view({ inProgress: true }), Object.assign(new Error('gone'), { status: 404 })];
  let gone = null;
  const updates = [];
  const poller = createResumePoller({
    load: async () => { const r = results.shift(); if (r instanceof Error) throw r; return r; },
    onUpdate: (v) => updates.push(v.inProgress),
    onGone: (e) => { gone = e; },
    timers,
  });
  poller.start(CONVO);
  await timers.next(); // offline — tried again
  assert.equal(poller.running, true);
  await timers.next(); // still answering
  assert.deepEqual(updates, [true]);
  await timers.next(); // 404
  assert.equal(gone.status, 404);
  assert.equal(poller.running, false);
});

// ---- fixes after review ----------------------------------------------------------------------------

const OTHER = '0199d0c4-6a1e-7000-8000-0000000000c9';

test('opening another conversation that fails to load keeps following the one on screen', async () => {
  const s = setup({
    stored: { [STORAGE_CONVERSATION]: CONVO },
    script: { views: [view({ messages: [userMessage('q')], inProgress: true }), Object.assign(new Error('offline'), { status: 0, messageEn: 'Could not reach the panel.', messageFa: 'x' })] },
  });
  await s.session.restore();
  assert.equal(s.session.polling, true);
  const result = await s.session.open(OTHER);
  assert.equal(result.error.status, 0);
  assert.equal(s.timeline.conversationId, CONVO, 'the conversation on screen is still the one it was');
  assert.equal(s.timeline.resuming, true);
  assert.equal(s.session.polling, true, 'never resuming without a poller — the dots cannot be stuck on');
});

test('a failed open of another conversation, with nothing being followed, leaves no dots behind', async () => {
  const s = setup({ script: { views: [view({ messages: [userMessage('q')] }), Object.assign(new Error('boom'), { status: 500 })] } });
  await s.session.open(CONVO);
  await s.session.open(OTHER);
  assert.equal(s.timeline.resuming, false);
  assert.equal(s.session.polling, false);
  assert.equal(isThinking(s.timeline), false);
});

test('a reload while the conversation is still being created loses no words', async () => {
  let releaseCreate;
  const first = setup();
  first.api.createConversation = () => new Promise((resolve) => { releaseCreate = resolve; });
  first.session.send('hello there');
  await settle();
  assert.equal(first.prefs.outbox(null).text, 'hello there', 'written before the create, under "new"');

  const second = setup({ stored: { ...first.storage.data } });
  const restored = await second.session.restore();
  assert.equal(restored.restoreDraft, 'hello there');
  releaseCreate({ id: CONVO });
});

test('once created, the waiting words move to the new conversation', async () => {
  const s = setup({ script: { send: async () => new Promise(() => {}) } });
  s.session.send('hello');
  await settle();
  assert.equal(s.prefs.outbox(null), null);
  assert.equal(s.prefs.outbox(CONVO).text, 'hello');
});

test('a failed read on reload is shown, keeps the remembered conversation, and blocks a fresh start until retried', async () => {
  const waiting = { [CONVO]: { conversationId: CONVO, text: 'my words', at: Date.parse('2026-10-11T09:59:00Z') } };
  const s = setup({
    stored: { [STORAGE_CONVERSATION]: CONVO, [STORAGE_OUTBOX]: JSON.stringify(waiting) },
    script: { views: [Object.assign(new Error('Bad gateway'), { status: 502, messageEn: 'The panel failed to answer (HTTP 502).', messageFa: 'x' }), view({ messages: [] })] },
  });
  const restored = await s.session.restore();
  assert.equal(restored.error.status, 502);
  assert.deepEqual(s.timeline.loadError, { conversationId: CONVO, messageEn: 'The panel failed to answer (HTTP 502).', messageFa: 'x' });
  assert.equal(s.storage.data[STORAGE_CONVERSATION], CONVO, 'the remembered id is kept');
  assert.equal(s.prefs.outbox(CONVO).text, 'my words', 'the waiting words are kept');
  assert.deepEqual(await s.session.send('a new message'), { sent: false }, 'not treated as a fresh conversation');
  assert.equal(s.calls.some((c) => c.op === 'create'), false);

  const retried = await s.session.retry();
  assert.equal(s.timeline.loadError, null);
  assert.equal(retried.restoreDraft, 'my words');
});

test('starting a new conversation from the failed state is allowed and leaves the old words waiting', async () => {
  const waiting = { [CONVO]: { conversationId: CONVO, text: 'my words', at: Date.parse('2026-10-11T09:59:00Z') } };
  const s = setup({ stored: { [STORAGE_CONVERSATION]: CONVO, [STORAGE_OUTBOX]: JSON.stringify(waiting) }, script: { views: [Object.assign(new Error('x'), { status: 500 })] } });
  await s.session.restore();
  s.session.startNew();
  assert.equal(s.timeline.loadError, null);
  assert.equal(s.prefs.outbox(CONVO).text, 'my words');
});

test('a message sent in one conversation does not overwrite words waiting for another', async () => {
  const waiting = { [OTHER]: { conversationId: OTHER, text: 'for the other one', at: Date.parse('2026-10-11T09:59:00Z') } };
  const s = setup({ stored: { [STORAGE_OUTBOX]: JSON.stringify(waiting) }, script: { send: async () => new Promise(() => {}) } });
  s.session.send('here');
  await settle();
  assert.equal(s.prefs.outbox(OTHER).text, 'for the other one');
  assert.equal(s.prefs.outbox(CONVO).text, 'here');
});

test('a create abandoned by starting a new conversation gives the words back', async () => {
  let releaseCreate;
  const s = setup();
  s.api.createConversation = () => new Promise((resolve) => { releaseCreate = resolve; });
  const sending = s.session.send('do not lose me');
  await settle();
  s.session.startNew();
  releaseCreate({ id: CONVO });
  assert.deepEqual(await sending, { sent: false, restoreDraft: 'do not lose me' });
  assert.equal(s.prefs.outbox(null), null);
  assert.equal(s.calls.some((c) => c.op === 'send'), false, 'nothing was sent');
});

test('no "started", but the server has the message: it is shown as sent and followed, not offered for resending', async () => {
  const s = setup({
    script: {
      views: [view({ messages: [userMessage('hello')], inProgress: true })],
      send: async (args) => { args.onEvent('error', { messageEn: 'The connection was lost.', messageFa: 'x' }); return { completed: false }; },
    },
  });
  const result = await s.session.send('hello');
  assert.deepEqual(result, { sent: true });
  assert.equal(s.timeline.items.some((i) => i.unsent), false);
  assert.equal(s.timeline.resuming, true, 'the server is still answering it');
  assert.equal(s.prefs.outbox(CONVO), null);
});

test('no "started" and the conversation cannot be read back: "may not have been sent", words back in the box', async () => {
  const s = setup({
    script: {
      views: [Object.assign(new Error('offline'), { status: 0 })],
      send: async (args) => { args.onEvent('error', { messageEn: 'Could not reach the panel.', messageFa: 'x' }); return { completed: false }; },
    },
  });
  const result = await s.session.send('hello');
  assert.deepEqual(result, { sent: false, restoreDraft: 'hello' });
  assert.equal(s.timeline.items.find((i) => i.kind === 'user').unsent, 'unsure');
  assert.match(s.timeline.notice.messageEn, /not clear whether/);
});

test('the "still answering" notice goes once the answer it waited for is in', async () => {
  const s = setup({
    script: {
      views: [view({ messages: [userMessage('first')] }), view({ messages: [userMessage('first'), assistantMessage('done')], inProgress: false })],
      send: async () => ({ completed: false, busy: true, notice: { messageEn: 'Still answering.', messageFa: 'x' } }),
    },
  });
  await s.session.open(CONVO);
  await s.session.send('second');
  assert.equal(s.timeline.notice.kind, 'busy');
  await s.timers.next();
  assert.equal(s.timeline.resuming, false);
  assert.equal(s.timeline.notice, null);
});

test('opening a conversation clears a notice left from before', async () => {
  const s = setup({ script: { views: [view(), view()] } });
  s.timeline.notice = { kind: 'busy', messageEn: 'Still answering.', messageFa: 'x' };
  await s.session.open(CONVO);
  assert.equal(s.timeline.notice, null);
});

test('a drawer closed while the remembered conversation was being read does not start re-reading until it opens', async () => {
  let release;
  const s = setup({ stored: { [STORAGE_CONVERSATION]: CONVO } });
  s.api.conversation = () => new Promise((resolve) => { release = resolve; });
  const restoring = s.session.restore();
  s.session.pause();
  release(view({ messages: [userMessage('q')], inProgress: true }));
  await restoring;
  assert.equal(s.timeline.resuming, true, 'it is still being answered');
  assert.equal(s.session.polling, false, 'but nothing re-reads while the drawer is closed');
  assert.equal(s.timers.queue.length, 0);
  s.session.wake();
  assert.equal(s.session.polling, true);
});

test('a slow read of one conversation cannot draw over the conversation opened after it', async () => {
  const s = setup();
  const pending = {};
  s.api.conversation = (id) => new Promise((resolve) => { pending[id] = resolve; });
  const first = s.session.open(CONVO);
  const second = s.session.open(OTHER);
  pending[OTHER]({ id: OTHER, title: 'b', messages: [userMessage('in B', 'b1')], pendingPlan: null, inProgress: false });
  await second;
  pending[CONVO](view({ messages: [userMessage('in A')], inProgress: true }));
  assert.deepEqual(await first, {});
  assert.equal(s.timeline.conversationId, OTHER);
  assert.deepEqual(s.timeline.items.map((i) => i.text), ['in B']);
  assert.equal(s.timeline.resuming, false);
  assert.equal(s.session.polling, false, 'the stale read did not start following A');
  assert.equal(s.timeline.loading, false);
});

test('opening a conversation that is not being answered stops the re-reading of the one before', async () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: CONVO }, script: { views: [view({ inProgress: true }), { id: OTHER, title: 'b', messages: [], pendingPlan: null, inProgress: false }] } });
  await s.session.restore();
  assert.equal(s.session.polling, true);
  await s.session.open(OTHER);
  assert.equal(s.session.polling, false);
  assert.equal(s.timeline.resuming, false);
  assert.equal(s.timers.queue.length, 0);
});

test('an abandoned create leaves a later message\'s waiting words alone', async () => {
  const s = setup({ script: { send: async () => new Promise(() => {}) } });
  const releases = [];
  s.api.createConversation = () => new Promise((resolve) => { releases.push(resolve); });
  let clock = 1000;
  s.session = createAssistantSession({ api: s.api, prefs: s.prefs, timeline: s.timeline, timers: s.timers, now: () => clock++ });
  const first = s.session.send('first words');
  await settle();
  s.session.startNew();
  s.session.send('second words');
  await settle();
  releases[0]({ id: CONVO });
  assert.deepEqual(await first, { sent: false, restoreDraft: 'first words' });
  assert.equal(s.prefs.outbox(null).text, 'second words', 'the second message still waits under "new"');
});

// ---- final review ----

test('a turn refused at the gate gives a superseded card its real status back', async () => {
  const pending = { id: '0199d0c4-6a1e-7000-8000-0000000000f1', status: 'pending', expiresAt: '2026-10-11T10:15:00Z', steps: [] };
  const s = setup({
    script: {
      views: [view({ messages: [userMessage('first')], pendingPlan: pending }), view({ messages: [userMessage('first'), userMessage('second', 'u2')], pendingPlan: pending })],
      send: async (args) => {
        args.onEvent('started', { conversationId: CONVO });
        args.onEvent('error', { messageEn: 'Today\'s assistant requests are used up.', messageFa: 'x' });
        args.onEvent('done', { conversationId: CONVO, remainingToday: 0 });
        return { completed: true };
      },
    },
  });
  await s.session.open(CONVO);
  await s.session.send('second');
  const card = s.timeline.items.find((i) => i.kind === 'plan');
  assert.equal(card.superseded, false);
  assert.equal(card.plan.status, 'pending');
  assert.ok(s.timeline.items.some((i) => i.kind === 'error'), 'the error that explains it is still shown');
  assert.equal(s.calls.filter((c) => c.op === 'get').length, 2);
});

test('a turn that got past the gate leaves the card superseded and does not re-read', async () => {
  const pending = { id: '0199d0c4-6a1e-7000-8000-0000000000f1', status: 'pending', expiresAt: '2026-10-11T10:15:00Z', steps: [] };
  const s = setup({
    script: {
      views: [view({ messages: [userMessage('first')], pendingPlan: pending })],
      send: async (args) => {
        args.onEvent('started', { conversationId: CONVO });
        args.onEvent('tool', { name: 'get_app', state: 'done' });
        args.onEvent('error', { messageEn: 'The provider failed.', messageFa: 'x' });
        args.onEvent('done', { conversationId: CONVO, remainingToday: 3 });
        return { completed: true };
      },
    },
  });
  await s.session.open(CONVO);
  await s.session.send('second');
  assert.equal(s.timeline.items.find((i) => i.kind === 'plan').superseded, true);
  assert.equal(s.calls.filter((c) => c.op === 'get').length, 1);
});

test('a plan running in another tab: the conversation is read once more, shortly after', async () => {
  const s = setup({ script: { views: [view({ messages: [userMessage('q')] }), view({ messages: [userMessage('q'), { id: 't1', role: 'tool', text: 'shop restarted', toolName: 'restart_app', createdAt: '2026-10-11T10:00:09Z' }] })] } });
  await s.session.open(CONVO);
  s.session.rereadSoon(3000);
  assert.equal(s.timers.queue.length, 1);
  assert.equal(s.timers.queue[0].ms, 3000);
  await s.timers.next();
  assert.equal(s.timeline.items.at(-1).text, 'shop restarted');
  assert.equal(s.timers.queue.length, 0, 'once, not a poll');
});

test('the delayed re-read is skipped when the person moved on, and dropped on dispose', async () => {
  const s = setup({ script: { views: [view({ messages: [userMessage('q')] })] } });
  await s.session.open(CONVO);
  s.session.rereadSoon(3000);
  s.session.startNew();
  await s.timers.next();
  assert.equal(s.calls.filter((c) => c.op === 'get').length, 1, 'no read for a conversation no longer on screen');

  const t = setup({ script: { views: [view()] } });
  await t.session.open(CONVO);
  t.session.rereadSoon(3000);
  t.session.dispose();
  assert.equal(t.timers.queue.length, 0);
});
