// What the conversation log draws, from the timeline's items (assistantTimeline.js): tool lines in
// people's words with their category's icon, runs of them as one activity block that collapses to
// "3 actions done · 2.4 s" once the work is over, a plan that ran drawn as one compact card with its
// result, and the thinking line that says what is happening. Plain functions, so `node --test` checks
// every rule; AssistantPanel.vue and the two cards only draw what these return.
//
// Honesty first, as everywhere in this pane: a check is drawn only where the server said "done". A
// stored tool line (tool-history) does not carry its outcome — AgentMessageView has no state — so a
// block of stored lines says they ran, in a neutral colour, never that they succeeded.

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
 * The words of one tool line. The running line reads "<label>…" and a finished one "<label>"; a failed
 * one gives the server's summary, which says what failed. The tool's identifier is the last resort,
 * for a server that sent no label. A stored line reads its label, else the stored summary.
 */
export function toolLineText(item, fa) {
  const t = (en, persian) => (fa ? persian : en);
  if (item?.kind === 'tool-history') return item.label || item.text || item.toolName || '';
  const label = toolLabel(item, fa);
  const summary = pickLang(fa, item.summaryEn, item.summaryFa);
  const name = label || item.name;
  switch (item.state) {
    case 'running': return label ? `${label}…` : summary || t(`Running ${item.name}…`, `در حال اجرای ${item.name}…`);
    case 'done': return label || summary || t(`${item.name} finished`, `${item.name} انجام شد`);
    case 'failed': return summary || t(`${name} failed`, `${name} ناموفق بود`);
    case 'interrupted': return t(`${name} — no result arrived`, `${name} — نتیجه‌ای نرسید`);
    default: return t(`${name} — outcome unknown`, `${name} — نتیجه نامشخص`);
  }
}

/**
 * The line's tooltip: what the server said that the line itself does not show (the summary behind a
 * label: "Read shop: running, 2 replicas"), else the tool's identifier for whoever wants it.
 */
export function toolLineTitle(item, fa) {
  const shown = toolLineText(item, fa);
  const detail = item?.kind === 'tool-history' ? item.text : pickLang(fa, item?.summaryEn, item?.summaryFa);
  if (detail && detail !== shown) return detail;
  return (item?.kind === 'tool-history' ? item.toolName : item?.name) || '';
}

/**
 * The small state mark beside a line: running (a spinner), done (a check), failed (a cross), unknown
 * (a question mark — also for "no result arrived"), or null. A stored line has none: its outcome was
 * not stored. A running line the thinking line is already naming does not spin a second spinner.
 */
export function toolLineMark(item, { thinkingKey = null } = {}) {
  if (item?.kind !== 'tool') return null;
  if (item.state === 'running') return item.key === thinkingKey ? null : 'running';
  if (item.state === 'done' || item.state === 'failed') return item.state;
  return 'unknown';
}

// ---- activity blocks -----------------------------------------------------------------------------

const isToolItem = (item) => item?.kind === 'tool' || item?.kind === 'tool-history';

/**
 * The counts a block's heading is made of. `ran`: stored lines, whose outcome is not known; `unsettled`:
 * live lines that never got one (interrupted, or a state the contract does not name). `totalMs` only
 * when every line has a duration — a sum with a gap in it would be a smaller number than the truth.
 */
function summarizeBlock(key, items) {
  const block = { key, items, running: 0, done: 0, failed: 0, unsettled: 0, ran: 0, totalMs: null };
  let total = 0;
  let timed = true;
  for (const item of items) {
    if (item.kind === 'tool-history') block.ran++;
    else if (item.state === 'running') block.running++;
    else if (item.state === 'done') block.done++;
    else if (item.state === 'failed') block.failed++;
    else block.unsettled++;
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

/**
 * How a block is drawn now. `answering`: a turn is being answered; `last`: the block is the last thing
 * in the log; `choice`: the person's own open/closed toggle for it, if any, which wins over the rules —
 * for the rest of the turn and after it.
 *   working  — a line is running, or the turn is still going and the block is the last thing (more
 *              tools may follow): open, "Working…" with a spinner;
 *   failed   — open, in the danger colour: "1 action failed, 2 done";
 *   unsettled — collapsed: lines that never got an outcome ("2 done, 1 with no result");
 *   ran      — collapsed, neutral: stored lines, whose outcome was not stored ("3 actions ran · 2.4 s");
 *   done     — collapsed, a success check: "3 actions done · 2.4 s".
 * @returns {{ tone: string, working: boolean, heading: string, open: boolean }}
 */
export function blockView(block, { answering = false, last = false, choice, fa = false } = {}) {
  const t = (en, persian) => (fa ? persian : en);
  const n = (k) => count(k, fa);
  const actions = (k) => (k === 1 ? 'action' : 'actions');
  const total = block.items.length;
  const duration = block.totalMs !== null && block.totalMs !== undefined ? ` · ${formatSeconds(block.totalMs, fa)}` : '';
  let tone;
  let heading;
  let open;
  if (block.running > 0 || (answering && last)) {
    tone = 'working';
    heading = t('Working…', 'در حال کار…');
    open = true;
  } else if (block.failed > 0) {
    tone = 'failed';
    const done = block.done + block.ran;
    heading = done > 0
      ? t(`${n(block.failed)} ${actions(block.failed)} failed, ${n(done)} done`, `${n(block.failed)} کار ناموفق، ${n(done)} انجام شد`)
      : t(`${n(block.failed)} ${actions(block.failed)} failed`, `${n(block.failed)} کار ناموفق`);
    open = true;
  } else if (block.unsettled > 0) {
    tone = 'unsettled';
    const done = block.done + block.ran;
    heading = done > 0
      ? t(`${n(done)} done, ${n(block.unsettled)} with no result`, `${n(done)} کار انجام شد، ${n(block.unsettled)} بی‌نتیجه`)
      : t(`${n(block.unsettled)} ${actions(block.unsettled)} with no result`, `${n(block.unsettled)} کار بی‌نتیجه`);
    open = false;
  } else if (block.ran > 0) {
    tone = 'ran';
    heading = t(`${n(total)} ${actions(total)} ran`, `${n(total)} کار اجرا شد`) + duration;
    open = false;
  } else {
    tone = 'done';
    heading = t(`${n(total)} ${actions(total)} done`, `${n(total)} کار انجام شد`) + duration;
    open = false;
  }
  if (typeof choice === 'boolean') open = choice;
  return { tone, working: tone === 'working', heading, open };
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

/** The newest tool line still running in the turn being streamed, if any. */
function runningTool(state) {
  if (!state?.streaming) return null;
  for (let i = state.items.length - 1; i >= 0; i--) {
    const item = state.items[i];
    if (item.kind === 'user') return null;
    if (item.kind === 'tool' && item.state === 'running') return item;
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
