import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  POLL_MS,
  SLOW_AFTER_MS,
  addressFor,
  afterPoll,
  createPoller,
  createQuoteRefresher,
  initLaunch,
  isSlow,
  isTooBig,
  slugFor,
  withoutValues,
} from '../launch.js';

// ---- what one poll's answer means ----

test('a launch still moving is swapped in and polled again', () => {
  assert.equal(afterPoll({ ok: true, recognised: true, terminal: false, reload: false }), 'swap');
});

test('a finished launch is swapped in and the polling stops', () => {
  assert.equal(afterPoll({ ok: true, recognised: true, terminal: true, reload: false }), 'swap-and-stop');
});

test('arriving at a page of its own reloads, even though that state is terminal', () => {
  assert.equal(afterPoll({ ok: true, recognised: true, terminal: true, reload: true }), 'reload');
});

test('a redirect is the login page and is never swapped in', () => {
  assert.equal(afterPoll({ ok: true, redirected: true, recognised: false }), 'stop');
});

test('a launch that is gone stops the polling', () => {
  assert.equal(afterPoll({ ok: false, status: 404 }), 'stop');
});

test('a server error says nothing about the launch, so the page keeps what it has and asks again', () => {
  assert.equal(afterPoll({ ok: false, status: 502 }), 'retry');
});

test('a 200 that is not the partial is not swapped in', () => {
  assert.equal(afterPoll({ ok: true, recognised: false }), 'retry');
});

// ---- the poll ----

function pollerWith(answers) {
  const swapped = [];
  const scheduled = [];
  let reloads = 0;
  let ticks = 0;
  const poller = createPoller({
    fetchProgress: async () => {
      const next = answers.shift();
      if (next instanceof Error) throw next;
      return next;
    },
    readState: (html) => {
      if (html.includes('garbage')) return null;
      return { terminal: html.includes('terminal'), reload: html.includes('reload') };
    },
    swap: (html) => swapped.push(html),
    reload: () => { reloads++; },
    tick: () => { ticks++; },
    schedule: (fn, ms) => scheduled.push({ fn, ms }),
  });
  return { poller, swapped, scheduled, reloads: () => reloads, ticks: () => ticks };
}

test('the poll waits between requests, and stops once the launch is terminal', async () => {
  const kit = pollerWith([{ ok: true, html: 'moving' }, { ok: true, html: 'terminal' }]);

  kit.poller.start();
  assert.equal(kit.scheduled.length, 1);
  assert.equal(kit.scheduled[0].ms, POLL_MS);

  await kit.scheduled.shift().fn();
  assert.deepEqual(kit.swapped, ['moving']);
  assert.equal(kit.scheduled.length, 1, 'the next request is only scheduled once this one has answered');

  await kit.scheduled.shift().fn();
  assert.deepEqual(kit.swapped, ['moving', 'terminal']);
  assert.equal(kit.poller.isStopped(), true);
  assert.equal(kit.scheduled.length, 0, 'nothing further is scheduled after the last state');
});

test('an unchanged answer is not put back, so the page does not flicker or re-announce', async () => {
  const kit = pollerWith([{ ok: true, html: 'moving' }, { ok: true, html: 'moving' }, { ok: true, html: 'moving 2' }]);

  await kit.poller.poll();
  await kit.poller.poll();
  await kit.poller.poll();

  assert.deepEqual(kit.swapped, ['moving', 'moving 2']);
});

test('a dropped connection leaves the page alone and asks again', async () => {
  const kit = pollerWith([new Error('offline')]);

  await kit.poller.poll();

  assert.deepEqual(kit.swapped, []);
  assert.equal(kit.poller.isStopped(), false);
  assert.equal(kit.scheduled.length, 1);
});

test('arriving at the confirm screen reloads the page rather than swapping a form in', async () => {
  const kit = pollerWith([{ ok: true, html: 'reload terminal' }]);

  await kit.poller.poll();

  assert.equal(kit.reloads(), 1);
  assert.deepEqual(kit.swapped, []);
  assert.equal(kit.scheduled.length, 0);
});

test('the slow line is repainted on every answer, including one that changes nothing', async () => {
  const kit = pollerWith([{ ok: true, html: 'moving' }, { ok: true, html: 'moving' }]);

  await kit.poller.poll();
  await kit.poller.poll();

  assert.equal(kit.ticks(), 2);
});

test('reading only says it is slow after a minute', () => {
  assert.equal(isSlow(1000, 1000 + SLOW_AFTER_MS - 1), false);
  assert.equal(isSlow(1000, 1000 + SLOW_AFTER_MS), true);
});

// ---- the figure ----

function refresherWith() {
  const applied = [];
  const sends = [];
  const timers = new Map();
  let nextTimer = 1;
  const refresher = createQuoteRefresher({
    send: () => new Promise((resolve, reject) => sends.push({ resolve, reject })),
    apply: (html) => applied.push(html),
    schedule: (fn) => { const id = nextTimer++; timers.set(id, fn); return id; },
    cancel: (id) => timers.delete(id),
  });
  const fire = () => { const fns = [...timers.values()]; timers.clear(); fns.forEach((fn) => fn()); };
  return { refresher, applied, sends, timers, fire };
}

const settle = () => new Promise((resolve) => setImmediate(resolve));

test('three ticks in a row are one question', () => {
  const kit = refresherWith();

  kit.refresher.trigger();
  kit.refresher.trigger();
  kit.refresher.trigger();

  assert.equal(kit.timers.size, 1);
  kit.fire();
  assert.equal(kit.sends.length, 1);
});

test('an answer about the form as it was is ignored once a newer question has been asked', async () => {
  const kit = refresherWith();

  kit.refresher.trigger();
  kit.fire();
  kit.refresher.trigger();
  kit.fire();

  // The second question answers first, then the first question's answer turns up late.
  kit.sends[1].resolve('newer');
  await settle();
  kit.sends[0].resolve('older');
  await settle();

  assert.deepEqual(kit.applied, ['newer']);
});

test('a failed refresh leaves the figure on the screen alone', async () => {
  const kit = refresherWith();

  kit.refresher.trigger();
  kit.fire();
  kit.sends[0].reject(new Error('offline'));
  await settle();

  kit.refresher.trigger();
  kit.fire();
  kit.sends[1].resolve(null);
  await settle();

  assert.deepEqual(kit.applied, []);
});

test('asking what it costs does not send anybody\'s answers along', () => {
  const form = new FormData();
  form.append('__RequestVerificationToken', 'token');
  form.append('Services', 'PostgreSql');
  form.append('Services', 'Redis');
  form.append('Name', 'shop');
  form.append('Values[STRIPE_SECRET_KEY]', 'sk_live_real');
  form.append('Values[LOG_LEVEL]', 'info');

  const body = withoutValues(form);

  assert.deepEqual(body.getAll('Services'), ['PostgreSql', 'Redis']);
  assert.equal(body.get('__RequestVerificationToken'), 'token');
  assert.equal(body.get('Name'), 'shop');
  assert.equal(body.has('Values[STRIPE_SECRET_KEY]'), false);
  assert.equal(body.has('Values[LOG_LEVEL]'), false);
});

// ---- the address ----

test('the address follows the name the way the server derives a slug', () => {
  assert.equal(addressFor('My Cool App', '', 'apps.example.com'), 'my-cool-app.apps.example.com');
  assert.equal(slugFor('  --Shop__API!!  '), 'shop-api');
  assert.equal(slugFor(''), 'app');
  assert.equal(slugFor('!!!'), 'app');
  assert.equal(slugFor('a'.repeat(60)).length, 50);
  assert.equal(slugFor(`${'a'.repeat(49)}-tail`), 'a'.repeat(49), 'a cut does not leave a trailing dash');
  assert.equal(slugFor('فروشگاه من'), 'فروشگاه-من', 'letters of any script survive, as they do on the server');
});

test('a typed slug wins over the name', () => {
  assert.equal(addressFor('My Cool App', ' shop ', 'apps.example.com'), 'shop.apps.example.com');
});

// ---- the upload ----

test('a file over the ceiling is caught before it is sent', () => {
  assert.equal(isTooBig({ size: 513 }, 512), true);
  assert.equal(isTooBig({ size: 512 }, 512), false);
  assert.equal(isTooBig(undefined, 512), false);
});

// ---- the wiring ----

test('on a page with none of its elements it does nothing and throws nothing', () => {
  const doc = { querySelector: () => null };
  assert.doesNotThrow(() => initLaunch(doc, {}));
});
