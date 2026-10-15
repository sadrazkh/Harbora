// What the conversation log draws, from the timeline's items (assistantTimeline.js): tool lines in
// people's words with their category's icon, runs of them as one activity block that collapses to
// "3 actions done · 2.4 s" once the work is over, a plan that ran drawn as one compact card with its
// result, and the thinking line that says what is happening. Plain functions, so `node --test` checks
// every rule; AssistantPanel.vue and the two cards only draw what these return.
//
// Honesty first, as everywhere in this pane: a check is drawn only where the server said "done" — on a
// live tool event, or on a stored tool message whose toolState says so. A stored line with no outcome
// (a proposal, a refused call, an old row) is neutral, and a block of such lines is only counted.
//
// The server's words are never hover-only. A line shows the tool's label as its main text and, under it,
// what the server said (its summary, the stored text) whenever that is something else; a failed line
// always has words there. A label names an activity ("Deleting a database"), which is true of a line
// that ran and false of one that only proposed a write or was refused: a line without an outcome shows
// its own words ("Proposed as step 1: delete old-db") instead.
//
// A server from before toolState: a stored line with a duration was a read that ran (only those were
// timed), so it gets its label and counts as "ran" — neutral, never "done"; anything else has no outcome.

import { isThinking } from './assistantTimeline.js';
import { summarizeResult } from './assistantPlan.js';

/**
 * The icon for each tool category (AgentToolLabels.Categories), as a lucide export name; the island
 * maps the names to glyphs. A category this pane does not know — "other", a newer one — gets the
 * generic wrench rather than nothing.
 */
const CATEGORY_ICONS = {
  app: 'AppWindow',
  deployment: 'Rocket',
  database: 'Database',
  project: 'Folder',
  domain: 'Globe',
  volume: 'HardDrive',
  env: 'KeyRound',
  catalog: 'LayoutGrid',
  usage: 'Gauge',
  resources: 'Boxes',
};

export function categoryIcon(category) {
  return Object.prototype.hasOwnProperty.call(CATEGORY_ICONS, category) ? CATEGORY_ICONS[category] : 'Wrench';
}

const pickLang = (fa, en, persian) => (fa ? persian || en : en || persian) || '';
const locale = (fa) => (fa ? 'fa-IR' : 'en-GB');
const count = (n, fa) => new Intl.NumberFormat(locale(fa)).format(n);
const isDuration = (ms) => typeof ms === 'number' && Number.isFinite(ms) && ms >= 0;

/**
 * A duration in seconds with one decimal, digits and separator in the panel's locale: "2.4 s",
 * «۲٫۴ ثانیه». Under a tenth of a second it says so rather than rounding a 40 ms read to "0.0 s" — or
 * up to a tenth it did not take.
 */
export function formatSeconds(ms, fa) {
  if (!isDuration(ms)) return '';
  if (ms < 100) return fa ? `کمتر از ${count(0.1, fa)} ثانیه` : '< 0.1 s';
  const seconds = new Intl.NumberFormat(locale(fa), { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(ms / 1000);
  return fa ? `${seconds} ثانیه` : `${seconds} s`;
}

/** Whole seconds, for the thinking line's ticking count: "12 s", «۱۲ ثانیه». */
export function formatElapsed(seconds, fa) {
  return fa ? `${count(seconds, fa)} ثانیه` : `${count(seconds, fa)} s`;
}

// ---- tool lines ----------------------------------------------------------------------------------

/** The tool's name for people, in the panel's language — or '' from a server that sent none. */
export function toolLabel(item, fa) {
  if (item?.kind === 'tool-history') return item.label || '';
  return pickLang(fa, item?.labelEn, item?.labelFa);
}

/**
 * What a line counts as in its block: running, done, failed; `unsettled` — a live line that never got an
 * outcome (interrupted, or a state the contract does not name); `ran` — a stored read from a server
 * before toolState (see the top of this file); `noted` — a stored line with no outcome.
 */
export function lineOutcome(item) {
  if (item?.kind === 'tool-history') {
    if (item.state === 'done' || item.state === 'failed') return item.state;
    return isDuration(item.durationMs) ? 'ran' : 'noted';
  }
  if (item?.state === 'running' || item?.state === 'done' || item?.state === 'failed') return item.state;
  return 'unsettled';
}

/**
 * The main words of one tool line: the label ("<label>…" while it runs) when there is one and the line
 * has an outcome; otherwise its own words — the stored text, or for a live line from a server that sent
 * no label, the summary and only then the tool's identifier.
 */
export function toolLineText(item, fa) {
  const t = (en, persian) => (fa ? persian : en);
  if (item?.kind === 'tool-history') {
    if (item.label && lineOutcome(item) !== 'noted') return item.label;
    return item.text || item.label || item.toolName || '';
  }
  const label = toolLabel(item, fa);
  if (label) return item.state === 'running' ? `${label}…` : label;
  const summary = pickLang(fa, item.summaryEn, item.summaryFa);
  const name = item.name;
  switch (item.state) {
    case 'running': return summary || t(`Running ${name}…`, `در حال اجرای ${name}…`);
    case 'done': return summary || t(`${name} finished`, `${name} انجام شد`);
    case 'failed': return summary || t(`${name} failed`, `${name} ناموفق بود`);
    case 'interrupted': return t(`${name} — no result arrived`, `${name} — نتیجه‌ای نرسید`);
    default: return t(`${name} — outcome unknown`, `${name} — نتیجه نامشخص`);
  }
}

/**
 * The visible second line under a tool line: what the server said, when the main words are something
 * else — the summary under a live label, the stored text under a stored label. A failed line always has
 * words here (or in its main text); a line that never got an outcome says so. '' when there is nothing
 * more to say.
 */
export function toolLineDetail(item, fa) {
  const t = (en, persian) => (fa ? persian : en);
  const main = toolLineText(item, fa);
  let words = '';
  if (item?.kind === 'tool-history') {
    words = item.text || (item.state === 'failed' ? t('It failed.', 'ناموفق بود.') : '');
  } else if (item) {
    const summary = pickLang(fa, item.summaryEn, item.summaryFa);
    if (!toolLabel(item, fa)) {
      words = item.state === 'unknown' ? summary : '';
    } else {
      switch (item.state) {
        case 'failed': words = summary || t('It failed.', 'ناموفق بود.'); break;
        case 'interrupted': words = t('No result arrived.', 'نتیجه‌ای نرسید.'); break;
        case 'unknown': words = summary || t('The outcome is not known.', 'نتیجه نامشخص است.'); break;
        default: words = summary;
      }
    }
  }
  return words && words !== main ? words : '';
}

/** The line's tooltip: the tool's identifier, for whoever wants it. Every word the server said is visible. */
export function toolLineTitle(item) {
  return (item?.kind === 'tool-history' ? item.toolName : item?.name) || '';
}

/**
 * The small state mark beside a line: running (a spinner), done (a check), failed (a cross), unknown
 * (a question mark — also for "no result arrived"), or null. A stored line has a check or a cross only
 * when the server stored that outcome, and none otherwise. A running line the thinking line is already
 * naming does not spin a second spinner.
 */
export function toolLineMark(item, { thinkingKey = null } = {}) {
  if (item?.kind === 'tool-history') return item.state === 'done' || item.state === 'failed' ? item.state : null;
  if (item?.kind !== 'tool') return null;
  if (item.state === 'running') return item.key === thinkingKey ? null : 'running';
  if (item.state === 'done' || item.state === 'failed') return item.state;
  return 'unknown';
}

// ---- activity blocks -----------------------------------------------------------------------------

const isToolItem = (item) => item?.kind === 'tool' || item?.kind === 'tool-history';

/**
 * The counts a block's heading is made of, one per lineOutcome — stored lines with an outcome count
 * exactly like live ones. `totalMs` only when every line has a duration — a sum with a gap in it would
 * be a smaller number than the truth.
 */
function summarizeBlock(key, items) {
  const block = { key, items, running: 0, done: 0, failed: 0, unsettled: 0, ran: 0, noted: 0, totalMs: null };
  let total = 0;
  let timed = true;
  for (const item of items) {
    block[lineOutcome(item)]++;
    if (isDuration(item.durationMs)) total += item.durationMs;
    else timed = false;
  }
  if (timed && items.length > 0) block.totalMs = total;
  return block;
}

/**
 * A plan that ran, and the result directly under it, become one compact card — but only when the
 * server says "executed" and the result says every step is done. Anything less (a failed step, a step
 * the result does not account for) keeps both cards in full: what failed is never behind a click.
 */
export function ranCompactly(planItem, resultItem) {
  if (planItem?.kind !== 'plan' || resultItem?.kind !== 'result') return false;
  const plan = planItem.plan;
  if (plan?.status !== 'executed' || planItem.resulted !== true) return false;
  const result = resultItem.result;
  if (resultItem.plan?.id !== plan.id && result?.planId !== plan.id) return false;
  return summarizeResult(result).allDone;
}

/** A result with no card above it (an `executed` event: every step was a remembered safe tool). */
function standaloneRan(resultItem) {
  return resultItem?.kind === 'result' && !resultItem.plan && resultItem.result?.status === 'executed'
    && summarizeResult(resultItem.result).allDone;
}

/**
 * The timeline as the log draws it, in order:
 *   { type: 'activity', key, block } — a run of consecutive tool / tool-history lines, keyed by its first
 *     line's key so the person's open/closed choice follows the block as lines are added to it;
 *   { type: 'ran', key, plan, result } — a plan that ran (plan: its card's item, or null) and its result;
 *   { type: 'item', key, item } — everything else, one by one.
 * Text, plans, results, errors and the person's messages each end a block.
 */
export function activityRows(items) {
  const rows = [];
  let run = null;
  const close = () => {
    if (run) rows.push({ type: 'activity', key: run.key, block: summarizeBlock(run.key, run.items) });
    run = null;
  };
  const list = items ?? [];
  for (let i = 0; i < list.length; i++) {
    const item = list[i];
    if (isToolItem(item)) {
      if (!run) run = { key: item.key, items: [] };
      run.items.push(item);
      continue;
    }
    close();
    if (item.kind === 'plan' && ranCompactly(item, list[i + 1])) {
      rows.push({ type: 'ran', key: item.key, plan: item, result: list[i + 1] });
      i++;
    } else if (standaloneRan(item)) {
      rows.push({ type: 'ran', key: item.key, plan: null, result: item });
    } else {
      rows.push({ type: 'item', key: item.key, item });
    }
  }
  close();
  return rows;
}

// The parts of a heading, in the order they are said: what failed first, then what was done, what got
// no outcome, what ran with its outcome unstored, and the lines that only say something (a proposal).
const HEADING_PARTS = [
  ['failed', 'failed', 'ناموفق'],
  ['done', 'done', 'انجام شد'],
  ['unsettled', 'with no result', 'بی‌نتیجه'],
  ['ran', 'ran', 'اجرا شد'],
  ['noted', 'other', 'مورد دیگر'],
];

/**
 * "3 actions done", "1 action failed, 2 done", «۱ کار ناموفق، ۲ انجام شد، ۱ مورد دیگر»: every kind of line
 * in the block is counted, the first with the noun. A block of only noted lines is just "2 actions".
 */
function countsHeading(block, fa) {
  const n = (k) => count(k, fa);
  const actions = (k) => (k === 1 ? 'action' : 'actions');
  const parts = HEADING_PARTS.filter(([kind]) => block[kind] > 0);
  return parts.map(([kind, en, persian], i) => {
    const k = block[kind];
    if (i > 0) return fa ? `${n(k)} ${persian}` : `${n(k)} ${en}`;
    if (kind === 'noted') return fa ? `${n(k)} کار` : `${n(k)} ${actions(k)}`;
    return fa ? `${n(k)} کار ${persian}` : `${n(k)} ${actions(k)} ${en}`;
  }).join(fa ? '، ' : ', ');
}

/**
 * How a block is drawn now. `answering`: a turn is being answered; `last`: the block is the last thing
 * in the log; `choice`: the person's own open/closed toggle for it, `{ open, tone }` (toggledChoice),
 * which wins over the rules for the rest of the turn and after it — except that a failure arriving after
 * it was made opens the block again: a choice made before anything failed is not a choice to hide a
 * failure.
 *   working   — a line is running, or the turn is still going and the block is the last thing (more
 *               tools may follow): open, "Working…" with a spinner;
 *   failed    — open, in the danger colour: "1 action failed, 2 done";
 *   unsettled — collapsed: lines that never got an outcome ("1 action done, 1 with no result");
 *   done      — collapsed, a success check: "3 actions done · 2.4 s";
 *   ran       — collapsed, neutral: stored reads from a server before toolState ("3 actions ran · 2.4 s");
 *   noted     — collapsed, neutral: stored lines without an outcome among them ("2 actions").
 * @returns {{ tone: string, working: boolean, heading: string, open: boolean }}
 */
export function blockView(block, { answering = false, last = false, choice, fa = false } = {}) {
  const total = block.items.length;
  const timed = block.totalMs !== null && block.totalMs !== undefined;
  let tone;
  if (block.running > 0 || (answering && last)) tone = 'working';
  else if (block.failed > 0) tone = 'failed';
  else if (block.unsettled > 0) tone = 'unsettled';
  else if (block.done === total) tone = 'done';
  else if (block.noted > 0) tone = 'noted';
  else tone = 'ran';

  let heading;
  if (tone === 'working') heading = fa ? 'در حال کار…' : 'Working…';
  else heading = countsHeading(block, fa) + ((tone === 'done' || tone === 'ran') && timed ? ` · ${formatSeconds(block.totalMs, fa)}` : '');

  let open = tone === 'working' || tone === 'failed';
  const stale = tone === 'failed' && choice?.tone !== 'failed';
  if (typeof choice?.open === 'boolean' && !stale) open = choice.open;
  return { tone, working: tone === 'working', heading, open };
}

/** The choice a click on a block's header records: the other state, and the tone it was made under. */
export function toggledChoice(view) {
  return { open: !view.open, tone: view.tone };
}

// ---- finished plan cards -------------------------------------------------------------------------

/** The compact card's heading: "Plan ran · 2 steps", «برنامه اجرا شد · ۲ گام». */
export function ranHeading(result, fa) {
  const steps = result?.steps?.length ?? 0;
  return fa
    ? `برنامه اجرا شد · ${count(steps, fa)} گام`
    : `Plan ran · ${count(steps, fa)} ${steps === 1 ? 'step' : 'steps'}`;
}

/** Every resource link a result names, in step order — the compact card's chips, always visible. */
export function resultLinks(result) {
  return (result?.steps ?? []).flatMap((s) => s?.links ?? []);
}

/**
 * The one muted line a cancelled or expired plan becomes — nothing ran, and the card's steps are
 * behind "Details". Null for every other card, which is drawn in full: a pending card needs its consent
 * UI, an executing one is still going, and a failed one says which step failed and why.
 */
export function planCardLine(item, fa) {
  const t = (en, persian) => (fa ? persian : en);
  switch (item?.plan?.status) {
    case 'rejected': return t('Cancelled — nothing ran', 'لغو شد — هیچ کاری اجرا نشد');
    case 'expired': return item.superseded
      ? t('Not run — you sent a new message', 'اجرا نشد — پیام تازه‌ای فرستادید')
      : t('Expired — nothing ran', 'منقضی شد — هیچ کاری اجرا نشد');
    default: return null;
  }
}

// ---- the thinking line ---------------------------------------------------------------------------

/**
 * The newest tool line still running in the turn being streamed, if any — among the tool lines at the
 * end of the log only, as isThinking looks at the last item: once text or anything else follows, that
 * is what is being shown, not the tool.
 */
function runningTool(state) {
  if (!state?.streaming) return null;
  for (let i = state.items.length - 1; i >= 0; i--) {
    const item = state.items[i];
    if (item.kind !== 'tool') return null;
    if (item.state === 'running') return item;
  }
  return null;
}

/**
 * Whether the thinking line shows: while nothing else says the turn is alive (isThinking — after the
 * send, after a tool until the next output, a conversation still being answered), and while a tool runs,
 * which it then names. Not while text is being written.
 */
export function thinkingShows(state) {
  return isThinking(state) || runningTool(state) !== null;
}

/**
 * Keeps `state.thinkingSince` — when the thinking line appeared — in step with whether it shows: set
 * when it appears, cleared when it goes, untouched while it stays (a tool starting and finishing under
 * it is the same wait). The island calls this whenever that changes. Returns whether it shows.
 */
export function trackThinking(state, now) {
  const showing = thinkingShows(state);
  if (!showing) state.thinkingSince = null;
  else if (typeof state.thinkingSince !== 'number') state.thinkingSince = now;
  return showing;
}

/**
 * The thinking line beside the dots, or null when it does not show: the running tool's label —
 * «در حال خواندن لاگ‌های استقرار…» — when one is running, else "Thinking…"; and from five seconds on,
 * the whole seconds since it appeared. `toolKey`: the line it names, which then does not spin itself.
 * @returns {{ labelEn: string, labelFa: string, category: string | null, toolKey: string | null, seconds: number | null } | null}
 */
export function thinkingLine(state, now) {
  const tool = runningTool(state);
  if (!tool && !isThinking(state)) return null;
  const since = state.thinkingSince;
  const elapsed = typeof since === 'number' && typeof now === 'number' ? now - since : 0;
  const seconds = elapsed >= 5000 ? Math.floor(elapsed / 1000) : null;
  if (!tool) return { labelEn: 'Thinking…', labelFa: 'در حال فکر کردن…', category: null, toolKey: null, seconds };
  const en = tool.labelEn || tool.labelFa;
  const persian = tool.labelFa || tool.labelEn;
  return {
    labelEn: en ? `${en}…` : `Running ${tool.name}…`,
    labelFa: tool.labelFa ? `در حال ${persian}…` : persian ? `${persian}…` : `در حال اجرای ${tool.name}…`,
    category: tool.category ?? null,
    toolKey: tool.key,
    seconds,
  };
}
