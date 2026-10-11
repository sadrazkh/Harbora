import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAssistantApi, AssistantRequestError, buildPageContext, parseDeclaredContext, isGuid, createTurnTracker } from '../islands/assistantApi.js';

// The pane's HTTP layer against a fake fetch. What matters: the request shapes match the server's
// contract (AgentViews.cs, the X-CSRF-TOKEN header, `new` for a new conversation), and every way a
// turn can fail — the network, the status code, a stream that stops before `done` — reaches the
// pane as an error event instead of disappearing.

const CSRF = 'csrf-token-value';

function sseResponse(chunks, { status = 200, contentType = 'text/event-stream; charset=utf-8', failAfter = false } = {}) {
  const encoder = new TextEncoder();
  const queue = [...chunks];
  // Pull-based, one chunk per read, so a failure comes after the reader has consumed what arrived —
  // erroring from start() would discard the queued chunks, which is not what a dropped socket does.
  const body = new ReadableStream({
    pull(controller) {
      if (queue.length > 0) controller.enqueue(encoder.encode(queue.shift()));
      else if (failAfter) controller.error(new TypeError('network error'));
      else controller.close();
    },
  });
  return new Response(body, { status, headers: { 'content-type': contentType } });
}

function jsonResponse(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json; charset=utf-8' } });
}

function recordingFetch(respond) {
  const calls = [];
  const fetch = async (url, init) => { calls.push({ url, init }); return respond(url, init); };
  return { calls, fetch };
}

async function sendCollect(api, args) {
  const events = [];
  const outcome = await api.send({ ...args, onEvent: (name, data) => events.push([name, data]) });
  return { events, outcome };
}

const done = 'event: done\ndata: {"conversationId":"c1","remainingToday":7}\n\n';

test('a new conversation posts to conversations/new with the contract body and headers', async () => {
  const { calls, fetch } = recordingFetch(() => sseResponse(['event: started\ndata: {"conversationId":"c1"}\n\n', done]));
  const api = createAssistantApi({ fetch, csrf: CSRF });
  const page = { path: '/apps/x', appId: null, serviceId: null, projectId: null, environmentId: null };
  const { events, outcome } = await sendCollect(api, { conversationId: null, text: 'سلام', page });

  assert.equal(calls[0].url, '/assistant/conversations/new/messages');
  assert.equal(calls[0].init.method, 'POST');
  assert.equal(calls[0].init.headers['X-CSRF-TOKEN'], CSRF);
  assert.equal(calls[0].init.headers['Content-Type'], 'application/json');
  assert.equal(calls[0].init.headers.Accept, 'text/event-stream');
  // Without this, [RequireFeature] answers a locked workspace with a redirect to an HTML page.
  assert.equal(calls[0].init.headers['X-Requested-With'], 'XMLHttpRequest');
  assert.equal(calls[0].init.redirect, 'manual');
  assert.deepEqual(JSON.parse(calls[0].init.body), { text: 'سلام', page });
  assert.deepEqual(events.map(([n]) => n), ['started', 'done']);
  assert.deepEqual(outcome, { completed: true });
});

test('an existing conversation posts to its own id', async () => {
  const { calls, fetch } = recordingFetch(() => sseResponse([done]));
  await sendCollect(createAssistantApi({ fetch, csrf: CSRF }), { conversationId: '0199d0c4-6a1e-7000-8000-000000000001', text: 'hi', page: null });
  assert.equal(calls[0].url, '/assistant/conversations/0199d0c4-6a1e-7000-8000-000000000001/messages');
});

test('every contract event is passed on in order and unknown ones are skipped', async () => {
  const { fetch } = recordingFetch(() => sseResponse([
    'event: started\ndata: {"conversationId":"c1"}\n\n',
    'event: tool\ndata: {"name":"get_app","state":"running"}\n\n',
    'event: heartbeat\ndata: {}\n\n',
    'event: tool\ndata: {"name":"get_app","state":"done"}\n\n',
    'event: message\ndata: {"id":"m","role":"assistant","text":"ok"}\n\n',
    'event: plan\ndata: {"id":"p","status":"pending","steps":[]}\n\n',
    'event: executed\ndata: {"planId":"q","status":"executed","steps":[]}\n\n',
    'event: error\ndata: {"messageEn":"e","messageFa":"خ"}\n\n',
    done,
  ]));
  const { events } = await sendCollect(createAssistantApi({ fetch }), { conversationId: null, text: 'x', page: null });
  assert.deepEqual(events.map(([n]) => n), ['started', 'tool', 'tool', 'message', 'plan', 'executed', 'error', 'done']);
});

test('a stream that ends without done is reported as an error', async () => {
  const { fetch } = recordingFetch(() => sseResponse(['event: started\ndata: {"conversationId":"c1"}\n\n', 'event: tool\ndata: {"name":"get_app","state":"running"}\n\n']));
  const { events, outcome } = await sendCollect(createAssistantApi({ fetch }), { conversationId: null, text: 'x', page: null });
  assert.equal(outcome.completed, false);
  const [name, data] = events.at(-1);
  assert.equal(name, 'error');
  assert.match(data.messageEn, /before the assistant finished/);
  assert.ok(data.messageFa.length > 0);
});

test('a done frame cut off mid-JSON by the end of the stream does not count as done', async () => {
  const { fetch } = recordingFetch(() => sseResponse(['event: done\ndata: {"conversationId":']));
  const { events, outcome } = await sendCollect(createAssistantApi({ fetch }), { conversationId: null, text: 'x', page: null });
  assert.equal(outcome.completed, false);
  assert.deepEqual(events.map(([n]) => n), ['error']);
});

test('a connection that drops mid-stream is reported as an error after what did arrive', async () => {
  const { fetch } = recordingFetch(() => sseResponse(['event: started\ndata: {"conversationId":"c1"}\n\n'], { failAfter: true }));
  const { events, outcome } = await sendCollect(createAssistantApi({ fetch }), { conversationId: null, text: 'x', page: null });
  assert.equal(outcome.completed, false);
  assert.deepEqual(events.map(([n]) => n), ['started', 'error']);
  assert.match(events[1][1].messageEn, /connection/i);
});

test('a request that never reaches the server is reported as an error', async () => {
  const api = createAssistantApi({ fetch: async () => { throw new TypeError('Failed to fetch'); } });
  const { events, outcome } = await sendCollect(api, { conversationId: null, text: 'x', page: null });
  assert.equal(outcome.completed, false);
  assert.equal(events.length, 1);
  assert.equal(events[0][0], 'error');
});

test('an aborted send is not reported as a failure of the assistant', async () => {
  const controller = new AbortController();
  controller.abort();
  const api = createAssistantApi({ fetch: async () => { throw new DOMException('aborted', 'AbortError'); } });
  const { events, outcome } = await sendCollect(api, { conversationId: null, text: 'x', page: null, signal: controller.signal });
  assert.deepEqual(outcome, { completed: false, aborted: true });
  assert.deepEqual(events, []);
});

test('a fault in the pane\'s own event handler is reported as the pane\'s, not as a lost connection', async () => {
  const cancelled = [];
  const encoder = new TextEncoder();
  const frames = ['event: started\ndata: {"conversationId":"c1"}\n\n', 'event: message\ndata: {"text":"x"}\n\n', done];
  const body = new ReadableStream({
    pull(controller) { if (frames.length) controller.enqueue(encoder.encode(frames.shift())); else controller.close(); },
    cancel(reason) { cancelled.push(reason); },
  });
  const api = createAssistantApi({ fetch: async () => new Response(body, { headers: { 'content-type': 'text/event-stream' } }) });
  const events = [];
  const originalError = console.error;
  console.error = () => {};
  let outcome;
  try {
    outcome = await api.send({
      conversationId: null, text: 'x', page: null,
      onEvent: (name, data) => {
        events.push([name, data]);
        if (name === 'message') throw new TypeError('cannot read properties of undefined');
      },
    });
  } finally {
    console.error = originalError;
  }
  assert.deepEqual(outcome, { completed: false, paneFault: true });
  const [name, data] = events.at(-1);
  assert.equal(name, 'error');
  assert.match(data.messageEn, /fault in the panel/);
  assert.doesNotMatch(data.messageEn, /connection was lost/);
  assert.equal(cancelled.length, 1, 'reading stopped and the body was cancelled');
  assert.equal(events.some(([n]) => n === 'done'), false, 'nothing after the fault is delivered');
});

test('a refused approve carries the refused step and the plan\'s new status from the server', async () => {
  const { fetch } = recordingFetch(() => jsonResponse({
    error: 'plan_expired', messageEn: 'This plan expired; nothing ran.', messageFa: 'این برنامه منقضی شد؛ هیچ کاری اجرا نشد.',
    status: 'expired', stepIndex: 2,
  }, 409));
  await assert.rejects(createAssistantApi({ fetch }).approve('p', {}), (err) => {
    assert.equal(err.status, 409);
    assert.equal(err.messageEn, 'This plan expired; nothing ran.');
    assert.equal(err.planStatus, 'expired');
    assert.equal(err.stepIndex, 2);
    return true;
  });
});

test('a numeric ProblemDetails status or a made-up one is not taken for a plan status', async () => {
  for (const body of [{ title: 'Bad Request', status: 400 }, { messageEn: 'x', messageFa: 'x', status: 'gone', stepIndex: '1' }]) {
    const { fetch } = recordingFetch(() => jsonResponse(body, 400));
    await assert.rejects(createAssistantApi({ fetch }).approve('p', {}), (err) => err.planStatus === null && err.stepIndex === null);
  }
});

test('a machine code alone is not shown to a person; the status words are', async () => {
  const { fetch } = recordingFetch(() => jsonResponse({ error: 'plan_not_pending' }, 409));
  await assert.rejects(createAssistantApi({ fetch }).approve('p', {}), (err) => {
    assert.doesNotMatch(err.messageEn, /plan_not_pending/);
    assert.match(err.messageEn, /no longer possible/);
    return true;
  });
});

test('an abandoned turn is aborted and its late events are recognisably stale', () => {
  const turns = createTurnTracker();
  const first = turns.begin();
  assert.equal(turns.active, true);
  assert.equal(first.isCurrent(), true);
  const second = turns.begin();
  assert.equal(first.signal.aborted, true, 'starting a turn abandons the one before');
  assert.equal(first.isCurrent(), false);
  assert.equal(second.isCurrent(), true);
  assert.equal(turns.abort(), true);
  assert.equal(second.signal.aborted, true);
  assert.equal(second.isCurrent(), false);
  assert.equal(turns.active, false);
  assert.equal(turns.abort(), false, 'nothing left to abort');
});

test('ending a turn that was already replaced leaves the new one alone', () => {
  const turns = createTurnTracker();
  const first = turns.begin();
  const second = turns.begin();
  turns.end(first);
  assert.equal(second.isCurrent(), true);
  turns.end(second);
  assert.equal(turns.active, false);
  assert.equal(second.signal.aborted, false, 'a turn that finished is not aborted');
});

test('aborting mid-stream ends the send quietly, with no error for a turn the pane abandoned', async () => {
  const controller = new AbortController();
  const encoder = new TextEncoder();
  let sentFirst = false;
  const body = new ReadableStream({
    pull(c) {
      if (!sentFirst) { sentFirst = true; c.enqueue(encoder.encode('event: started\ndata: {"conversationId":"c1"}\n\n')); return; }
      return new Promise((resolve, reject) => controller.signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError'))));
    },
  });
  const api = createAssistantApi({ fetch: async () => new Response(body, { headers: { 'content-type': 'text/event-stream' } }) });
  const events = [];
  const sending = api.send({ conversationId: null, text: 'x', page: null, signal: controller.signal, onEvent: (n) => { events.push(n); if (n === 'started') controller.abort(); } });
  const outcome = await sending;
  assert.deepEqual(outcome, { completed: false, aborted: true });
  assert.deepEqual(events, ['started'], 'no error is reported for a turn the pane abandoned');
});

test('a known event with unreadable data becomes an error rather than vanishing', async () => {
  const { fetch } = recordingFetch(() => sseResponse(['event: plan\ndata: {broken\n\n', done]));
  const { events } = await sendCollect(createAssistantApi({ fetch }), { conversationId: null, text: 'x', page: null });
  assert.deepEqual(events.map(([n]) => n), ['error', 'done']);
});

test('a locked workspace\'s 403 is explained, not shown as a bare status', async () => {
  const { fetch } = recordingFetch(() => jsonResponse({ error: 'feature_not_enabled', feature: 'assistant', state: 'locked' }, 403));
  const { events } = await sendCollect(createAssistantApi({ fetch }), { conversationId: null, text: 'x', page: null });
  assert.equal(events[0][0], 'error');
  assert.match(events[0][1].messageEn, /not enabled/);
});

test('a redirect (the session ended) is reported as a signed-out session', async () => {
  const opaque = { type: 'opaqueredirect', status: 0, ok: false, headers: new Headers(), text: async () => '' };
  const { events } = await sendCollect(createAssistantApi({ fetch: async () => opaque }), { conversationId: null, text: 'x', page: null });
  assert.match(events[0][1].messageEn, /sign in/i);
});

test('a 200 that is not an event stream is an error, not an empty turn', async () => {
  const { fetch } = recordingFetch(() => new Response('<html>login</html>', { status: 200, headers: { 'content-type': 'text/html' } }));
  const { events, outcome } = await sendCollect(createAssistantApi({ fetch }), { conversationId: null, text: 'x', page: null });
  assert.equal(outcome.completed, false);
  assert.equal(events[0][0], 'error');
});

test('JSON reads send the JSON headers and return the parsed body', async () => {
  const status = { available: true, reasonEn: null, reasonFa: null, model: 'openrouter/free', remainingToday: 12 };
  const { calls, fetch } = recordingFetch(() => jsonResponse(status));
  const api = createAssistantApi({ fetch, csrf: CSRF });
  assert.deepEqual(await api.status(), status);
  assert.equal(calls[0].url, '/assistant/status');
  assert.equal(calls[0].init.method, 'GET');
  assert.equal(calls[0].init.headers.Accept, 'application/json');
  assert.equal(calls[0].init.headers['X-Requested-With'], 'XMLHttpRequest');
});

test('conversation reads use the contract paths', async () => {
  const { calls, fetch } = recordingFetch(() => jsonResponse([]));
  const api = createAssistantApi({ fetch });
  await api.conversations();
  await api.conversation('0199d0c4-6a1e-7000-8000-000000000001');
  assert.deepEqual(calls.map((c) => c.url), ['/assistant/conversations', '/assistant/conversations/0199d0c4-6a1e-7000-8000-000000000001']);
});

test('approve posts the approve body with the token and returns the result', async () => {
  const result = { planId: 'p', status: 'executed', steps: [] };
  const { calls, fetch } = recordingFetch(() => jsonResponse(result));
  const api = createAssistantApi({ fetch, csrf: CSRF });
  const body = { confirmations: { 2: 'old-db' }, rememberTools: ['create_database'] };
  assert.deepEqual(await api.approve('p', body), result);
  assert.equal(calls[0].url, '/assistant/plans/p/approve');
  assert.equal(calls[0].init.method, 'POST');
  assert.equal(calls[0].init.headers['X-CSRF-TOKEN'], CSRF);
  assert.deepEqual(JSON.parse(calls[0].init.body), { confirmations: { 2: 'old-db' }, rememberTools: ['create_database'] });
});

test('reject posts with the token and accepts 204 No Content', async () => {
  const { calls, fetch } = recordingFetch(() => new Response(null, { status: 204 }));
  await createAssistantApi({ fetch, csrf: CSRF }).reject('p');
  assert.equal(calls[0].url, '/assistant/plans/p/reject');
  assert.equal(calls[0].init.headers['X-CSRF-TOKEN'], CSRF);
});

test('a refused approve carries the server\'s own words in both languages', async () => {
  const { fetch } = recordingFetch(() => jsonResponse({ error: 'Type the name to confirm', errorFa: 'برای تأیید نام را بنویسید' }, 400));
  await assert.rejects(createAssistantApi({ fetch }).approve('p', {}), (err) => {
    assert.ok(err instanceof AssistantRequestError);
    assert.equal(err.status, 400);
    assert.equal(err.messageEn, 'Type the name to confirm');
    assert.equal(err.messageFa, 'برای تأیید نام را بنویسید');
    return true;
  });
});

test('a server message in messageEn/messageFa form is used as it is', async () => {
  const { fetch } = recordingFetch(() => jsonResponse({ messageEn: 'This plan has expired', messageFa: 'این برنامه منقضی شده است' }, 409));
  await assert.rejects(createAssistantApi({ fetch }).approve('p', {}), (err) => err.messageFa === 'این برنامه منقضی شده است');
});

test('a failure with no readable body still says what happened, in both languages', async () => {
  for (const status of [400, 404, 409, 500]) {
    const { fetch } = recordingFetch(() => new Response('', { status }));
    await assert.rejects(createAssistantApi({ fetch }).status(), (err) => {
      assert.equal(err.status, status);
      assert.ok(err.messageEn.length > 0 && err.messageFa.length > 0, String(status));
      return true;
    });
  }
});

test('the page context sends only well-formed ids, so a stray value cannot fail model binding', () => {
  const app = '0199d0c4-6a1e-7000-8000-000000000001';
  assert.deepEqual(buildPageContext({ appId: app, serviceId: '', projectId: 'shop', environmentId: 42 }, '/apps/' + app), {
    path: '/apps/' + app, appId: app, serviceId: null, projectId: null, environmentId: null,
  });
  assert.deepEqual(buildPageContext(null, '/'), { path: '/', appId: null, serviceId: null, projectId: null, environmentId: null });
});

test('the declared context attribute is parsed defensively', () => {
  assert.deepEqual(parseDeclaredContext('{"appId":"a"}'), { appId: 'a' });
  assert.equal(parseDeclaredContext(''), null);
  assert.equal(parseDeclaredContext('not json'), null);
  assert.equal(parseDeclaredContext('[1,2]'), null);
  assert.equal(parseDeclaredContext(undefined), null);
});

test('isGuid accepts the server\'s ids and nothing else', () => {
  assert.equal(isGuid('0199d0c4-6a1e-7000-8000-000000000001'), true);
  assert.equal(isGuid('0199D0C4-6A1E-7000-8000-000000000001'), true);
  assert.equal(isGuid('../admin'), false);
  assert.equal(isGuid(''), false);
  assert.equal(isGuid(null), false);
});
