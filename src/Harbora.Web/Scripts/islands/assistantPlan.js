// The plan card's and the result card's rules, apart from the markup so `node --test` can check them.
//
// Shapes are AgentViews.cs, camelCase: AgentPlanView { id, status, expiresAt, steps }, each step
// AgentPlanStepView { index, tool, kind, titleEn/Fa, detailEn/Fa, warningsEn/Fa, confirmationPhrase,
// autoApproved, rememberable }, and AgentPlanResultView { planId, status, steps: [{ index, state,
// summaryEn/Fa, errorEn/Fa, links }] }.
//
// The server checks every one of these rules again on approve — this file is what lets the card say
// "type old-db to delete it" before a round trip, never the thing that grants consent.

const KIND_LABELS = {
  safe: ['Safe', 'کم‌خطر'],
  dangerous: ['Dangerous', 'پرخطر'],
  destructive: ['Destructive', 'حذف‌کننده'],
};

/** @param {string} kind @param {boolean} fa */
export function kindLabel(kind, fa) {
  const pair = KIND_LABELS[kind];
  return pair ? pair[fa ? 1 : 0] : String(kind ?? '');
}

/** Destructive steps are confirmed by typing the resource's name; nothing else is. */
export function needsPhrase(step) {
  return step?.kind === 'destructive';
}

/**
 * Exact and case-sensitive, ignoring only surrounding whitespace — the server's comparison.
 * A destructive step that arrived without a phrase can never match: an empty field equal to an
 * empty phrase must not read as consent to a delete.
 */
export function phraseMatches(step, typed) {
  const phrase = typeof step?.confirmationPhrase === 'string' ? step.confirmationPhrase.trim() : '';
  if (phrase === '') return false;
  return typeof typed === 'string' && typed.trim() === phrase;
}

/**
 * Whether the card offers "don't ask again" for this step. Only for safe steps the server marked
 * rememberable and the user has not already remembered — the server refuses to remember anything
 * else, so a checkbox on a dangerous step would be a promise nobody keeps.
 */
export function isRememberable(step) {
  return step?.kind === 'safe' && step.rememberable === true && step.autoApproved !== true;
}

/** The distinct tools the card offers to remember, sorted. "Don't ask again" is per tool, not per step. */
export function rememberableTools(plan) {
  return [...new Set((plan?.steps ?? []).filter(isRememberable).map((s) => s.tool))].sort();
}

/**
 * The card's own state: the text typed for each destructive step (by step index) and the ticked
 * tools (by tool name, so two steps of the same tool share one answer).
 */
export function createPlanForm(plan) {
  const phrases = {};
  for (const s of plan?.steps ?? []) if (needsPhrase(s)) phrases[s.index] = '';
  const remember = {};
  for (const tool of rememberableTools(plan)) remember[tool] = false;
  return { phrases, remember };
}

/** @returns {{ ok: boolean, mismatched: number[] }} the destructive steps whose phrase does not match. */
export function checkPlanForm(plan, form) {
  const mismatched = (plan?.steps ?? [])
    .filter((s) => needsPhrase(s) && !phraseMatches(s, form?.phrases?.[s.index]))
    .map((s) => s.index);
  return { ok: mismatched.length === 0, mismatched };
}

/**
 * The body of `POST /assistant/plans/{id}/approve` — AgentApproveRequest { confirmations,
 * rememberTools }. Confirmations are keyed by step index (System.Text.Json reads the string keys
 * back as the int dictionary the record declares). Only tools the card actually offered can be
 * remembered, whatever else is in the form.
 */
export function buildApproveBody(plan, form) {
  const confirmations = {};
  for (const s of plan?.steps ?? []) {
    if (needsPhrase(s)) confirmations[s.index] = String(form?.phrases?.[s.index] ?? '').trim();
  }
  const rememberTools = rememberableTools(plan).filter((tool) => form?.remember?.[tool] === true);
  return { confirmations, rememberTools };
}

/**
 * Expired only by the server's word. The card does not compare `expiresAt` with this browser's
 * clock: a clock running a few minutes fast would hide Run on a plan the server still accepts, and
 * a slow one would offer Run on a plan it has already expired. A late approve is refused by the
 * server, which says so, and the card shows that refusal (applyApproveRefusal).
 */
export function planIsExpired(plan) {
  return plan?.status === 'expired';
}

/** Run and Cancel are offered for a pending plan — pending as the server last said. */
export function planIsActionable(plan) {
  return plan?.status === 'pending';
}

/**
 * done / failed / skipped, or `unknown` for anything else. A step is drawn as done only when the
 * result says exactly "done" — a new state or a typo must never come out looking like success.
 */
export function stepResultState(stepResult) {
  const state = stepResult?.state;
  return state === 'done' || state === 'failed' || state === 'skipped' ? state : 'unknown';
}

export function summarizeResult(result) {
  const counts = { total: 0, done: 0, failed: 0, skipped: 0, unknown: 0 };
  for (const s of result?.steps ?? []) {
    counts.total++;
    counts[stepResultState(s)]++;
  }
  return { ...counts, allDone: counts.total > 0 && counts.done === counts.total };
}
