<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import {
  ArrowUp, Check, ChevronLeft, ChevronRight, CircleHelp, History, Info, LoaderCircle, MessageSquare,
  Plus, Sparkles, TriangleAlert, Wrench, X, Lock, Eye, EyeOff,
  AppWindow, Boxes, CheckCheck, ChevronDown, Database, Folder, Gauge, Globe, HardDrive, KeyRound, LayoutGrid,
  ListChecks, Rocket, SquareStop,
} from 'lucide';
import { AssistantIcon } from './assistantIcon';
import AssistantText from './AssistantText.vue';
import AssistantPlanCard from './AssistantPlanCard.vue';
import AssistantResultCard from './AssistantResultCard.vue';
import { createAssistantApi, buildPageContext, parseDeclaredContext } from './assistantApi.js';
import { textDirection, firstStrongDirection } from './assistantFormat.js';
import {
  createTimeline, applyStreamEvent, applyPlanResult, applyApproveRefusal, markPlanRejected,
} from './assistantTimeline.js';
import {
  activityRows, blockView, categoryIcon, formatElapsed, formatSeconds, thinkingLine, thinkingShows, toolLineMark,
  toolLineText, toolLineTitle, trackThinking,
} from './assistantActivity.js';
import { checkPlanForm, buildApproveBody } from './assistantPlan.js';
import { createAssistantSession } from './assistantSession.js';
import { createSecretBook, createSecretForm, looksLikeSecret, withoutSecrets } from './assistantSecrets.js';
import AssistantSecretText from './AssistantSecretText.vue';
import { createAssistantPrefs } from '../assistantDrawer.js';

// The assistant pane: status, conversations, the message stream, plan and result cards.
//
// Everything with a rule in it lives in plain modules beside this file, tested by node --test:
// the SSE parser (assistantStream.js), the HTTP layer and its error words (assistantApi.js), the
// conversation reducer (assistantTimeline.js), the plan card's consent rules (assistantPlan.js) and
// the message formatter (assistantFormat.js), and sending, streaming and coming back mid-answer
// (assistantSession.js). This file wires them to markup. The drawer around it, its toggle and
// Ctrl/Cmd+J belong to ../assistantDrawer.js.

const props = defineProps<{ csrf: string; lang: string; context?: string; autofocus?: boolean }>();
const emit = defineEmits<{ (e: 'close'): void }>();

const fa = props.lang === 'fa';
const t = (en: string, persian: string) => (fa ? persian : en);

const api = createAssistantApi({ csrf: props.csrf });
const prefs = createAssistantPrefs(globalThis.localStorage);
const declared = parseDeclaredContext(props.context);

const status = ref<any>(null);
const statusError = ref<{ messageEn: string; messageFa: string } | null>(null);
const statusLoading = ref(false);

const timeline = reactive(createTimeline());
const session = createAssistantSession({
  api,
  prefs,
  timeline,
  pageContext: () => buildPageContext(declared, location.pathname),
  onChange: (what) => {
    scrollToEnd(what === 'sent' || what === 'opened');
    // A conversation that finished while it was being re-read: today's count has moved.
    if (what === 'settled') refreshStatus();
  },
});
const thinking = computed(() => thinkingShows(timeline));
// The composer waits while a turn is streaming here or still being answered after a reload.
const answering = computed(() => timeline.streaming || timeline.resuming);

const view = ref<'chat' | 'history'>('chat');
const conversations = ref<any[]>([]);
const conversationsLoading = ref(false);
const conversationsError = ref<{ messageEn: string; messageFa: string } | null>(null);

const draft = ref('');

// ---- secrets (the lock button) -------------------------------------------------------------------
// The value is typed into this form only, posted once, and forgotten; the draft gets a placeholder
// naming it, which becomes the handle when the message is sent (assistantSecrets.js).
const secrets = createSecretBook();
const secretForm = createSecretForm({ api, book: secrets, wrap: reactive });
const secretName = ref<HTMLInputElement | null>(null);
let secretCursor: number | null = null;

function openSecretForm() {
  secretCursor = composer.value?.selectionStart ?? null;
  secretForm.open();
  nextTick(() => secretName.value?.focus());
}

function closeSecretForm() {
  secretForm.close();
  focusComposer();
}

async function holdSecret() {
  const placeholder = await secretForm.submit();
  if (!placeholder) return;
  const { draft: next, cursor } = secrets.insert(draft.value, secretCursor ?? draft.value.length, placeholder);
  draft.value = next;
  nextTick(() => {
    resizeComposer();
    composer.value?.focus();
    composer.value?.setSelectionRange(cursor, cursor);
  });
}

// A draft that seems to carry a password or key in plain text: a gentle warning offering the lock
// button. The first Send of such a draft is held so the warning is seen; Send again sends it as typed.
const secretWarning = computed(() => looksLikeSecret(secrets.expand(draft.value)));
const warnedDraft = ref('');
const composer = ref<HTMLTextAreaElement | null>(null);
const scroller = ref<HTMLElement | null>(null);

const words = (e: any) => ({ messageEn: e?.messageEn ?? String(e), messageFa: e?.messageFa ?? e?.messageEn ?? String(e) });
const say = (w: { messageEn: string; messageFa: string } | null) => (w ? (fa ? w.messageFa || w.messageEn : w.messageEn || w.messageFa) : '');

// ---- status ----------------------------------------------------------------------------------

const remaining = computed<number | null | undefined>(() =>
  timeline.remainingToday !== undefined ? timeline.remainingToday : status.value?.remainingToday);
const unavailable = computed(() => status.value?.available === false);
const outOfRequests = computed(() => remaining.value === 0);
const canSend = computed(() => !answering.value && !timeline.loading && !timeline.loadError && !unavailable.value
  && !outOfRequests.value && draft.value.trim().length > 0);

const unavailableReason = computed(() => {
  const s = status.value;
  return (fa ? s?.reasonFa || s?.reasonEn : s?.reasonEn || s?.reasonFa)
    || t('The assistant is not available right now.', 'دستیار در حال حاضر در دسترس نیست.');
});

const remainingLabel = computed(() => {
  const r = remaining.value;
  if (r === undefined) return '';
  if (r === null) return t('no daily limit', 'بدون سقف روزانه');
  return t(`${r} requests left today`, `${r} درخواست باقی‌مانده برای امروز`);
});

async function refreshStatus() {
  statusLoading.value = true;
  try {
    status.value = await api.status();
    statusError.value = null;
    // The server's count is newer than whatever the last stream said.
    timeline.remainingToday = undefined;
  } catch (e) {
    statusError.value = words(e);
  } finally {
    statusLoading.value = false;
  }
}

// ---- conversations -----------------------------------------------------------------------------

async function showHistory() {
  if (view.value === 'history') { view.value = 'chat'; return; }
  view.value = 'history';
  conversationsLoading.value = true;
  conversationsError.value = null;
  try {
    conversations.value = (await api.conversations()) ?? [];
  } catch (e) {
    conversationsError.value = words(e);
  } finally {
    conversationsLoading.value = false;
  }
}

async function openConversation(id: string) {
  secretForm.close(); // a half-typed secret does not follow the person into another conversation
  const outcome = await session.open(id);
  if (outcome.error) {
    if (view.value === 'history') conversationsError.value = words(outcome.error);
    else applyStreamEvent(timeline, 'error', words(outcome.error));
    return;
  }
  view.value = 'chat';
  restoreDraft(outcome.restoreDraft);
}

function newConversation() {
  secretForm.close();
  session.startNew();
  view.value = 'chat';
  focusComposer();
}

/** Words that were not taken go back in the composer — unless the person has started typing again. */
function restoreDraft(text?: string | null) {
  if (!text || draft.value.trim()) return;
  // Handles this pane holds become their placeholders again; others stay as handles, still valid.
  draft.value = secrets.collapse(text);
  nextTick(resizeComposer);
}

function formatDate(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat(fa ? 'fa-IR' : 'en-GB', { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

// ---- sending -----------------------------------------------------------------------------------

async function send() {
  const typed = draft.value.trim();
  if (!canSend.value || !typed) return;
  if (secretWarning.value && warnedDraft.value !== typed) {
    warnedDraft.value = typed;
    return;
  }
  warnedDraft.value = '';
  draft.value = '';
  resizeComposer();
  const outcome = await session.send(secrets.expand(typed));
  restoreDraft(outcome.restoreDraft);
  scrollToEnd();
  focusComposer();
}

function onComposerKey(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    send();
  }
}

function useSuggestion(text: string) {
  draft.value = text;
  focusComposer();
  nextTick(resizeComposer);
}

function resizeComposer() {
  const el = composer.value;
  if (!el) return;
  el.style.height = 'auto';
  el.style.height = `${Math.min(el.scrollHeight, 160)}px`;
}

function focusComposer() {
  nextTick(() => composer.value?.focus());
}

// ---- plans -------------------------------------------------------------------------------------

async function runPlan(item: any) {
  const check = checkPlanForm(item.plan, item.form);
  item.mismatched = check.mismatched;
  if (!check.ok || item.busy) return;
  item.busy = true;
  item.error = null;
  item.errorStep = null;
  try {
    const result = await api.approve(item.plan.id, buildApproveBody(item.plan, item.form));
    applyPlanResult(timeline, item.plan.id, result);
    scrollToEnd();
  } catch (e: any) {
    // The server's refusal, in its words, with the step it names and the plan's status now — a
    // late approve is how a card learns it expired; this pane never decides that by its clock.
    applyApproveRefusal(timeline, item.plan.id, { ...words(e), stepIndex: e?.stepIndex ?? null, planStatus: e?.planStatus ?? null, code: e?.code ?? null });
    // Approved meanwhile in another tab and running there: this card cannot see that run end, so the
    // conversation is read once more shortly, and shows what it did.
    if (e?.code === 'not_pending' && e?.planStatus === 'executing') session.rereadSoon();
  } finally {
    item.busy = false;
  }
}

async function cancelPlan(item: any) {
  if (item.busy) return;
  item.busy = true;
  item.error = null;
  item.errorStep = null;
  try {
    await api.reject(item.plan.id);
    markPlanRejected(timeline, item.plan.id);
  } catch (e: any) {
    applyApproveRefusal(timeline, item.plan.id, { ...words(e), stepIndex: null, planStatus: e?.planStatus ?? null, code: e?.code ?? null });
    if (e?.code === 'not_pending' && e?.planStatus === 'executing') session.rereadSoon();
  } finally {
    item.busy = false;
  }
}

// ---- tool lines, activity blocks and the thinking line --------------------------------------------
// The words, counts and open/closed rules are assistantActivity.js's; this only maps them to glyphs.

const CATEGORY_GLYPHS: Record<string, any> = {
  AppWindow, Rocket, Database, Folder, Globe, HardDrive, KeyRound, LayoutGrid, Gauge, Boxes, Wrench,
};
const categoryGlyph = (category: string | null | undefined) => CATEGORY_GLYPHS[categoryIcon(category)] ?? Wrench;

const toolText = (item: any) => toolLineText(item, fa);
const toolTitle = (item: any) => toolLineTitle(item, fa);
// The state mark beside a line (spinner, check, cross, question mark), by toolLineMark's name for it.
const toolIcon = (mark: string | null) => ({ running: LoaderCircle, done: Check, failed: X } as Record<string, any>)[mark ?? ''] ?? CircleHelp;
const toolIconClass = (mark: string | null) => ({ running: 'text-accent-text', done: 'text-ok', failed: 'text-danger' } as Record<string, string>)[mark ?? ''] ?? 'text-ink-faint';
// A running line the thinking line is naming does not spin a second spinner.
const markOf = (item: any) => toolLineMark(item, { thinkingKey: line.value?.toolKey ?? null });

// The person's own open/closed choice per block, by the block's first line's key: it holds while lines
// are added to the block and after the turn, over what the rules would say.
const blockChoices = reactive<Record<string, boolean>>({});
const rows = computed(() => {
  const list = activityRows(timeline.items);
  return list.map((row: any, i: number) => (row.type !== 'activity' ? row : {
    ...row,
    view: blockView(row.block, { answering: answering.value, last: i === list.length - 1, choice: blockChoices[row.key], fa }),
  }));
});
const toggleBlock = (row: any) => { blockChoices[row.key] = !row.view.open; };
const activityId = (row: any) => `assistant-activity-${row.key}`;
const blockIcon = (tone: string) => ({ working: LoaderCircle, failed: TriangleAlert, done: CheckCheck, ran: ListChecks, noted: ListChecks } as Record<string, any>)[tone] ?? CircleHelp;
const blockIconClass = (tone: string) => ({ working: 'text-accent-text', failed: 'text-danger', done: 'text-ok' } as Record<string, string>)[tone] ?? 'text-ink-faint';

const stoppedText = (item: any) => say(item) || t('You stopped this answer.', 'این پاسخ را متوقف کردید.');

// The thinking line's clock: `now` ticks once a second only while the line shows, for its "12 s"; the
// timer goes with the line and with the island.
const now = ref(Date.now());
let ticker: ReturnType<typeof setInterval> | null = null;
function stopTicker() {
  if (ticker !== null) clearInterval(ticker);
  ticker = null;
}
watch(thinking, (showing) => {
  now.value = Date.now();
  trackThinking(timeline, now.value);
  if (showing && ticker === null) ticker = setInterval(() => { now.value = Date.now(); }, 1000);
  if (!showing) stopTicker();
}, { immediate: true });
onUnmounted(stopTicker);
const line = computed(() => thinkingLine(timeline, now.value));
const lineLabel = computed(() => (line.value ? (fa ? line.value.labelFa : line.value.labelEn) : ''));

// ---- scrolling ---------------------------------------------------------------------------------

// Follow new lines only when the person is already at the bottom; someone scrolled up to reread a
// plan is not yanked away from it by the next tool line.
function scrollToEnd(force = false) {
  const el = scroller.value;
  if (!el) return;
  const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 96;
  if (!force && !atBottom) return;
  nextTick(() => { el.scrollTop = el.scrollHeight; });
}

// ---- lifecycle ---------------------------------------------------------------------------------

function onOpened(event: Event) {
  refreshStatus();
  session.wake();
  if ((event as CustomEvent).detail?.focus) focusComposer();
}

function onClosed() {
  session.pause();
  // A closed drawer keeps no secret typed into it.
  secretForm.close();
}

onMounted(async () => {
  refreshStatus();
  document.addEventListener('harbora:assistant-opened', onOpened);
  document.addEventListener('harbora:assistant-closed', onClosed);
  if (props.autofocus) focusComposer();
  // The remembered conversation (the person's own message shows at once, and a turn still being
  // answered is followed to its end), and any words that were on their way when the page went. A
  // read that fails is shown with "try again" (timeline.loadError), never as a blank new conversation.
  const restored = await session.restore();
  restoreDraft(restored.restoreDraft);
});

async function retryLoad() {
  const outcome = await session.retry();
  restoreDraft(outcome.restoreDraft);
}

onUnmounted(() => {
  secretForm.close();
  document.removeEventListener('harbora:assistant-opened', onOpened);
  document.removeEventListener('harbora:assistant-closed', onClosed);
  session.dispose();
});

const suggestions = fa
  ? ['در این پروژه چه چیزهایی در حال اجراست؟', 'یک دیتابیس Postgres 16 به نام shop-db بساز', 'آخرین استقرار این اپ چرا ناموفق بود؟']
  : ['What is running in this project?', 'Create a Postgres 16 database called shop-db', 'Why did the last deployment of this app fail?'];
const Back = fa ? ChevronRight : ChevronLeft;
</script>

<template>
  <div class="flex h-full min-h-0 flex-col" :dir="fa ? 'rtl' : 'ltr'" data-assistant-island>
    <!-- Header: who this is, what model answers, and how much of today is left. -->
    <header class="flex items-start gap-2.5 border-b border-line px-4 py-3">
      <span class="mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-accent-soft text-accent-text">
        <AssistantIcon :icon="Sparkles" />
      </span>
      <div class="min-w-0 flex-1">
        <h2 class="text-lg font-semibold leading-6 text-ink">{{ t('Assistant', 'دستیار') }}</h2>
        <!-- Two lines rather than one truncated one: a long model id must not push the remaining
             count — the number people actually need — off the end. -->
        <div class="text-[11px] leading-4 text-ink-muted" data-assistant-status
             :data-available="status ? String(status.available) : 'unknown'">
          <p v-if="statusLoading && !status">{{ t('Checking…', 'در حال بررسی…') }}</p>
          <p v-else-if="statusError" class="truncate" :title="say(statusError)">{{ say(statusError) }}</p>
          <p v-else-if="unavailable">{{ t('Unavailable', 'در دسترس نیست') }}</p>
          <template v-else-if="status">
            <p v-if="remainingLabel" :class="outOfRequests ? 'text-danger' : ''" data-assistant-remaining>{{ remainingLabel }}</p>
            <p class="truncate" dir="ltr" :class="fa ? 'text-end' : ''" :title="status.model || ''">{{ status.model || t('default model', 'مدل پیش‌فرض') }}</p>
          </template>
        </div>
      </div>
      <button type="button" class="rounded-lg p-1.5 text-ink-muted hover:bg-surface-2 hover:text-ink disabled:opacity-40"
              :class="view === 'history' ? 'bg-surface-2 text-ink' : ''" :aria-pressed="view === 'history'"
              :title="t('Conversations', 'گفت‌وگوها')" :aria-label="t('Conversations', 'گفت‌وگوها')"
              data-assistant-history @click="showHistory">
        <AssistantIcon :icon="History" />
      </button>
      <button type="button" class="rounded-lg p-1.5 text-ink-muted hover:bg-surface-2 hover:text-ink disabled:opacity-40"
              :title="t('New conversation', 'گفت‌وگوی تازه')" :aria-label="t('New conversation', 'گفت‌وگوی تازه')"
              data-assistant-new @click="newConversation">
        <AssistantIcon :icon="Plus" />
      </button>
      <button type="button" class="rounded-lg p-1.5 text-ink-muted hover:bg-surface-2 hover:text-ink"
              :title="t('Close (Esc)', 'بستن (Esc)')" :aria-label="t('Close the assistant', 'بستن دستیار')"
              data-assistant-close @click="emit('close')">
        <AssistantIcon :icon="X" />
      </button>
    </header>

    <!-- Conversation list -->
    <div v-if="view === 'history'" class="min-h-0 flex-1 overflow-y-auto overscroll-contain p-3" data-assistant-conversations>
      <button type="button" class="mb-2 flex items-center gap-1 text-sm text-ink-muted hover:text-ink" @click="view = 'chat'">
        <AssistantIcon :icon="Back" :size="14" />{{ t('Back to the conversation', 'بازگشت به گفت‌وگو') }}
      </button>
      <p v-if="conversationsLoading" class="flex items-center gap-2 p-3 text-sm text-ink-muted">
        <AssistantIcon :icon="LoaderCircle" spin />{{ t('Loading…', 'در حال بارگذاری…') }}
      </p>
      <p v-else-if="conversationsError" class="alert-danger text-sm" role="alert">{{ say(conversationsError) }}</p>
      <p v-else-if="conversations.length === 0" class="p-3 text-sm text-ink-muted">{{ t('No conversations yet.', 'هنوز گفت‌وگویی نیست.') }}</p>
      <ul v-else class="space-y-1">
        <li v-for="c in conversations" :key="c.id">
          <button type="button" class="flex w-full items-start gap-2.5 rounded-lg px-2.5 py-2 text-start hover:bg-surface-2 disabled:cursor-not-allowed disabled:opacity-50"
                  :class="c.id === timeline.conversationId ? 'bg-accent-soft' : ''" @click="openConversation(c.id)">
            <AssistantIcon :icon="MessageSquare" class="mt-0.5 text-ink-faint" />
            <span class="min-w-0 flex-1">
              <span class="block truncate text-sm text-ink" :dir="textDirection(c.title)">{{ c.title || t('Untitled', 'بی‌عنوان') }}</span>
              <span class="block text-[11px] text-ink-muted">{{ formatDate(c.updatedAt) }}</span>
            </span>
          </button>
        </li>
      </ul>
    </div>

    <!-- The conversation -->
    <div v-else ref="scroller" class="min-h-0 flex-1 space-y-3 overflow-y-auto overscroll-contain px-4 py-4"
         role="log" aria-live="polite" :aria-busy="answering ? 'true' : 'false'" data-assistant-log>
      <div v-if="unavailable" class="flex items-start gap-2 rounded-xl border border-warn/20 bg-warn-soft px-3 py-2.5 text-sm leading-5 text-warn" role="status" data-assistant-unavailable>
        <AssistantIcon :icon="TriangleAlert" :size="14" class="mt-0.5" /><span :dir="textDirection(unavailableReason)">{{ unavailableReason }}</span>
      </div>
      <div v-else-if="outOfRequests" class="rounded-xl border border-warn/20 bg-warn-soft px-3 py-2.5 text-sm leading-5 text-warn" role="status">
        {{ t("Today's assistant requests for this workspace are used up.", 'سهمیهٔ درخواست‌های امروز دستیار برای این ورک‌اسپیس تمام شده است.') }}
      </div>

      <div v-if="timeline.loadError && !timeline.loading" class="space-y-2 rounded-xl border border-danger/20 bg-danger-soft px-3 py-2.5 text-sm leading-5 text-danger" role="alert" data-assistant-load-error>
        <p class="flex items-start gap-2">
          <AssistantIcon :icon="TriangleAlert" :size="14" class="mt-0.5 shrink-0" />
          <span :dir="textDirection(say(timeline.loadError))">{{ t('Your conversation could not be loaded.', 'گفت‌وگوی شما بارگذاری نشد.') }} {{ say(timeline.loadError) }}</span>
        </p>
        <div class="flex gap-2 ps-6">
          <button type="button" class="btn-secondary py-1.5" data-assistant-retry @click="retryLoad">{{ t('Try again', 'دوباره تلاش کنید') }}</button>
          <button type="button" class="btn-secondary py-1.5" @click="newConversation">{{ t('Start a new conversation', 'گفت‌وگوی تازه') }}</button>
        </div>
      </div>

      <p v-if="timeline.loading" class="flex items-center gap-2 text-sm text-ink-muted">
        <AssistantIcon :icon="LoaderCircle" spin />{{ t('Loading the conversation…', 'در حال بارگذاری گفت‌وگو…') }}
      </p>

      <div v-else-if="timeline.items.length === 0 && !unavailable && !timeline.loadError" class="space-y-3 pt-6 text-center">
        <p class="text-sm text-ink-muted">{{ t('Ask in Persian or English. Changes are shown to you as a plan first — nothing runs until you approve it.', 'به فارسی یا انگلیسی بپرسید. هر تغییری اول به‌صورت برنامه نشانتان داده می‌شود — تا تأیید نکنید، چیزی اجرا نمی‌شود.') }}</p>
        <div class="flex flex-col items-stretch gap-1.5">
          <button v-for="s in suggestions" :key="s" type="button" class="rounded-lg border border-line bg-surface-2 px-3 py-2 text-start text-sm text-ink-muted hover:border-accent/30 hover:text-ink" :dir="textDirection(s)" @click="useSuggestion(s)">{{ s }}</button>
        </div>
      </div>

      <template v-for="row in rows" :key="row.key">
        <!-- A run of tool lines is one block: open while it works, then one line — "3 actions done · 2.4 s"
             (assistantActivity.js, blockView). A failure keeps it open; the person's own toggle wins. -->
        <section v-if="row.type === 'activity'" class="overflow-hidden rounded-xl border bg-surface"
                 :class="row.view.tone === 'failed' ? 'border-danger/30' : 'border-line'"
                 data-assistant-activity :data-activity-tone="row.view.tone">
          <button type="button" class="flex w-full items-center gap-2 px-3 py-2 text-start text-sm hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent/40"
                  :aria-expanded="row.view.open ? 'true' : 'false'" :aria-controls="activityId(row)" data-activity-toggle @click="toggleBlock(row)">
            <AssistantIcon :icon="blockIcon(row.view.tone)" :size="14" :spin="row.view.working" :class="blockIconClass(row.view.tone)" />
            <span class="min-w-0 flex-1" :class="row.view.tone === 'failed' ? 'font-medium text-danger' : 'text-ink-muted'" data-activity-heading>{{ row.view.heading }}</span>
            <AssistantIcon :icon="ChevronDown" :size="14" class="text-ink-faint motion-safe:transition-transform" :class="row.view.open ? 'rotate-180' : ''" />
          </button>
          <!-- Each line: its category's icon, the tool's name for people (a failed one: what failed; a saved
               line that is not a read: its own words), its own duration, and a small mark for its state. A
               saved line has no mark — a stored tool message does not say whether the tool succeeded — so it
               is never drawn as a check. -->
          <ul v-show="row.view.open" :id="activityId(row)" class="space-y-1.5 border-t border-line px-3 py-2">
            <li v-for="item in row.block.items" :key="item.key" class="flex items-start gap-2 text-sm text-ink-muted" data-tool-line
                :data-tool-state="item.kind === 'tool' ? item.state : undefined" :data-tool-history="item.kind === 'tool-history' ? 'true' : undefined"
                :data-tool-category="item.category || undefined" :title="toolTitle(item)">
              <AssistantIcon :icon="categoryGlyph(item.category)" :size="14" class="mt-[3px] text-ink-faint" />
              <span class="min-w-0 flex-1 break-words" :class="item.state === 'failed' ? 'text-danger' : ''" :dir="textDirection(toolText(item))">{{ toolText(item) }}</span>
              <span v-if="formatSeconds(item.durationMs, fa)" class="mt-0.5 shrink-0 text-[11px] text-ink-muted" data-tool-duration>{{ formatSeconds(item.durationMs, fa) }}</span>
              <AssistantIcon v-if="markOf(item)" :icon="toolIcon(markOf(item))" :size="14" :spin="markOf(item) === 'running'" class="mt-[3px]" :class="toolIconClass(markOf(item))" />
            </li>
          </ul>
        </section>

        <!-- A plan that ran with every step done: its card and its result as one compact card. -->
        <AssistantResultCard v-else-if="row.type === 'ran'" :result="row.result.result" :plan="row.result.plan ?? row.plan?.plan ?? null" :fa="fa" compact />

        <div v-else-if="row.item.kind === 'user'" class="flex flex-col items-end gap-1" data-assistant-message="user" :data-unsent="row.item.unsent ? 'true' : undefined">
          <div class="max-w-[85%] whitespace-pre-wrap break-words rounded-2xl rounded-ee-md px-3 py-2 text-sm leading-6"
               :class="row.item.unsent ? 'border border-dashed border-line-strong bg-surface-2 text-ink' : 'bg-accent text-white'"
               :dir="textDirection(row.item.text)"><AssistantSecretText :text="row.item.text" :name-of="secrets.nameOf" :fa="fa" :inverse="!row.item.unsent" /></div>
          <span v-if="row.item.unsent === 'unsure'" class="text-[11px] text-ink-muted">{{ t('May not have been sent', 'شاید فرستاده نشده باشد') }}</span>
          <span v-else-if="row.item.unsent" class="text-[11px] text-ink-muted">{{ t('Not sent', 'فرستاده نشد') }}</span>
        </div>

        <!-- The model's text — the bubble being written while deltas arrive, then the whole answer. -->
        <div v-else-if="row.item.kind === 'assistant'" class="text-sm text-ink" data-assistant-message="assistant" :data-live="row.item.live ? 'true' : undefined">
          <AssistantText :text="row.item.text" :live="row.item.live === true" :streamed="row.item.streamed === true" :name-of="secrets.nameOf" :fa="fa" />
          <p v-if="row.item.cut" class="mt-1 text-[11px] text-ink-muted" data-assistant-cut>{{ t('The rest of this answer did not arrive.', 'بقیهٔ این پاسخ نرسید.') }}</p>
        </div>

        <AssistantPlanCard v-else-if="row.item.kind === 'plan'" :item="row.item" :fa="fa" :locked="answering"
                           @run="runPlan(row.item)" @cancel="cancelPlan(row.item)" />

        <AssistantResultCard v-else-if="row.item.kind === 'result'" :result="row.item.result" :plan="row.item.plan" :fa="fa" />

        <!-- The person pressed Stop: not a failure, one muted line in the server's words. -->
        <p v-else-if="row.item.kind === 'stopped'" class="flex items-center gap-2 text-sm text-ink-muted" data-assistant-stopped>
          <AssistantIcon :icon="SquareStop" :size="14" class="shrink-0 text-ink-faint" />
          <span class="min-w-0" :dir="textDirection(stoppedText(row.item))">{{ stoppedText(row.item) }}</span>
        </p>

        <div v-else-if="row.item.kind === 'error'" class="flex items-start gap-2 rounded-xl border border-danger/20 bg-danger-soft px-3 py-2.5 text-sm leading-5 text-danger" role="alert" data-assistant-error>
          <AssistantIcon :icon="TriangleAlert" :size="14" class="mt-0.5" />
          <span :dir="textDirection(say(row.item))">{{ say(row.item) }}</span>
        </div>
      </template>

      <!-- The thinking line: three dots as a bubble of their own, then what is happening — the running
           tool's name ("Reading the deployment's logs…"), else "Thinking…" — and from five seconds on,
           the seconds since it appeared. From the send until the first text, while a tool runs and after
           it until the next output, and for a conversation opened mid-answer (assistantActivity.js,
           thinkingLine). The dots stand still for anyone who asked for less motion; screen readers get
           the words from the live region below the log, which is not aria-busy, without the ticking. -->
      <div v-if="line" class="flex items-center gap-2" aria-hidden="true" data-assistant-thinking>
        <span class="assistant-dots inline-flex shrink-0 items-center gap-1 rounded-2xl rounded-es-md bg-surface-2 px-3 py-3">
          <span></span><span></span><span></span>
        </span>
        <span class="flex min-w-0 items-center gap-1.5 text-sm text-ink-muted" data-thinking-label>
          <AssistantIcon :icon="line.toolKey ? categoryGlyph(line.category) : Sparkles" :size="14" class="text-ink-faint" />
          <span class="min-w-0 truncate" :dir="textDirection(lineLabel)">{{ lineLabel }}</span>
          <span v-if="line.seconds !== null" class="shrink-0 text-[11px] text-ink-muted" data-thinking-seconds>{{ formatElapsed(line.seconds, fa) }}</span>
        </span>
      </div>
    </div>

    <!-- Always in the page, outside the log (which is aria-busy while a turn runs, and screen readers
         hold back changes inside a busy region): only its text changes, so what is happening —
         "Thinking…", the running tool's name — is announced when it changes. -->
    <p class="sr-only" role="status" aria-live="polite" data-assistant-thinking-status>{{ lineLabel }}</p>

    <!-- The lock button's form: a secret goes to the panel, and only a placeholder into the message.
         Deliberately not a login form to the browser: no <form> (Enter is handled here), no password
         field (the value is a text field masked by CSS), autocomplete off and every password manager's
         opt-out. As a <form> with a password field, Chrome and Edge offered to save "DB_PASSWORD" and
         the secret as a Harbora login when it closed — and then filled them into the sign-in page. -->
    <div v-if="secretForm.state.open" class="space-y-2 border-t border-line bg-surface-2 p-3" role="group"
         :aria-label="t('Add a secret privately', 'افزودن رمز به‌صورت خصوصی')" data-assistant-secret-form
         @keydown.esc.prevent.stop="closeSecretForm">
      <p class="flex items-start gap-2 text-sm leading-5 text-ink-muted">
        <AssistantIcon :icon="Lock" :size="14" class="mt-0.5 shrink-0" />
        <span>{{ t('The value goes to the panel only — the AI sees a placeholder, never the secret. It is kept for 30 minutes.', 'مقدار فقط به پنل می‌رود — هوش مصنوعی فقط یک جای‌نگهدار می‌بیند، نه خود رمز را. تا ۳۰ دقیقه نگه داشته می‌شود.') }}</span>
      </p>
      <label class="block text-sm text-ink-muted" for="assistant-secret-name">{{ t('Name', 'نام') }}</label>
      <input id="assistant-secret-name" ref="secretName" v-model="secretForm.state.name" type="text" dir="ltr" maxlength="100"
             autocomplete="off" data-1p-ignore data-lpignore="true" data-bwignore data-form-type="other" spellcheck="false"
             class="form-control py-1.5 font-mono text-sm" :placeholder="t('e.g. DATABASE_PASSWORD', 'مثلاً DATABASE_PASSWORD')" data-secret-name
             @keydown.enter.prevent="holdSecret" />
      <label class="block text-sm text-ink-muted" for="assistant-secret-value">{{ t('Secret', 'مقدار') }}</label>
      <div class="flex items-center gap-2">
        <input id="assistant-secret-value" v-model="secretForm.state.value" type="text" dir="ltr"
               autocomplete="off" data-1p-ignore data-lpignore="true" data-bwignore data-form-type="other" spellcheck="false"
               autocapitalize="off" autocorrect="off"
               class="form-control py-1.5 font-mono text-sm" :class="secretForm.state.reveal ? '' : 'assistant-secret-mask'" data-secret-value
               @keydown.enter.prevent="holdSecret" />
        <button type="button" class="btn-secondary px-2 py-1.5" :aria-pressed="secretForm.state.reveal"
                :aria-label="secretForm.state.reveal ? t('Hide the secret', 'پنهان کردن') : t('Show the secret', 'نمایش')"
                @click="secretForm.state.reveal = !secretForm.state.reveal">
          <AssistantIcon :icon="secretForm.state.reveal ? EyeOff : Eye" :size="14" />
        </button>
      </div>
      <p v-if="secretForm.state.error" class="field-error text-sm" role="alert" :dir="textDirection(say(secretForm.state.error))">{{ say(secretForm.state.error) }}</p>
      <div class="flex gap-2">
        <button type="button" class="btn-primary py-1.5" :disabled="secretForm.state.busy" data-secret-add @click="holdSecret">
          <AssistantIcon v-if="secretForm.state.busy" :icon="LoaderCircle" spin />{{ t('Add to message', 'افزودن به پیام') }}
        </button>
        <button type="button" class="btn-secondary py-1.5" @click="closeSecretForm">{{ t('Cancel', 'لغو') }}</button>
      </div>
    </div>

    <!-- Composer -->
    <form class="border-t border-line p-3" @submit.prevent="send">
      <!-- A password or key typed into the message: the AI would see it. Offered, never forced. -->
      <p v-if="secretWarning" class="mb-2 flex items-start gap-2 rounded-lg border border-warn/20 bg-warn-soft px-3 py-2 text-sm leading-5 text-warn" role="status" data-assistant-secret-warning>
        <AssistantIcon :icon="TriangleAlert" :size="14" class="mt-0.5 shrink-0" />
        <span class="flex-1">
          {{ t('This looks like a password or key. Messages go to the AI — give it with the lock button so only the panel sees it.', 'این شبیه رمز یا کلید است. پیام‌ها به هوش مصنوعی می‌رسند — با دکمهٔ قفل بدهید تا فقط پنل آن را ببیند.') }}
          <template v-if="warnedDraft && warnedDraft === draft.trim()">{{ t(' Press Send again to send it as it is.', ' برای فرستادن همین متن، دوباره «ارسال» را بزنید.') }}</template>
          <button type="button" class="ms-1 font-semibold underline" @click="openSecretForm">{{ t('Use the lock button', 'استفاده از دکمهٔ قفل') }}</button>
        </span>
      </p>
      <!-- Information, not a failure: "still answering", "your last message is back in the box". -->
      <p v-if="timeline.notice" class="mb-2 flex items-start gap-2 rounded-lg bg-surface-2 px-3 py-2 text-sm leading-5 text-ink-muted" role="status" data-assistant-notice
         :dir="textDirection(say(timeline.notice))">
        <AssistantIcon :icon="Info" :size="14" class="mt-0.5 shrink-0" /><span>{{ say(timeline.notice) }}</span>
      </p>
      <div class="flex items-end gap-2 rounded-xl border border-line-strong bg-surface px-2 py-1.5 focus-within:border-accent focus-within:ring-2 focus-within:ring-accent/15">
        <label for="assistant-composer" class="sr-only">{{ t('Message the assistant', 'پیام به دستیار') }}</label>
        <textarea id="assistant-composer" ref="composer" v-model="draft" rows="1" :dir="firstStrongDirection(withoutSecrets(draft), fa ? 'rtl' : 'ltr')"
                  :disabled="unavailable || outOfRequests"
                  :placeholder="t('Ask the assistant…', 'از دستیار بپرسید…')"
                  class="max-h-40 min-h-[2.25rem] flex-1 resize-none bg-transparent px-1 py-1.5 text-sm text-ink outline-none placeholder:text-ink-faint focus-visible:outline-none disabled:cursor-not-allowed"
                  data-assistant-composer @keydown="onComposerKey" @input="resizeComposer"></textarea>
        <button type="button" class="grid h-8 w-8 shrink-0 place-items-center rounded-lg text-ink-muted hover:bg-surface-2 hover:text-ink disabled:cursor-not-allowed disabled:opacity-40"
                :disabled="unavailable || outOfRequests" :aria-pressed="secretForm.state.open"
                :aria-label="t('Add a secret privately', 'افزودن رمز به‌صورت خصوصی')" :title="t('Add a secret — the AI never sees it', 'افزودن رمز — هوش مصنوعی آن را نمی‌بیند')"
                data-assistant-lock @click="secretForm.state.open ? closeSecretForm() : openSecretForm()">
          <AssistantIcon :icon="Lock" />
        </button>
        <button type="submit" class="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-accent text-white hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-40"
                :disabled="!canSend" :aria-label="t('Send', 'ارسال')" :title="t('Send', 'ارسال')" data-assistant-send>
          <AssistantIcon :icon="answering ? LoaderCircle : ArrowUp" :spin="answering" />
        </button>
      </div>
      <p class="mt-1.5 px-1 text-[11px] text-ink-muted">{{ t('Enter to send · Shift+Enter for a new line · Ctrl+J to open or close', 'Enter برای ارسال · Shift+Enter برای خط تازه · Ctrl+J برای باز و بسته کردن') }}</p>
    </form>
  </div>
</template>
