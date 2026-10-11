<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref } from 'vue';
import {
  ArrowUp, Check, ChevronLeft, ChevronRight, CircleHelp, History, Info, LoaderCircle, MessageSquare,
  Plus, Sparkles, TriangleAlert, Wrench, X,
} from 'lucide';
import { AssistantIcon } from './assistantIcon';
import AssistantText from './AssistantText.vue';
import AssistantPlanCard from './AssistantPlanCard.vue';
import AssistantResultCard from './AssistantResultCard.vue';
import { createAssistantApi, buildPageContext, parseDeclaredContext } from './assistantApi.js';
import { textDirection, firstStrongDirection } from './assistantFormat.js';
import {
  createTimeline, applyStreamEvent, applyPlanResult, applyApproveRefusal, markPlanRejected, isThinking,
} from './assistantTimeline.js';
import { checkPlanForm, buildApproveBody } from './assistantPlan.js';
import { createAssistantSession } from './assistantSession.js';
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
const thinking = computed(() => isThinking(timeline));
// The composer waits while a turn is streaming here or still being answered after a reload.
const answering = computed(() => timeline.streaming || timeline.resuming);

const view = ref<'chat' | 'history'>('chat');
const conversations = ref<any[]>([]);
const conversationsLoading = ref(false);
const conversationsError = ref<{ messageEn: string; messageFa: string } | null>(null);

const draft = ref('');
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
  session.startNew();
  view.value = 'chat';
  focusComposer();
}

/** Words that were not taken go back in the composer — unless the person has started typing again. */
function restoreDraft(text?: string | null) {
  if (!text || draft.value.trim()) return;
  draft.value = text;
  nextTick(resizeComposer);
}

function formatDate(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat(fa ? 'fa-IR' : 'en-GB', { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

// ---- sending -----------------------------------------------------------------------------------

async function send() {
  const text = draft.value.trim();
  if (!canSend.value || !text) return;
  draft.value = '';
  resizeComposer();
  const outcome = await session.send(text);
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
    applyApproveRefusal(timeline, item.plan.id, { ...words(e), stepIndex: e?.stepIndex ?? null, planStatus: e?.planStatus ?? null });
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
    applyApproveRefusal(timeline, item.plan.id, { ...words(e), stepIndex: null, planStatus: e?.planStatus ?? null });
  } finally {
    item.busy = false;
  }
}

// ---- tool lines --------------------------------------------------------------------------------

function toolText(item: any) {
  const summary = fa ? item.summaryFa || item.summaryEn : item.summaryEn || item.summaryFa;
  if (summary) return summary;
  switch (item.state) {
    case 'running': return t(`Running ${item.name}…`, `در حال اجرای ${item.name}…`);
    case 'done': return t(`${item.name} finished`, `${item.name} انجام شد`);
    case 'failed': return t(`${item.name} failed`, `${item.name} ناموفق بود`);
    case 'interrupted': return t(`${item.name} — no result arrived`, `${item.name} — نتیجه‌ای نرسید`);
    default: return t(`${item.name} — outcome unknown`, `${item.name} — نتیجه نامشخص`);
  }
}
const toolIcon = (state: string) => ({ running: LoaderCircle, done: Check, failed: X } as Record<string, any>)[state] ?? CircleHelp;
const toolIconClass = (state: string) => ({ running: 'text-accent-text', done: 'text-ok', failed: 'text-danger' } as Record<string, string>)[state] ?? 'text-ink-faint';

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

      <template v-for="item in timeline.items" :key="item.key">
        <div v-if="item.kind === 'user'" class="flex flex-col items-end gap-1" data-assistant-message="user" :data-unsent="item.unsent ? 'true' : undefined">
          <div class="max-w-[85%] whitespace-pre-wrap break-words rounded-2xl rounded-ee-md px-3 py-2 text-sm leading-6"
               :class="item.unsent ? 'border border-dashed border-line-strong bg-surface-2 text-ink' : 'bg-accent text-white'"
               :dir="textDirection(item.text)">{{ item.text }}</div>
          <span v-if="item.unsent === 'unsure'" class="text-[11px] text-ink-muted">{{ t('May not have been sent', 'شاید فرستاده نشده باشد') }}</span>
          <span v-else-if="item.unsent" class="text-[11px] text-ink-muted">{{ t('Not sent', 'فرستاده نشد') }}</span>
        </div>

        <!-- The model's text — the bubble being written while deltas arrive, then the whole answer. -->
        <div v-else-if="item.kind === 'assistant'" class="text-sm text-ink" data-assistant-message="assistant" :data-live="item.live ? 'true' : undefined">
          <AssistantText :text="item.text" :steady="item.streamed === true" />
          <p v-if="item.cut" class="mt-1 text-[11px] text-ink-muted" data-assistant-cut>{{ t('The rest of this answer did not arrive.', 'بقیهٔ این پاسخ نرسید.') }}</p>
        </div>

        <div v-else-if="item.kind === 'tool'" class="flex items-center gap-2 text-sm text-ink-muted" :data-tool-state="item.state">
          <AssistantIcon :icon="toolIcon(item.state)" :size="14" :spin="item.state === 'running'" :class="toolIconClass(item.state)" />
          <span class="min-w-0 truncate" :dir="textDirection(toolText(item))">{{ toolText(item) }}</span>
        </div>

        <details v-else-if="item.kind === 'tool-history'" class="group text-[11px] text-ink-muted">
          <summary class="flex cursor-pointer list-none items-center gap-2 [&::-webkit-details-marker]:hidden">
            <AssistantIcon :icon="Wrench" :size="14" />
            <span>{{ t('Tool result:', 'نتیجهٔ ابزار:') }} <code dir="ltr" class="font-mono">{{ item.toolName }}</code></span>
          </summary>
          <pre class="mt-1.5 max-h-48 overflow-auto whitespace-pre-wrap break-words rounded-lg bg-surface-2 p-2 font-mono text-[11px] leading-4 text-ink-muted" dir="ltr">{{ item.text }}</pre>
        </details>

        <AssistantPlanCard v-else-if="item.kind === 'plan'" :item="item" :fa="fa" :locked="answering"
                           @run="runPlan(item)" @cancel="cancelPlan(item)" />

        <AssistantResultCard v-else-if="item.kind === 'result'" :result="item.result" :plan="item.plan" :fa="fa" />

        <div v-else-if="item.kind === 'error'" class="flex items-start gap-2 rounded-xl border border-danger/20 bg-danger-soft px-3 py-2.5 text-sm leading-5 text-danger" role="alert" data-assistant-error>
          <AssistantIcon :icon="TriangleAlert" :size="14" class="mt-0.5" />
          <span :dir="textDirection(fa ? item.messageFa || item.messageEn : item.messageEn || item.messageFa)">{{ fa ? item.messageFa || item.messageEn : item.messageEn || item.messageFa }}</span>
        </div>
      </template>

      <!-- Thinking: three dots, as a bubble of its own, from the send until the first text or tool line,
           after a tool until the next output, and for a conversation opened mid-answer. The dots stand
           still for anyone who asked for less motion; the words for screen readers are in the live
           region below the log, which is not aria-busy. -->
      <div v-if="thinking" class="flex" data-assistant-thinking>
        <span class="assistant-dots inline-flex items-center gap-1 rounded-2xl rounded-es-md bg-surface-2 px-3 py-3" aria-hidden="true">
          <span></span><span></span><span></span>
        </span>
      </div>
    </div>

    <!-- Always in the page, outside the log (which is aria-busy while a turn runs, and screen readers
         hold back changes inside a busy region): only its text changes, so "Thinking…" is announced
         when the dots appear. -->
    <p class="sr-only" role="status" aria-live="polite" data-assistant-thinking-status>{{ thinking ? t('Thinking…', 'در حال فکر کردن…') : '' }}</p>

    <!-- Composer -->
    <form class="border-t border-line p-3" @submit.prevent="send">
      <!-- Information, not a failure: "still answering", "your last message is back in the box". -->
      <p v-if="timeline.notice" class="mb-2 flex items-start gap-2 rounded-lg bg-surface-2 px-3 py-2 text-sm leading-5 text-ink-muted" role="status" data-assistant-notice
         :dir="textDirection(say(timeline.notice))">
        <AssistantIcon :icon="Info" :size="14" class="mt-0.5 shrink-0" /><span>{{ say(timeline.notice) }}</span>
      </p>
      <div class="flex items-end gap-2 rounded-xl border border-line-strong bg-surface px-2 py-1.5 focus-within:border-accent focus-within:ring-2 focus-within:ring-accent/15">
        <label for="assistant-composer" class="sr-only">{{ t('Message the assistant', 'پیام به دستیار') }}</label>
        <textarea id="assistant-composer" ref="composer" v-model="draft" rows="1" :dir="firstStrongDirection(draft, fa ? 'rtl' : 'ltr')"
                  :disabled="unavailable || outOfRequests"
                  :placeholder="t('Ask the assistant…', 'از دستیار بپرسید…')"
                  class="max-h-40 min-h-[2.25rem] flex-1 resize-none bg-transparent px-1 py-1.5 text-sm text-ink outline-none placeholder:text-ink-faint focus-visible:outline-none disabled:cursor-not-allowed"
                  data-assistant-composer @keydown="onComposerKey" @input="resizeComposer"></textarea>
        <button type="submit" class="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-accent text-white hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-40"
                :disabled="!canSend" :aria-label="t('Send', 'ارسال')" :title="t('Send', 'ارسال')" data-assistant-send>
          <AssistantIcon :icon="answering ? LoaderCircle : ArrowUp" :spin="answering" />
        </button>
      </div>
      <p class="mt-1.5 px-1 text-[11px] text-ink-muted">{{ t('Enter to send · Shift+Enter for a new line · Ctrl+J to open or close', 'Enter برای ارسال · Shift+Enter برای خط تازه · Ctrl+J برای باز و بسته کردن') }}</p>
    </form>
  </div>
</template>
