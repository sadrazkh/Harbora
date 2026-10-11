import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  createTimeline, loadConversation, addUserMessage, applyStreamEvent, applyPlanResult,
  markPlanRejected, settleTurn,
} from '../islands/assistantTimeline.js';

// The conversation as the pane draws it: one list of items fed by GET /assistant/conversations/{id}
// and by the stream's events. The rules worth pinning are the honest ones — a tool line is a check
// only when the server said "done", and a turn the stream abandoned leaves no spinner pretending.

const PLAN_ID = '0199d0c4-6a1e-7000-8000-0000000000aa';

function plan(status = 'pending') {
  return {
    id: PLAN_ID, status, expiresAt: '2026-10-11T10:15:00Z',
    steps: [{
      index: 0, tool: 'delete_database', kind: 'destructive', titleEn: 'Delete old-db', titleFa: 'حذف old-db',
      detailEn: null, detailFa: null, warningsEn: [], warningsFa: [], confirmationPhrase: 'old-db',
      autoApproved: false, rememberable: false,
    }],
  };
}

const kinds = (state) => state.items.map((i) => i.kind);

test('a loaded conversation lists its messages in order and puts the pending plan last', () => {
  const state = createTimeline();
  loadConversation(state, {
    id: 'c1', title: 'Shop database',
    messages: [
      { id: 'm1', role: 'user', text: 'delete old-db', toolName: null, createdAt: '2026-10-11T10:00:00Z' },
      { id: 'm2', role: 'assistant', text: null, toolName: null, createdAt: '2026-10-11T10:00:01Z' },
      { id: 'm3', role: 'tool', text: '{"name":"old-db"}', toolName: 'get_database', createdAt: '2026-10-11T10:00:02Z' },
      { id: 'm4', role: 'assistant', text: 'Here is the plan.', toolName: null, createdAt: '2026-10-11T10:00:03Z' },
    ],
    pendingPlan: plan(),
  });
  assert.equal(state.conversationId, 'c1');
  assert.equal(state.title, 'Shop database');
  // The assistant message with no text (it only carried tool calls) has nothing to show.
  assert.deepEqual(kinds(state), ['user', 'tool-history', 'assistant', 'plan']);
  assert.equal(state.items[1].toolName, 'get_database');
  assert.equal(state.items[3].plan.id, PLAN_ID);
  assert.deepEqual(state.items[3].form.phrases, { 0: '' });
});

test('loading a conversation replaces what was shown, it does not append to it', () => {
  const state = createTimeline();
  addUserMessage(state, 'hello');
  loadConversation(state, { id: 'c2', title: 't', messages: [], pendingPlan: null });
  assert.deepEqual(state.items, []);
});

test('started keeps the conversation id the server assigned', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'started', { conversationId: 'new-id' });
  assert.equal(state.conversationId, 'new-id');
});

test('a tool line goes from running to done in place, not as a second line', () => {
  const state = createTimeline();
  addUserMessage(state, 'how is shop?');
  applyStreamEvent(state, 'tool', { name: 'get_app', kind: 'read', state: 'running', summaryEn: null, summaryFa: null });
  assert.equal(state.items[1].state, 'running');
  applyStreamEvent(state, 'tool', { name: 'get_app', kind: 'read', state: 'done', summaryEn: 'Read shop', summaryFa: 'shop خوانده شد' });
  assert.deepEqual(kinds(state), ['user', 'tool']);
  assert.equal(state.items[1].state, 'done');
  assert.equal(state.items[1].summaryEn, 'Read shop');
});

test('the same tool called twice in a turn gets two lines', () => {
  const state = createTimeline();
  addUserMessage(state, 'compare');
  for (const s of ['running', 'done', 'running', 'failed']) {
    applyStreamEvent(state, 'tool', { name: 'get_app', kind: 'read', state: s, summaryEn: null, summaryFa: null });
  }
  assert.deepEqual(state.items.filter((i) => i.kind === 'tool').map((i) => i.state), ['done', 'failed']);
});

test('a running line from an earlier turn is never updated by a later one', () => {
  const state = createTimeline();
  addUserMessage(state, 'one');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  addUserMessage(state, 'two');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
  assert.deepEqual(state.items.filter((i) => i.kind === 'tool').map((i) => i.state), ['running', 'done']);
});

test('a tool state the contract does not name is not drawn as done', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'ok' });
  assert.equal(state.items[1].state, 'unknown');
});

test('message, plan, executed and error each add their own item', () => {
  const state = createTimeline();
  addUserMessage(state, 'go');
  applyStreamEvent(state, 'message', { id: 'm9', role: 'assistant', text: 'Planned.', toolName: null, createdAt: '2026-10-11T10:00:00Z' });
  applyStreamEvent(state, 'plan', plan());
  applyStreamEvent(state, 'executed', { planId: 'p2', status: 'executed', steps: [] });
  applyStreamEvent(state, 'error', { messageEn: 'Daily limit reached', messageFa: 'سقف روزانه پر شد' });
  assert.deepEqual(kinds(state), ['user', 'assistant', 'plan', 'result', 'error']);
  assert.equal(state.items[2].plan.id, PLAN_ID);
  assert.equal(state.items[3].result.planId, 'p2');
  assert.equal(state.items[4].messageFa, 'سقف روزانه پر شد');
});

test('a plan sent twice updates its card instead of drawing a second one', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  applyStreamEvent(state, 'plan', plan('expired'));
  assert.equal(state.items.filter((i) => i.kind === 'plan').length, 1);
  assert.equal(state.items[0].plan.status, 'expired');
});

test('done records the remaining requests, including "unlimited" as null', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'done', { conversationId: 'c1', remainingToday: 41 });
  assert.equal(state.remainingToday, 41);
  assert.equal(state.conversationId, 'c1');
  applyStreamEvent(state, 'done', { conversationId: 'c1', remainingToday: null });
  assert.equal(state.remainingToday, null);
});

test('an approve result marks the card with the server\'s status and adds the result under it', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  applyStreamEvent(state, 'message', { id: 'later', role: 'assistant', text: 'anything else?' });
  applyPlanResult(state, PLAN_ID, { planId: PLAN_ID, status: 'failed', steps: [{ index: 0, state: 'failed', errorEn: 'busy', errorFa: 'مشغول', links: [] }] });
  assert.deepEqual(kinds(state), ['plan', 'result', 'assistant']);
  assert.equal(state.items[0].plan.status, 'failed');
  assert.equal(state.items[1].result.status, 'failed');
  // The result card knows the steps' titles, which a result alone does not carry.
  assert.equal(state.items[1].plan.steps[0].titleEn, 'Delete old-db');
});

test('rejecting marks the card rejected', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  markPlanRejected(state, PLAN_ID);
  assert.equal(state.items[0].plan.status, 'rejected');
});

test('a turn the stream abandoned leaves no spinner behind', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  applyStreamEvent(state, 'tool', { name: 'get_usage', state: 'done' });
  settleTurn(state);
  assert.deepEqual(state.items.filter((i) => i.kind === 'tool').map((i) => i.state), ['interrupted', 'done']);
});

test('item keys are unique, so the list never confuses two lines', () => {
  const state = createTimeline();
  for (let i = 0; i < 5; i++) {
    addUserMessage(state, 'same');
    applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
    applyStreamEvent(state, 'error', { messageEn: 'e', messageFa: 'e' });
  }
  const keys = state.items.map((i) => i.key);
  assert.equal(new Set(keys).size, keys.length);
});
