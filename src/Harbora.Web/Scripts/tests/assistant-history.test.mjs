import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  PAGE_SIZE, SEARCH_DEBOUNCE_MS, createConversationList, groupConversations, groupKey, groupLabel, mergePage, timeLabel,
} from '../islands/assistantHistory.js';

// The conversation list's rules against a scripted server and clock: which group a row is in (by the
// browser's local calendar), what the search box asks and which answers it believes, what "show more"
// appends, and the deleting — where the only thing that matters is that a row never leaves the list,
// and the pane never forgets a conversation, before the server said it is gone.

// Every date here is built from local parts, never from a "Z" string, so the expectations hold in any
// time zone the test runs in: the grouping is by the *local* calendar date.
const local = (y, m, d, h = 12, min = 0) => new Date(y, m - 1, d, h, min);
const row = (id, title, updatedAt) => ({ id, title, updatedAt: (updatedAt instanceof Date ? updatedAt : local(2026, 10, 15)).toISOString() });
const ids = (rows) => rows.map((r) => r.id);

// ---- groups ---------------------------------------------------------------------------------------

test('a row belongs to the group of its local calendar date, across midnight', () => {
  const now = local(2026, 10, 15, 0, 20); // twenty past midnight
  assert.equal(groupKey(local(2026, 10, 15, 0, 5), now), 'today', 'fifteen minutes ago, same date');
  assert.equal(groupKey(local(2026, 10, 14, 23, 50), now), 'yesterday', 'thirty minutes ago, but yesterday’s date');
  assert.equal(groupKey(local(2026, 10, 14, 0, 0), now), 'yesterday');
  assert.equal(groupKey(local(2026, 10, 13, 23, 59), now), 'week');
});

test('the previous 7 days are the 2nd to the 7th day back, and the 8th is older', () => {
  const now = local(2026, 10, 15, 9, 0);
  assert.equal(groupKey(local(2026, 10, 13, 23, 59), now), 'week', '2 days ago');
  assert.equal(groupKey(local(2026, 10, 8, 0, 0), now), 'week', '7 days ago, the first minute of it');
  assert.equal(groupKey(local(2026, 10, 7, 23, 59), now), 'older', '8 days ago, the last minute of it');
});

test('a month or year boundary does not change what a day is', () => {
  const now = local(2027, 1, 1, 0, 10);
  assert.equal(groupKey(local(2026, 12, 31, 23, 0), now), 'yesterday');
  assert.equal(groupKey(local(2026, 12, 25, 12, 0), now), 'week', '7 days ago');
  assert.equal(groupKey(local(2026, 12, 24, 12, 0), now), 'older', '8 days ago');
});

test('a time ahead of the clock is today, and one that cannot be read is older', () => {
  const now = local(2026, 10, 15, 9, 0);
  assert.equal(groupKey(local(2026, 10, 15, 9, 3), now), 'today');
  assert.equal(groupKey(local(2026, 10, 16, 9, 0), now), 'today');
  assert.equal(groupKey('not a date', now), 'older');
});

test('groups come newest first, empty ones are left out, and the server’s order holds inside each', () => {
  const now = local(2026, 10, 15, 9, 0);
  const rows = [
    row('a', 'a', local(2026, 10, 1)), // older
    row('b', 'b', local(2026, 10, 15, 8)), // today
    row('c', 'c', local(2026, 10, 15, 7)), // today
    row('d', 'd', local(2026, 10, 10)), // week
    row('e', 'e', local(2026, 10, 15, 9)), // today, though sent after c
  ];
  const groups = groupConversations(rows, now);
  assert.deepEqual(groups.map((g) => g.key), ['today', 'week', 'older'], 'no yesterday group');
  assert.deepEqual(ids(groups[0].rows), ['b', 'c', 'e'], 'in the order sent, not sorted again');
  assert.deepEqual(groupConversations([], now), []);
  assert.deepEqual(groupConversations(undefined, now), []);
});

test('the captions are the owner’s words in both languages', () => {
  assert.deepEqual(['today', 'yesterday', 'week', 'older'].map((k) => groupLabel(k, true)), ['امروز', 'دیروز', '۷ روز گذشته', 'قدیمی‌تر']);
  assert.deepEqual(['today', 'yesterday', 'week', 'older'].map((k) => groupLabel(k, false)), ['Today', 'Yesterday', 'Previous 7 days', 'Older']);
});

test('today and yesterday show the time of day, older rows the date', () => {
  const now = local(2026, 10, 15, 18, 0);
  const today = timeLabel(local(2026, 10, 15, 14, 5), now, false);
  const yesterday = timeLabel(local(2026, 10, 14, 9, 30), now, false);
  const week = timeLabel(local(2026, 10, 10, 9, 30), now, false);
  const older = timeLabel(local(2026, 3, 2, 9, 30), now, false);
  assert.match(today, /^14:05$/);
  assert.match(yesterday, /^09:30$/);
  assert.match(week, /10 Oct 2026/);
  assert.match(older, /2 Mar 2026/);
  assert.doesNotMatch(week, /:/, 'a date row carries no time');
  assert.equal(timeLabel('nonsense', now, false), '');
});

test('in Persian the digits are Persian and the date is in the Persian calendar', () => {
  const now = local(2026, 10, 15, 18, 0);
  const time = timeLabel(local(2026, 10, 15, 14, 5), now, true);
  const date = timeLabel(local(2026, 10, 10, 9, 30), now, true);
  assert.match(time, /^[۰-۹]{2}:[۰-۹]{2}$/);
  assert.match(date, /[۰-۹]/);
  assert.doesNotMatch(date, /2026|2025/, '2026 is 1405 in the Persian calendar');
  assert.match(date, /۱۴۰۵/);
});

test('mergePage drops ids already shown, ids deleted here, and repeats inside the page', () => {
  const rows = [row('a', 'a'), row('b', 'b')];
  const merged = mergePage(rows, [row('b', 'b'), row('c', 'c'), row('c', 'c'), row('d', 'd')], new Set(['d']));
  assert.deepEqual(ids(merged), ['a', 'b', 'c']);
  assert.deepEqual(ids(rows), ['a', 'b'], 'the rows given are not changed');
});

// ---- a scripted server and clock ----------------------------------------------------------------------

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

function fakeTimers() {
  const queue = [];
  let id = 0;
  return {
    queue,
    setTimeout(fn, ms) { const handle = ++id; queue.push({ handle, fn, ms }); return handle; },
    clearTimeout(handle) { const at = queue.findIndex((t) => t.handle === handle); if (at >= 0) queue.splice(at, 1); },
    /** Runs the next timer and lets the promise chain behind it settle. */
    async fire() { const t = queue.shift(); assert.ok(t, 'a timer was expected'); t.fn(); await settle(); return t; },
  };
}

/** A promise settled from outside, for answering requests in the order a test wants. */
function deferred() {
  let resolve; let reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

const refusal = (status, code, messageEn, messageFa) => Object.assign(new Error(messageEn), { status, code, messageEn, messageFa });

/**
 * `script.conversations(args)` / `deleteConversation(id)` / `deleteAllConversations()` /
 * `conversation(id)` answer each call; every call is recorded.
 */
function setup(script = {}, options = {}) {
  const calls = [];
  const deleted = [];
  const timers = fakeTimers();
  const api = {
    conversations: (args) => { calls.push({ op: 'list', ...args }); return (script.conversations ?? (() => []))(args); },
    deleteConversation: (id) => { calls.push({ op: 'delete', id }); return (script.deleteConversation ?? (() => null))(id); },
    deleteAllConversations: () => { calls.push({ op: 'delete-all' }); return (script.deleteAllConversations ?? (() => ({ deleted: 0, skipped: 0 })))(); },
    conversation: (id) => { calls.push({ op: 'get', id }); return (script.conversation ?? (() => ({ id })))(id); },
  };
  const list = createConversationList({ api, timers, onDeleted: (id) => deleted.push(id), ...options });
  return { list, state: list.state, calls, deleted, timers };
}

const page = (from, count) => Array.from({ length: count }, (_, i) => row(`id-${from + i}`, `title ${from + i}`));

// ---- search -------------------------------------------------------------------------------------------

test('the first page is asked for with no query and no offset', async () => {
  const s = setup({ conversations: () => page(0, 3) });
  await s.list.load();
  assert.deepEqual(s.calls, [{ op: 'list', q: '', skip: 0 }]);
  assert.deepEqual(ids(s.state.rows), ['id-0', 'id-1', 'id-2']);
  assert.equal(s.state.hasMore, false, 'a short page is the last');
  assert.equal(s.state.loading, false);
  assert.equal(s.state.loaded, true);
});

test('typing waits for a pause, then asks once with the trimmed text', async () => {
  const s = setup({ conversations: () => [] });
  await s.list.load();
  s.calls.length = 0;

  s.list.setQuery('s');
  s.list.setQuery('sh');
  s.list.setQuery(' shop ');
  assert.equal(s.calls.length, 0, 'nothing asked while the person is still typing');
  assert.equal(s.timers.queue.length, 1, 'one timer, restarted by each keystroke');
  assert.equal(s.timers.queue[0].ms, SEARCH_DEBOUNCE_MS);
  assert.equal(s.state.loading, true, 'the wait counts as loading');

  await s.timers.fire();
  assert.deepEqual(s.calls, [{ op: 'list', q: 'shop', skip: 0 }]);
  assert.equal(s.state.query, ' shop ', 'the box keeps what was typed');
  assert.equal(s.state.shown, 'shop');
});

test('a space after the words changes nothing', async () => {
  const s = setup({ conversations: () => [] });
  await s.list.load();
  s.list.setQuery('shop');
  await s.timers.fire();
  s.calls.length = 0;
  s.list.setQuery('shop ');
  assert.equal(s.timers.queue.length, 0);
  assert.equal(s.calls.length, 0);
});

test('an answer that arrives after a newer query was asked is ignored', async () => {
  const first = deferred();
  const second = deferred();
  const answers = [first, second];
  const s = setup({ conversations: () => answers.shift().promise });

  s.list.setQuery('sh');
  await s.timers.fire(); // asks for "sh"
  s.list.setQuery('shop');
  await s.timers.fire(); // asks for "shop"
  assert.deepEqual(s.calls.map((c) => c.q), ['sh', 'shop']);

  second.resolve([row('shop-1', 'shop one')]);
  await settle();
  assert.deepEqual(ids(s.state.rows), ['shop-1']);
  assert.equal(s.state.shown, 'shop');

  first.resolve([row('sh-1', 'sh one'), row('sh-2', 'sh two')]);
  await settle();
  assert.deepEqual(ids(s.state.rows), ['shop-1'], 'the older answer did not overwrite the newer one');
  assert.equal(s.state.shown, 'shop');
  assert.equal(s.state.loading, false);
});

test('an answer that arrives after a newer query was typed, before it was asked, is ignored too', async () => {
  const first = deferred();
  const second = deferred();
  const answers = [first, second];
  const s = setup({ conversations: () => answers.shift().promise });

  s.list.setQuery('sh');
  await s.timers.fire(); // "sh" is out
  s.list.setQuery('shop'); // typed, not asked yet
  first.resolve([row('sh-1', 'sh one')]);
  await settle();
  assert.deepEqual(s.state.rows, [], 'the box says "shop"; rows for "sh" are not shown');
  assert.equal(s.state.loading, true, 'still waiting for the query that is wanted');

  await s.timers.fire();
  second.resolve([row('shop-1', 'shop one')]);
  await settle();
  assert.deepEqual(ids(s.state.rows), ['shop-1']);
  assert.equal(s.state.loading, false);
});

test('a failure of an older query is not shown over the newer one', async () => {
  const first = deferred();
  const second = deferred();
  const answers = [first, second];
  const s = setup({ conversations: () => answers.shift().promise });
  s.list.setQuery('a');
  await s.timers.fire();
  s.list.setQuery('ab');
  await s.timers.fire();
  first.reject(refusal(500, null, 'The panel failed to answer', 'پنل نتوانست پاسخ دهد'));
  await settle();
  assert.equal(s.state.error, null);
  second.resolve([row('x', 'x')]);
  await settle();
  assert.deepEqual(ids(s.state.rows), ['x']);
  assert.equal(s.state.error, null);
});

test('the empty state is worded by the query the rows answer, not by what is being typed', async () => {
  const s = setup({ conversations: ({ q }) => (q ? [] : page(0, 2)) });
  await s.list.load();
  assert.equal(s.state.shown, '');

  s.list.setQuery('zzz');
  assert.equal(s.state.shown, '', 'still the unfiltered list until the server answers');
  await s.timers.fire();
  assert.deepEqual(s.state.rows, []);
  assert.equal(s.state.shown, 'zzz', 'now an empty list means "nothing with that title"');

  s.list.setQuery('');
  await s.timers.fire();
  assert.equal(s.state.shown, '');
  assert.equal(s.state.rows.length, 2);
});

test('a failed load says so in the server’s words and can be tried again', async () => {
  let fail = true;
  const s = setup({ conversations: () => { if (fail) throw refusal(502, null, 'The panel failed to answer (HTTP 502).', 'پنل نتوانست پاسخ دهد (HTTP 502).'); return page(0, 1); } });
  await s.list.load();
  assert.deepEqual(s.state.error, { messageEn: 'The panel failed to answer (HTTP 502).', messageFa: 'پنل نتوانست پاسخ دهد (HTTP 502).' });
  assert.equal(s.state.loading, false);
  fail = false;
  await s.list.load();
  assert.equal(s.state.error, null);
  assert.equal(s.state.rows.length, 1);
});

// ---- show more ----------------------------------------------------------------------------------------

test('a full page offers more, and the next page is asked for after the rows shown, for the same query', async () => {
  const s = setup({ conversations: ({ skip }) => (skip === 0 ? page(0, PAGE_SIZE) : page(PAGE_SIZE, 4)) });
  s.list.setQuery('title');
  await s.timers.fire();
  assert.equal(s.state.hasMore, true);
  s.calls.length = 0;

  await s.list.loadMore();
  assert.deepEqual(s.calls, [{ op: 'list', q: 'title', skip: PAGE_SIZE }]);
  assert.equal(s.state.rows.length, PAGE_SIZE + 4);
  assert.equal(s.state.rows.at(-1).id, `id-${PAGE_SIZE + 3}`, 'appended after, in order');
  assert.equal(s.state.hasMore, false, 'the short page was the last');
  assert.equal(s.state.loadingMore, false);

  s.calls.length = 0;
  await s.list.loadMore();
  assert.equal(s.calls.length, 0, 'nothing asked when there is no more');
});

test('a conversation that moved between pages is not shown twice, and hasMore follows the page that came', async () => {
  const s = setup({
    conversations: ({ skip }) => (skip === 0 ? page(0, PAGE_SIZE) : [...page(PAGE_SIZE - 1, 1), ...page(PAGE_SIZE, PAGE_SIZE - 1)]),
  });
  await s.list.load();
  await s.list.loadMore();
  const shown = ids(s.state.rows);
  assert.equal(new Set(shown).size, shown.length, 'no id twice');
  assert.equal(shown.length, PAGE_SIZE * 2 - 1, 'the repeated one dropped, the rest appended');
  assert.equal(s.state.hasMore, true, 'the page that came was full, whatever was dropped from it');
});

test('a failed "show more" keeps the rows and can be asked again', async () => {
  let fail = true;
  const s = setup({ conversations: ({ skip }) => { if (skip > 0 && fail) throw refusal(0, null, 'Could not reach the panel.', 'ارتباط با پنل برقرار نشد.'); return skip === 0 ? page(0, PAGE_SIZE) : page(PAGE_SIZE, 2); } });
  await s.list.load();
  await s.list.loadMore();
  assert.equal(s.state.rows.length, PAGE_SIZE);
  assert.equal(s.state.moreError.messageEn, 'Could not reach the panel.');
  assert.equal(s.state.hasMore, true);
  fail = false;
  await s.list.loadMore();
  assert.equal(s.state.rows.length, PAGE_SIZE + 2);
  assert.equal(s.state.moreError, null);
});

test('a "show more" that is out when the query changes is dropped', async () => {
  const more = deferred();
  const s = setup({ conversations: ({ q, skip }) => (skip > 0 ? more.promise : (q ? [row('hit', 'hit')] : page(0, PAGE_SIZE))) });
  await s.list.load();
  const asking = s.list.loadMore();
  assert.equal(s.state.loadingMore, true);
  s.list.setQuery('hit');
  assert.equal(s.state.loadingMore, false);
  await s.timers.fire();
  more.resolve(page(PAGE_SIZE, 3));
  await asking;
  assert.deepEqual(ids(s.state.rows), ['hit'], 'rows of the old query did not join the new list');
  assert.equal(s.state.hasMore, false);
});

test('"show more" is not offered while the first page of a new query is on its way', async () => {
  const s = setup({ conversations: () => page(0, PAGE_SIZE) });
  await s.list.load();
  s.list.setQuery('x');
  s.calls.length = 0;
  await s.list.loadMore();
  assert.equal(s.calls.length, 0);
});

// ---- deleting one --------------------------------------------------------------------------------------

async function withRows(script = {}, options = {}) {
  const s = setup({ conversations: () => [row('a', 'first'), row('b', 'second'), row('c', 'third')], ...script }, options);
  await s.list.load();
  s.calls.length = 0;
  return s;
}

test('asking replaces one row; asking about another moves the question, never two at once', async () => {
  const s = await withRows();
  assert.equal(s.list.ask('b'), true);
  assert.deepEqual({ ...s.state.confirm }, { target: 'one', id: 'b', phase: 'asking', error: null });
  assert.equal(s.list.ask('c'), true);
  assert.equal(s.state.confirm.id, 'c');
  assert.equal(s.list.ask('nope'), false, 'a row that is not in the list cannot be asked about');
  assert.equal(s.state.confirm.id, 'c');
  assert.equal(s.calls.length, 0, 'asking deletes nothing');
  assert.deepEqual(ids(s.state.rows), ['a', 'b', 'c']);
});

test('"No" and Esc close the question and nothing was deleted', async () => {
  const s = await withRows();
  s.list.ask('a');
  assert.equal(s.list.cancel(), true);
  assert.equal(s.state.confirm, null);
  assert.equal(s.list.cancel(), false, 'nothing to cancel');
  assert.deepEqual(s.calls, []);
  assert.deepEqual(s.deleted, []);
  assert.deepEqual(ids(s.state.rows), ['a', 'b', 'c']);
});

test('the row stays until the server says it is gone, then goes and the pane is told', async () => {
  const request = deferred();
  const s = await withRows({ deleteConversation: () => request.promise });
  s.list.ask('b');
  const deleting = s.list.confirmDelete();
  assert.equal(s.state.confirm.phase, 'working');
  assert.deepEqual(ids(s.state.rows), ['a', 'b', 'c'], 'still there while the request is out');
  assert.deepEqual(s.deleted, []);
  assert.deepEqual(s.calls, [{ op: 'delete', id: 'b' }]);

  request.resolve(null);
  await deleting;
  assert.deepEqual(ids(s.state.rows), ['a', 'c']);
  assert.equal(s.state.confirm, null);
  assert.deepEqual(s.deleted, ['b']);
});

test('while the request is out nothing else can be asked, cancelled or sent twice', async () => {
  const request = deferred();
  const s = await withRows({ deleteConversation: () => request.promise });
  s.list.ask('a');
  const deleting = s.list.confirmDelete();
  assert.equal(s.list.ask('b'), false);
  assert.equal(s.list.askAll(), false);
  assert.equal(s.list.cancel(), false);
  await s.list.confirmDelete(); // a second press of "Delete"
  assert.equal(s.calls.filter((c) => c.op === 'delete').length, 1);
  assert.equal(s.state.confirm.id, 'a');
  request.resolve(null);
  await deleting;
  assert.deepEqual(ids(s.state.rows), ['b', 'c']);
});

test('a busy conversation is refused in the server’s words, stays in the list, and OK closes the strip', async () => {
  const words = ['It is still answering or running something. Try again when it has finished.', 'هنوز در حال پاسخ یا اجراست. وقتی تمام شد دوباره امتحان کنید.'];
  const s = await withRows({ deleteConversation: () => { throw refusal(409, 'busy', words[0], words[1]); } });
  s.list.ask('b');
  await s.list.confirmDelete();
  assert.equal(s.state.confirm.phase, 'refused');
  assert.deepEqual({ ...s.state.confirm.error }, { messageEn: words[0], messageFa: words[1] });
  assert.deepEqual(ids(s.state.rows), ['a', 'b', 'c'], 'refused: nothing left the list');
  assert.deepEqual(s.deleted, [], 'and the pane forgot nothing');

  await s.list.confirmDelete();
  assert.equal(s.calls.filter((c) => c.op === 'delete').length, 1, 'a refusal is not retried by pressing again');
  assert.equal(s.list.cancel(), true);
  assert.equal(s.state.confirm, null);
  assert.deepEqual(ids(s.state.rows), ['a', 'b', 'c']);
});

test('a conversation that is already gone (404) leaves the list, and the pane is told', async () => {
  const s = await withRows({ deleteConversation: () => { throw refusal(404, 'not_found', 'There is no such conversation.', 'چنین گفت‌وگویی وجود ندارد.'); } });
  s.list.ask('c');
  await s.list.confirmDelete();
  assert.deepEqual(ids(s.state.rows), ['a', 'b']);
  assert.equal(s.state.confirm, null);
  assert.deepEqual(s.deleted, ['c']);
});

test('any other failure shows its words, keeps the row, and "try again" asks again', async () => {
  let fail = true;
  const s = await withRows({ deleteConversation: () => { if (fail) throw refusal(0, null, 'Could not reach the panel.', 'ارتباط با پنل برقرار نشد.'); return null; } });
  s.list.ask('a');
  await s.list.confirmDelete();
  assert.equal(s.state.confirm.phase, 'failed');
  assert.equal(s.state.confirm.error.messageFa, 'ارتباط با پنل برقرار نشد.');
  assert.deepEqual(ids(s.state.rows), ['a', 'b', 'c']);
  assert.deepEqual(s.deleted, []);

  fail = false;
  await s.list.confirmDelete(); // "try again"
  assert.deepEqual(ids(s.state.rows), ['b', 'c']);
  assert.deepEqual(s.deleted, ['a']);
  assert.equal(s.calls.filter((c) => c.op === 'delete').length, 2);
});

test('a failed question can be closed', async () => {
  const s = await withRows({ deleteConversation: () => { throw refusal(500, null, 'The panel failed to answer (HTTP 500).', 'پنل نتوانست پاسخ دهد (HTTP 500).'); } });
  s.list.ask('a');
  await s.list.confirmDelete();
  assert.equal(s.list.cancel(), true);
  assert.equal(s.state.confirm, null);
  assert.deepEqual(ids(s.state.rows), ['a', 'b', 'c']);
});

test('a page asked before the delete finished cannot bring the deleted row back', async () => {
  const stale = deferred();
  const request = deferred();
  let listCalls = 0;
  const s = setup({
    conversations: () => (++listCalls === 1 ? [row('a', 'a'), row('b', 'b')] : stale.promise),
    deleteConversation: () => request.promise,
  });
  await s.list.load();
  s.list.ask('a');
  const deleting = s.list.confirmDelete();
  s.list.setQuery('x');
  await s.timers.fire(); // a page for "x" is asked for while the delete is out
  request.resolve(null);
  await deleting;
  stale.resolve([row('a', 'a'), row('b', 'b')]); // the server answered before it had deleted
  await settle();
  assert.deepEqual(ids(s.state.rows), ['b'], '"a" was deleted; an older page does not undo that');
});

test('a "show more" asked before a delete is dropped, because every row after the deleted one moved up', async () => {
  const more = deferred();
  const s = setup({
    conversations: ({ skip }) => (skip === 0 ? page(0, PAGE_SIZE) : more.promise),
    deleteConversation: () => null,
  });
  await s.list.load();
  const asking = s.list.loadMore();
  s.list.ask('id-3');
  await s.list.confirmDelete();
  assert.equal(s.state.loadingMore, false);
  more.resolve(page(PAGE_SIZE, 5));
  await asking;
  assert.equal(s.state.rows.length, PAGE_SIZE - 1, 'the late page was not appended');
  assert.equal(s.state.hasMore, true, 'and "show more" is still on offer, asking from the right place');
  s.calls.length = 0;
  await s.list.loadMore();
  assert.equal(s.calls[0].skip, PAGE_SIZE - 1);
});

test('a reload that no longer has the row drops its question', async () => {
  const s = setup({ conversations: ({ q }) => (q ? [row('c', 'c')] : [row('a', 'a'), row('b', 'b'), row('c', 'c')]) });
  await s.list.load();
  s.list.ask('a');
  s.list.setQuery('c');
  await s.timers.fire();
  assert.equal(s.state.confirm, null, 'no strip hanging on a row that is not on screen');
  assert.deepEqual(s.deleted, []);
});

// ---- deleting all ---------------------------------------------------------------------------------------

test('"delete all" asks first, and only one question is open at a time', async () => {
  const s = await withRows();
  s.list.ask('a');
  s.list.askAll();
  assert.equal(s.state.confirm.target, 'all');
  assert.equal(s.calls.length, 0);
  s.list.ask('b');
  assert.equal(s.state.confirm.target, 'one', 'asking about a row closed the other question');
  assert.equal(s.list.cancel(), true);
});

test('delete all: the list reloads, and when nothing was skipped the open conversation is forgotten', async () => {
  let deletedAll = false;
  const s = await withRows({
    deleteAllConversations: () => { deletedAll = true; return { deleted: 3, skipped: 0 }; },
    conversations: () => (deletedAll ? [] : [row('a', 'a'), row('b', 'b'), row('c', 'c')]),
  });
  s.list.askAll();
  const result = await s.list.confirmDeleteAll({ openId: 'b' });
  assert.deepEqual(result, { deleted: 3, skipped: 0, openGone: true });
  assert.deepEqual(s.calls.map((c) => c.op), ['delete-all', 'list']);
  assert.deepEqual(s.state.rows, []);
  assert.equal(s.state.confirm, null);
  assert.equal(s.state.kept, 0);
  assert.deepEqual(s.deleted, ['b']);
});

test('delete all with some kept running: they stay listed, the count is shown, and the open one is checked with the server', async () => {
  let deletedAll = false;
  const s = await withRows({
    deleteAllConversations: () => { deletedAll = true; return { deleted: 2, skipped: 1 }; },
    conversations: () => (deletedAll ? [row('b', 'b')] : [row('a', 'a'), row('b', 'b'), row('c', 'c')]),
    conversation: (id) => ({ id }), // the open one still exists: it was one of those kept
  });
  s.list.askAll();
  const result = await s.list.confirmDeleteAll({ openId: 'b' });
  assert.deepEqual(result, { deleted: 2, skipped: 1, openGone: false });
  assert.deepEqual(ids(s.state.rows), ['b']);
  assert.equal(s.state.kept, 1);
  assert.deepEqual(s.deleted, [], 'the open conversation survived, so the pane keeps it');
  assert.ok(s.calls.some((c) => c.op === 'get' && c.id === 'b'));
});

test('delete all with some kept, when the open conversation was among those deleted, forgets it', async () => {
  const s = await withRows({
    deleteAllConversations: () => ({ deleted: 2, skipped: 1 }),
    conversations: () => [row('a', 'a')],
    conversation: () => { throw refusal(404, 'not_found', 'There is no such conversation.', 'چنین گفت‌وگویی وجود ندارد.'); },
  });
  s.list.askAll();
  const result = await s.list.confirmDeleteAll({ openId: 'b' });
  assert.equal(result.openGone, true);
  assert.deepEqual(s.deleted, ['b']);
});

test('delete all with some kept, when the server cannot be asked about the open one, keeps it', async () => {
  const s = await withRows({
    deleteAllConversations: () => ({ deleted: 2, skipped: 1 }),
    conversations: () => [row('a', 'a')],
    conversation: () => { throw refusal(0, null, 'Could not reach the panel.', 'ارتباط با پنل برقرار نشد.'); },
  });
  s.list.askAll();
  const result = await s.list.confirmDeleteAll({ openId: 'b' });
  assert.equal(result.openGone, false, 'not known to be gone, so not forgotten (a reload settles it quietly)');
  assert.deepEqual(s.deleted, []);
});

test('delete all with no conversation open forgets nothing', async () => {
  const s = await withRows({ deleteAllConversations: () => ({ deleted: 3, skipped: 0 }), conversations: () => [] });
  s.list.askAll();
  const result = await s.list.confirmDeleteAll({ openId: null });
  assert.equal(result.openGone, false);
  assert.deepEqual(s.deleted, []);
});

test('delete all that fails leaves the list as it was, with the words and a way to try again', async () => {
  let fail = true;
  const s = await withRows({ deleteAllConversations: () => { if (fail) throw refusal(500, null, 'The panel failed to answer (HTTP 500).', 'پنل نتوانست پاسخ دهد (HTTP 500).'); return { deleted: 3, skipped: 0 }; }, conversations: () => [row('a', 'a'), row('b', 'b'), row('c', 'c')] });
  s.list.askAll();
  assert.equal(await s.list.confirmDeleteAll({ openId: 'b' }), null);
  assert.equal(s.state.confirm.phase, 'failed');
  assert.equal(s.state.confirm.error.messageFa, 'پنل نتوانست پاسخ دهد (HTTP 500).');
  assert.deepEqual(ids(s.state.rows), ['a', 'b', 'c']);
  assert.deepEqual(s.deleted, []);

  fail = false;
  s.calls.length = 0;
  const result = await s.list.confirmDeleteAll({ openId: 'b' });
  assert.equal(result.deleted, 3);
  assert.deepEqual(s.deleted, ['b']);
});

test('a page asked before delete all finished cannot bring deleted rows back', async () => {
  const stale = deferred();
  let listCalls = 0;
  const s = setup({
    conversations: () => (++listCalls === 1 ? [row('a', 'a'), row('b', 'b')] : listCalls === 2 ? stale.promise : []),
    deleteAllConversations: () => ({ deleted: 2, skipped: 0 }),
  });
  await s.list.load();
  s.list.setQuery('x');
  await s.timers.fire(); // a page for "x" is out
  s.list.askAll();
  await s.list.confirmDeleteAll({ openId: null });
  stale.resolve([row('a', 'a')]);
  await settle();
  assert.deepEqual(s.state.rows, []);
});

test('disposing stops the search timer and drops what is in flight', async () => {
  const request = deferred();
  const s = setup({ conversations: () => request.promise });
  s.list.setQuery('x');
  s.list.dispose();
  assert.equal(s.timers.queue.length, 0);
  const loading = s.list.load();
  s.list.dispose();
  request.resolve([row('a', 'a')]);
  await loading;
  assert.deepEqual(s.state.rows, []);
});
