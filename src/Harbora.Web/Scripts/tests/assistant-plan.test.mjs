import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  kindLabel, needsPhrase, phraseMatches, isRememberable, rememberableTools,
  createPlanForm, checkPlanForm, buildApproveBody, planIsExpired, planIsActionable,
  stepResultState, summarizeResult, planHeading, resultNote,
} from '../islands/assistantPlan.js';

// Shapes exactly as AgentViews.cs serialises them (camelCase): AgentPlanView, AgentPlanStepView,
// AgentPlanResultView. A renamed property there is a broken card here, so these fixtures are written
// field by field rather than built by a helper that would hide a missing one.
function step(index, tool, kind, extra = {}) {
  return {
    index, tool, kind,
    titleEn: `Step ${index}`, titleFa: `گام ${index}`,
    detailEn: null, detailFa: null, warningsEn: [], warningsFa: [],
    confirmationPhrase: null, autoApproved: false, rememberable: false,
    ...extra,
  };
}

const plan = {
  id: '0199d0c4-6a1e-7000-8000-000000000001',
  status: 'pending',
  expiresAt: '2026-10-11T10:15:00Z',
  steps: [
    step(0, 'create_database', 'safe', { rememberable: true }),
    step(1, 'attach_database', 'safe', { rememberable: true }),
    step(2, 'create_database', 'safe', { rememberable: true }),
    step(3, 'stop_app', 'dangerous'),
    step(4, 'delete_database', 'destructive', { confirmationPhrase: 'old-db' }),
    step(5, 'deploy', 'safe', { rememberable: true, autoApproved: true }),
  ],
};

test('kind badges are named in both languages, and an unknown kind is shown as itself', () => {
  assert.equal(kindLabel('safe', false), 'Safe');
  assert.equal(kindLabel('safe', true), 'کم‌خطر');
  assert.equal(kindLabel('dangerous', false), 'Dangerous');
  assert.equal(kindLabel('dangerous', true), 'پرخطر');
  assert.equal(kindLabel('destructive', false), 'Destructive');
  assert.equal(kindLabel('destructive', true), 'حذف‌کننده');
  assert.equal(kindLabel('surprising', true), 'surprising');
});

test('only destructive steps ask for a typed phrase', () => {
  assert.deepEqual(plan.steps.filter(needsPhrase).map((s) => s.index), [4]);
  // A phrase on a non-destructive step is not a reason to ask: the server only checks destructive ones.
  assert.equal(needsPhrase(step(9, 'stop_app', 'dangerous', { confirmationPhrase: 'x' })), false);
});

test('the phrase must match exactly, ignoring only surrounding whitespace', () => {
  const s = plan.steps[4];
  assert.equal(phraseMatches(s, 'old-db'), true);
  assert.equal(phraseMatches(s, '  old-db \n'), true);
  assert.equal(phraseMatches(s, 'Old-db'), false, 'case-sensitive, like the server');
  assert.equal(phraseMatches(s, 'old-d'), false);
  assert.equal(phraseMatches(s, ''), false);
  assert.equal(phraseMatches(s, undefined), false);
});

test('a Persian phrase matches itself', () => {
  const s = step(0, 'delete_project', 'destructive', { confirmationPhrase: 'فروشگاه' });
  assert.equal(phraseMatches(s, 'فروشگاه'), true);
  assert.equal(phraseMatches(s, 'فروشگاه‌ها'), false);
});

test('a destructive step the server sent without a phrase can never be confirmed', () => {
  // Fails closed: an empty field equal to an empty phrase must not read as consent to a delete.
  const s = step(0, 'delete_app', 'destructive', { confirmationPhrase: null });
  assert.equal(phraseMatches(s, ''), false);
  assert.equal(phraseMatches(s, 'anything'), false);
  const blank = step(1, 'delete_app', 'destructive', { confirmationPhrase: '   ' });
  assert.equal(phraseMatches(blank, '   '), false);
});

test('"don\'t ask again" is offered for rememberable safe steps that are not already remembered', () => {
  assert.deepEqual(plan.steps.filter(isRememberable).map((s) => s.index), [0, 1, 2]);
  // Defence in depth: a dangerous step can never be remembered, whatever the flag says.
  assert.equal(isRememberable(step(0, 'stop_app', 'dangerous', { rememberable: true })), false);
  assert.deepEqual(rememberableTools(plan), ['attach_database', 'create_database']);
});

test('a fresh form has an empty phrase per destructive step and nothing ticked', () => {
  const form = createPlanForm(plan);
  assert.deepEqual(form.phrases, { 4: '' });
  assert.deepEqual(form.remember, { attach_database: false, create_database: false });
});

test('checking the form names exactly the steps whose phrase does not match', () => {
  const twoDeletes = { ...plan, steps: [...plan.steps, step(6, 'delete_app', 'destructive', { confirmationPhrase: 'shop' })] };
  const form = createPlanForm(twoDeletes);
  assert.deepEqual(checkPlanForm(twoDeletes, form), { ok: false, mismatched: [4, 6] });
  form.phrases[4] = 'old-db';
  assert.deepEqual(checkPlanForm(twoDeletes, form), { ok: false, mismatched: [6] });
  form.phrases[6] = 'shop ';
  assert.deepEqual(checkPlanForm(twoDeletes, form), { ok: true, mismatched: [] });
});

test('the approve body carries the typed phrases by step index and the ticked tools', () => {
  const form = createPlanForm(plan);
  form.phrases[4] = ' old-db ';
  form.remember.create_database = true;
  assert.deepEqual(buildApproveBody(plan, form), {
    confirmations: { 4: 'old-db' },
    rememberTools: ['create_database'],
  });
});

test('the approve body never asks to remember a tool the card did not offer', () => {
  const form = createPlanForm(plan);
  form.phrases[4] = 'old-db';
  // Whatever ended up in the form — a stale entry, a crafted one — only offered tools are sent.
  form.remember.stop_app = true;
  form.remember.deploy = true;
  form.remember.attach_database = true;
  assert.deepEqual(buildApproveBody(plan, form).rememberTools, ['attach_database']);
});

test('a plan with nothing to confirm sends empty collections, which the server binds fine', () => {
  const simple = { ...plan, steps: [step(0, 'stop_app', 'dangerous')] };
  assert.deepEqual(buildApproveBody(simple, createPlanForm(simple)), { confirmations: {}, rememberTools: [] });
});

test('the browser clock never expires a plan — only the server status does', () => {
  // expiresAt is in the past by any clock, and the card still offers Run: the server decides, and
  // refuses a late approve with its own words (see applyApproveRefusal in the timeline tests).
  const stale = { ...plan, expiresAt: '2000-01-01T00:00:00Z' };
  assert.equal(planIsExpired(stale), false);
  assert.equal(planIsActionable(stale), true);
  assert.equal(planIsExpired({ ...plan, status: 'expired' }), true);
  // And a far-future expiry does not keep an expired plan alive.
  assert.equal(planIsActionable({ ...plan, status: 'expired', expiresAt: '3000-01-01T00:00:00Z' }), false);
});

test('only a pending plan can be run or cancelled', () => {
  for (const status of ['executing', 'executed', 'failed', 'rejected', 'expired']) {
    assert.equal(planIsActionable({ ...plan, status }), false, status);
  }
});

test('a step is done only when the result says exactly "done"', () => {
  assert.equal(stepResultState({ state: 'done' }), 'done');
  assert.equal(stepResultState({ state: 'failed' }), 'failed');
  assert.equal(stepResultState({ state: 'skipped' }), 'skipped');
  // Anything else — a new state, a typo, nothing — is never drawn as success.
  assert.equal(stepResultState({ state: 'Done' }), 'unknown');
  assert.equal(stepResultState({ state: 'ok' }), 'unknown');
  assert.equal(stepResultState({}), 'unknown');
});

test('a result summary counts each state and calls it complete only when every step is done', () => {
  const result = {
    planId: plan.id, status: 'failed',
    steps: [
      { index: 0, state: 'done', summaryEn: 'Created', summaryFa: 'ساخته شد', errorEn: null, errorFa: null, links: [] },
      { index: 1, state: 'failed', summaryEn: null, summaryFa: null, errorEn: 'No such app', errorFa: 'چنین اپی نیست', links: [] },
      { index: 2, state: 'skipped', summaryEn: null, summaryFa: null, errorEn: null, errorFa: null, links: [] },
    ],
  };
  assert.deepEqual(summarizeResult(result), { total: 3, done: 1, failed: 1, skipped: 1, unknown: 0, allDone: false });
  const allDone = { ...result, status: 'executed', steps: [result.steps[0]] };
  assert.equal(summarizeResult(allDone).allDone, true);
  assert.equal(summarizeResult({ ...result, steps: [] }).allDone, false, 'nothing ran is not "all done"');
});

test('a card refused at re-check says nothing ran; one decided elsewhere says it already ran', () => {
  const failed = { ...plan, status: 'failed' };
  assert.equal(planHeading({ plan: failed, resulted: false, notRun: true }, false), 'Not run — a step was refused when it was checked again');
  assert.equal(planHeading({ plan: failed, resulted: false, notRun: true }, true), 'اجرا نشد — یکی از گام‌ها هنگام بررسی دوباره رد شد');
  assert.equal(planHeading({ plan: failed, resulted: false, notRun: false }, false), 'This plan has already run and stopped');
  assert.equal(planHeading({ plan: failed, resulted: true, notRun: true }, false), 'Plan stopped — see the result below');
  assert.equal(planHeading({ plan: { ...plan, status: 'expired' }, superseded: true }, false), 'Not run — you sent a new message');
});

test('a skipped step shows the server\'s own reason, and the generic words only when there is none', () => {
  const skipped = { index: 2, state: 'skipped', summaryEn: 'Not run: another step was refused when the plan was re-checked.', summaryFa: 'اجرا نشد: مرحلهٔ دیگری هنگام بررسی دوباره رد شد.', errorEn: null, errorFa: null, links: [] };
  assert.equal(resultNote(skipped, false), 'Not run: another step was refused when the plan was re-checked.');
  assert.equal(resultNote(skipped, true), 'اجرا نشد: مرحلهٔ دیگری هنگام بررسی دوباره رد شد.');
  assert.match(resultNote({ ...skipped, summaryEn: null, summaryFa: null }, false), /earlier step failed/);
  assert.equal(resultNote({ index: 0, state: 'failed', errorEn: null, errorFa: null }, false), 'Failed.');
  assert.match(resultNote({ index: 0, state: 'weird' }, false), /did not say/);
});
