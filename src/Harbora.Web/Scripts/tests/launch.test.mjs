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
  isAnswered,
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

test('a session that ended is a stop too', () => {
  assert.equal(afterPoll({ ok: false, status: 401 }), 'stop');
  assert.equal(afterPoll({ ok: false, status: 403 }), 'stop');
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
  let lost = 0;
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
    lostSight: () => { lost++; },
    schedule: (fn, ms) => scheduled.push({ fn, ms }),
  });
  return { poller, swapped, scheduled, reloads: () => reloads, ticks: () => ticks, lost: () => lost };
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

test('a poll that stops with no answer says so once, and asks no more', async () => {
  for (const answer of [{ ok: true, redirected: true, html: '<login>' }, { ok: false, status: 404 }, { ok: false, status: 401 }]) {
    const kit = pollerWith([answer]);

    await kit.poller.poll();

    assert.equal(kit.lost(), 1);
    assert.equal(kit.poller.isStopped(), true);
    assert.equal(kit.scheduled.length, 0);
    assert.deepEqual(kit.swapped, []);
  }
});

test('a launch that finished, or a connection that dropped, has not lost sight of anything', async () => {
  const finished = pollerWith([{ ok: true, html: 'terminal' }]);
  await finished.poller.poll();
  assert.equal(finished.lost(), 0);

  const dropped = pollerWith([new Error('offline')]);
  await dropped.poller.poll();
  assert.equal(dropped.lost(), 0);

  const reloaded = pollerWith([{ ok: true, html: 'reload terminal' }]);
  await reloaded.poller.poll();
  assert.equal(reloaded.lost(), 0);
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
});

test('a slug is ascii, because an image tag and a hostname take nothing else', () => {
  assert.equal(slugFor('فروشگاه من'), 'app', 'a wholly Persian name comes to the fallback');
  assert.equal(addressFor('فروشگاه من', '', 'apps.example.com'), 'app.apps.example.com');
  assert.equal(slugFor('فروشگاه shop ۲'), 'shop');
  assert.equal(slugFor('Café 2'), 'caf-2');
});

test('a typed slug wins over the name, and is cleaned up the way the server will', () => {
  assert.equal(addressFor('My Cool App', ' shop ', 'apps.example.com'), 'shop.apps.example.com');
  assert.equal(addressFor('anything', 'My Shop', 'apps.example.com'), 'my-shop.apps.example.com');
});

test('the name as proposed previews what the server worked out, and any other name does not', () => {
  const suggested = { name: 'shop', slug: 'shop-2' };

  assert.equal(addressFor('shop', '', 'apps.example.com', suggested), 'shop-2.apps.example.com');
  assert.equal(addressFor(' shop ', '', 'apps.example.com', suggested), 'shop-2.apps.example.com');
  assert.equal(addressFor('', '', 'apps.example.com', suggested), 'shop-2.apps.example.com', 'a blanked name goes back to the proposed one');
  assert.equal(addressFor('other', '', 'apps.example.com', suggested), 'other.apps.example.com');
  assert.equal(addressFor('shop', 'mine', 'apps.example.com', suggested), 'mine.apps.example.com');
  assert.equal(addressFor('shop', '', 'apps.example.com', { name: 'shop' }), 'shop.apps.example.com', 'with no suggestion it is only the name');
});

// ---- connections ----

test('a name is answered when any service that could set it is ticked', () => {
  const ticked = new Set(['MySql']);

  assert.equal(isAnswered('PostgreSql MySql MariaDb', ticked), true);
  assert.equal(isAnswered('PostgreSql', ticked), false);
  assert.equal(isAnswered('', ticked), false, 'a name no service is known to set is never answered');
  assert.equal(isAnswered(undefined, ticked), false);
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

/** As much of an element as the wiring touches. Selectors answer from what the test hands in. */
function element({ dataset = {}, classes = [], selected = {}, all = {}, ...rest } = {}) {
  const held = new Set(classes);
  const listeners = {};
  return {
    dataset,
    classList: {
      add: (name) => held.add(name),
      remove: (name) => held.delete(name),
      contains: (name) => held.has(name),
      toggle: (name, on) => (on ? held.add(name) : held.delete(name)),
    },
    addEventListener: (type, fn) => { listeners[type] = fn; },
    fire: (type, event) => listeners[type]?.(event),
    querySelector: (selector) => selected[selector] ?? null,
    querySelectorAll: (selector) => all[selector] ?? [],
    ...rest,
  };
}

const quietWindow = { setTimeout: () => 0, clearTimeout: () => {}, Date };

function confirmPage() {
  const input = (name) => ({ name, disabled: false });
  const box = (value, checked) => element({ name: 'Services', value, checked });
  const postgres = box('PostgreSql', true);
  const redis = box('Redis', false);

  const urlInput = input('Values[DATABASE_URL]');
  const redisInput = input('Values[REDIS_URL]');
  const urlField = element({ dataset: { launchConnection: 'PostgreSql MySql MariaDb' }, selected: { input: urlInput } });
  const redisField = element({ dataset: { launchConnection: 'Redis' }, selected: { input: redisInput } });
  const urlName = element({ dataset: { launchAnsweredName: 'PostgreSql MySql MariaDb' } });
  const redisName = element({ dataset: { launchAnsweredName: 'Redis' }, classes: ['hidden'] });
  const line = element();
  const own = element();
  const details = element({ open: false });
  const address = element({ textContent: '' });
  const addressLine = element({ classes: ['hidden'] });
  const reserved = element();
  const name = element({ value: 'status-shop' });
  const slug = element({ value: '' });

  const form = element({
    dataset: { rootDomain: 'apps.example.com', suggestedName: 'shop', suggestedSlug: 'shop-2' },
    selected: {
      '[data-launch-details]': details,
      '[data-launch-address]': address,
      '[data-launch-address-line]': addressLine,
      '[data-launch-reserved]': reserved,
      '[data-launch-name]': name,
      '[data-launch-slug]': slug,
      '[data-launch-aliases]': line,
      '[data-launch-own-connection]': own,
    },
    all: {
      '[data-launch-connection]': [urlField, redisField],
      '[data-launch-answered-name]': [urlName, redisName],
    },
  });
  form.querySelectorAll = (selector) => selector === 'input[name="Services"]:checked'
    ? [postgres, redis].filter((b) => b.checked)
    : { '[data-launch-connection]': [urlField, redisField], '[data-launch-answered-name]': [urlName, redisName] }[selector] ?? [];

  const doc = { querySelector: (selector) => (selector === '[data-launch-confirm]' ? form : null) };
  initLaunch(doc, quietWindow);

  const untick = (target) => {
    target.checked = false;
    form.fire('change', { target });
  };
  const tick = (target) => {
    target.checked = true;
    form.fire('change', { target });
  };

  return {
    postgres, redis, urlField, redisField, urlInput, redisInput, urlName, redisName, line, own, details,
    address, addressLine, reserved, name, slug, untick, tick,
  };
}

test('what a ticked service answers is promised on the line and not asked', () => {
  const page = confirmPage();

  assert.equal(page.urlField.classList.contains('hidden'), true);
  assert.equal(page.urlInput.disabled, true, 'a hidden value would outrank the database that is ticked');
  assert.equal(page.urlName.classList.contains('hidden'), false);

  assert.equal(page.redisField.classList.contains('hidden'), false, 'Redis is not ticked, so nothing sets this');
  assert.equal(page.redisInput.disabled, false);
  assert.equal(page.redisName.classList.contains('hidden'), true);
  assert.equal(page.details.open, false, 'a guess left unticked is no reason to open anything');
});

test('unticking a database for one of your own moves its names to the questions and opens the fold', () => {
  const page = confirmPage();

  page.untick(page.postgres);

  assert.equal(page.urlField.classList.contains('hidden'), false);
  assert.equal(page.urlInput.disabled, false);
  assert.equal(page.urlName.classList.contains('hidden'), true);
  assert.equal(page.line.classList.contains('hidden'), true, 'nothing left to promise');
  assert.equal(page.details.open, true);
});

test('ticking a service takes its question away again, and the heading with the last one', () => {
  const page = confirmPage();

  page.tick(page.redis);

  assert.equal(page.redisField.classList.contains('hidden'), true);
  assert.equal(page.redisInput.disabled, true);
  assert.equal(page.redisName.classList.contains('hidden'), false);
  assert.equal(page.own.classList.contains('hidden'), true);
  assert.equal(page.details.open, false, 'ticking something opens nothing');
});

test('typing a new name takes back what was said about the last one', () => {
  const page = confirmPage();

  page.name.value = 'my shop';
  page.name.fire('input');

  assert.equal(page.reserved.classList.contains('hidden'), true);
  assert.equal(page.addressLine.classList.contains('hidden'), false);
  assert.equal(page.address.textContent, 'my-shop.apps.example.com');
});

test('going back to the proposed name previews what the server worked out for it', () => {
  const page = confirmPage();

  page.name.value = 'shop';
  page.name.fire('input');

  assert.equal(page.address.textContent, 'shop-2.apps.example.com');
});

test('a poll that loses sight of the launch stops claiming anything and offers a refresh', async () => {
  const spinner = element({ classes: ['animate-spin'] });
  const keepsGoing = element();
  const state = element({ dataset: { terminal: 'false', reload: 'false' } });
  const region = element({
    dataset: { launchPoll: '/launch/x/progress' },
    selected: { '[data-launch-state]': state },
    all: { '.animate-spin': [spinner], '[data-launch-keeps-going]': [keepsGoing] },
  });
  const stopped = element({ classes: ['hidden'] });
  const doc = {
    querySelector: (selector) => ({ '[data-launch-poll]': region, '[data-launch-poll-stopped]': stopped }[selector] ?? null),
  };

  let next;
  const win = {
    Date,
    fetch: async () => ({ ok: false, status: 404, redirected: false }),
    setTimeout: (fn) => { next = fn; return 0; },
  };

  initLaunch(doc, win);
  await next();

  assert.equal(stopped.classList.contains('hidden'), false);
  assert.equal(spinner.classList.contains('animate-spin'), false);
  assert.equal(keepsGoing.classList.contains('hidden'), true);
});
