import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAssistantApi, AssistantRequestError } from '../islands/assistantApi.js';
import { createAssistantSession } from '../islands/assistantSession.js';
import { createTimeline, loadConversation } from '../islands/assistantTimeline.js';
import { createAssistantPrefs, STORAGE_CONVERSATION, STORAGE_OUTBOX } from '../assistantDrawer.js';

// The two pieces of the conversation list that live in shared modules: the api functions that list,
// delete one and delete all, and `session.forget` — what the pane does when a conversation it knows is
// deleted. (The list's own rules are in assistant-history.test.mjs.)

const CSRF = 'csrf-token-value';
const A = '0199d0c4-6a1e-7000-8000-0000000000c1';
const B = '0199d0c4-6a1e-7000-8000-0000000000c2';

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json; charset=utf-8' } });
}

function recordingFetch(respond) {
  const calls = [];
  const fetch = async (url, init) => { calls.push({ url, init }); return respond(url, init); };
  return { calls, fetch };
}

// ---- the api ----------------------------------------------------------------------------------------

test('the list is one GET with no parameters when there is no query and no offset', async () => {
  const { calls, fetch } = recordingFetch(() => jsonResponse([]));
  const api = createAssistantApi({ fetch, csrf: CSRF });
  await api.conversations();
  await api.conversations({});
  await api.conversations({ q: '   ', skip: 0 });
  assert.deepEqual(calls.map((c) => c.url), ['/assistant/conversations', '/assistant/conversations', '/assistant/conversations']);
  assert.equal(calls[0].init.method, 'GET');
  assert.equal(calls[0].init.headers['X-CSRF-TOKEN'], undefined, 'a GET carries no token');
  assert.equal(calls[0].init.headers['X-Requested-With'], 'XMLHttpRequest');
});

test('q and skip go in the query string, encoded, trimmed, and only when they mean something', async () => {
  const { calls, fetch } = recordingFetch(() => jsonResponse([]));
  const api = createAssistantApi({ fetch, csrf: CSRF });
  await api.conversations({ q: ' shop db ', skip: 30 });
  await api.conversations({ q: 'دیتابیس', skip: 60 });
  await api.conversations({ q: 'a&b=c#d' });
  await api.conversations({ skip: 30.9 });
  await api.conversations({ skip: -5 });
  assert.deepEqual(calls.map((c) => c.url), [
    '/assistant/conversations?q=shop+db&skip=30',
    `/assistant/conversations?q=${encodeURIComponent('دیتابیس')}&skip=60`,
    '/assistant/conversations?q=a%26b%3Dc%23d',
    '/assistant/conversations?skip=30',
    '/assistant/conversations',
  ]);
  // The server reads the query string with ASP.NET's binder: "+" is a space there, and the Persian
  // text is percent-encoded UTF-8.
  assert.equal(new URL(calls[1].url, 'http://x').searchParams.get('q'), 'دیتابیس');
});

test('deleting one conversation is a DELETE with the token to its own id, and a 204 is null', async () => {
  const { calls, fetch } = recordingFetch(() => new Response(null, { status: 204 }));
  const api = createAssistantApi({ fetch, csrf: CSRF });
  const result = await api.deleteConversation(A);
  assert.equal(result, null);
  assert.equal(calls[0].url, `/assistant/conversations/${A}`);
  assert.equal(calls[0].init.method, 'DELETE');
  assert.equal(calls[0].init.headers['X-CSRF-TOKEN'], CSRF);
  assert.equal(calls[0].init.headers['X-Requested-With'], 'XMLHttpRequest');
  assert.equal(calls[0].init.credentials, 'same-origin');
  assert.equal(calls[0].init.body, undefined);
});

test('an id is encoded, so it cannot reach another route', async () => {
  const { calls, fetch } = recordingFetch(() => new Response(null, { status: 204 }));
  await createAssistantApi({ fetch, csrf: CSRF }).deleteConversation('../x?y');
  assert.equal(calls[0].url, '/assistant/conversations/..%2Fx%3Fy');
});

test('deleting all is a DELETE with the token to the collection and answers { deleted, skipped }', async () => {
  const { calls, fetch } = recordingFetch(() => jsonResponse({ deleted: 4, skipped: 1 }));
  const api = createAssistantApi({ fetch, csrf: CSRF });
  assert.deepEqual(await api.deleteAllConversations(), { deleted: 4, skipped: 1 });
  assert.equal(calls[0].url, '/assistant/conversations');
  assert.equal(calls[0].init.method, 'DELETE');
  assert.equal(calls[0].init.headers['X-CSRF-TOKEN'], CSRF);
});

test('a refused delete arrives as the usual error with the server’s words and code', async () => {
  const busy = { error: 'busy', messageEn: 'It is still answering or running something.', messageFa: 'هنوز در حال پاسخ یا اجراست.' };
  const { fetch } = recordingFetch((url) => (url.endsWith(A) ? jsonResponse(busy, 409) : jsonResponse({ error: 'not_found', messageEn: 'There is no such conversation.', messageFa: 'چنین گفت‌وگویی وجود ندارد.' }, 404)));
  const api = createAssistantApi({ fetch, csrf: CSRF });

  await assert.rejects(api.deleteConversation(A), (error) => {
    assert.ok(error instanceof AssistantRequestError);
    assert.equal(error.status, 409);
    assert.equal(error.code, 'busy');
    assert.equal(error.messageFa, 'هنوز در حال پاسخ یا اجراست.');
    assert.equal(error.messageEn, 'It is still answering or running something.');
    return true;
  });
  await assert.rejects(api.deleteConversation(B), (error) => error.status === 404 && error.code === 'not_found' && error.messageFa === 'چنین گفت‌وگویی وجود ندارد.');
});

test('a delete that never reached the server is an error with status 0, not a success', async () => {
  const api = createAssistantApi({ fetch: async () => { throw new TypeError('offline'); }, csrf: CSRF });
  await assert.rejects(api.deleteConversation(A), (error) => error.status === 0 && /Could not reach the panel/.test(error.messageEn));
  await assert.rejects(api.deleteAllConversations(), (error) => error.status === 0);
});

// ---- session.forget -----------------------------------------------------------------------------------

function fakeStorage(initial = {}) {
  const data = { ...initial };
  return {
    data,
    getItem: (k) => (k in data ? data[k] : null),
    setItem: (k, v) => { data[k] = String(v); },
    removeItem: (k) => { delete data[k]; },
  };
}

const message = (id, role, text) => ({ id, role, text, toolName: null, createdAt: '2026-10-15T10:00:00Z' });

function setup({ stored = {}, onScreen = null } = {}) {
  const storage = fakeStorage(stored);
  const prefs = createAssistantPrefs(storage);
  const timeline = createTimeline();
  const api = { conversation: async (id) => ({ id, title: 'shop', messages: [message('u1', 'user', 'hi')], pendingPlan: null, inProgress: false }) };
  const session = createAssistantSession({ api, prefs, timeline, now: () => Date.parse('2026-10-15T10:00:00Z') });
  if (onScreen) loadConversation(timeline, { id: onScreen, title: 'shop', messages: [message('u1', 'user', 'hi')], pendingPlan: null, inProgress: false });
  return { storage, prefs, timeline, session };
}

const outbox = (conversationId, text) => ({ conversationId, text, at: Date.parse('2026-10-15T10:00:00Z') });

test('forgetting the open conversation starts a new one and clears its remembered id and waiting words', () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: A }, onScreen: A });
  s.prefs.setOutbox(outbox(A, 'words that never left'));
  assert.equal(s.timeline.conversationId, A);

  assert.equal(s.session.forget(A), true);

  assert.equal(s.timeline.conversationId, null, 'a new, empty conversation');
  assert.deepEqual(s.timeline.items, []);
  assert.equal(s.storage.data[STORAGE_CONVERSATION], undefined, 'a reload will not try to open it');
  assert.equal(s.prefs.outbox(A), null, 'and will not offer its unsent words');
  assert.equal(s.storage.data[STORAGE_OUTBOX], undefined);
});

test('forgetting another conversation leaves the open one, its remembered id and everyone’s waiting words alone', () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: A }, onScreen: A });
  s.prefs.setOutbox(outbox(A, 'for the open one'));
  s.prefs.setOutbox(outbox(B, 'for the other one'));
  s.prefs.setOutbox({ conversationId: null, text: 'for a new one', at: Date.parse('2026-10-15T10:00:00Z') });

  assert.equal(s.session.forget(B), false);

  assert.equal(s.timeline.conversationId, A);
  assert.equal(s.timeline.items.length, 1);
  assert.equal(s.storage.data[STORAGE_CONVERSATION], A);
  assert.equal(s.prefs.outbox(A).text, 'for the open one');
  assert.equal(s.prefs.outbox(B), null, 'the deleted conversation’s own words are dropped');
  assert.equal(s.prefs.outbox(null).text, 'for a new one');
});

test('a remembered conversation that is not on screen is forgotten too', () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: B } });
  s.prefs.setOutbox(outbox(B, 'unsent'));
  assert.equal(s.session.forget(B), false, 'nothing on screen was replaced');
  assert.equal(s.storage.data[STORAGE_CONVERSATION], undefined);
  assert.equal(s.prefs.outbox(B), null);
});

test('a conversation whose read failed, and that is then deleted, is let go of', () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: A } });
  s.timeline.loadError = { conversationId: A, messageEn: 'x', messageFa: 'y' };
  assert.equal(s.session.forget(A), true);
  assert.equal(s.timeline.loadError, null);
  assert.equal(s.storage.data[STORAGE_CONVERSATION], undefined);
});

test('after forgetting the open conversation a reload finds nothing to open', async () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: A }, onScreen: A });
  s.prefs.setOutbox(outbox(A, 'unsent'));
  s.session.forget(A);

  const reloaded = setup({ stored: { ...s.storage.data } });
  const restored = await reloaded.session.restore();
  assert.equal(restored.restoreDraft ?? null, null, 'no remembered conversation, no words coming back');
  assert.equal(reloaded.timeline.conversationId, null);
  assert.equal(reloaded.timeline.notice, null);
});

test('forgetting nothing does nothing', () => {
  const s = setup({ stored: { [STORAGE_CONVERSATION]: A }, onScreen: A });
  assert.equal(s.session.forget(null), false);
  assert.equal(s.session.forget(''), false);
  assert.equal(s.timeline.conversationId, A);
  assert.equal(s.storage.data[STORAGE_CONVERSATION], A);
});
