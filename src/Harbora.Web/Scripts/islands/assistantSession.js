// One conversation's life in the pane: sending a message, streaming its answer, and finding the
// conversation again after a reload or a page change — including one the server is still answering.
//
// The owner's report after the first deploy, and why this file exists: a reload while an answer was
// coming lost the message that had been sent. The pane learnt a new conversation's id only from the
// stream's `started` event, so a reload before that event had been read left it with no conversation to
// reopen (the message was in the conversation list, unreferenced); and a conversation reopened mid-answer
// was drawn once, as it stood, and never read again. Now:
//   1. a new conversation is created (`POST /assistant/conversations`) and its id remembered before the
//      first message is sent, so a reload at any moment after that knows the conversation;
//   2. the message itself is kept in an outbox until the server says it has it (`started`), so even a
//      reload before it was saved does not lose the words — they come back in the composer;
//   3. a conversation that is still being answered (`inProgress`) is shown with the typing indicator and
//      re-read every few seconds until it is not.
//
// Plain JS over a plain timeline object, with the API, storage and timers injected, so `node --test`
// drives every one of these paths without a browser; AssistantPanel.vue is the wiring.

import { createTurnTracker, isGuid } from './assistantApi.js';
import {
  addUserMessage, applyStreamEvent, loadConversation, markUnsent, settleTurn, withdrawUserMessage,
} from './assistantTimeline.js';

const OUTBOX_LIFETIME_MS = 60 * 60 * 1000;

const MAYBE_UNSENT = {
  messageEn: 'Your last message may not have reached the assistant before the page changed. It is back in the box below — send it again if it is not in the conversation.',
  messageFa: 'شاید پیام آخر شما پیش از تغییر صفحه به دستیار نرسیده باشد. دوباره در کادر پایین است — اگر در گفت‌وگو نیست، دوباره بفرستید.',
};

const NOT_SENT = {
  messageEn: 'Your message was not sent. It is back in the box below.',
  messageFa: 'پیام شما فرستاده نشد. دوباره در کادر پایین است.',
};

/**
 * Re-reads a conversation every `intervalMs` while the server says it is still being answered.
 * Starts on `inProgress`, stops when a read says it is over (after drawing that final state), and
 * stops on `stop()` — a close, an unmount, another conversation. A read that fails is tried again a
 * few times; a conversation that is gone stops it. `maxReads` bounds it whatever the server says.
 */
export function createResumePoller({
  load, onUpdate, onSettled, onGone,
  intervalMs = 2500, maxReads = 150, maxFailures = 4,
  timers = { setTimeout: globalThis.setTimeout.bind(globalThis), clearTimeout: globalThis.clearTimeout.bind(globalThis) },
}) {
  let timer = null;
  let conversationId = null;
  let generation = 0;
  let reads = 0;
  let failures = 0;

  function schedule(gen) {
    timer = timers.setTimeout(() => tick(gen), intervalMs);
  }

  async function tick(gen) {
    timer = null;
    let view;
    try {
      view = await load(conversationId);
    } catch (error) {
      if (gen !== generation) return;
      if (error?.status === 404 || error?.status === 403 || ++failures >= maxFailures) {
        generation++;
        onGone?.(error);
        return;
      }
      schedule(gen);
      return;
    }
    if (gen !== generation) return; // stopped, or restarted for another conversation, meanwhile
    failures = 0;
    reads++;
    onUpdate(view);
    if (view?.inProgress && reads < maxReads) {
      schedule(gen);
    } else {
      generation++;
      onSettled?.(view);
    }
  }

  function stop() {
    generation++;
    if (timer !== null) timers.clearTimeout(timer);
    timer = null;
  }

  return {
    start(id) {
      stop();
      conversationId = id;
      reads = 0;
      failures = 0;
      schedule(generation);
    },
    stop,
    get running() { return timer !== null; },
  };
}

/**
 * @param {{
 *   api: ReturnType<typeof import('./assistantApi.js').createAssistantApi>,
 *   prefs: ReturnType<typeof import('../assistantDrawer.js').createAssistantPrefs>,
 *   timeline: ReturnType<typeof import('./assistantTimeline.js').createTimeline>,
 *   pageContext?: () => object | null,
 *   onChange?: (what: string) => void,
 *   now?: () => number,
 *   timers?: { setTimeout: Function, clearTimeout: Function },
 *   pollIntervalMs?: number,
 * }} deps
 */
export function createAssistantSession({
  api, prefs, timeline, pageContext = () => null, onChange = () => {}, now = () => Date.now(),
  timers, pollIntervalMs = 2500,
}) {
  const turns = createTurnTracker();
  const poller = createResumePoller({
    load: (id) => api.conversation(id),
    onUpdate: (view) => {
      loadConversation(timeline, view);
      timeline.resuming = view?.inProgress === true;
      onChange('resumed');
    },
    onSettled: () => {
      timeline.resuming = false;
      onChange('settled');
    },
    onGone: () => {
      timeline.resuming = false;
      onChange('settled');
    },
    timers,
    intervalMs: pollIntervalMs,
  });

  function resumeIfAnswering(view) {
    if (view?.inProgress === true && timeline.conversationId) {
      timeline.resuming = true;
      poller.start(timeline.conversationId);
    } else {
      timeline.resuming = false;
    }
  }

  /** The outbox against what the server has: the words to put back in the composer, or null. */
  function reconcileOutbox(view) {
    const box = prefs.outbox();
    if (!box) return null;
    if (now() - (box.at ?? 0) > OUTBOX_LIFETIME_MS) { prefs.clearOutbox(); return null; }
    // Another conversation's message waits for that conversation to be opened.
    if (box.conversationId && box.conversationId !== (view?.id ?? null)) return null;
    prefs.clearOutbox();
    const text = box.text.trim();
    const arrived = (view?.messages ?? []).some((m) => m.role === 'user' && (m.text ?? '').trim() === text);
    return arrived ? null : box.text;
  }

  /** Abandons the turn in flight, if any — see createTurnTracker. */
  function abandonTurn() {
    if (turns.abort()) {
      settleTurn(timeline);
      timeline.streaming = false;
    }
  }

  const session = {
    /**
     * Opens a conversation. Returns `{ restoreDraft }` when a message from before a reload never
     * reached the server, or `{ error }` (the caller decides where to show it).
     */
    async open(id, { quietIfGone = false } = {}) {
      if (!isGuid(id)) { prefs.setConversationId(null); return {}; }
      abandonTurn();
      poller.stop();
      timeline.loading = true;
      try {
        const view = await api.conversation(id);
        loadConversation(timeline, view);
        prefs.setConversationId(id);
        resumeIfAnswering(view);
        const restoreDraft = reconcileOutbox(view);
        if (restoreDraft) timeline.notice = MAYBE_UNSENT;
        onChange('opened');
        return { restoreDraft };
      } catch (error) {
        // The remembered conversation may belong to another workspace or user on this browser, or have
        // been removed: start fresh rather than greet the person with an error they did not cause.
        if (quietIfGone && (error?.status === 404 || error?.status === 403)) {
          prefs.setConversationId(null);
          return {};
        }
        return { error };
      } finally {
        timeline.loading = false;
      }
    },

    /** On mount: the remembered conversation, and any message that was on its way when the page went. */
    async restore() {
      const remembered = prefs.conversationId();
      if (remembered) return session.open(remembered, { quietIfGone: true });
      // No conversation known: a message sent to `new` on an older server, or one whose conversation
      // could not be created — its words still come back.
      const restoreDraft = reconcileOutbox(null);
      if (restoreDraft) timeline.notice = MAYBE_UNSENT;
      return { restoreDraft };
    },

    startNew() {
      abandonTurn();
      poller.stop();
      loadConversation(timeline, null);
      timeline.resuming = false;
      timeline.notice = null;
      prefs.setConversationId(null);
      prefs.clearOutbox();
    },

    /**
     * Sends one message. Returns `{ sent, restoreDraft? }`: `restoreDraft` is the text to put back in
     * the composer when the message was not taken (still answering, or it never reached the server).
     */
    async send(text) {
      const message = String(text ?? '').trim();
      if (!message || timeline.streaming || timeline.resuming || timeline.loading) return { sent: false };

      poller.stop();
      timeline.notice = null;
      const userKey = addUserMessage(timeline, message);
      const turn = turns.begin();
      timeline.streaming = true;
      onChange('sent');

      // 1. Know the conversation before sending anything, so a reload from here on finds it.
      if (!timeline.conversationId) {
        try {
          const created = await api.createConversation();
          if (!turn.isCurrent()) return { sent: false };
          if (isGuid(created?.id)) {
            timeline.conversationId = created.id;
            prefs.setConversationId(created.id);
          }
        } catch {
          if (!turn.isCurrent()) return { sent: false };
          // A server without the endpoint (or a hiccup creating it): send to `new`, as before.
        }
      }

      // 2. Keep the words until the server has them.
      prefs.setOutbox({ conversationId: timeline.conversationId, text: message, at: now() });

      let outcome = null;
      let started = false;
      try {
        outcome = await api.send({
          conversationId: timeline.conversationId,
          text: message,
          page: pageContext(),
          signal: turn.signal,
          onEvent: (name, data) => {
            if (!turn.isCurrent()) return; // abandoned: the conversation on screen is another one now
            applyStreamEvent(timeline, name, data);
            if (name === 'started') {
              started = true;
              prefs.clearOutbox();
            }
            if ((name === 'started' || name === 'done') && timeline.conversationId) prefs.setConversationId(timeline.conversationId);
            onChange(name);
          },
        });
      } finally {
        if (turn.isCurrent()) {
          turns.end(turn);
          settleTurn(timeline);
          timeline.streaming = false;
        }
      }
      // Abandoned (a new conversation, another one opened, the pane going away): the conversation on
      // screen is not this turn's any more, so nothing below may touch it. (isCurrent() is false
      // after a normal end too, which is why the abort signal is what is asked.)
      if (turn.signal.aborted) return { sent: started };

      if (outcome?.busy) {
        // The server is still answering the previous message and did not take this one: it leaves the
        // conversation, goes back in the composer, and the pane waits for the answer in progress.
        withdrawUserMessage(timeline, userKey);
        prefs.clearOutbox();
        timeline.notice = outcome.notice;
        if (timeline.conversationId) {
          timeline.resuming = true;
          poller.start(timeline.conversationId);
        }
        onChange('busy');
        return { sent: false, restoreDraft: message };
      }

      if (!started && !outcome?.aborted) {
        // Never reached the server (the error is already in the conversation): the bubble stays,
        // marked as not sent, and the words go back in the box.
        markUnsent(timeline, userKey);
        prefs.clearOutbox();
        timeline.notice = NOT_SENT;
        onChange('unsent');
        return { sent: false, restoreDraft: message };
      }

      onChange('ended');
      return { sent: true };
    },

    /** The drawer closed: stop re-reading (the island stays mounted). */
    pause() {
      poller.stop();
    },

    /** The drawer opened again: carry on re-reading a conversation that is still being answered. */
    wake() {
      if (timeline.resuming && timeline.conversationId && !poller.running) poller.start(timeline.conversationId);
    },

    /** The island is going away. */
    dispose() {
      poller.stop();
      turns.abort();
    },

    get polling() { return poller.running; },
  };
  return session;
}
