// The conversation list of the assistant pane (round seven, P1): rows grouped by day, a title search,
// "show more", and deleting — one conversation at a time, or all of them.
//
// Everything with a rule in it is here, over a plain state object with the API and the clock injected, so
// `node --test` drives it without a browser; AssistantHistory.vue only wires it to markup. The rules that
// matter, because the list is where a person decides to throw something away:
//
//   * a row is removed only after the server said it is gone (204, or 404 — gone is gone). A refusal
//     ("still answering") or a failure leaves the row where it was and says why, in the strip that replaced
//     the row; nothing is ever shown as deleted that is not;
//   * a page of results is applied only if it answers the question still being asked: a response for a
//     query the person has since changed, or for a "show more" asked before a delete moved every row up
//     by one, is dropped instead of being drawn over newer words;
//   * a row that was deleted does not come back because an older page, asked before the delete finished,
//     arrived after it.
//
// Server shapes: AgentViews.cs (`AgentConversationSummaryView`, `AgentDeleteAllView`); the endpoints are
// in assistantApi.js.

/** The server's page size (AgentConversationService.ConversationListSize): a shorter page is the last. */
export const PAGE_SIZE = 30;

/** How long the search box waits after the last keystroke before asking the server. */
export const SEARCH_DEBOUNCE_MS = 300;

/** The groups, newest first. */
export const GROUP_KEYS = ['today', 'yesterday', 'week', 'older'];

const GROUP_LABELS = {
  today: ['Today', 'امروز'],
  yesterday: ['Yesterday', 'دیروز'],
  week: ['Previous 7 days', '۷ روز گذشته'],
  older: ['Older', 'قدیمی‌تر'],
};

/** The caption of a group, in the panel's language. */
export function groupLabel(key, fa) {
  const label = GROUP_LABELS[key] ?? GROUP_LABELS.older;
  return fa ? label[1] : label[0];
}

/**
 * Whole calendar days since 1970 of a moment's *local* date. Built from the local year, month and day
 * and counted in UTC, so a day is always one day: across a daylight-saving change a local day is 23 or
 * 25 hours long, and dividing a difference of timestamps by 24 hours would put a row at 00:30 in the
 * wrong group on those two days a year.
 */
function localDay(date) {
  return Math.floor(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / 86_400_000);
}

const asDate = (value) => (value instanceof Date ? value : new Date(value));

/**
 * Which group a moment belongs to, by the browser's local calendar date, relative to `now`:
 * today; yesterday; the previous 7 days (2 to 7 calendar days ago — yesterday has its own group above
 * it, so "7 days" is the week before yesterday's group, not one more); older. A time in the future
 * (a clock a little ahead of the server's) counts as today, and one that cannot be read as older.
 */
export function groupKey(updatedAt, now = new Date()) {
  const date = asDate(updatedAt);
  if (Number.isNaN(date.getTime())) return 'older';
  const daysAgo = localDay(asDate(now)) - localDay(date);
  if (daysAgo <= 0) return 'today';
  if (daysAgo === 1) return 'yesterday';
  if (daysAgo <= 7) return 'week';
  return 'older';
}

/**
 * The rows split into groups, newest group first, empty groups left out. Within a group the rows keep
 * the order they came in — the server's, most recently active first.
 *
 * @template {{ updatedAt: string }} T
 * @param {T[]} rows
 * @returns {{ key: string, rows: T[] }[]}
 */
export function groupConversations(rows, now = new Date()) {
  const buckets = new Map(GROUP_KEYS.map((key) => [key, []]));
  for (const row of rows ?? []) buckets.get(groupKey(row.updatedAt, now)).push(row);
  return GROUP_KEYS.filter((key) => buckets.get(key).length > 0).map((key) => ({ key, rows: buckets.get(key) }));
}

/**
 * The time under a row's title: today and yesterday show the time of day only (the group already says
 * which day), older rows show the date — in the Persian calendar for Persian, as the pane's dates always
 * were. Empty when the server's timestamp cannot be read.
 */
export function timeLabel(updatedAt, now = new Date(), fa = false) {
  const date = asDate(updatedAt);
  if (Number.isNaN(date.getTime())) return '';
  const key = groupKey(date, now);
  const options = key === 'today' || key === 'yesterday' ? { timeStyle: 'short' } : { dateStyle: 'medium' };
  return new Intl.DateTimeFormat(fa ? 'fa-IR' : 'en-GB', options).format(date);
}

/**
 * `page` appended to `rows`, leaving out any conversation already shown (one can move between pages
 * while the person scrolls: answering in it puts it first) or deleted here since the page was asked for.
 */
export function mergePage(rows, page, removed = new Set()) {
  const seen = new Set(rows.map((row) => row.id));
  const added = [];
  for (const row of page ?? []) {
    if (!row?.id || seen.has(row.id) || removed.has(row.id)) continue;
    seen.add(row.id);
    added.push(row);
  }
  return [...rows, ...added];
}

const words = (error) => ({
  messageEn: error?.messageEn ?? String(error),
  messageFa: error?.messageFa ?? error?.messageEn ?? String(error),
});

/**
 * The list's state and everything that changes it.
 *
 * `state.confirm` is the one open question at a time — `null`, or `{ target: 'one' | 'all', id, phase,
 * error }` with phase `asking` (the strip with Delete / No), `working` (the request is out), `refused`
 * (409: the server says it is still answering or running — the row stays), or `failed` (anything else;
 * the strip offers "try again"). While `working` nothing else can be asked or cancelled: the request is
 * out, and the strip has to stay to say how it ended.
 *
 * @param {{
 *   api: { conversations(o: { q?: string, skip?: number }): Promise<any[]>, deleteConversation(id: string): Promise<null>,
 *          deleteAllConversations(): Promise<{ deleted: number, skipped: number }>, conversation(id: string): Promise<any> },
 *   wrap?: <T extends object>(state: T) => T,
 *   onDeleted?: (id: string) => void,
 *   pageSize?: number,
 *   debounceMs?: number,
 *   timers?: { setTimeout: Function, clearTimeout: Function },
 * }} deps
 *   `wrap`: how the state is made reactive (`reactive` in the island; identity in tests).
 *   `onDeleted(id)`: a conversation is gone — deleted here, or found already gone. The pane uses it to
 *   forget the open one.
 */
export function createConversationList({
  api, wrap = (state) => state, onDeleted = () => {}, pageSize = PAGE_SIZE, debounceMs = SEARCH_DEBOUNCE_MS, timers,
}) {
  const clock = timers ?? { setTimeout: globalThis.setTimeout.bind(globalThis), clearTimeout: globalThis.clearTimeout.bind(globalThis) };
  const state = wrap({
    /** What the search box holds, as typed. */
    query: '',
    rows: [],
    /** A first page is wanted: the box was just typed in (the wait counts), or the request is out. */
    loading: false,
    loadingMore: false,
    error: null,
    moreError: null,
    /** The last page was a full one, so there may be another. */
    hasMore: false,
    /** The (trimmed) query the rows answer — what the empty state is worded by, not what is typed now. */
    shown: '',
    /** Whether any first page has been applied yet. */
    loaded: false,
    confirm: null,
    /** After "delete all": how many were kept because a turn or plan was still running in them. */
    kept: 0,
  });

  /** The query now wanted (trimmed). */
  let wanted = '';
  let timer = null;
  // Two numbers, because two things go stale differently. `listToken` changes when a different first page
  // is wanted (the query changed, a reload): everything in flight is then for something else. `moreToken`
  // changes with that too, and also when a delete moves the rows under a "show more" that is out.
  let listToken = 0;
  let moreToken = 0;
  const removed = new Set();

  function clearTimer() {
    if (timer !== null) clock.clearTimeout(timer);
    timer = null;
  }

  function staleMore() {
    moreToken++;
    state.loadingMore = false;
  }

  async function fetchFirst() {
    clearTimer();
    const mine = ++listToken;
    staleMore();
    const q = wanted;
    state.loading = true;
    state.error = null;
    state.moreError = null;
    try {
      const page = (await api.conversations({ q, skip: 0 })) ?? [];
      if (mine !== listToken) return;
      state.rows = mergePage([], page, removed);
      state.hasMore = page.length >= pageSize;
      state.shown = q;
      state.loaded = true;
      // A question about a row that is not in this list any more has nothing to be asked about.
      const open = state.confirm;
      if (open?.target === 'one' && open.phase !== 'working' && !state.rows.some((row) => row.id === open.id)) state.confirm = null;
    } catch (error) {
      if (mine !== listToken) return;
      state.error = words(error);
    } finally {
      if (mine === listToken) state.loading = false;
    }
  }

  /** Removes a row the server no longer has, and tells the pane. */
  function gone(id) {
    removed.add(id);
    state.rows = state.rows.filter((row) => row.id !== id);
    state.confirm = null;
    // A "show more" asked before this asked for an offset that no longer matches the rows.
    staleMore();
    onDeleted(id);
  }

  const list = {
    state,

    /** The first page for the current query — on open, and "try again". */
    load: fetchFirst,

    /** The search box changed. Asks the server `debounceMs` after the last keystroke. */
    setQuery(text) {
      state.query = String(text ?? '');
      const next = state.query.trim();
      if (next === wanted) return; // a space at the end asks nothing new
      wanted = next;
      // Whatever is out is for a query nobody wants any more; its answer will be dropped.
      listToken++;
      staleMore();
      state.loading = true;
      clearTimer();
      timer = clock.setTimeout(() => { timer = null; fetchFirst(); }, debounceMs);
    },

    /** The next page: after the rows already shown, for the query they answer. */
    async loadMore() {
      if (!state.hasMore || state.loadingMore || state.loading) return;
      const mine = ++moreToken;
      state.loadingMore = true;
      state.moreError = null;
      try {
        const page = (await api.conversations({ q: state.shown, skip: state.rows.length })) ?? [];
        if (mine !== moreToken) return;
        state.rows = mergePage(state.rows, page, removed);
        state.hasMore = page.length >= pageSize;
      } catch (error) {
        if (mine !== moreToken) return;
        state.moreError = words(error);
      } finally {
        if (mine === moreToken) state.loadingMore = false;
      }
    },

    // ---- deleting ------------------------------------------------------------------------------

    /** Opens the question for one row, in place of it. False when it cannot be asked now. */
    ask(id) {
      if (state.confirm?.phase === 'working') return false;
      if (!state.rows.some((row) => row.id === id)) return false;
      state.kept = 0;
      state.confirm = { target: 'one', id, phase: 'asking', error: null };
      return true;
    },

    /** Opens the question for deleting every conversation. */
    askAll() {
      if (state.confirm?.phase === 'working') return false;
      state.kept = 0;
      state.confirm = { target: 'all', id: null, phase: 'asking', error: null };
      return true;
    },

    /** "No", Esc, or "OK" after a refusal. Not while the request is out. */
    cancel() {
      if (!state.confirm || state.confirm.phase === 'working') return false;
      state.confirm = null;
      return true;
    },

    /** "Delete" (or "try again") on a row's question. */
    async confirmDelete() {
      const question = state.confirm;
      if (!question || question.target !== 'one' || question.phase === 'working' || question.phase === 'refused') return;
      question.phase = 'working';
      question.error = null;
      const id = question.id;
      try {
        await api.deleteConversation(id);
      } catch (error) {
        if (state.confirm !== question) return;
        // Not there any more — deleted in another tab, or by the retention setting. The row goes.
        if (error?.status === 404 || error?.code === 'not_found') { gone(id); return; }
        question.phase = error?.status === 409 ? 'refused' : 'failed';
        question.error = words(error);
        return;
      }
      gone(id);
    },

    /**
     * "Delete all". Resolves to `{ deleted, skipped, openGone }` when the server answered, null when it
     * did not (the strip then says why and offers "try again"). `openId`, the conversation open in the
     * pane, is reported through `onDeleted` when it is among those deleted: when nothing was skipped it
     * certainly is; when some were kept it is asked of the server, since a conversation still answering
     * — usually the one open — is exactly what is kept.
     */
    async confirmDeleteAll({ openId = null } = {}) {
      const question = state.confirm;
      if (!question || question.target !== 'all' || question.phase === 'working' || question.phase === 'refused') return null;
      question.phase = 'working';
      question.error = null;
      let result;
      try {
        result = await api.deleteAllConversations();
      } catch (error) {
        if (state.confirm !== question) return null;
        question.phase = error?.status === 409 ? 'refused' : 'failed';
        question.error = words(error);
        return null;
      }
      const deleted = Number(result?.deleted) || 0;
      const skipped = Number(result?.skipped) || 0;
      state.confirm = null;
      state.kept = skipped;
      // Whatever was shown is old now; and a page asked before this must not draw rows back.
      state.rows = [];
      state.hasMore = false;
      const reloaded = fetchFirst();

      let openGone = false;
      if (openId) {
        if (skipped === 0) {
          openGone = true;
        } else {
          try {
            await api.conversation(openId);
          } catch (error) {
            openGone = error?.status === 404;
          }
        }
        if (openGone) onDeleted(openId);
      }
      await reloaded;
      return { deleted, skipped, openGone };
    },

    /** The island is going away: nothing in flight may touch the state afterwards. */
    dispose() {
      clearTimer();
      listToken++;
      staleMore();
    },
  };
  return list;
}
