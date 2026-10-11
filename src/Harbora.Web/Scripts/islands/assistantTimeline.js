// The conversation as the pane draws it: one ordered list of items, fed by
// `GET /assistant/conversations/{id}` (AgentConversationView) and by the message stream's events
// (AgentStreamEvents). Plain functions over a plain object, so `node --test` drives them without Vue;
// the island hands in a `reactive()` proxy and Vue tracks the same mutations.
//
// Item kinds: user, assistant, tool (a live `tool` event), tool-history (a stored tool message),
// plan, result, error, notice. An assistant item with `live: true` is the bubble being written by
// `delta` events; the `message` event that follows replaces its text with the whole answer.

import { createPlanForm } from './assistantPlan.js';

let sequence = 0;
const nextKey = (prefix) => `${prefix}-${++sequence}`;

export function createTimeline() {
  return {
    conversationId: null,
    title: '',
    items: [],
    // undefined: not known yet; null: unlimited (AgentStatusView.RemainingToday's own meaning).
    remainingToday: undefined,
    // A turn this pane is streaming right now.
    streaming: false,
    // A turn the server is still answering that this pane is not streaming — the person reloaded or
    // came back mid-answer (AgentConversationView.InProgress). The pane re-reads until it is over.
    resuming: false,
    // A conversation is being read.
    loading: false,
    // A gentle line above the composer — "still answering", "your last message is back in the box" —
    // that is information, not a failure.
    notice: null,
  };
}

function planItem(plan) {
  return {
    key: `plan-${plan.id}`, kind: 'plan', plan, form: createPlanForm(plan), mismatched: [], busy: false,
    // The server's refusal of an approve, and the step it named (AgentPlanStepView.Index), if any.
    error: null, errorStep: null,
    // Expired because the person sent a new message while it was waiting (see `started`).
    superseded: false,
    // A result card for this plan is on screen, directly under it.
    resulted: false,
  };
}

/** Running → done/failed in place; anything the contract does not name is not drawn as success. */
function toolState(state) {
  return state === 'running' || state === 'done' || state === 'failed' ? state : 'unknown';
}

function currentTurnStart(state) {
  for (let i = state.items.length - 1; i >= 0; i--) if (state.items[i].kind === 'user') return i;
  return -1;
}

/**
 * Replaces whatever is shown with a stored conversation. Each `loadConversation` is the server's
 * whole account of it, so items that existed only on screen (a live bubble, an error line) go.
 */
export function loadConversation(state, view) {
  state.conversationId = view?.id ?? null;
  state.title = view?.title ?? '';
  const items = [];
  for (const m of view?.messages ?? []) {
    if (m.role === 'tool') {
      // A stored tool message carries what the tool returned to the model, not whether it worked;
      // it is drawn neutral, never as a check.
      items.push({ key: `msg-${m.id}`, kind: 'tool-history', toolName: m.toolName ?? '', text: m.text ?? '' });
    } else if (m.text) {
      // An assistant message without text only carried tool calls — there is nothing to show.
      items.push({ key: `msg-${m.id}`, kind: m.role === 'user' ? 'user' : 'assistant', text: m.text });
    }
  }
  if (view?.pendingPlan) items.push(planItem(view.pendingPlan));
  state.items = items;
}

/** The person's message, drawn at once. Returns its key, so a refused send can take it back. */
export function addUserMessage(state, text) {
  const key = nextKey('user');
  state.items.push({ key, kind: 'user', text });
  return key;
}

/** A message the server refused to accept (a turn still in progress) — it was never sent. */
export function withdrawUserMessage(state, key) {
  const at = state.items.findIndex((i) => i.key === key);
  if (at >= 0) state.items.splice(at, 1);
}

/** A message that never reached the server: still shown, marked as not sent. */
export function markUnsent(state, key) {
  const item = state.items.find((i) => i.key === key);
  if (item) item.unsent = true;
}

function liveBubble(state) {
  const from = currentTurnStart(state);
  for (let i = state.items.length - 1; i > from; i--) {
    const item = state.items[i];
    if (item.kind === 'assistant' && item.live) return item;
  }
  return null;
}

/**
 * The bubble being written stops being live. Whitespace alone was never an answer and goes; anything
 * else stays as what arrived. `cut`: it ended without the `message` that confirms the whole text —
 * the stream broke, or the server failed after writing it — and the pane says the rest did not arrive.
 */
function closeLive(state, { cut }) {
  const item = liveBubble(state);
  if (!item) return;
  if (!item.text.trim()) {
    state.items.splice(state.items.indexOf(item), 1);
    return;
  }
  item.live = false;
  if (cut) item.cut = true;
}

/**
 * Whether the three-dot indicator shows: a turn has been sent and nothing is being shown for it yet —
 * after the person's message, after a tool has finished (until the next output) — or a conversation
 * opened mid-answer is still being answered. Not while text is being written (the text is the sign of
 * life) and not while a tool line is running (it has its own spinner).
 */
export function isThinking(state) {
  if (state.resuming) return true;
  if (!state.streaming) return false;
  const from = currentTurnStart(state);
  const last = state.items[state.items.length - 1];
  if (!last || state.items.length - 1 <= from) return true;
  if (last.kind === 'tool') return last.state === 'done' || last.state === 'failed';
  return false;
}

export function applyStreamEvent(state, name, data) {
  data = data ?? {};
  switch (name) {
    case 'started': {
      if (data.conversationId) state.conversationId = data.conversationId;
      // The server has the new message. It expires a plan that was still waiting and tells the model
      // that plan did not run, so a card left offering Run would be offering something that no
      // longer exists. Only plans from before this turn: this turn's own plan has not arrived yet.
      // A card whose Run or Cancel is still in flight is left alone: the server claimed it before this
      // message arrived, so it is running (or being rejected), and "nothing ran" would be false.
      const from = currentTurnStart(state);
      for (let i = 0; i < from; i++) {
        const item = state.items[i];
        if (item.kind === 'plan' && item.plan.status === 'pending' && !item.busy) {
          item.plan = { ...item.plan, status: 'expired' };
          item.superseded = true;
        }
      }
      return;
    }

    case 'delta': {
      // Purely for display: the next piece of the model's text, appended to the bubble being written.
      // Rendered as it arrives — no per-character animation holding real text back.
      if (typeof data.text !== 'string' || data.text === '') return;
      const last = state.items[state.items.length - 1];
      if (last && last.kind === 'assistant' && last.live) last.text += data.text;
      else state.items.push({ key: nextKey('live'), kind: 'assistant', text: data.text, live: true });
      return;
    }

    case 'tool': {
      closeLive(state, { cut: false });
      const next = toolState(data.state);
      // The running line for this tool in this turn becomes its outcome; a second call of the same
      // tool, or one in a later turn, gets a line of its own.
      const from = currentTurnStart(state);
      for (let i = state.items.length - 1; i > from; i--) {
        const item = state.items[i];
        if (item.kind === 'tool' && item.name === data.name && item.state === 'running') {
          item.state = next;
          if (data.summaryEn != null) item.summaryEn = data.summaryEn;
          if (data.summaryFa != null) item.summaryFa = data.summaryFa;
          return;
        }
      }
      state.items.push({
        key: nextKey('tool'), kind: 'tool', name: data.name ?? '', toolKind: data.kind ?? '',
        state: next, summaryEn: data.summaryEn ?? null, summaryFa: data.summaryFa ?? null,
      });
      return;
    }

    case 'message': {
      if (!data.text) return;
      // The whole text, which counts: it replaces what the deltas built, rather than being drawn twice.
      const live = data.role === 'user' ? null : liveBubble(state);
      if (live) {
        live.text = data.text;
        live.live = false;
        return;
      }
      state.items.push({ key: data.id ? `msg-${data.id}` : nextKey('msg'), kind: data.role === 'user' ? 'user' : 'assistant', text: data.text });
      return;
    }

    case 'plan': {
      closeLive(state, { cut: false });
      if (!data.id) return;
      const existing = state.items.find((i) => i.kind === 'plan' && i.plan.id === data.id);
      if (existing) existing.plan = data;
      else state.items.push(planItem(data));
      return;
    }

    case 'executed':
      closeLive(state, { cut: false });
      state.items.push({ key: nextKey('result'), kind: 'result', result: data, plan: null });
      return;

    case 'error':
      closeLive(state, { cut: true });
      state.items.push({ key: nextKey('error'), kind: 'error', messageEn: data.messageEn ?? '', messageFa: data.messageFa ?? '' });
      return;

    case 'done':
      if (data.conversationId) state.conversationId = data.conversationId;
      if ('remainingToday' in data) state.remainingToday = data.remainingToday;
      // The turn is over. A tool line the server never finished must not spin for ever.
      settleTurn(state);
      return;

    default:
      // Unknown events never reach here (parseAssistantFrame drops them); ignore regardless.
      return;
  }
}

/**
 * The response to approve: the card takes the server's status, and the result goes directly under
 * it — carrying the plan, because a step result has an index but no title.
 */
export function applyPlanResult(state, planId, result) {
  const at = state.items.findIndex((i) => i.kind === 'plan' && i.plan.id === planId);
  const item = { key: nextKey('result'), kind: 'result', result, plan: at >= 0 ? state.items[at].plan : null };
  if (at >= 0) {
    const card = state.items[at];
    card.plan = { ...card.plan, status: result?.status ?? card.plan.status };
    card.resulted = true;
    card.error = null;
    card.errorStep = null;
    item.plan = card.plan;
    state.items.splice(at + 1, 0, item);
  } else {
    state.items.push(item);
  }
}

/**
 * The server refused an approve (AssistantRequestError). Its words go on the card; the step it named
 * is marked; and if it said what the plan is now — "expired", "executed" — the card believes it.
 * This is the only way a card becomes expired on its own: the pane never decides that by its clock.
 */
export function applyApproveRefusal(state, planId, error) {
  const item = state.items.find((i) => i.kind === 'plan' && i.plan.id === planId);
  if (!item) return;
  item.error = { messageEn: error?.messageEn ?? '', messageFa: error?.messageFa ?? error?.messageEn ?? '' };
  item.errorStep = Number.isInteger(error?.stepIndex) ? error.stepIndex : null;
  if (typeof error?.planStatus === 'string' && error.planStatus) item.plan = { ...item.plan, status: error.planStatus };
}

export function markPlanRejected(state, planId) {
  const item = state.items.find((i) => i.kind === 'plan' && i.plan.id === planId);
  if (item) item.plan = { ...item.plan, status: 'rejected' };
}

/**
 * The turn is over — `done`, an error, a cut connection, a stop — and a tool line is still running:
 * the server never sent its outcome. The spinner stops and the line says no result arrived. It never
 * becomes a check; nobody said it worked. Safe to call more than once.
 */
export function settleTurn(state) {
  closeLive(state, { cut: true });
  const from = currentTurnStart(state);
  for (let i = from + 1; i < state.items.length; i++) {
    const item = state.items[i];
    if (item.kind === 'tool' && item.state === 'running') item.state = 'interrupted';
  }
}
