import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  createTimeline, loadConversation, addUserMessage, applyStreamEvent, applyPlanResult,
  markPlanRejected, settleTurn, applyApproveRefusal, isThinking, withdrawUserMessage, restoreSuperseded,
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

test('done settles a tool line the server left running — never as done', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  applyStreamEvent(state, 'tool', { name: 'get_usage', state: 'running' });
  applyStreamEvent(state, 'tool', { name: 'get_usage', state: 'done' });
  applyStreamEvent(state, 'done', { conversationId: 'c1', remainingToday: 3 });
  assert.deepEqual(state.items.filter((i) => i.kind === 'tool').map((i) => [i.name, i.state]),
    [['get_app', 'interrupted'], ['get_usage', 'done']]);
});

test('settling twice changes nothing more, and an earlier turn is left as it was', () => {
  const state = createTimeline();
  addUserMessage(state, 'one');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
  addUserMessage(state, 'two');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  settleTurn(state);
  settleTurn(state);
  assert.deepEqual(state.items.filter((i) => i.kind === 'tool').map((i) => i.state), ['done', 'interrupted']);
});

test('a new message the server has received supersedes a card that was still waiting', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  addUserMessage(state, 'actually, do something else');
  applyStreamEvent(state, 'started', { conversationId: 'c1' });
  assert.equal(state.items[0].plan.status, 'expired');
  assert.equal(state.items[0].superseded, true);
});

test('a card whose Run is still in flight is not superseded: the server is executing it', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  state.items[0].busy = true;
  addUserMessage(state, 'and also this');
  applyStreamEvent(state, 'started', { conversationId: 'c1' });
  assert.equal(state.items[0].plan.status, 'pending');
  assert.equal(state.items[0].superseded, false);
});

test('superseding leaves finished cards and this turn\'s own new plan alone', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', { ...plan(), id: 'old-done', status: 'executed' });
  applyStreamEvent(state, 'plan', { ...plan(), id: 'old-rejected', status: 'rejected' });
  addUserMessage(state, 'next');
  applyStreamEvent(state, 'started', { conversationId: 'c1' });
  applyStreamEvent(state, 'plan', { ...plan(), id: 'new' });
  const byId = Object.fromEntries(state.items.filter((i) => i.kind === 'plan').map((i) => [i.plan.id, i]));
  assert.equal(byId['old-done'].plan.status, 'executed');
  assert.equal(byId['old-rejected'].plan.status, 'rejected');
  assert.equal(byId['old-done'].superseded, false);
  assert.equal(byId.new.plan.status, 'pending');
  assert.equal(byId.new.superseded, false);
});

test('a send that never reached the server leaves the waiting card as it was', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  addUserMessage(state, 'next');
  applyStreamEvent(state, 'error', { messageEn: 'Could not reach the panel.', messageFa: 'x' });
  assert.equal(state.items[0].plan.status, 'pending');
});

test('a refused approve puts the server\'s words and the named step on the card', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  applyApproveRefusal(state, PLAN_ID, { status: 400, messageEn: 'Type old-db to confirm.', messageFa: 'برای تأیید old-db را بنویسید.', stepIndex: 0, planStatus: null });
  const card = state.items[0];
  assert.deepEqual(card.error, { messageEn: 'Type old-db to confirm.', messageFa: 'برای تأیید old-db را بنویسید.' });
  assert.equal(card.errorStep, 0);
  assert.equal(card.plan.status, 'pending', 'still pending: the person can correct it and run again');
});

test('a refusal that says the plan expired is the only thing that expires a card', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  applyApproveRefusal(state, PLAN_ID, { status: 409, messageEn: 'This plan expired; nothing ran.', messageFa: 'x', stepIndex: null, planStatus: 'expired' });
  assert.equal(state.items[0].plan.status, 'expired');
  assert.equal(state.items[0].errorStep, null);
});

test('a result after a refusal clears the refusal from the card', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  applyApproveRefusal(state, PLAN_ID, { messageEn: 'no', messageFa: 'نه', stepIndex: 0 });
  applyPlanResult(state, PLAN_ID, { planId: PLAN_ID, status: 'executed', steps: [] });
  assert.equal(state.items[0].error, null);
  assert.equal(state.items[0].errorStep, null);
  assert.equal(state.items[0].resulted, true);
});

// ---- streamed text (delta) and the typing indicator -----------------------------------------------

test('deltas build one live bubble, in order, and the message replaces it with the whole text', () => {
  const state = createTimeline();
  addUserMessage(state, 'how is shop?');
  for (const piece of ['shop ', 'is ', 'running']) applyStreamEvent(state, 'delta', { text: piece });
  assert.equal(state.items.length, 2);
  assert.deepEqual({ kind: state.items[1].kind, text: state.items[1].text, live: state.items[1].live }, { kind: 'assistant', text: 'shop is running', live: true });
  const key = state.items[1].key;
  applyStreamEvent(state, 'message', { id: 'm1', role: 'assistant', text: 'shop is running with 2 replicas.' });
  assert.equal(state.items.length, 2, 'replaced, not drawn twice');
  assert.equal(state.items[1].text, 'shop is running with 2 replicas.');
  assert.equal(state.items[1].live, false);
  assert.equal(state.items[1].key, key, 'the same bubble, so it does not jump');
});

test('a delta carries text only: HTML in it stays characters for the formatter', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: '<img src=x onerror=alert(1)>' });
  assert.equal(state.items[1].text, '<img src=x onerror=alert(1)>');
});

test('empty or malformed deltas are ignored', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: '' });
  applyStreamEvent(state, 'delta', { text: 42 });
  applyStreamEvent(state, 'delta', {});
  assert.equal(state.items.length, 1);
});

test('text after a tool line starts a new bubble rather than joining the one before the tool', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'Let me look.' });
  applyStreamEvent(state, 'message', { id: 'm1', role: 'assistant', text: 'Let me look.' });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
  applyStreamEvent(state, 'delta', { text: 'It is fine.' });
  assert.deepEqual(state.items.map((i) => [i.kind, i.text ?? i.name]), [
    ['user', 'x'], ['assistant', 'Let me look.'], ['tool', 'get_app'], ['assistant', 'It is fine.'],
  ]);
});

test('a bubble the stream abandoned keeps what arrived and says the rest did not', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'shop is' });
  settleTurn(state);
  assert.equal(state.items[1].text, 'shop is');
  assert.equal(state.items[1].live, false);
  assert.equal(state.items[1].cut, true);
});

test('an error after streamed text marks that text as cut off, and a bubble of only spaces goes', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'half an ans' });
  applyStreamEvent(state, 'error', { messageEn: 'provider failed', messageFa: 'x' });
  assert.equal(state.items[1].cut, true);

  const blank = createTimeline();
  addUserMessage(blank, 'x');
  applyStreamEvent(blank, 'delta', { text: '  \n ' });
  applyStreamEvent(blank, 'tool', { name: 'get_app', state: 'running' });
  assert.deepEqual(blank.items.map((i) => i.kind), ['user', 'tool']);
});

test('a confirmed answer is not marked cut when the turn ends', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'done' });
  applyStreamEvent(state, 'message', { id: 'm', role: 'assistant', text: 'done.' });
  applyStreamEvent(state, 'done', { conversationId: 'c', remainingToday: 1 });
  assert.equal(state.items[1].cut, undefined);
});

test('the dots show only while a sent turn has nothing on screen, or a reloaded one is still answering', () => {
  const state = createTimeline();
  assert.equal(isThinking(state), false, 'idle');
  addUserMessage(state, 'x');
  state.streaming = true;
  assert.equal(isThinking(state), true, 'sent, nothing yet');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  assert.equal(isThinking(state), false, 'a running tool has its own spinner');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'failed' });
  assert.equal(isThinking(state), true, 'after a tool, until the next output');
  applyStreamEvent(state, 'delta', { text: 'a' });
  assert.equal(isThinking(state), false, 'text is arriving');
  state.streaming = false;
  assert.equal(isThinking(state), false);
  state.resuming = true;
  assert.equal(isThinking(state), true, 'a conversation opened mid-answer');
});

test('withdrawing a refused message removes exactly that bubble', () => {
  const state = createTimeline();
  addUserMessage(state, 'one');
  const key = addUserMessage(state, 'two');
  withdrawUserMessage(state, key);
  assert.deepEqual(state.items.map((i) => i.text), ['one']);
});

test('text written before a tool call, with no message yet, is kept when the tool line arrives', () => {
  // "On it." streamed as deltas, then the model calls a tool before any `message` for that text.
  const state = createTimeline();
  addUserMessage(state, 'restart shop');
  applyStreamEvent(state, 'delta', { text: 'On ' });
  applyStreamEvent(state, 'delta', { text: 'it.' });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  const bubble = state.items[1];
  assert.deepEqual({ kind: bubble.kind, text: bubble.text, live: bubble.live, cut: bubble.cut }, { kind: 'assistant', text: 'On it.', live: false, cut: undefined });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
  applyStreamEvent(state, 'delta', { text: 'shop is running.' });
  assert.deepEqual(state.items.map((i) => [i.kind, i.text ?? i.name, i.live === true]), [
    ['user', 'restart shop', false], ['assistant', 'On it.', false], ['tool', 'get_app', false], ['assistant', 'shop is running.', true],
  ], 'later deltas start a fresh live bubble');
});

test('a plan arriving while a bubble is live finalizes it with the text it has', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'On it.' });
  applyStreamEvent(state, 'plan', { id: 'p1', status: 'pending', expiresAt: '2026-10-11T10:15:00Z', steps: [] });
  assert.equal(state.items[1].text, 'On it.');
  assert.equal(state.items[1].live, false);
});

test('a message for text written before a tool, arriving after the tool line, replaces that bubble instead of doubling it', () => {
  const state = createTimeline();
  addUserMessage(state, 'restart shop');
  applyStreamEvent(state, 'delta', { text: 'On it.' });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  applyStreamEvent(state, 'message', { id: 'm1', role: 'assistant', text: 'On it.' });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
  assert.deepEqual(state.items.map((i) => [i.kind, i.text ?? i.name]), [['user', 'restart shop'], ['assistant', 'On it.'], ['tool', 'get_app']]);
  assert.equal(state.items[1].confirmed, true);
});

test('a late message for the earlier bubble does not touch the one being written now', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'Checking.' });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
  applyStreamEvent(state, 'delta', { text: 'shop is' });
  applyStreamEvent(state, 'message', { id: 'm1', role: 'assistant', text: 'Checking.' });
  const bubbles = state.items.filter((i) => i.kind === 'assistant');
  assert.deepEqual(bubbles.map((b) => [b.text, b.live]), [['Checking.', false], ['shop is', true]]);
  applyStreamEvent(state, 'message', { id: 'm2', role: 'assistant', text: 'shop is running.' });
  assert.deepEqual(state.items.filter((i) => i.kind === 'assistant').map((b) => [b.text, b.live]), [['Checking.', false], ['shop is running.', false]]);
});

test('a cut-off bubble is completed by its late message and loses the cut note', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'shop is' });
  applyStreamEvent(state, 'error', { messageEn: 'blip', messageFa: 'x' });
  assert.equal(state.items[1].cut, true);
  applyStreamEvent(state, 'message', { id: 'm', role: 'assistant', text: 'shop is running.' });
  assert.equal(state.items[1].text, 'shop is running.');
  assert.equal(state.items[1].cut, undefined);
  assert.equal(state.items.filter((i) => i.kind === 'assistant').length, 1);
});

test('a message whose text matches no streamed bubble is drawn as its own', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'On it.' });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
  applyStreamEvent(state, 'message', { id: 'm9', role: 'assistant', text: 'Something else entirely.' });
  assert.deepEqual(state.items.filter((i) => i.kind === 'assistant').map((b) => b.text), ['On it.', 'Something else entirely.']);
});

test('a turn that ends with a live bubble and no message keeps the text, not marked cut', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'All good.' });
  applyStreamEvent(state, 'done', { conversationId: 'c', remainingToday: 3 });
  assert.equal(state.items[1].text, 'All good.');
  assert.equal(state.items[1].live, false);
  assert.equal(state.items[1].cut, undefined);
});

test('a result card after streamed text keeps the text above it', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'Done that.' });
  applyStreamEvent(state, 'executed', { planId: 'p', status: 'executed', steps: [] });
  assert.deepEqual(state.items.map((i) => i.kind), ['user', 'assistant', 'result']);
  assert.equal(state.items[1].live, false);
});

test('a message completing the newer bubble is not taken by an older bubble that shares its start', () => {
  // The probe the review ran: "Let me check" → tool → "Let me check the logs: shop is running." → message.
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'Let me check' });
  applyStreamEvent(state, 'tool', { name: 'get_deployment', state: 'done' });
  applyStreamEvent(state, 'delta', { text: 'Let me check the logs: shop is running.' });
  applyStreamEvent(state, 'message', { id: 'm2', role: 'assistant', text: 'Let me check the logs: shop is running.' });
  assert.deepEqual(state.items.filter((i) => i.kind === 'assistant').map((b) => [b.text, b.live]), [
    ['Let me check', false], ['Let me check the logs: shop is running.', false],
  ]);
  // And the first bubble's own message, arriving late, still finds it.
  applyStreamEvent(state, 'message', { id: 'm1', role: 'assistant', text: 'Let me check' });
  assert.equal(state.items.filter((i) => i.kind === 'assistant').length, 2);
});

test('when only a prefix matches, the newest bubble that it starts is the one completed', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'delta', { text: 'shop' });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'done' });
  applyStreamEvent(state, 'delta', { text: 'shop is' });
  applyStreamEvent(state, 'message', { id: 'm', role: 'assistant', text: 'shop is running.' });
  assert.deepEqual(state.items.filter((i) => i.kind === 'assistant').map((b) => b.text), ['shop', 'shop is running.']);
});

// ---- final review --------------------------------------------------------------------------------

test('a plan refused at re-check is "not run", not "already ran", and the refused step is marked', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  applyApproveRefusal(state, PLAN_ID, {
    status: 409, code: 'revalidation_refused', planStatus: 'failed', stepIndex: 0,
    messageEn: 'Nothing ran: step 1 was refused when the plan was checked again.', messageFa: 'x',
  });
  const card = state.items[0];
  assert.equal(card.plan.status, 'failed');
  assert.equal(card.notRun, true);
  assert.equal(card.errorStep, 0);
});

test('"already decided" (not_pending, failed) is not mistaken for a refusal at re-check', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  applyApproveRefusal(state, PLAN_ID, { status: 409, code: 'not_pending', planStatus: 'failed', messageEn: 'This plan is already failed.', messageFa: 'x' });
  assert.equal(state.items[0].notRun, false);
});

test('a failed status from any other refusal code also means nothing ran here', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  applyApproveRefusal(state, PLAN_ID, { status: 409, code: 'something_new', planStatus: 'failed', messageEn: 'x', messageFa: 'x' });
  assert.equal(state.items[0].notRun, true);
});

test('a card superseded on started gets its real status back when the turn was refused at the gate', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  addUserMessage(state, 'next');
  applyStreamEvent(state, 'started', { conversationId: 'c1' });
  assert.equal(state.items[0].superseded, true);
  restoreSuperseded(state, { id: 'c1', messages: [], pendingPlan: plan() });
  assert.equal(state.items[0].superseded, false);
  assert.equal(state.items[0].plan.status, 'pending');
});

test('a superseded card the server did withdraw stays withdrawn', () => {
  const state = createTimeline();
  applyStreamEvent(state, 'plan', plan());
  addUserMessage(state, 'next');
  applyStreamEvent(state, 'started', { conversationId: 'c1' });
  restoreSuperseded(state, { id: 'c1', messages: [], pendingPlan: null });
  assert.equal(state.items[0].superseded, true);
  assert.equal(state.items[0].plan.status, 'expired');
});

// ---- round seven: people's words for tools, plans that stay, the stopped event ----------------------

test('a tool line keeps the label, category and duration its events bring, updated in place', () => {
  const state = createTimeline();
  addUserMessage(state, 'how is shop?');
  applyStreamEvent(state, 'tool', {
    name: 'get_app', kind: 'read', state: 'running', summaryEn: null, summaryFa: null,
    labelEn: 'Reading the app', labelFa: 'خواندن اپ', category: 'app', durationMs: null,
  });
  const line = state.items[1];
  assert.deepEqual([line.labelEn, line.labelFa, line.category, line.durationMs], ['Reading the app', 'خواندن اپ', 'app', null]);
  applyStreamEvent(state, 'tool', { name: 'get_app', kind: 'read', state: 'done', summaryEn: 'Read shop', summaryFa: 'shop خوانده شد', labelEn: 'Reading the app', labelFa: 'خواندن اپ', category: 'app', durationMs: 412 });
  assert.deepEqual(kinds(state), ['user', 'tool']);
  assert.deepEqual([line.state, line.durationMs, line.labelFa], ['done', 412, 'خواندن اپ']);
  // An outcome event that leaves a field out does not erase what the running one said.
  applyStreamEvent(state, 'tool', { name: 'get_usage', state: 'running', labelEn: 'Reading usage', category: 'usage' });
  applyStreamEvent(state, 'tool', { name: 'get_usage', state: 'failed', summaryEn: 'No metrics yet' });
  assert.deepEqual([state.items[2].labelEn, state.items[2].category, state.items[2].durationMs], ['Reading usage', 'usage', null]);
});

test('a tool event from an older server, with no label, still makes a line, labelled with nothing', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'tool', { name: 'get_app', kind: 'read', state: 'done', summaryEn: 'Read shop', summaryFa: null });
  const line = state.items[1];
  assert.deepEqual([line.labelEn, line.labelFa, line.category, line.durationMs], [null, null, null, null]);
  applyStreamEvent(state, 'tool', { name: 'get_usage', state: 'done', durationMs: -5 });
  applyStreamEvent(state, 'tool', { name: 'get_db', state: 'done', durationMs: '30' });
  assert.deepEqual([state.items[2].durationMs, state.items[3].durationMs], [null, null], 'not a duration');
});

test('a stored tool message keeps its label, category, duration and plan', () => {
  const state = createTimeline();
  loadConversation(state, {
    id: 'c1', title: 't', pendingPlan: null,
    messages: [
      { id: 'm1', role: 'tool', text: 'shop خوانده شد', toolName: 'get_app', createdAt: '2026-10-15T10:00:00Z', toolLabel: 'خواندن اپ', toolCategory: 'app', durationMs: 412, planId: null },
      { id: 'm2', role: 'tool', text: 'old', toolName: 'list_apps', createdAt: '2026-10-15T10:00:01Z' },
    ],
  });
  const [labelled, older] = state.items;
  assert.deepEqual({ ...labelled }, { key: 'msg-m1', kind: 'tool-history', toolName: 'get_app', text: 'shop خوانده شد', label: 'خواندن اپ', category: 'app', durationMs: 412, planId: null });
  assert.deepEqual([older.label, older.category, older.durationMs, older.planId], [null, null, null, null], 'an older server: no label');
});

const HISTORY = '0199d0c4-6a1e-7000-8000-0000000000b1';
const OTHER = '0199d0c4-6a1e-7000-8000-0000000000b2';
const msg = (id, role, text, extra = {}) => ({ id, role, text, toolName: null, createdAt: '2026-10-15T10:00:00Z', ...extra });
const decided = (id, status, { result = null, after = null } = {}) => ({ plan: { ...plan(status), id }, result, afterMessageId: after });
const ran = (id) => ({ planId: id, status: 'executed', steps: [{ index: 0, state: 'done', summaryEn: 'Deleted old-db', summaryFa: 'old-db حذف شد', errorEn: null, errorFa: null, links: [] }] });

test('a decided plan is drawn right after the message it names, with its result under it', () => {
  const state = createTimeline();
  loadConversation(state, {
    id: 'c1', title: 't', pendingPlan: null,
    messages: [msg('m1', 'user', 'delete old-db'), msg('m2', 'assistant', 'Here is the plan.'), msg('m3', 'user', 'thanks'), msg('m4', 'assistant', 'Anytime.')],
    plans: [decided(HISTORY, 'executed', { result: ran(HISTORY), after: 'm2' })],
  });
  assert.deepEqual(state.items.map((i) => i.key), ['msg-m1', 'msg-m2', `plan-${HISTORY}`, `result-${HISTORY}`, 'msg-m3', 'msg-m4']);
  const [card, outcome] = [state.items[2], state.items[3]];
  assert.equal(card.plan.status, 'executed', "the server's status");
  assert.equal(card.resulted, true);
  assert.equal(outcome.kind, 'result');
  assert.equal(outcome.result.planId, HISTORY);
  assert.equal(outcome.plan.steps[0].titleEn, 'Delete old-db', 'the result card knows the step titles');
});

test('a decided plan goes first when it names no message, and last when the message it names is not in the view', () => {
  const state = createTimeline();
  loadConversation(state, {
    id: 'c1', title: 't', pendingPlan: null,
    messages: [msg('m1', 'user', 'one'), msg('m2', 'assistant', 'two')],
    plans: [decided(HISTORY, 'rejected', { after: null }), decided(OTHER, 'expired', { after: 'older-than-the-view' })],
  });
  assert.deepEqual(state.items.map((i) => i.key), [`plan-${HISTORY}`, 'msg-m1', 'msg-m2', `plan-${OTHER}`]);
  assert.deepEqual(state.items.filter((i) => i.kind === 'plan').map((i) => [i.plan.status, i.resulted]), [['rejected', false], ['expired', false]], 'no result: none drawn');
});

test('decided plans keep their order, and one placed after a text-less message goes where that message was', () => {
  const state = createTimeline();
  loadConversation(state, {
    id: 'c1', title: 't', pendingPlan: null,
    messages: [msg('m1', 'user', 'go'), msg('m2', 'assistant', null), msg('m3', 'assistant', 'done')],
    plans: [decided(HISTORY, 'rejected', { after: 'm2' }), decided(OTHER, 'expired', { after: 'm2' })],
  });
  assert.deepEqual(state.items.map((i) => i.key), ['msg-m1', `plan-${HISTORY}`, `plan-${OTHER}`, 'msg-m3']);
});

test("the tool lines a drawn plan wrote are hidden; other lines, and the pending plan's, stay", () => {
  const PENDING = '0199d0c4-6a1e-7000-8000-0000000000b3';
  const state = createTimeline();
  loadConversation(state, {
    id: 'c1', title: 't',
    messages: [
      msg('m1', 'user', 'delete old-db'),
      msg('m2', 'tool', 'Proposed a plan', { toolName: 'propose_plan', planId: HISTORY }),
      msg('m3', 'tool', 'Read old-db', { toolName: 'get_database' }),
      msg('m4', 'assistant', 'Here is the plan.'),
      msg('m5', 'tool', 'Deleted old-db', { toolName: 'delete_database', planId: HISTORY }),
      msg('m6', 'user', 'and new-db'),
      msg('m7', 'tool', 'Proposed a plan', { toolName: 'propose_plan', planId: PENDING }),
    ],
    plans: [decided(HISTORY, 'executed', { result: ran(HISTORY), after: 'm4' })],
    pendingPlan: { ...plan(), id: PENDING },
  });
  assert.deepEqual(state.items.map((i) => i.key), ['msg-m1', 'msg-m3', 'msg-m4', `plan-${HISTORY}`, `result-${HISTORY}`, 'msg-m6', 'msg-m7', `plan-${PENDING}`]);
  const pending = state.items.at(-1);
  assert.equal(pending.plan.status, 'pending', 'the pending plan is untouched: last, and actionable');
  assert.equal(pending.resulted, false);
});

test('a decided plan keeps the status the server gave it, and the pending one is never drawn twice', () => {
  const state = createTimeline();
  loadConversation(state, {
    id: 'c1', title: 't', messages: [msg('m1', 'user', 'x')],
    plans: [decided(HISTORY, 'executing', { after: 'm1' }), decided(PLAN_ID, 'pending', { after: 'm1' })],
    pendingPlan: plan(),
  });
  assert.deepEqual(state.items.filter((i) => i.kind === 'plan').map((i) => [i.plan.id, i.plan.status]), [[HISTORY, 'executing'], [PLAN_ID, 'pending']]);
  assert.equal(state.items.at(-1).plan.id, PLAN_ID);
});

test('a re-read of the conversation keeps the same keys for its decided plans and results', () => {
  const view = {
    id: 'c1', title: 't', pendingPlan: null, messages: [msg('m1', 'user', 'x')],
    plans: [decided(HISTORY, 'executed', { result: ran(HISTORY), after: 'm1' })],
  };
  const state = createTimeline();
  loadConversation(state, view);
  const first = state.items.map((i) => i.key);
  loadConversation(state, view);
  assert.deepEqual(state.items.map((i) => i.key), first);
});

test('stopped closes the bubble without calling it cut, settles running tools as interrupted and adds its line', () => {
  const state = createTimeline();
  addUserMessage(state, 'list everything');
  applyStreamEvent(state, 'delta', { text: 'Looking at ' });
  applyStreamEvent(state, 'tool', { name: 'list_apps', state: 'running', labelEn: 'Listing the apps' });
  applyStreamEvent(state, 'delta', { text: 'shop and' });
  applyStreamEvent(state, 'stopped', { messageEn: 'Stopped. What was written so far is kept.', messageFa: 'متوقف شد. آنچه تا این‌جا نوشته شد نگه داشته شد.' });
  assert.deepEqual(kinds(state), ['user', 'assistant', 'tool', 'assistant', 'stopped']);
  const bubble = state.items[3];
  assert.deepEqual([bubble.text, bubble.live, bubble.cut], ['shop and', false, undefined], 'the person stopped it: not "the rest did not arrive"');
  assert.equal(state.items[2].state, 'interrupted');
  assert.deepEqual([state.items[4].messageEn, state.items[4].messageFa], ['Stopped. What was written so far is kept.', 'متوقف شد. آنچه تا این‌جا نوشته شد نگه داشته شد.']);
});

test('the done after stopped still settles the turn, and changes nothing the stop decided', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  state.streaming = true;
  applyStreamEvent(state, 'delta', { text: 'Half' });
  applyStreamEvent(state, 'message', { id: 'm1', role: 'assistant', text: 'Half' });
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  applyStreamEvent(state, 'stopped', { messageEn: 'Stopped.', messageFa: 'متوقف شد.' });
  assert.equal(isThinking(state), false, 'nothing is being worked on any more');
  applyStreamEvent(state, 'done', { conversationId: 'c1', remainingToday: 7 });
  settleTurn(state); // the session settles once more when the stream closes
  assert.deepEqual(kinds(state), ['user', 'assistant', 'tool', 'stopped']);
  assert.equal(state.items[1].cut, undefined);
  assert.equal(state.items[2].state, 'interrupted');
  assert.equal(state.remainingToday, 7);
  assert.equal(state.conversationId, 'c1');
});

test('stopped before anything was written adds only its line; a stop with no words still makes one', () => {
  const state = createTimeline();
  addUserMessage(state, 'x');
  applyStreamEvent(state, 'started', { conversationId: 'c1' });
  applyStreamEvent(state, 'stopped', {});
  assert.deepEqual(kinds(state), ['user', 'stopped']);
  assert.deepEqual([state.items[1].messageEn, state.items[1].messageFa], ['', '']);
});

test("stopping leaves an earlier turn's lines alone", () => {
  const state = createTimeline();
  addUserMessage(state, 'one');
  applyStreamEvent(state, 'tool', { name: 'get_app', state: 'running' });
  addUserMessage(state, 'two');
  applyStreamEvent(state, 'stopped', { messageEn: 'Stopped.', messageFa: 'x' });
  assert.equal(state.items[1].state, 'running', 'not this turn');
});
