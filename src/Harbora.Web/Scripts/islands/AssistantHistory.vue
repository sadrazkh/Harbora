<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { ChevronLeft, ChevronRight, LoaderCircle, MessageSquare, Search, Trash2, TriangleAlert } from 'lucide';
import { AssistantIcon } from './assistantIcon';
import { textDirection } from './assistantFormat.js';
import { createConversationList, groupConversations, groupLabel, timeLabel } from './assistantHistory.js';

// The pane's conversation list: a title search, rows grouped by day, "show more", and deleting — one
// conversation (the row turns into a question, in place) or all of them (the question at the bottom).
//
// Every rule is in assistantHistory.js (tested by node --test); this file draws its state and keeps the
// keyboard where the person is: «نه» takes the focus when a question opens, Esc closes it (without also
// closing the drawer), and a row that is deleted hands the focus to its neighbour instead of dropping it
// on the page. It tells its parent three things — `open(id)`, `back`, and `deleted(id)` — and the parent
// decides what a deleted conversation means for the one on screen.

const props = defineProps<{
  api: any;
  fa: boolean;
  /** The conversation on screen, which gets the soft background. */
  currentId: string | null;
  /** Why the conversation just asked for could not be opened (the parent's words). */
  openError?: { messageEn: string; messageFa: string } | null;
}>();
const emit = defineEmits<{
  (e: 'open', id: string): void;
  (e: 'back'): void;
  (e: 'deleted', id: string): void;
}>();

const t = (en: string, persian: string) => (props.fa ? persian : en);
const say = (w: { messageEn: string; messageFa: string } | null | undefined) =>
  (w ? (props.fa ? w.messageFa || w.messageEn : w.messageEn || w.messageFa) : '');
const Back = props.fa ? ChevronRight : ChevronLeft;

const list = createConversationList({ api: props.api, wrap: reactive, onDeleted: (id: string) => emit('deleted', id) });
const state = list.state as any;

const groups = computed(() => groupConversations(state.rows, new Date()));
const working = computed(() => state.confirm?.phase === 'working');
const number = (n: number) => new Intl.NumberFormat(props.fa ? 'fa-IR' : 'en').format(n);
const keptLine = computed(() => {
  const n = state.kept as number;
  return n === 1
    ? t('1 conversation was kept because it is still answering or running something.', '۱ گفت‌وگو حذف نشد، چون هنوز در حال پاسخ یا اجراست.')
    : t(`${number(n)} conversations were kept because they are still answering or running something.`, `${number(n)} گفت‌وگو حذف نشد، چون هنوز در حال پاسخ یا اجراست.`);
});

const asking = (id: string) => state.confirm?.target === 'one' && state.confirm.id === id;
const askingAll = computed(() => state.confirm?.target === 'all');

// A danger button inside the soft-danger strip is an outline on the surface colour: a soft red button on
// a soft red strip would not read as a button.
const dangerButton = 'inline-flex items-center gap-2 rounded-lg border border-danger/40 bg-surface px-3 py-1.5 text-sm font-semibold text-danger hover:bg-danger-soft';

// ---- keeping the focus where the person is ------------------------------------------------------------

const root = ref<HTMLElement | null>(null);
const searchBox = ref<HTMLInputElement | null>(null);
/** Where the focus goes when the question closes: the trigger, or the neighbour of a row that is gone. */
let returnTo: { id: string | null; next: string | null } | null = null;

function askRow(id: string) {
  const at = state.rows.findIndex((row: any) => row.id === id);
  returnTo = { id, next: state.rows[at + 1]?.id ?? state.rows[at - 1]?.id ?? null };
  list.ask(id);
}

function askAll() {
  returnTo = { id: null, next: null };
  list.askAll();
}

watch(() => {
  const c = state.confirm;
  return c ? `${c.target}|${c.id}|${c.phase}` : '';
}, async (now) => {
  await nextTick();
  const el = root.value;
  if (!el) return;
  if (now) {
    // «نه» / «باشه» / «دوباره»; while the request is out, the strip itself, so Esc still lands in it.
    (el.querySelector<HTMLElement>('[data-confirm-focus]') ?? el.querySelector<HTMLElement>('[data-confirm-strip]'))?.focus();
    return;
  }
  const back = returnTo;
  returnTo = null;
  if (!back) return;
  const target = (back.id && el.querySelector<HTMLElement>(`[data-trash="${back.id}"]`))
    || (back.next && el.querySelector<HTMLElement>(`[data-conversation-open="${back.next}"]`))
    || (back.id === null && el.querySelector<HTMLElement>('[data-delete-all]'))
    || searchBox.value;
  target?.focus();
});

// Esc in the search box clears it first; with nothing in it Esc is the drawer's (it closes).
function onSearchKey(event: KeyboardEvent) {
  if (event.key === 'Escape' && state.query) {
    event.preventDefault();
    event.stopPropagation();
    list.setQuery('');
  }
}

onMounted(() => { list.load(); });
onUnmounted(() => list.dispose());
</script>

<template>
  <div ref="root" class="flex min-h-0 flex-1 flex-col" data-assistant-conversations>
    <div class="space-y-2 px-3 pt-3">
      <button type="button" class="flex items-center gap-1 text-sm text-ink-muted hover:text-ink" data-history-back @click="emit('back')">
        <AssistantIcon :icon="Back" :size="14" />{{ t('Back to the conversation', 'بازگشت به گفت‌وگو') }}
      </button>

      <div class="relative">
        <label for="assistant-history-search" class="sr-only">{{ t('Search titles', 'جست‌وجو در عنوان‌ها') }}</label>
        <span class="pointer-events-none absolute inset-y-0 start-3 grid place-items-center text-ink-faint"><AssistantIcon :icon="Search" :size="14" /></span>
        <input id="assistant-history-search" ref="searchBox" type="search" autocomplete="off" spellcheck="false"
               class="form-control py-1.5 ps-9" :value="state.query" :placeholder="t('Search titles', 'جست‌وجو در عنوان‌ها')"
               data-history-search @input="list.setQuery(($event.target as HTMLInputElement).value)" @keydown="onSearchKey" />
      </div>
    </div>

    <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain px-3 pb-3" :aria-busy="state.loading ? 'true' : 'false'" data-history-list>
      <p v-if="openError" class="alert-danger mt-2 text-sm" role="alert" :dir="textDirection(say(openError))">{{ say(openError) }}</p>
      <p v-if="state.kept > 0" class="mt-2 rounded-lg bg-surface-2 px-3 py-2 text-sm leading-5 text-ink-muted" role="status" data-history-kept>{{ keptLine }}</p>

      <p v-if="!state.loaded && state.loading" class="flex items-center gap-2 p-3 text-sm text-ink-muted">
        <AssistantIcon :icon="LoaderCircle" spin />{{ t('Loading…', 'در حال بارگذاری…') }}
      </p>
      <div v-else-if="state.error" class="alert-danger mt-2 space-y-2 text-sm" role="alert">
        <p :dir="textDirection(say(state.error))">{{ say(state.error) }}</p>
        <button type="button" class="btn-secondary py-1.5" data-history-retry @click="list.load()">{{ t('Try again', 'دوباره تلاش کنید') }}</button>
      </div>
      <p v-else-if="state.loading && state.rows.length === 0" class="flex items-center gap-2 p-3 text-sm text-ink-muted">
        <AssistantIcon :icon="LoaderCircle" spin />{{ t('Loading…', 'در حال بارگذاری…') }}
      </p>
      <p v-else-if="state.rows.length === 0" class="p-3 text-sm text-ink-muted" role="status" data-history-empty>
        {{ state.shown ? t('No conversation with that title', 'گفت‌وگویی با این عنوان پیدا نشد') : t('No conversations yet.', 'هنوز گفت‌وگویی نیست.') }}
      </p>

      <template v-else>
        <div :class="state.loading ? 'opacity-60' : ''">
          <section v-for="g in groups" :key="g.key" :aria-labelledby="`assistant-history-group-${g.key}`" :data-history-group="g.key">
            <h3 :id="`assistant-history-group-${g.key}`" class="px-2.5 pb-1 pt-3 text-[11px] font-medium text-ink-muted">{{ groupLabel(g.key, fa) }}</h3>
            <ul class="space-y-0.5">
              <li v-for="c in g.rows" :key="c.id" class="assistant-row relative" data-history-row>
                <!-- The question that replaces the row. Nothing is deleted until «حذف». -->
                <div v-if="asking(c.id)" class="rounded-lg border border-danger/20 bg-danger-soft px-3 py-2.5" role="group" tabindex="-1"
                     :aria-label="t('Delete this conversation?', 'این گفت‌وگو حذف شود؟')" data-confirm-strip data-history-confirm="one"
                     @keydown.esc.stop.prevent="list.cancel()">
                  <template v-if="state.confirm.phase === 'asking'">
                    <p class="text-sm font-medium text-danger">{{ t('Delete this conversation?', 'این گفت‌وگو حذف شود؟') }}</p>
                    <p class="mt-0.5 truncate text-[11px] text-ink-muted" :dir="textDirection(c.title)">{{ c.title || t('Untitled', 'بی‌عنوان') }}</p>
                    <div class="mt-2 flex gap-2">
                      <button type="button" :class="dangerButton" data-history-confirm-yes @click="list.confirmDelete()">{{ t('Delete', 'حذف') }}</button>
                      <button type="button" class="btn-secondary py-1.5" data-confirm-focus data-history-confirm-no @click="list.cancel()">{{ t('No', 'نه') }}</button>
                    </div>
                  </template>
                  <p v-else-if="state.confirm.phase === 'working'" class="flex items-center gap-2 text-sm text-danger" role="status">
                    <AssistantIcon :icon="LoaderCircle" spin />{{ t('Deleting…', 'در حال حذف…') }}
                  </p>
                  <template v-else>
                    <p class="flex items-start gap-2 text-sm leading-5 text-danger" role="alert">
                      <AssistantIcon :icon="TriangleAlert" :size="14" class="mt-0.5" /><span class="min-w-0" :dir="textDirection(say(state.confirm.error))">{{ say(state.confirm.error) }}</span>
                    </p>
                    <div class="mt-2 flex gap-2">
                      <button v-if="state.confirm.phase === 'refused'" type="button" class="btn-secondary py-1.5" data-confirm-focus data-history-confirm-ok @click="list.cancel()">{{ t('OK', 'باشه') }}</button>
                      <template v-else>
                        <button type="button" :class="dangerButton" data-confirm-focus data-history-confirm-retry @click="list.confirmDelete()">{{ t('Try again', 'دوباره') }}</button>
                        <button type="button" class="btn-secondary py-1.5" @click="list.cancel()">{{ t('Close', 'بستن') }}</button>
                      </template>
                    </div>
                  </template>
                </div>

                <template v-else>
                  <button type="button" class="flex w-full items-start gap-2.5 rounded-lg py-2 ps-2.5 pe-11 text-start hover:bg-surface-2"
                          :class="c.id === currentId ? 'bg-accent-soft' : ''" :aria-current="c.id === currentId ? 'true' : undefined"
                          :data-conversation-open="c.id" @click="emit('open', c.id)">
                    <AssistantIcon :icon="MessageSquare" class="mt-0.5 text-ink-faint" />
                    <span class="min-w-0 flex-1">
                      <span :id="`assistant-conversation-${c.id}`" class="block truncate text-sm text-ink" :dir="textDirection(c.title)">{{ c.title || t('Untitled', 'بی‌عنوان') }}</span>
                      <time :datetime="c.updatedAt" class="block text-[11px] text-ink-muted">{{ timeLabel(c.updatedAt, new Date(), fa) }}</time>
                    </span>
                  </button>
                  <button type="button" class="assistant-row-trash absolute inset-y-0 end-1 my-auto grid h-8 w-8 place-items-center rounded-lg text-ink-faint hover:bg-danger-soft hover:text-danger focus-visible:text-danger"
                          :disabled="working" :aria-label="t('Delete this conversation', 'حذف این گفت‌وگو')" :title="t('Delete this conversation', 'حذف این گفت‌وگو')"
                          :aria-describedby="`assistant-conversation-${c.id}`" :data-trash="c.id" @click="askRow(c.id)">
                    <AssistantIcon :icon="Trash2" :size="15" />
                  </button>
                </template>
              </li>
            </ul>
          </section>
        </div>

        <div v-if="state.hasMore" class="flex flex-col items-center gap-2 pt-3">
          <p v-if="state.moreError" class="text-center text-sm text-danger" role="alert" :dir="textDirection(say(state.moreError))">{{ say(state.moreError) }}</p>
          <button type="button" class="btn-secondary py-1.5" :disabled="state.loadingMore || state.loading" data-history-more @click="list.loadMore()">
            <AssistantIcon v-if="state.loadingMore" :icon="LoaderCircle" spin />{{ t('Show more', 'نمایش بیشتر') }}
          </button>
        </div>
      </template>
    </div>

    <!-- Delete all: apart from the list, under a hairline, and asked the same way as one. -->
    <div v-if="state.rows.length > 0 || askingAll" class="border-t border-line px-3 py-2.5" data-history-delete-all>
      <div v-if="askingAll" class="rounded-lg border border-danger/20 bg-danger-soft px-3 py-2.5" role="group" tabindex="-1"
           :aria-label="t('Delete all conversations?', 'همهٔ گفت‌وگوها حذف شوند؟')" data-confirm-strip data-history-confirm="all"
           @keydown.esc.stop.prevent="list.cancel()">
        <template v-if="state.confirm.phase === 'asking'">
          <p class="text-sm font-medium text-danger">{{ t('Delete all conversations?', 'همهٔ گفت‌وگوها حذف شوند؟') }}</p>
          <p v-if="state.shown" class="mt-0.5 text-[11px] leading-4 text-ink-muted">{{ t('Every conversation is deleted, not only the ones found.', 'همهٔ گفت‌وگوها حذف می‌شوند، نه فقط نتیجه‌های جست‌وجو.') }}</p>
          <p class="mt-0.5 text-[11px] leading-4 text-ink-muted">{{ t('What the assistant did is not undone, and the audit log keeps it.', 'کارهایی که دستیار انجام داده برنمی‌گردند و در گزارش رخدادها می‌مانند.') }}</p>
          <div class="mt-2 flex gap-2">
            <button type="button" :class="dangerButton" data-history-confirm-yes @click="list.confirmDeleteAll({ openId: currentId })">{{ t('Delete all', 'حذف همه') }}</button>
            <button type="button" class="btn-secondary py-1.5" data-confirm-focus data-history-confirm-no @click="list.cancel()">{{ t('No', 'نه') }}</button>
          </div>
        </template>
        <p v-else-if="state.confirm.phase === 'working'" class="flex items-center gap-2 text-sm text-danger" role="status">
          <AssistantIcon :icon="LoaderCircle" spin />{{ t('Deleting…', 'در حال حذف…') }}
        </p>
        <template v-else>
          <p class="flex items-start gap-2 text-sm leading-5 text-danger" role="alert">
            <AssistantIcon :icon="TriangleAlert" :size="14" class="mt-0.5" /><span class="min-w-0" :dir="textDirection(say(state.confirm.error))">{{ say(state.confirm.error) }}</span>
          </p>
          <div class="mt-2 flex gap-2">
            <button v-if="state.confirm.phase === 'refused'" type="button" class="btn-secondary py-1.5" data-confirm-focus data-history-confirm-ok @click="list.cancel()">{{ t('OK', 'باشه') }}</button>
            <template v-else>
              <button type="button" :class="dangerButton" data-confirm-focus data-history-confirm-retry @click="list.confirmDeleteAll({ openId: currentId })">{{ t('Try again', 'دوباره') }}</button>
              <button type="button" class="btn-secondary py-1.5" @click="list.cancel()">{{ t('Close', 'بستن') }}</button>
            </template>
          </div>
        </template>
      </div>
      <template v-else>
        <button type="button" class="inline-flex items-center gap-1.5 rounded-lg px-2 py-1.5 text-sm font-medium text-danger hover:bg-danger-soft"
                :disabled="working" data-delete-all @click="askAll">
          <AssistantIcon :icon="Trash2" :size="14" />{{ t('Delete all conversations', 'حذف همهٔ گفت‌وگوها') }}
        </button>
        <p class="px-2 text-[11px] text-ink-muted">{{ t('What was done is not undone', 'کارهای انجام‌شده برنمی‌گردند') }}</p>
      </template>
    </div>
  </div>
</template>
