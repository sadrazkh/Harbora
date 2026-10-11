// The conversation as the pane draws it: one ordered list of items, fed by
// `GET /assistant/conversations/{id}` (AgentConversationView) and by the message stream's events
// (AgentStreamEvents). Plain functions over a plain object, so `node --test` drives them without Vue;
// the island hands in a `reactive()` proxy and Vue tracks the same mutations.
//
// Item kinds: user, assistant, tool (a live `tool` event), tool-history (a stored tool message),
// plan, result, error.

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
  };
}

function planItem(plan) {
  return { key: `plan-${plan.id}`, kind: 'plan', plan, form: createPlanForm(plan), mismatched: [], busy: false, error: null };
}

/** Running → done/failed in place; anything the contract does not name is not drawn as success. */
function toolState(state) {
  return state === 'running' || state === 'done' || state === 'failed' ? state : 'unknown';
}

function currentTurnStart(state) {
  for (let i = state.items.length - 1; i >= 0; i--) if (state.items[i].kind === 'user') return i;
  return -1;
}

/** Replaces whatever is shown with a stored conversation. */
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

export function addUserMessage(state, text) {
  state.items.push({ key: nextKey('user'), kind: 'user', text });
}

export function applyStreamEvent(state, name, data) {
  data = data ?? {};
  switch (name) {
    case 'started':
      if (data.conversationId) state.conversationId = data.conversationId;
      return;

    case 'tool': {
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

    case 'message':
      if (data.text) {
        state.items.push({ key: data.id ? `msg-${data.id}` : nextKey('msg'), kind: data.role === 'user' ? 'user' : 'assistant', text: data.text });
      }
      return;

    case 'plan': {
      if (!data.id) return;
      const existing = state.items.find((i) => i.kind === 'plan' && i.plan.id === data.id);
      if (existing) existing.plan = data;
      else state.items.push(planItem(data));
      return;
    }

    case 'executed':
      state.items.push({ key: nextKey('result'), kind: 'result', result: data, plan: null });
      return;

    case 'error':
      state.items.push({ key: nextKey('error'), kind: 'error', messageEn: data.messageEn ?? '', messageFa: data.messageFa ?? '' });
      return;

    case 'done':
      if (data.conversationId) state.conversationId = data.conversationId;
      if ('remainingToday' in data) state.remainingToday = data.remainingToday;
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
    state.items[at].plan = { ...state.items[at].plan, status: result?.status ?? state.items[at].plan.status };
    item.plan = state.items[at].plan;
    state.items.splice(at + 1, 0, item);
  } else {
    state.items.push(item);
  }
}

export function markPlanRejected(state, planId) {
  const item = state.items.find((i) => i.kind === 'plan' && i.plan.id === planId);
  if (item) item.plan = { ...item.plan, status: 'rejected' };
}

/**
 * The stream ended without finishing a tool's line (it was cut off, or the server never sent the
 * outcome). The spinner stops and the line says it has no result — it does not become a check.
 */
export function settleTurn(state) {
  const from = currentTurnStart(state);
  for (let i = from + 1; i < state.items.length; i++) {
    const item = state.items[i];
    if (item.kind === 'tool' && item.state === 'running') item.state = 'interrupted';
  }
}
