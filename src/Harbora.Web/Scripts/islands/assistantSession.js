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
// A conversation deleted somewhere else while it is on screen (another tab, the retention setting) is let
// go of the moment the server says so — a send answered 404, or a read answered 404 — with the words back
// in the composer, rather than staying on screen to be sent to.
//
// Plain JS over a plain timeline object, with the API, storage and timers injected, so `node --test`
// drives every one of these paths without a browser; AssistantPanel.vue is the wiring.

import { createTurnTracker, isGuid } from './assistantApi.js';
import {
  addUserMessage, applyStreamEvent, loadConversation, markUnsent, restoreSuperseded, settleTurn, withdrawUserMessage,
} from './assistantTimeline.js';

const OUTBOX_LIFETIME_MS = 60 * 60 * 1000;

// After Stop, a conversation only being followed (no stream in this tab) is read again this soon: long
// enough for the server to have saved what was written and ended the turn, far shorter than a poll.
const STOP_REREAD_MS = 400;

// What "nothing to stop" means for a Stop sent from a tab whose own stream is open depends on whether
// `started` had reached the tab:
//  - before it, the server may not have had the turn yet: ask again the moment `started` arrives (a bound of
//    this many requests in all is kept as a guard, with the words below);
//  - after it, the turn is registered (the server does that before it emits `started`), so "nothing to stop"
//    means it is already finishing — from the model's last word on, and during a plan that runs without
//    asking, the turn is deliberately released, and no `stopped` event will follow. Asking again could never
//    succeed, so the stopping state ends at once with a gentle notice (below), not an error.
const STOP_MAX_TRIES = 6;

// The guard's words: the button is Stop again — it is not left on «stopping» for the rest of the turn.
const STOP_NOT_TAKEN = {
  messageEn: "Couldn't stop it — try again",
  messageFa: 'توقف انجام نشد — دوباره امتحان کنید',
};

// Information, not a failure: it went on to finish before it could be stopped. Gone with that turn's `done`.
const STOP_FINISHING = {
  messageEn: "It can't be stopped now — it's finishing.",
  messageFa: 'دیگر قابل توقف نیست — پاسخ در حال تمام شدن است.',
};

/** The notices Stop puts in the composer; each belongs to one turn and goes when that turn ends. */
const isStopNotice = (notice) => notice?.kind === 'stop-failed' || notice?.kind === 'stop-finishing';

const MAYBE_UNSENT = {
  kind: 'restored',
  messageEn: 'Your last message may not have reached the assistant before the page changed. It is back in the box below — send it again if it is not in the conversation.',
  messageFa: 'شاید پیام آخر شما پیش از تغییر صفحه به دستیار نرسیده باشد. دوباره در کادر پایین است — اگر در گفت‌وگو نیست، دوباره بفرستید.',
};

const NOT_SENT = {
  kind: 'unsent',
  messageEn: 'Your message was not sent. It is back in the box below.',
  messageFa: 'پیام شما فرستاده نشد. دوباره در کادر پایین است.',
};

const UNSURE_SENT = {
  kind: 'unsent',
  messageEn: 'It is not clear whether your message reached the assistant. It is back in the box below — look at the conversation before sending it again.',
  messageFa: 'معلوم نیست پیام شما به دستیار رسیده باشد. دوباره در کادر پایین است — پیش از فرستادن دوباره، گفت‌وگو را نگاه کنید.',
};

// The conversation on screen was deleted somewhere else — another tab, the retention setting. Nothing can
// be sent to it, and nothing sent to it was kept: the pane starts a new one, and the words go back in the box.
const CONVERSATION_GONE = {
  kind: 'gone',
  messageEn: 'This conversation was deleted — your message is back in the box; send it again to start a new one.',
  messageFa: 'این گفت‌وگو حذف شده است — پیامتان در گفت‌وگوی تازه است، دوباره بفرستید.',
};

// The same, found by the poller while no words of the person's were waiting: there is nothing to give back,
// so the notice does not say there is.
const CONVERSATION_GONE_QUIETLY = {
  kind: 'gone',
  messageEn: 'This conversation was deleted.',
  messageFa: 'این گفت‌وگو حذف شده است.',
};

/**
 * Whether a failed read of a conversation says it is gone: 404, or a 403 that is not the workspace's
 * assistant being switched off (`feature_not_enabled` — the conversation is still there, and saying it was
 * deleted would be false).
 */
const isGone = (error) => error?.status === 404 || (error?.status === 403 && error?.code !== 'feature_not_enabled');

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
        onGone?.(error, conversationId);
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
  const clock = timers ?? { setTimeout: globalThis.setTimeout.bind(globalThis), clearTimeout: globalThis.clearTimeout.bind(globalThis) };
  let reread = null;
  // The Stop the person asked for, until the turn it is about to end has ended (see stop()).
  let stopIntent = null;
  // Whether this tab's stream has delivered `started`: from then on the server knows the turn exists.
  let streamStarted = false;
  // Whether it has delivered `done` or `error`: the turn is over, though the stream may not have closed yet.
  let streamOver = false;
  timeline.stopping = false;
  const poller = createResumePoller({
    load: (id) => api.conversation(id),
    onUpdate: (view) => {
      loadConversation(timeline, view);
      endStopIfMoved();
      timeline.resuming = view?.inProgress === true;
      onChange('resumed');
    },
    onSettled: () => {
      timeline.resuming = false;
      endStop();
      if (timeline.notice?.kind === 'busy' || isStopNotice(timeline.notice)) timeline.notice = null; // the answer it waited for is in
      onChange('settled');
    },
    onGone: (error, id) => {
      timeline.resuming = false;
      endStop();
      // Deleted while it was being followed: the pane lets go of it. The words the person was told are back
      // in the box ("still answering", or a message that may not have been sent) are still there to send.
      if (isGone(error) && id && timeline.conversationId === id) {
        const handedBack = timeline.notice?.kind === 'busy' || timeline.notice?.kind === 'restored';
        conversationGone(id, handedBack ? CONVERSATION_GONE : CONVERSATION_GONE_QUIETLY);
        return;
      }
      if (timeline.notice?.kind === 'busy') timeline.notice = null;
      onChange('settled');
    },
    timers,
    intervalMs: pollIntervalMs,
  });

  // The drawer is closed: nothing is re-read until it opens again (wake), even if a read that was
  // already under way finds the conversation still being answered.
  let paused = false;

  /**
   * Follow a conversation the server is still answering. `resuming` is never left true without a
   * poller, except while the drawer is closed — and wake() starts it again on the next open.
   */
  function follow(conversationId) {
    timeline.resuming = true;
    if (!paused) poller.start(conversationId);
  }

  function resumeIfAnswering(view) {
    endStopIfMoved();
    if (view?.inProgress === true && timeline.conversationId) {
      // A Stop that was told "nothing to stop" by a conversation that is still being answered has not
      // stopped anything (another tab began a new turn, say): the button is Stop again, not Stopping.
      if (stopIntent?.sawNothing) endStop();
      follow(timeline.conversationId);
    } else {
      // Not being answered: nothing to re-read — including a poller some earlier open started.
      poller.stop();
      timeline.resuming = false;
      endStop();
    }
  }

  // ---- Stop ------------------------------------------------------------------------------------------
  // Pressing Stop is an intent that is remembered until the turn it is meant for has ended — it is not a
  // request that is made once and forgotten. `timeline.stopping` is what the button shows («در حال توقف…»);
  // it stays true after the request has been answered and goes false only when the turn is over (or the
  // asking failed, or the person moved to another conversation).

  /** Ends the stopping state; `outcome` settles what stop() returned. */
  function endStop(outcome = { stopped: false }) {
    const intent = stopIntent;
    if (!intent) {
      timeline.stopping = false; // nothing is stopping, whatever the flag says
      return;
    }
    stopIntent = null;
    timeline.stopping = false;
    intent.settle(outcome);
  }

  /**
   * A Stop is for one conversation. Whenever the timeline has taken in another one (open, a re-read, the
   * poller), a stop that was meant for the old id has nothing left to stop and must not outlive it — it
   * would show «stopping» for a conversation it never asked about.
   */
  function endStopIfMoved() {
    if (stopIntent?.id && stopIntent.id !== timeline.conversationId) endStop();
  }

  /** The asking is over (answered, or given up): stop() resolves, the stopping state goes on. */
  function stopAsked(intent, outcome) {
    intent.asked = true;
    intent.settle(outcome);
  }

  /** One request to `POST …/{id}/stop`, if one can be made now; never more than one in flight. */
  async function askToStop(intent) {
    if (stopIntent !== intent || intent.asked || intent.asking || !intent.id) return;
    intent.asking = true;
    intent.tries++;
    const startedThen = streamStarted;
    let outcome;
    try {
      outcome = await api.stop(intent.id);
    } catch (error) {
      intent.asking = false;
      // The turn ended, or the person moved on, while it was in flight: the words are not for this screen.
      if (stopIntent !== intent) return;
      if (timeline.conversationId === intent.id) timeline.notice = { kind: 'stop-failed', ...wordsOf(error) };
      onChange('stop-failed');
      endStop({ stopped: false, error });
      return;
    }
    intent.asking = false;
    if (stopIntent !== intent) return; // over meanwhile: nothing more to ask, and no retry

    if (outcome?.stopped === true) {
      stopAsked(intent, { stopped: true });
      if (intent.following && timeline.conversationId === intent.id) session.rereadSoon(STOP_REREAD_MS);
      return;
    }

    if (intent.following) {
      // No stream here, so "nothing to stop" is the truth: the turn had already finished. The
      // conversation is read to show it — and to end the stopping state.
      intent.sawNothing = true;
      stopAsked(intent, { stopped: false });
      if (timeline.conversationId === intent.id) session.rereadSoon(STOP_REREAD_MS);
      return;
    }

    // This tab's stream is open.
    if (startedThen) {
      // `started` had arrived when this was sent, so the server had the turn: "nothing to stop" means it is
      // already finishing and can no longer be stopped. No retry could succeed — say so, and end «stopping».
      if (timeline.conversationId === intent.id) timeline.notice = { kind: 'stop-finishing', ...STOP_FINISHING };
      onChange('stop-finishing');
      endStop({ stopped: false });
      return;
    }

    // Sent before `started` reached this tab: the turn may not have been registered yet. Ask again when
    // `started` arrives — at once, if it arrived while this request was out. The bound is a guard.
    if (intent.tries >= STOP_MAX_TRIES) {
      if (timeline.conversationId === intent.id) timeline.notice = { kind: 'stop-failed', ...STOP_NOT_TAKEN };
      onChange('stop-failed');
      endStop({ stopped: false });
    } else if (streamStarted) {
      askToStop(intent);
    }
    // else: the stream's `started` (see send) asks again.
  }

  /** The conversation's id became known, or `started` arrived: a Stop that was waiting for it can go. */
  function stopCanProceed() {
    const intent = stopIntent;
    if (!intent || intent.following || intent.asked) return;
    intent.id ??= timeline.conversationId;
    askToStop(intent);
  }

  // Each open() takes a number; a read that comes back after a later open() began is stale and is
  // dropped, so a slow read of one conversation cannot draw over the conversation opened after it.
  let opening = 0;

  /** The person's latest message in a conversation, as the server has it. */
  function lastUserText(view) {
    const users = (view?.messages ?? []).filter((m) => m.role === 'user');
    return (users.at(-1)?.text ?? '').trim();
  }

  /** The outbox against what the server has: the words to put back in the composer, or null. */
  function reconcileOutbox(view) {
    const conversationId = view?.id ?? null;
    const box = prefs.outbox(conversationId);
    if (!box) return null;
    prefs.clearOutbox(conversationId);
    if (now() - (box.at ?? 0) > OUTBOX_LIFETIME_MS) return null;
    return lastUserText(view) === box.text.trim() ? null : box.text;
  }

  /** Abandons the turn in flight, if any — see createTurnTracker. Returns whether there was one. */
  function abandonTurn() {
    if (!turns.abort()) return false;
    endStop();
    streamStarted = false;
    settleTurn(timeline);
    timeline.streaming = false;
    return true;
  }

  function wordsOf(error) {
    return {
      messageEn: error?.messageEn ?? String(error),
      messageFa: error?.messageFa ?? error?.messageEn ?? String(error),
    };
  }

  /**
   * The conversation on screen is gone — deleted in another tab, or by the retention setting. The pane lets
   * go of it the way a delete from the list does (forget: a new, empty conversation; its remembered id and
   * waiting words dropped, so a reload does not try it again) and says so, gently: it is not a failure.
   */
  function conversationGone(id, notice) {
    session.forget(id);
    timeline.notice = notice;
    onChange('gone');
  }

  const session = {
    /**
     * Opens a conversation. Returns `{ restoreDraft }` when a message from before a reload never
     * reached the server, or `{ error }`.
     *
     * `holdOnFailure` (the remembered conversation, on mount): a read that fails leaves the pane in a
     * "could not load — try again" state rather than looking like a fresh conversation, so the next
     * message cannot start a new one and orphan the remembered conversation and its waiting words.
     * Otherwise (opening another one from the list) the conversation already on screen stays — and
     * if it was being followed, it still is.
     */
    async open(id, { quietIfGone = false, holdOnFailure = false } = {}) {
      if (!isGuid(id)) { prefs.setConversationId(null); return {}; }
      const mine = ++opening;
      endStop();
      const previousId = timeline.conversationId;
      const wasResuming = timeline.resuming;
      const abandoned = abandonTurn();
      poller.stop();
      timeline.notice = null;
      timeline.loading = true;
      try {
        const view = await api.conversation(id);
        if (mine !== opening) return {};
        timeline.loadError = null;
        loadConversation(timeline, view);
        prefs.setConversationId(id);
        resumeIfAnswering(view);
        const restoreDraft = reconcileOutbox(view);
        if (restoreDraft) timeline.notice = MAYBE_UNSENT;
        onChange('opened');
        return { restoreDraft };
      } catch (error) {
        if (mine !== opening) return {};
        // The remembered conversation may belong to another workspace or user on this browser, or have
        // been removed: start fresh rather than greet the person with an error they did not cause.
        if (quietIfGone && (error?.status === 404 || error?.status === 403)) {
          prefs.setConversationId(null);
          timeline.loadError = null;
          timeline.resuming = false;
          return {};
        }
        if (holdOnFailure) {
          loadConversation(timeline, null);
          timeline.resuming = false;
          timeline.loadError = { conversationId: id, ...wordsOf(error) };
          onChange('load-failed');
          return { error };
        }
        // The conversation on screen is still the one it was. If it was being followed — or a turn
        // in it was just abandoned, which the server carries on answering — follow it again.
        if ((wasResuming || abandoned) && previousId && timeline.conversationId === previousId) follow(previousId);
        else timeline.resuming = false;
        return { error };
      } finally {
        if (mine === opening) timeline.loading = false;
      }
    },

    /** On mount: the remembered conversation, and any message that was on its way when the page went. */
    async restore() {
      const remembered = prefs.conversationId();
      if (remembered) return session.open(remembered, { quietIfGone: true, holdOnFailure: true });
      // No conversation known: a message sent to `new` on an older server, or one whose conversation
      // was still being created when the page went — its words still come back.
      const restoreDraft = reconcileOutbox(null);
      if (restoreDraft) timeline.notice = MAYBE_UNSENT;
      return { restoreDraft };
    },

    /** Tries the remembered conversation again after a failed read. */
    retry() {
      const failed = timeline.loadError;
      return failed ? session.open(failed.conversationId, { quietIfGone: true, holdOnFailure: true }) : Promise.resolve({});
    },

    startNew() {
      endStop();
      abandonTurn();
      poller.stop();
      loadConversation(timeline, null);
      timeline.resuming = false;
      timeline.notice = null;
      timeline.loadError = null;
      prefs.setConversationId(null);
    },

    /**
     * A conversation was deleted. The pane forgets it: its waiting words are dropped, and if it is the
     * one on screen (or the one a failed read is holding on to) a new empty conversation takes its place
     * — so a reload never tries to open it again. Another conversation's remembered id and waiting words
     * are not touched. Returns whether the conversation on screen was replaced.
     */
    forget(id) {
      if (!id) return false;
      prefs.clearOutbox(id);
      const onScreen = timeline.conversationId === id || timeline.loadError?.conversationId === id;
      if (onScreen) session.startNew();
      else if (prefs.conversationId() === id) prefs.setConversationId(null);
      return onScreen;
    },

    /**
     * Sends one message. Returns `{ sent, restoreDraft? }`: `restoreDraft` is the text to put back in
     * the composer when the message was not taken (still answering, it never reached the server, or the
     * conversation had been deleted elsewhere — then a new, empty one is on screen to send it to).
     */
    async send(text) {
      const message = String(text ?? '').trim();
      if (!message || timeline.streaming || timeline.resuming || timeline.loading || timeline.loadError) return { sent: false };

      // Nothing is being answered here, so a stopping state still standing is stale.
      endStop();
      poller.stop();
      timeline.notice = null;
      const userKey = addUserMessage(timeline, message);
      const turn = turns.begin();
      streamStarted = false;
      streamOver = false;
      timeline.streaming = true;
      onChange('sent');

      // 1. Keep the words until the server has them — from the very first moment, so a reload during
      //    the create below loses nothing either.
      const box = { conversationId: timeline.conversationId, text: message, at: now() };
      prefs.setOutbox(box);

      // 2. Know the conversation before sending anything, so a reload from here on finds it.
      if (!timeline.conversationId) {
        let created = null;
        try {
          created = await api.createConversation();
        } catch {
          // A server without the endpoint (or a hiccup creating it): send to `new`, as before.
        }
        if (!turn.isCurrent()) {
          // Abandoned while the conversation was being created (a new one started, another opened):
          // nothing was sent, so the words go back in the composer of whatever is on screen now. The
          // "new" entry is dropped only if it is still this message's — a later send may own it now.
          const waiting = prefs.outbox(null);
          if (waiting && waiting.text === box.text && waiting.at === box.at) prefs.clearOutbox(null);
          return { sent: false, restoreDraft: message };
        }
        if (isGuid(created?.id)) {
          timeline.conversationId = created.id;
          prefs.setConversationId(created.id);
          prefs.moveOutbox(created.id);
          // A Stop pressed during the create round trip goes out now — after the message request that
          // is about to be made, hence the microtask.
          Promise.resolve().then(stopCanProceed);
        }
      }
      const conversationId = timeline.conversationId;

      let outcome = null;
      let started = false;
      // Whether the turn got past the server's gate: any output at all. An error with none before it
      // was a refusal at the gate (no model, today's cap), which withdraws no waiting card.
      let progressed = false;
      let refusedAtGate = false;
      try {
        outcome = await api.send({
          conversationId,
          text: message,
          page: pageContext(),
          signal: turn.signal,
          onEvent: (name, data) => {
            if (!turn.isCurrent()) return; // abandoned: the conversation on screen is another one now
            applyStreamEvent(timeline, name, data);
            if (name === 'started') {
              started = true;
              streamStarted = true;
              prefs.clearOutbox(conversationId);
              stopCanProceed();
            }
            if (name === 'done' || name === 'error') { // the turn is over: nothing left to stop
              streamOver = true;
              endStop();
            }
            if (name === 'done' && isStopNotice(timeline.notice)) timeline.notice = null; // "try again" and "finishing" are moot
            if (['tool', 'delta', 'message', 'plan', 'executed'].includes(name)) progressed = true;
            if (name === 'error' && started && !progressed) refusedAtGate = true;
            if ((name === 'started' || name === 'done') && timeline.conversationId) prefs.setConversationId(timeline.conversationId);
            onChange(name);
          },
        });
      } finally {
        if (turn.isCurrent()) {
          turns.end(turn);
          settleTurn(timeline);
          timeline.streaming = false;
          streamStarted = false;
          streamOver = false;
          endStop();
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
        prefs.clearOutbox(conversationId);
        timeline.notice = { kind: 'busy', ...outcome.notice };
        if (timeline.conversationId) follow(timeline.conversationId);
        onChange('busy');
        return { sent: false, restoreDraft: message };
      }

      if (outcome?.gone) {
        // 404 on the send: the conversation is not there, so the message was certainly not taken — and there
        // is nothing to read back. A new conversation takes its place, with the words back in the box.
        conversationGone(conversationId, CONVERSATION_GONE);
        return { sent: false, restoreDraft: message };
      }

      if (!started && !outcome?.aborted) {
        // `started` never arrived, which does not prove the server did not take the message: the
        // connection can break after it was saved, and the server answers on regardless. So the
        // conversation is read again before anything is called "not sent" — offering a resend of a
        // message the server already has would put it in the conversation twice.
        let unsure = false;
        let gone = false;
        if (conversationId) {
          timeline.loading = true;
          try {
            const view = await api.conversation(conversationId);
            if (timeline.conversationId === conversationId && lastUserText(view) === message) {
              prefs.clearOutbox(conversationId);
              loadConversation(timeline, view);
              resumeIfAnswering(view);
              onChange('opened');
              return { sent: true };
            }
          } catch (error) {
            // A conversation that is not there kept nothing sent to it: not "unsure", gone.
            if (isGone(error)) gone = true;
            else unsure = true;
          } finally {
            timeline.loading = false;
          }
          if (timeline.conversationId !== conversationId) return { sent: false }; // moved on meanwhile
          if (gone) {
            conversationGone(conversationId, CONVERSATION_GONE);
            return { sent: false, restoreDraft: message };
          }
        }
        markUnsent(timeline, userKey, unsure ? 'unsure' : true);
        prefs.clearOutbox(conversationId);
        timeline.notice = unsure ? UNSURE_SENT : NOT_SENT;
        onChange('unsent');
        return { sent: false, restoreDraft: message };
      }

      // Refused at the gate: the cards marked superseded on `started` were not withdrawn after all —
      // the server's own account of the conversation says what they are.
      if (refusedAtGate && conversationId && timeline.items.some((i) => i.kind === 'plan' && i.superseded)) {
        try {
          const view = await api.conversation(conversationId);
          if (timeline.conversationId === conversationId) restoreSuperseded(timeline, view);
        } catch {
          // Unreadable just now: the card stays as drawn; the next open shows the truth.
        }
      }

      onChange('ended');
      return { sent: true };
    },

    /**
     * Reads the conversation once more after `delayMs` — for a plan someone approved in another tab
     * ("not_pending", status "executing"): its card cannot know when that run ends, and the
     * conversation will say what it did. Skipped if the person has moved on, or a turn is running.
     */
    rereadSoon(delayMs = 3000) {
      const conversationId = timeline.conversationId;
      if (!conversationId) return;
      if (reread !== null) clock.clearTimeout(reread);
      reread = clock.setTimeout(async () => {
        reread = null;
        if (timeline.conversationId !== conversationId || timeline.streaming || timeline.loading) return;
        try {
          const view = await api.conversation(conversationId);
          if (timeline.conversationId !== conversationId || timeline.streaming) return;
          loadConversation(timeline, view);
          resumeIfAnswering(view);
          onChange('reread');
        } catch {
          // Nothing more to do; the next open reads it again.
        }
      }, delayMs);
    },

    /**
     * The Stop button: asks the server to end the turn in progress (`POST …/{id}/stop`). Resolves to
     * `{ stopped }` once the asking is over — or `{ stopped: false, error }`, the error also being the
     * composer's notice. Pressed again while the intent stands, it is the same intent, never a second
     * request. `timeline.stopping` is true from the press until the turn has ended.
     *
     *  - A turn streaming in this tab ends by itself: its `stopped` and `done` events arrive on the
     *    stream that is open, and drawing them is the timeline's job. If the conversation has no id yet
     *    (the create round trip of a first message, or a server without the create endpoint), the request
     *    waits for the id, or for `started`. `{ stopped: false }` before `started` has reached this tab
     *    means the server may not have had the turn yet, so it is asked again when `started` arrives;
     *    after `started` it means the turn is already finishing and can no longer be stopped — the
     *    stopping state ends at once with a gentle notice (STOP_FINISHING), and nothing is retried.
     *  - A conversation only being followed after a reload (`resuming`) has no stream here, just the
     *    poller. One request; then it is read again promptly instead of when the next poll comes round.
     *    `stopped: false` there is the truth — the turn had already finished — and says nothing.
     */
    stop() {
      if (stopIntent) return stopIntent.promise;
      // While another conversation is being opened, `resuming` still belongs to the one being left: a Stop
      // now would be for a turn that is no longer on screen. And once `done` or `error` has come, the turn is
      // over although the stream has not quite closed.
      if (timeline.loading || (timeline.streaming && streamOver)) return Promise.resolve({ stopped: false });
      if (!(timeline.streaming || timeline.resuming)) return Promise.resolve({ stopped: false });
      const intent = {
        id: timeline.conversationId,
        following: !timeline.streaming,
        tries: 0, asking: false, asked: false, sawNothing: false,
      };
      intent.promise = new Promise((resolve) => { intent.settle = resolve; });
      stopIntent = intent;
      timeline.stopping = true;
      if (isStopNotice(timeline.notice)) timeline.notice = null; // a new press retires what the last one said
      askToStop(intent); // does nothing yet when the conversation has no id
      return intent.promise;
    },

    /** The drawer closed: stop re-reading (the island stays mounted). */
    pause() {
      paused = true;
      poller.stop();
    },

    /** The drawer opened again: carry on re-reading a conversation that is still being answered. */
    wake() {
      paused = false;
      if (timeline.resuming && timeline.conversationId && !poller.running) poller.start(timeline.conversationId);
    },

    /** The island is going away. */
    dispose() {
      endStop();
      poller.stop();
      turns.abort();
      if (reread !== null) clock.clearTimeout(reread);
      reread = null;
    },

    get polling() { return poller.running; },
  };
  return session;
}
