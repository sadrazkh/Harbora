// The conversation as the pane draws it: one ordered list of items, fed by
// `GET /assistant/conversations/{id}` (AgentConversationView) and by the message stream's events
// (AgentStreamEvents). Plain functions over a plain object, so `node --test` drives them without Vue;
// the island hands in a `reactive()` proxy and Vue tracks the same mutations.
//
// Item kinds: user, assistant, tool (a live `tool` event), tool-history (a stored tool message),
// plan, result, error, stopped, notice. An assistant item with `live: true` is the bubble being written
// by `delta` events; the `message` event that follows replaces its text with the whole answer.
//
// Tool items carry the tool's name for people and its icon group (AgentToolLabels): `tool` keeps
// labelEn/labelFa, category and durationMs from its events; `tool-history` keeps label, category,
// durationMs and planId from the stored message. assistantActivity.js turns them into what is drawn.

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
    // that is information, not a failure. `{ kind, messageEn, messageFa }`.
    notice: null,
    // The remembered conversation could not be read (not "gone" — a failure): `{ conversationId,
    // messageEn, messageFa }`. The pane offers to try again and does not pretend to be a fresh
    // conversation meanwhile, or the next message would start a new one and orphan the old.
    loadError: null,
    // When the thinking line appeared (a Date.now() value), for its "12 s"; kept by trackThinking in
    // assistantActivity.js, null while it is not showing.
    thinkingSince: null,
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

const duration = (ms) => (typeof ms === 'number' && Number.isFinite(ms) && ms >= 0 ? ms : null);

/** The labels, icon group and duration an event brings; what it leaves out stays as the line had it. */
function describeTool(item, data) {
  if (data.labelEn != null) item.labelEn = data.labelEn;
  if (data.labelFa != null) item.labelFa = data.labelFa;
  if (data.category != null) item.category = data.category;
  if (duration(data.durationMs) !== null) item.durationMs = data.durationMs;
}

/**
 * A decided plan from the conversation view (AgentPlanHistoryView): its card, with the server's status
 * — so it is not actionable unless that says pending — and its result card directly under it when it
 * ran. Keys are the plan's id, so a re-read of the conversation keeps the same cards.
 *
 * "failed" with no result is a plan refused when it was checked again at approval: nothing ran
 * (AgentPlanHistoryView.Result), and its card must not say "already ran and stopped". Its reason is in
 * the lines its execution wrote, which loadConversation puts on the card as `notes`.
 */
function decidedPlanItems(entry) {
  const card = planItem(entry.plan);
  if (!entry.result) {
    if (entry.plan.status === 'failed') {
      card.notRun = true;
      card.notes = [];
    }
    return [card];
  }
  card.resulted = true;
  return [card, { key: `result-${entry.plan.id}`, kind: 'result', result: entry.result, plan: entry.plan }];
}

function currentTurnStart(state) {
  for (let i = state.items.length - 1; i >= 0; i--) if (state.items[i].kind === 'user') return i;
  return -1;
}

/**
 * Replaces whatever is shown with a stored conversation. Each `loadConversation` is the server's
 * whole account of it, so items that existed only on screen (a live bubble, an error line) go.
 *
 * Decided plans (`view.plans`, oldest first) stay where they were proposed: each card goes right after
 * the message its `afterMessageId` names — at the start when that is null, at the end when the id is not
 * among the messages (older than the ones the view returns) — followed by its result. The anchor may be
 * a line that is not drawn (a text-less message, or the plan's own proposal line, hidden below): the
 * card still goes where it would have been.
 *
 * The tool lines a plan wrote (its proposal, its execution) go only when its result card is drawn,
 * which says the same thing. A plan without a result keeps them — and one refused when it was checked
 * again gets the lines written after its card (its execution's, the only place its reason is written)
 * on the card itself as `notes`, so the reason is in view rather than inside a folded block. The pending
 * plan is last, as before.
 */
export function loadConversation(state, view) {
  state.conversationId = view?.id ?? null;
  state.title = view?.title ?? '';
  const messages = view?.messages ?? [];
  const pendingId = view?.pendingPlan?.id ?? null;
  // The pending plan is never in `plans` as well (AgentConversationView); if it were, it is drawn once.
  const decided = (view?.plans ?? []).filter((p) => p?.plan?.id && p.plan.id !== pendingId);
  const reported = new Set(decided.filter((p) => p.result).map((p) => p.plan.id));
  const ids = new Set(messages.map((m) => m.id));
  const leading = [];
  const trailing = [];
  const after = new Map();
  for (const entry of decided) {
    const at = entry.afterMessageId ?? null;
    if (at === null) leading.push(entry);
    else if (!ids.has(at)) trailing.push(entry);
    else after.set(at, [...(after.get(at) ?? []), entry]);
  }

  const items = [];
  const placed = new Map(); // plan id → its card, once drawn
  const place = (entries) => {
    for (const entry of entries) {
      const drawn = decidedPlanItems(entry);
      placed.set(entry.plan.id, drawn[0]);
      items.push(...drawn);
    }
  };
  place(leading);
  for (const m of messages) {
    if (m.role === 'tool') {
      // A stored tool message carries what the tool returned to the model, not whether it worked;
      // it is drawn neutral, never as a check. A refused plan's line after its card goes on the card; a
      // line of a plan whose result card is drawn goes, since that card says the same.
      const card = m.planId ? placed.get(m.planId) : null;
      if (card?.notRun && m.text) {
        card.notes.push(m.text);
      } else if (!(m.planId && reported.has(m.planId))) {
        items.push({
          key: `msg-${m.id}`, kind: 'tool-history', toolName: m.toolName ?? '', text: m.text ?? '',
          label: m.toolLabel ?? null, category: m.toolCategory ?? null, durationMs: duration(m.durationMs), planId: m.planId ?? null,
        });
      }
    } else if (m.text) {
      // An assistant message without text only carried tool calls — there is nothing to show.
      items.push({ key: `msg-${m.id}`, kind: m.role === 'user' ? 'user' : 'assistant', text: m.text });
    }
    // After the message whether or not it was drawn: a card placed after a text-less message, or after
    // its own hidden proposal line, goes where that message would have been.
    place(after.get(m.id) ?? []);
  }
  place(trailing);
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

/**
 * A message that did not reach the server (`true`), or that may not have (`'unsure'`: the
 * conversation could not be read back to check): still shown, marked accordingly.
 */
export function markUnsent(state, key, how = true) {
  const item = state.items.find((i) => i.key === key);
  if (item) item.unsent = how;
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
 * The unconfirmed bubble of this turn, written by deltas, that this message completes. Newest first —
 * the live bubble, if any, is the newest — and an exact match before a prefix: the model may write
 * "Let me check", call a tool, then write "Let me check the logs: …", and the message for the second
 * bubble starts with the first bubble's text without being its message. A prefix match covers a
 * bubble cut short before its message arrived.
 */
function streamedBubbleFor(state, text) {
  const whole = String(text ?? '').trim();
  const from = currentTurnStart(state);
  const candidates = [];
  for (let i = state.items.length - 1; i > from; i--) {
    const item = state.items[i];
    if (item.kind === 'assistant' && item.streamed && !item.confirmed && item.text.trim() !== '') candidates.push(item);
  }
  return candidates.find((c) => c.text.trim() === whole)
    ?? candidates.find((c) => whole.startsWith(c.text.trim()))
    ?? null;
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
 * life) and not while a tool line is running (that is something else being shown).
 *
 * This is "Thinking…". The line the pane draws beside the dots (assistantActivity.js, thinkingLine)
 * also shows for a running tool, named by its label — and that tool's own line then does not spin.
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
      // `streamed`: written by deltas, so its direction is held steady (first strong letter) and the
      // `message` that confirms it — even one arriving after a tool line — replaces it, not doubles it.
      else state.items.push({ key: nextKey('live'), kind: 'assistant', text: data.text, live: true, streamed: true, confirmed: false });
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
          describeTool(item, data);
          return;
        }
      }
      state.items.push({
        key: nextKey('tool'), kind: 'tool', name: data.name ?? '', toolKind: data.kind ?? '',
        state: next, summaryEn: data.summaryEn ?? null, summaryFa: data.summaryFa ?? null,
        labelEn: data.labelEn ?? null, labelFa: data.labelFa ?? null, category: data.category ?? null,
        durationMs: duration(data.durationMs),
      });
      return;
    }

    case 'message': {
      if (!data.text) return;
      // The whole text, which counts: it replaces what the deltas built, rather than being drawn twice.
      // That bubble may still be live, or already finalized — the model wrote "On it." and called a
      // tool, and the server sent this `message` after the `tool` line. The bubble whose text this is
      // (the same, or the start of it) is the one replaced; otherwise the one still being written.
      const target = data.role === 'user' ? null : (streamedBubbleFor(state, data.text) ?? liveBubble(state));
      if (target) {
        target.text = data.text;
        target.live = false;
        target.confirmed = true;
        delete target.cut;
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

    case 'stopped': {
      // The person pressed Stop and the turn ended there — not a failure. The bubble being written keeps
      // what it has and is not marked cut (the server saved that text, in the `message` just before);
      // a tool still running gets no outcome and says so; and one muted line says it was stopped, in the
      // server's words. `done` still follows and settles the rest.
      closeLive(state, { cut: false });
      const from = currentTurnStart(state);
      for (let i = from + 1; i < state.items.length; i++) {
        const item = state.items[i];
        if (item.kind === 'tool' && item.state === 'running') item.state = 'interrupted';
      }
      state.items.push({ key: nextKey('stopped'), kind: 'stopped', messageEn: data.messageEn ?? '', messageFa: data.messageFa ?? '' });
      return;
    }

    case 'done':
      if (data.conversationId) state.conversationId = data.conversationId;
      if ('remainingToday' in data) state.remainingToday = data.remainingToday;
      // The turn ended normally: a bubble still live (its `message` never came) keeps what it has and
      // stops being live — not marked cut, since nothing says the turn broke. Then a tool line the
      // server never finished must not spin for ever.
      closeLive(state, { cut: false });
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
  // "failed" from a refusal is not "it ran and stopped". A step refused when the plan was checked
  // again (revalidation_refused — after a deploy, say, which empties the secret vault) failed the plan
  // with nothing run. Only "not_pending" means the plan was already decided before this approve.
  item.notRun = error?.code === 'revalidation_refused'
    || (item.plan.status === 'failed' && typeof error?.code === 'string' && error.code !== 'not_pending');
}

/**
 * A turn refused before it got anywhere (the gate: no model, today's cap) does not supersede the card
 * that was waiting — the server only withdraws a plan for a message it will answer. Cards this pane
 * marked superseded on `started` get the server's word back from a fresh read of the conversation.
 */
export function restoreSuperseded(state, view) {
  const pending = view?.pendingPlan ?? null;
  for (const item of state.items) {
    if (item.kind !== 'plan' || !item.superseded) continue;
    if (pending && pending.id === item.plan.id) {
      item.plan = pending;
      item.superseded = false;
    }
  }
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
