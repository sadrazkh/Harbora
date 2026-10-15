import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  categoryIcon, formatSeconds, formatElapsed, toolLabel, toolLineText, toolLineDetail, toolLineTitle, toolLineMark, lineOutcome,
  activityRows, blockView, toggledChoice, ranCompactly, ranHeading, resultLinks, planCardLine,
  thinkingShows, trackThinking, thinkingLine,
} from '../islands/assistantActivity.js';
import { createTimeline, addUserMessage, applyStreamEvent, applyPlanResult, loadConversation } from '../islands/assistantTimeline.js';

// What the log draws from the timeline: tool lines in people's words, runs of them as one block that
// collapses once the work is over, a plan that ran as one compact card, and the thinking line. The
// rules worth pinning are the honest ones — a block says "done" only for lines the server said were
// done, a failure is never folded away, and a plan that did not fully run is never drawn as "ran".

const PLAN_ID = '0199d0c4-6a1e-7000-8000-0000000000aa';

const live = (key, state, extra = {}) => ({ key, kind: 'tool', name: 'get_app', toolKind: 'read', state, summaryEn: null, summaryFa: null, labelEn: null, labelFa: null, category: null, durationMs: null, ...extra });
const saved = (key, extra = {}) => ({ key, kind: 'tool-history', toolName: 'get_app', text: 'Read shop', state: null, label: null, category: null, durationMs: null, planId: null, ...extra });
const user = (key, text = 'x') => ({ key, kind: 'user', text });
const said = (key, text = 'ok') => ({ key, kind: 'assistant', text });

function plan(status = 'executed', steps = 2) {
  return {
    id: PLAN_ID, status, expiresAt: '2026-10-15T10:15:00Z',
    steps: Array.from({ length: steps }, (_, i) => ({
      index: i, tool: 'create_database', kind: 'safe', titleEn: `Step ${i}`, titleFa: `گام ${i}`, detailEn: null, detailFa: null,
      warningsEn: [], warningsFa: [], confirmationPhrase: null, autoApproved: false, rememberable: false,
    })),
  };
}
function result(states = ['done', 'done'], { planId = PLAN_ID, status = 'executed' } = {}) {
  return {
    planId, status,
    steps: states.map((state, index) => ({
      index, state, summaryEn: null, summaryFa: null, errorEn: null, errorFa: null,
      links: index === 0 ? [{ kind: 'database', id: 'd1', label: 'shop-db', url: '/databases/d1' }] : [],
    })),
  };
}
const planCard = (p, extra = {}) => ({ key: `plan-${p.id}`, kind: 'plan', plan: p, resulted: false, superseded: false, ...extra });
const resultCard = (key, r, p = null) => ({ key, kind: 'result', result: r, plan: p });

// ---- icons and words -----------------------------------------------------------------------------

test('every category has its icon, and anything else gets the generic wrench', () => {
  assert.deepEqual(
    ['app', 'deployment', 'database', 'project', 'domain', 'volume', 'env', 'catalog', 'usage', 'resources'].map(categoryIcon),
    ['AppWindow', 'Rocket', 'Database', 'Folder', 'Globe', 'HardDrive', 'KeyRound', 'LayoutGrid', 'Gauge', 'Boxes']);
  for (const other of ['other', 'something-new', '', null, undefined, 'constructor', '__proto__', 'toString']) {
    assert.equal(categoryIcon(other), 'Wrench', String(other));
  }
});

test('a labelled line reads "<label>…" while running and "<label>" after, whatever its outcome', () => {
  const labels = { labelEn: "Reading the deployment's logs", labelFa: 'خواندن لاگ‌های استقرار', category: 'deployment' };
  assert.equal(toolLineText(live('a', 'running', labels), false), "Reading the deployment's logs…");
  assert.equal(toolLineText(live('a', 'running', labels), true), 'خواندن لاگ‌های استقرار…');
  for (const state of ['done', 'failed', 'interrupted', 'unknown']) {
    assert.equal(toolLineText(live('a', state, { ...labels, summaryEn: 'No deployments yet' }), false), "Reading the deployment's logs", state);
  }
  assert.equal(toolLineText(live('a', 'done', labels), true), 'خواندن لاگ‌های استقرار');
});

test('under a label, the server\'s words are a visible second line — and a failed line always has words', () => {
  const labels = { labelEn: "Reading the deployment's logs", labelFa: 'خواندن لاگ‌های استقرار' };
  const done = live('a', 'done', { ...labels, summaryEn: 'Read 40 lines', summaryFa: '۴۰ خط خوانده شد' });
  assert.equal(toolLineDetail(done, false), 'Read 40 lines');
  assert.equal(toolLineDetail(done, true), '۴۰ خط خوانده شد');
  const failed = live('a', 'failed', { ...labels, summaryEn: 'No deployments yet', summaryFa: 'هنوز استقراری نیست' });
  assert.equal(toolLineDetail(failed, true), 'هنوز استقراری نیست', 'the summary says what failed');
  assert.equal(toolLineDetail(live('a', 'failed', labels), false), 'It failed.', 'no summary: still words');
  assert.equal(toolLineDetail(live('a', 'failed', labels), true), 'ناموفق بود.');
  assert.equal(toolLineDetail(live('a', 'interrupted', labels), true), 'نتیجه‌ای نرسید.');
  assert.equal(toolLineDetail(live('a', 'unknown', { ...labels, summaryEn: 'Read shop' }), false), 'Read shop', 'an unnamed state: its words, under a label, not as a check');
  assert.equal(toolLineDetail(live('a', 'unknown', labels), false), 'The outcome is not known.');
  assert.equal(toolLineDetail(live('a', 'running', labels), false), '', 'nothing said yet');
  assert.equal(toolLineDetail(live('a', 'done', { ...labels, summaryEn: "Reading the deployment's logs" }), false), '', 'never the same words twice');
});

test('an unlabelled line, from an older server, falls back to the summary and only then to the identifier', () => {
  assert.equal(toolLineText(live('a', 'running'), false), 'Running get_app…');
  assert.equal(toolLineText(live('a', 'running'), true), 'در حال اجرای get_app…');
  assert.equal(toolLineText(live('a', 'done', { summaryEn: 'Read shop: running', summaryFa: 'shop خوانده شد' }), false), 'Read shop: running');
  assert.equal(toolLineText(live('a', 'done'), false), 'get_app finished');
  assert.equal(toolLineText(live('a', 'failed'), true), 'get_app ناموفق بود');
  assert.equal(toolLineText(live('a', 'interrupted'), false), 'get_app — no result arrived');
  // One language missing: the other is used rather than nothing.
  assert.equal(toolLineText(live('a', 'done', { labelEn: 'Reading the app' }), true), 'Reading the app');
  // The words are already the main text: no second line repeats them.
  assert.equal(toolLineDetail(live('a', 'failed', { summaryEn: 'No deployments yet' }), false), '');
  assert.equal(toolLineDetail(live('a', 'done', { summaryEn: 'Read shop: running' }), false), '');
  assert.equal(toolLineDetail(live('a', 'unknown', { summaryEn: 'Read shop' }), false), 'Read shop', 'under "get_app — outcome unknown"');
});

test('a saved line with an outcome reads its label, with the stored text visible under it', () => {
  const read = saved('s', { label: 'خواندن اپ', text: 'shop خوانده شد: در حال اجرا', state: 'done', durationMs: 410 });
  assert.equal(toolLineText(read, true), 'خواندن اپ');
  assert.equal(toolLineDetail(read, true), 'shop خوانده شد: در حال اجرا', 'never hover-only');
  const failed = saved('f', { label: 'Reading an app\'s details', text: 'App not found', state: 'failed', durationMs: 90 });
  assert.equal(toolLineText(failed, false), "Reading an app's details");
  assert.equal(toolLineDetail(failed, false), 'App not found', 'a stored failure says what failed');
  assert.equal(toolLineDetail(saved('f', { label: 'Deleting a database', text: '', state: 'failed' }), true), 'ناموفق بود.', 'a failed line always has words');
  const executed = saved('e', { toolName: 'delete_database', label: 'Deleting a database', text: 'Deleted old-db', state: 'done', planId: 'x' });
  assert.equal(toolLineText(executed, false), 'Deleting a database', 'an execution line that ran: the label fits');
  assert.equal(toolLineDetail(executed, false), 'Deleted old-db');
  assert.equal(toolLabel(saved('s', { label: 'خواندن اپ' }), false), 'خواندن اپ', 'the server already chose the language');
});

test('a saved line without an outcome reads its own words: "Deleting a database" would say it happened', () => {
  const proposal = saved('p', { toolName: 'delete_database', text: 'Proposed as step 1: delete old-db', label: 'Deleting a database', planId: 'x' });
  assert.equal(toolLineText(proposal, false), 'Proposed as step 1: delete old-db');
  assert.equal(toolLineDetail(proposal, false), '', 'the words are the line');
  assert.equal(toolLineText(saved('s', { text: '', label: 'Deleting a database' }), false), 'Deleting a database', 'no words: the label');
  assert.equal(toolLineText(saved('s', { text: '' }), true), 'get_app', 'nothing else: the identifier');
});

test('a server from before toolState: a timed stored line was a read that ran — its label, its words under it', () => {
  const read = saved('s', { label: 'خواندن اپ', durationMs: 410 });
  assert.equal(lineOutcome(read), 'ran');
  assert.equal(toolLineText(read, true), 'خواندن اپ');
  assert.equal(toolLineDetail(read, true), 'Read shop');
  assert.equal(toolLineMark(read), null, 'its outcome was not stored: no check');
  assert.equal(lineOutcome(saved('s', { label: 'خواندن اپ' })), 'noted', 'an untimed row');
  assert.equal(toolLineText(saved('s', { label: 'خواندن اپ' }), true), 'Read shop');
});

test('the tooltip is only the identifier: every word the server said is on the line', () => {
  assert.equal(toolLineTitle(saved('s', { label: 'Reading the app', state: 'done' })), 'get_app');
  assert.equal(toolLineTitle(live('a', 'done', { labelEn: 'Reading the app', summaryEn: 'Read shop: running' })), 'get_app');
});

test('the state mark: a spinner, a check or a cross where the server said so, and none on a saved line without an outcome', () => {
  assert.equal(toolLineMark(live('a', 'running')), 'running');
  assert.equal(toolLineMark(live('a', 'running'), { thinkingKey: 'a' }), null, 'the thinking line names it: no second spinner');
  assert.equal(toolLineMark(live('a', 'running'), { thinkingKey: 'b' }), 'running');
  assert.equal(toolLineMark(live('a', 'done')), 'done');
  assert.equal(toolLineMark(live('a', 'failed')), 'failed');
  assert.equal(toolLineMark(live('a', 'interrupted')), 'unknown');
  assert.equal(toolLineMark(live('a', 'unknown')), 'unknown');
  assert.equal(toolLineMark(saved('s')), null, 'a stored line without an outcome');
  assert.equal(toolLineMark(saved('s', { state: 'done' })), 'done', 'the server stored "done"');
  assert.equal(toolLineMark(saved('s', { state: 'failed' })), 'failed');
});

test('durations are seconds with one decimal in the panel\'s digits, and a tiny one is not rounded to zero', () => {
  assert.equal(formatSeconds(2400, false), '2.4 s');
  assert.equal(formatSeconds(2400, true), '۲٫۴ ثانیه');
  assert.equal(formatSeconds(12_000, false), '12.0 s');
  assert.equal(formatSeconds(40, false), '< 0.1 s');
  assert.equal(formatSeconds(40, true), 'کمتر از ۰٫۱ ثانیه');
  for (const nothing of [null, undefined, -1, NaN, '2400']) assert.equal(formatSeconds(nothing, false), '', String(nothing));
  assert.equal(formatElapsed(12, false), '12 s');
  assert.equal(formatElapsed(12, true), '۱۲ ثانیه');
});

// ---- activity blocks -----------------------------------------------------------------------------

test('consecutive tool lines are one block; text, plans, results and the person\'s messages each end it', () => {
  const p = plan('pending');
  const rows = activityRows([
    user('u1'), live('t1', 'done'), live('t2', 'done'), said('a1'), live('t3', 'done'),
    planCard(p), saved('t4'), user('u2'), saved('t5'), saved('t6'), resultCard('r1', result(['failed'])), live('t7', 'failed'),
  ]);
  assert.deepEqual(rows.map((r) => [r.type, r.key]), [
    ['item', 'u1'], ['activity', 't1'], ['item', 'a1'], ['activity', 't3'], ['item', p.id && `plan-${p.id}`],
    ['activity', 't4'], ['item', 'u2'], ['activity', 't5'], ['item', 'r1'], ['activity', 't7'],
  ]);
  assert.deepEqual(rows[1].block.items.map((i) => i.key), ['t1', 't2']);
  assert.deepEqual(rows[7].block.items.map((i) => i.key), ['t5', 't6']);
  assert.equal(rows[1].block.key, 't1', 'keyed by its first line');
});

test('a block counts its lines by outcome, and sums durations only when every line has one', () => {
  const [row] = activityRows([
    live('a', 'done', { durationMs: 400 }), live('b', 'failed', { durationMs: 1200 }), live('c', 'running'),
    live('d', 'interrupted'), saved('e', { durationMs: 800 }),
  ]);
  const { running, done, failed, unsettled, ran, totalMs } = row.block;
  assert.deepEqual({ running, done, failed, unsettled, ran, totalMs }, { running: 1, done: 1, failed: 1, unsettled: 1, ran: 1, totalMs: null });

  const [timed] = activityRows([live('a', 'done', { durationMs: 400 }), live('b', 'done', { durationMs: 2000 })]);
  assert.equal(timed.block.totalMs, 2400);
  const [partly] = activityRows([live('a', 'done', { durationMs: 400 }), live('b', 'done')]);
  assert.equal(partly.block.totalMs, null, 'a sum with a gap in it would understate the time');
});

test('a block with a running line is open and says it is working', () => {
  const [row] = activityRows([live('a', 'done'), live('b', 'running')]);
  assert.deepEqual(blockView(row.block, { answering: true, last: true }), { tone: 'working', working: true, heading: 'Working…', open: true });
  assert.equal(blockView(row.block, { answering: true, last: false, fa: true }).heading, 'در حال کار…');
});

test('the last block of a turn still streaming stays open — more tools may follow — and collapses once text follows it', () => {
  const [row] = activityRows([live('a', 'done', { durationMs: 900 })]);
  assert.equal(blockView(row.block, { answering: true, last: true }).tone, 'working');
  const after = blockView(row.block, { answering: true, last: false });
  assert.equal(after.tone, 'done');
  assert.equal(after.open, false);
});

test('a finished block collapses to "3 actions done · 2.4 s", in the panel\'s digits', () => {
  const [row] = activityRows([live('a', 'done', { durationMs: 400 }), live('b', 'done', { durationMs: 1500 }), live('c', 'done', { durationMs: 500 })]);
  assert.deepEqual(blockView(row.block), { tone: 'done', working: false, heading: '3 actions done · 2.4 s', open: false });
  assert.equal(blockView(row.block, { fa: true }).heading, '۳ کار انجام شد · ۲٫۴ ثانیه');
  const [untimed] = activityRows([live('a', 'done'), live('b', 'done', { durationMs: 10 })]);
  assert.equal(blockView(untimed.block).heading, '2 actions done', 'no total without every duration');
});

test('a single line without a failure is still a block, with the same look', () => {
  const rows = activityRows([user('u'), live('a', 'done', { durationMs: 300 }), said('m')]);
  assert.equal(rows[1].type, 'activity');
  assert.equal(blockView(rows[1].block).heading, '1 action done · 0.3 s');
  assert.equal(blockView(rows[1].block, { fa: true }).heading, '۱ کار انجام شد · ۰٫۳ ثانیه');
});

test('a failure keeps its block open, in the danger colour, and is counted in the heading', () => {
  const [row] = activityRows([live('a', 'failed'), live('b', 'done'), live('c', 'done')]);
  const view = blockView(row.block, { fa: true });
  assert.equal(view.tone, 'failed');
  assert.equal(view.open, true, 'a failure is never hidden behind a click');
  assert.equal(view.heading, '۱ کار ناموفق، ۲ انجام شد');
  assert.equal(blockView(row.block).heading, '1 action failed, 2 done');
  const [only] = activityRows([live('a', 'failed'), live('b', 'failed')]);
  assert.equal(blockView(only.block).heading, '2 actions failed');
  assert.equal(blockView(only.block, { fa: true }).heading, '۲ کار ناموفق');
});

test('saved lines with an outcome count and colour exactly like live ones', () => {
  const [done] = activityRows([saved('a', { state: 'done', durationMs: 400 }), saved('b', { state: 'done', durationMs: 2000 })]);
  assert.deepEqual(blockView(done.block), { tone: 'done', working: false, heading: '2 actions done · 2.4 s', open: false });
  const [failed] = activityRows([saved('a', { state: 'failed', durationMs: 90 }), saved('b', { state: 'done', durationMs: 400 })]);
  const view = blockView(failed.block, { fa: true });
  assert.deepEqual([view.tone, view.open, view.heading], ['failed', true, '۱ کار ناموفق، ۱ انجام شد'], 'a stored failure is open too');
});

test('saved lines from a server before toolState say they ran, never that they succeeded', () => {
  const [row] = activityRows([saved('a', { durationMs: 1200 }), saved('b', { durationMs: 1200 })]);
  const view = blockView(row.block);
  assert.equal(view.tone, 'ran');
  assert.equal(view.open, false);
  assert.equal(view.heading, '2 actions ran · 2.4 s');
  assert.equal(blockView(row.block, { fa: true }).heading, '۲ کار اجرا شد · ۲٫۴ ثانیه');
});

test('saved lines without an outcome are only counted, with no time — a proposal did not do what it proposed', () => {
  const proposal = { toolName: 'delete_database', text: 'Proposed as step 1: delete old-db', planId: 'x' };
  const [only] = activityRows([saved('a', proposal), saved('b', proposal)]);
  assert.deepEqual([blockView(only.block).tone, blockView(only.block).heading, blockView(only.block).open], ['noted', '2 actions', false]);
  assert.equal(blockView(only.block, { fa: true }).heading, '۲ کار');
  // Mixed: every kind is counted, the first with the noun.
  const [mixed] = activityRows([saved('a', { state: 'done', durationMs: 640 }), saved('b', proposal)]);
  assert.deepEqual([mixed.block.done, mixed.block.noted, mixed.block.totalMs], [1, 1, null], 'no total: the proposal has no duration');
  assert.deepEqual([blockView(mixed.block).tone, blockView(mixed.block).heading], ['noted', '1 action done, 1 other']);
  assert.equal(blockView(mixed.block, { fa: true }).heading, '۱ کار انجام شد، ۱ مورد دیگر');
  const [older] = activityRows([saved('a', { durationMs: 640 }), saved('b', proposal)]);
  assert.equal(blockView(older.block).heading, '1 action ran, 1 other');
  const [failedToo] = activityRows([saved('a', { state: 'failed' }), saved('b', { state: 'done' }), saved('c', proposal)]);
  assert.equal(blockView(failedToo.block).heading, '1 action failed, 1 done, 1 other');
  assert.equal(blockView(failedToo.block, { fa: true }).heading, '۱ کار ناموفق، ۱ انجام شد، ۱ مورد دیگر');
});

test('lines that never got an outcome are said to have none, not counted as done', () => {
  const [row] = activityRows([live('a', 'done'), live('b', 'interrupted')]);
  const view = blockView(row.block);
  assert.equal(view.tone, 'unsettled');
  assert.equal(view.heading, '1 action done, 1 with no result');
  assert.equal(blockView(row.block, { fa: true }).heading, '۱ کار انجام شد، ۱ بی‌نتیجه');
  const [none] = activityRows([live('a', 'interrupted')]);
  assert.equal(blockView(none.block).heading, '1 action with no result');
});

test('the person\'s choice wins over the rules, both ways', () => {
  const [running] = activityRows([live('a', 'running')]);
  const working = blockView(running.block, { answering: true, last: true });
  assert.equal(blockView(running.block, { answering: true, last: true, choice: toggledChoice(working) }).open, false, 'closed while it works');
  const [failed] = activityRows([live('a', 'failed')]);
  const failing = blockView(failed.block);
  assert.deepEqual(toggledChoice(failing), { open: false, tone: 'failed' });
  assert.equal(blockView(failed.block, { choice: toggledChoice(failing) }).open, false, 'a failure they have seen and folded');
  const [done] = activityRows([live('a', 'done')]);
  assert.equal(blockView(done.block, { choice: toggledChoice(blockView(done.block)) }).open, true, 'opened after the turn');
  assert.equal(blockView(done.block, { choice: undefined }).open, false);
});

test('a failure arriving after the person folded the block opens it again', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  state.streaming = true;
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running', labelEn: 'Reading the app' });
  const [first] = activityRows(state.items).filter((r) => r.type === 'activity');
  const choice = toggledChoice(blockView(first.block, { answering: true, last: true })); // folded while it works
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'failed', summaryEn: 'App not found' });
  applyStreamEvent(state, 'done', { conversationId: 'c', remainingToday: 1 });
  state.streaming = false;
  const [after] = activityRows(state.items).filter((r) => r.type === 'activity');
  const view = blockView(after.block, { choice });
  assert.deepEqual([view.tone, view.open], ['failed', true], 'not hidden behind the earlier click');
  // Folded again now that it failed: that choice holds.
  assert.equal(blockView(after.block, { choice: toggledChoice(view) }).open, false);
  // A block that turns done keeps a fold made while it worked.
  const [ok] = activityRows([live('a', 'done')]);
  assert.equal(blockView(ok.block, { choice: { open: false, tone: 'working' } }).open, false);
  assert.equal(blockView(ok.block, { choice: { open: true, tone: 'working' } }).open, true);
});

test('the choice survives the next events of the turn: the block keeps its first line\'s key as lines are added', () => {
  const state = createTimeline();
  addUserMessage(state, 'how is shop?');
  state.streaming = true;
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running', labelEn: 'Reading the app', category: 'app' });
  const first = activityRows(state.items).find((r) => r.type === 'activity');
  const choices = { [first.key]: toggledChoice(blockView(first.block, { answering: true, last: true })) }; // folded while it works
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done', durationMs: 300 });
  applyStreamEvent(state, 'tool', { name: 'get_deployment', state: 'running' });
  const rows = activityRows(state.items);
  const block = rows.find((r) => r.type === 'activity');
  assert.equal(block.key, first.key);
  assert.equal(block.block.items.length, 2);
  assert.equal(blockView(block.block, { answering: true, last: true, choice: choices[block.key] }).open, false);
  applyStreamEvent(state, 'tool', { name: 'get_deployment', state: 'done' });
  applyStreamEvent(state, 'done', { conversationId: 'c', remainingToday: 1 });
  state.streaming = false;
  const ended = activityRows(state.items).find((r) => r.type === 'activity');
  assert.equal(blockView(ended.block, { choice: choices[ended.key] }).open, false);
  assert.equal(blockView(ended.block, { choice: { open: true, tone: 'done' } }).open, true);
});

// ---- plans that ran ------------------------------------------------------------------------------

test('a plan that ran with every step done and its result become one compact row', () => {
  const p = plan('executed');
  const rows = activityRows([said('a'), planCard(p, { resulted: true }), resultCard('r', result(), p), said('b')]);
  assert.deepEqual(rows.map((r) => r.type), ['item', 'ran', 'item']);
  assert.equal(rows[1].plan.plan.id, PLAN_ID);
  assert.equal(rows[1].result.key, 'r');
  assert.equal(rows[1].key, `plan-${PLAN_ID}`);
});

test('a plan that failed, or whose result does not show every step done, keeps both cards in full', () => {
  const failed = plan('failed');
  assert.deepEqual(activityRows([planCard(failed, { resulted: true }), resultCard('r', result(['done', 'failed'], { status: 'failed' }), failed)]).map((r) => r.type), ['item', 'item']);
  const odd = plan('executed');
  assert.equal(ranCompactly(planCard(odd, { resulted: true }), resultCard('r', result(['done', 'skipped']), odd)), false);
  assert.equal(ranCompactly(planCard(odd, { resulted: true }), resultCard('r', result(['done', 'weird']), odd)), false);
  assert.equal(ranCompactly(planCard(odd, { resulted: true }), resultCard('r', { planId: PLAN_ID, status: 'executed', steps: [] }, odd)), false, 'nothing ran is not "ran"');
});

test('a plan card and a result are merged only when they are the same plan, with the result under it', () => {
  const p = plan('executed');
  const other = { ...plan('executed'), id: 'other' };
  assert.equal(ranCompactly(planCard(p, { resulted: true }), resultCard('r', result(['done', 'done'], { planId: 'other' }), other)), false);
  assert.equal(ranCompactly(planCard(p, { resulted: false }), resultCard('r', result(), p)), false, 'not marked as having its result under it');
  assert.equal(ranCompactly(planCard(p, { resulted: true }), said('x')), false);
  assert.equal(ranCompactly(planCard(p, { resulted: true }), resultCard('r', result(), null)), true, 'the result names the plan itself');
});

test('a plan that ran without a card (an executed event) is compact too, and only when every step is done', () => {
  const rows = activityRows([resultCard('r1', result()), resultCard('r2', result(['done', 'failed'], { status: 'failed' }))]);
  assert.deepEqual(rows.map((r) => [r.type, r.plan ?? null]), [['ran', null], ['item', null]]);
});

test('the compact card says how many steps ran, and lists every link for its chips', () => {
  assert.equal(ranHeading(result(), false), 'Plan ran · 2 steps');
  assert.equal(ranHeading(result(), true), 'برنامه اجرا شد · ۲ گام');
  assert.equal(ranHeading(result(['done']), false), 'Plan ran · 1 step');
  const r = result();
  r.steps[1].links = [{ kind: 'app', id: 'a1', label: 'shop', url: '/apps/a1' }];
  assert.deepEqual(resultLinks(r).map((l) => l.label), ['shop-db', 'shop']);
  assert.deepEqual(resultLinks({ steps: [{ index: 0 }] }), []);
});

test('cancelled and expired cards fold to one muted line; every other card is drawn in full', () => {
  assert.equal(planCardLine(planCard(plan('rejected')), true), 'لغو شد — هیچ کاری اجرا نشد');
  assert.equal(planCardLine(planCard(plan('rejected')), false), 'Cancelled — nothing ran');
  assert.equal(planCardLine(planCard(plan('expired')), true), 'منقضی شد — هیچ کاری اجرا نشد');
  assert.equal(planCardLine(planCard(plan('expired')), false), 'Expired — nothing ran');
  assert.equal(planCardLine(planCard(plan('expired'), { superseded: true }), false), 'Not run — you sent a new message', 'it says why');
  for (const status of ['pending', 'executing', 'executed', 'failed', 'surprising']) {
    assert.equal(planCardLine(planCard(plan(status)), true), null, status);
  }
});

test('an approve result turns a waiting card into the compact card when everything ran', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan('pending'));
  applyPlanResult(state, PLAN_ID, result());
  const rows = activityRows(state.items);
  assert.deepEqual(rows.map((r) => r.type), ['ran']);
  assert.equal(rows[0].result.plan.steps[0].titleEn, 'Step 0', 'the compact card still knows the step titles');
});

// ---- the thinking line ---------------------------------------------------------------------------

test('the thinking line names the running tool, with its category, and says "Thinking…" when none runs', () => {
  const state = createTimeline();
  addUserMessage(state, 'why did it fail?');
  state.streaming = true;
  assert.deepEqual(thinkingLine(state, 0), { labelEn: 'Thinking…', labelFa: 'در حال فکر کردن…', category: null, toolKey: null, seconds: null });
  applyStreamEvent(state, 'tool', { name: 'get_deployment', state: 'running', labelEn: "Reading the deployment's logs", labelFa: 'خواندن لاگ‌های استقرار', category: 'deployment' });
  const line = thinkingLine(state, 0);
  assert.equal(line.labelEn, "Reading the deployment's logs…");
  assert.equal(line.labelFa, 'در حال خواندن لاگ‌های استقرار…');
  assert.equal(line.category, 'deployment');
  assert.equal(line.toolKey, state.items[1].key);
  applyStreamEvent(state, 'tool', { name: 'get_deployment', state: 'done' });
  assert.equal(thinkingLine(state, 0).labelEn, 'Thinking…', 'after a tool, until the next output');
  applyStreamEvent(state, 'delta', { text: 'It failed because' });
  assert.equal(thinkingLine(state, 0), null, 'text is being written');
  assert.equal(thinkingShows(state), false);
});

test('a running tool from an older server is named by its identifier', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  state.streaming = true;
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  assert.deepEqual([thinkingLine(state, 0).labelEn, thinkingLine(state, 0).labelFa, thinkingLine(state, 0).category], ['Running get_app…', 'در حال اجرای get_app…', null]);
});

test('a running tool with text after it is not what the thinking line names: the text is the sign of life', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  state.streaming = true;
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running', labelEn: 'Reading the app' });
  applyStreamEvent(state, 'delta', { text: 'Meanwhile' });
  assert.equal(thinkingLine(state, 0), null);
  assert.equal(thinkingShows(state), false);
  assert.equal(toolLineMark(state.items[1], { thinkingKey: null }), 'running', 'so the line spins itself');
});

test('no thinking line when nothing is being answered; "Thinking…" for a conversation opened mid-answer', () => {
  const state = createTimeline();
  assert.equal(thinkingLine(state, 0), null);
  addUserMessage(state, 'x');
  assert.equal(thinkingLine(state, 0), null, 'not streaming');
  state.resuming = true;
  assert.equal(thinkingLine(state, 0).labelEn, 'Thinking…');
});

test('the seconds appear from five seconds after the line did, and count whole seconds', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  state.streaming = true;
  trackThinking(state, 1_000);
  assert.equal(thinkingLine(state, 1_000).seconds, null);
  assert.equal(thinkingLine(state, 5_999).seconds, null, '4.999 s');
  assert.equal(thinkingLine(state, 6_000).seconds, 5);
  assert.equal(thinkingLine(state, 13_345).seconds, 12);
});

test('the clock starts when the line appears, runs on under a tool, and is cleared when the line goes', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  state.streaming = true;
  assert.equal(trackThinking(state, 100), true);
  assert.equal(state.thinkingSince, 100);
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  assert.equal(trackThinking(state, 2_000), true);
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
  assert.equal(trackThinking(state, 3_000), true);
  assert.equal(state.thinkingSince, 100, 'the same wait');
  applyStreamEvent(state, 'delta', { text: 'shop' });
  assert.equal(trackThinking(state, 4_000), false);
  assert.equal(state.thinkingSince, null);
  applyStreamEvent(state, 'tool', { name: 'get_usage', state: 'running' });
  trackThinking(state, 9_000);
  assert.equal(state.thinkingSince, 9_000, 'a new wait counts from its own start');
  assert.equal(thinkingLine(state, 9_500).seconds, null);
});

test('a stored conversation\'s tool lines feed the same blocks, with their labels, words, outcomes and durations', () => {
  const state = createTimeline();
  const read = (id, tool, text, label, category, durationMs, toolState) => ({ id, role: 'tool', text, toolName: tool, createdAt: '2026-10-15T10:00:01Z', toolLabel: label, toolCategory: category, durationMs, toolState });
  loadConversation(state, {
    id: 'c1', title: 't', pendingPlan: null, plans: [],
    messages: [
      { id: 'm1', role: 'user', text: 'how is shop?', toolName: null, createdAt: '2026-10-15T10:00:00Z' },
      read('m2', 'get_app', 'shop خوانده شد', 'خواندن اپ', 'app', 400, 'done'),
      read('m3', 'get_deployment', 'هنوز استقراری نیست', 'خواندن لاگ‌های استقرار', 'deployment', 2000, 'failed'),
      { id: 'm4', role: 'assistant', text: 'shop is fine.', toolName: null, createdAt: '2026-10-15T10:00:03Z' },
    ],
  });
  const rows = activityRows(state.items);
  assert.deepEqual(rows.map((r) => r.type), ['item', 'activity', 'item']);
  const view = blockView(rows[1].block, { fa: true });
  assert.deepEqual([view.tone, view.open, view.heading], ['failed', true, '۱ کار ناموفق، ۱ انجام شد']);
  assert.deepEqual(rows[1].block.items.map((i) => [toolLineText(i, true), toolLineDetail(i, true), toolLineMark(i), categoryIcon(i.category)]), [
    ['خواندن اپ', 'shop خوانده شد', 'done', 'AppWindow'],
    ['خواندن لاگ‌های استقرار', 'هنوز استقراری نیست', 'failed', 'Rocket'],
  ]);

  // The same conversation from a server before toolState: reads that ran, neutral, words still visible.
  const older = createTimeline();
  loadConversation(older, {
    id: 'c1', title: 't', pendingPlan: null,
    messages: [read('m2', 'get_app', 'shop خوانده شد', 'خواندن اپ', 'app', 400, undefined), read('m3', 'get_deployment', 'لاگ خوانده شد', 'خواندن لاگ‌های استقرار', 'deployment', 2000, undefined)],
  });
  const [block] = activityRows(older.items);
  assert.equal(blockView(block.block, { fa: true }).heading, '۲ کار اجرا شد · ۲٫۴ ثانیه');
  assert.deepEqual(block.block.items.map((i) => [toolLineText(i, true), toolLineDetail(i, true), toolLineMark(i)]), [
    ['خواندن اپ', 'shop خوانده شد', null], ['خواندن لاگ‌های استقرار', 'لاگ خوانده شد', null],
  ]);
});
