// The pane's HTTP layer: the endpoints of AssistantController, shapes from AgentViews.cs.
//
//   GET  /assistant/status                      → AgentStatusView
//   GET  /assistant/conversations               → AgentConversationSummaryView[]
//   GET  /assistant/conversations/{id}          → AgentConversationView
//   POST /assistant/conversations/{id|new}/messages  AgentSendMessageRequest → text/event-stream
//   POST /assistant/plans/{id}/approve          AgentApproveRequest → AgentPlanResultView
//   POST /assistant/plans/{id}/reject           → 204
//
// `fetch` is injected so `node --test` can stand in for the server. Every failure becomes words in
// both languages: a turn that went wrong must say so, never just stop.

import { readEventStream, parseAssistantFrame } from './assistantStream.js';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** @param {unknown} value */
export function isGuid(value) {
  return typeof value === 'string' && GUID.test(value);
}

const MESSAGES = {
  network: [
    'Could not reach the panel. Check your connection and try again.',
    'ارتباط با پنل برقرار نشد. اتصال را بررسی کنید و دوباره تلاش کنید.',
  ],
  connectionLost: [
    'The connection was lost while the assistant was working. Nothing is reported as done unless a result above says so.',
    'ارتباط در میانهٔ کار دستیار قطع شد. هیچ کاری انجام‌شده حساب نمی‌شود مگر اینکه نتیجه‌ای در بالا آن را نشان دهد.',
  ],
  endedEarly: [
    'The response ended before the assistant finished. Nothing is reported as done unless a result above says so.',
    'پاسخ پیش از پایان کار دستیار قطع شد. هیچ کاری انجام‌شده حساب نمی‌شود مگر اینکه نتیجه‌ای در بالا آن را نشان دهد.',
  ],
  unreadable: [
    'The assistant sent a part of its answer that the pane could not read.',
    'بخشی از پاسخ دستیار قابل خواندن نبود.',
  ],
  unexpected: [
    'The panel answered with something other than the assistant\'s response. Reload the page and try again.',
    'پنل پاسخی غیر از پاسخ دستیار فرستاد. صفحه را دوباره بارگذاری کنید و دوباره تلاش کنید.',
  ],
  signedOut: [
    'Your session has ended. Reload the page and sign in again.',
    'نشست شما به پایان رسیده است. صفحه را دوباره بارگذاری کنید و دوباره وارد شوید.',
  ],
  notEnabled: [
    'The assistant is not enabled for this workspace.',
    'دستیار برای این ورک‌اسپیس فعال نیست.',
  ],
  forbidden: [
    'You do not have permission to do this.',
    'اجازهٔ انجام این کار را ندارید.',
  ],
  notFound: [
    'Not found — it may have been removed, or it belongs to another workspace.',
    'پیدا نشد — شاید حذف شده باشد یا متعلق به ورک‌اسپیس دیگری باشد.',
  ],
  conflict: [
    'This is no longer possible — it may already have run, been cancelled or expired.',
    'این کار دیگر ممکن نیست — شاید قبلاً اجرا، لغو یا منقضی شده باشد.',
  ],
  stillAnswering: [
    'The assistant is still answering your previous message. Your new message was not sent; it is back in the box below.',
    'دستیار هنوز در حال پاسخ به پیام قبلی شماست. پیام تازه‌تان فرستاده نشد؛ دوباره در کادر پایین است.',
  ],
  paneFault: [
    'The pane failed while showing the answer — a fault in the panel, not in the assistant or your connection. Reload the page to see the conversation as it was saved.',
    'پنل هنگام نمایش پاسخ دچار خطا شد — ایراد از خود پنل است، نه از دستیار یا اتصال شما. صفحه را دوباره بارگذاری کنید تا گفت‌وگو را همان‌طور که ذخیره شده ببینید.',
  ],
};

/** AgentPlanView.Status values; a refusal's `status` is believed only when it is one of these. */
const PLAN_STATUSES = new Set(['pending', 'executing', 'executed', 'failed', 'rejected', 'expired']);

const words = (key) => ({ messageEn: MESSAGES[key][0], messageFa: MESSAGES[key][1] });

function statusWords(status) {
  if (status >= 500) {
    return {
      messageEn: `The panel failed to answer (HTTP ${status}). Try again in a moment.`,
      messageFa: `پنل نتوانست پاسخ دهد (HTTP ${status}). کمی بعد دوباره تلاش کنید.`,
    };
  }
  return {
    messageEn: `The panel refused the request (HTTP ${status}).`,
    messageFa: `پنل درخواست را نپذیرفت (HTTP ${status}).`,
  };
}

/**
 * A failed JSON call, with the reason in both languages. A refused approve may also say which step
 * was refused (`stepIndex`, AgentPlanStepView.Index) and what the plan is now (`planStatus`, e.g.
 * "expired") — the server's word on both, since the pane does not judge expiry by its own clock.
 */
export class AssistantRequestError extends Error {
  constructor(status, messageEn, messageFa, { stepIndex = null, planStatus = null, code = null } = {}) {
    super(messageEn);
    this.name = 'AssistantRequestError';
    this.status = status;
    this.messageEn = messageEn;
    this.messageFa = messageFa;
    this.stepIndex = stepIndex;
    this.planStatus = planStatus;
    // The server's machine-readable reason (`error`: "revalidation_refused", "not_pending", …) — never
    // shown to a person, but it is what tells "nothing ran" from "it already ran".
    this.code = code;
  }
}

/** Thrown out of the stream reader when the pane's own event handler failed, so it is not mistaken for the network. */
class PaneFault extends Error {
  constructor(cause) {
    super('the pane failed to handle an assistant event', { cause });
    this.name = 'PaneFault';
  }
}

/** The best words for a non-2xx response: the server's own when it sent any, ours otherwise. */
async function describeFailure(response) {
  // `redirect: 'manual'` turns the cookie middleware's redirect to the sign-in page into an opaque
  // response — the only way to tell "signed out" from an HTML page that happens to be a 200.
  if (response.type === 'opaqueredirect' || response.status === 0 || response.status === 401) {
    return { status: response.status, ...words('signedOut') };
  }

  let body = null;
  try {
    const text = await response.text();
    body = text ? JSON.parse(text) : null;
  } catch {
    body = null;
  }

  // The server's own words, in whichever of the shapes this codebase uses: messageEn/messageFa (the
  // stream's error event and the plan refusals), error/errorFa (AgentValidation, AgentToolResult), or
  // ProblemDetails. A bare `error` is used only when it is a sentence: "plan_expired" is a code for
  // programs, and the fallback below says more to a person than that does.
  const firstText = (...values) => values.find((v) => typeof v === 'string' && v.trim() !== '') ?? null;
  const sentence = typeof body?.error === 'string' && /\s/.test(body.error.trim()) ? body.error : null;
  const en = firstText(body?.messageEn, body?.errorEn, sentence, body?.detail, body?.title);
  const fa = firstText(body?.messageFa, body?.errorFa);
  const fromServer =
    response.status === 403 && body?.error === 'feature_not_enabled' ? words('notEnabled')
      : en || fa ? { messageEn: en || fa, messageFa: fa || en }
        : null;

  const fallback =
    response.status === 403 ? words('forbidden')
      : response.status === 404 ? words('notFound')
        : response.status === 409 ? words('conflict')
          : statusWords(response.status);

  return {
    status: response.status,
    ...(fromServer ?? fallback),
    fromServer: fromServer !== null,
    stepIndex: Number.isInteger(body?.stepIndex) ? body.stepIndex : null,
    code: typeof body?.error === 'string' && body.error ? body.error : null,
    // ProblemDetails has a numeric `status` too; only a plan status the contract names counts.
    planStatus: typeof body?.status === 'string' && PLAN_STATUSES.has(body.status) ? body.status : null,
  };
}

/**
 * The page the user is on, as AgentPageContext. Only well-formed GUIDs are sent: the record's ids are
 * `Guid?`, so an empty string or a slug in one of them would fail model binding for the whole
 * message, and the user would see a refusal for a question they asked correctly.
 */
export function buildPageContext(declared, path) {
  const id = (v) => (isGuid(v) ? v : null);
  return {
    path: typeof path === 'string' && path ? path : null,
    appId: id(declared?.appId),
    serviceId: id(declared?.serviceId),
    projectId: id(declared?.projectId),
    environmentId: id(declared?.environmentId),
  };
}

/** The `data-assistant-context` attribute a page may declare, or null. */
export function parseDeclaredContext(json) {
  if (!json) return null;
  try {
    const value = JSON.parse(json);
    return value && typeof value === 'object' && !Array.isArray(value) ? value : null;
  } catch {
    return null;
  }
}

/**
 * The turn in flight, if any, and the means to abandon it. Starting a new conversation, opening
 * another one, or the pane going away aborts the turn: its stream is cancelled (the server sees the
 * connection close), and anything still arriving for it is dropped by `isCurrent()` instead of being
 * written into whatever conversation is on screen by then.
 *
 * Closing the drawer does not abort: the island stays mounted, and the answer is there when it
 * opens again — throwing away a turn the workspace already paid a model call for would be worse.
 */
export function createTurnTracker(makeController = () => new AbortController()) {
  let current = null;
  return {
    begin() {
      current?.controller.abort();
      const turn = { controller: makeController(), isCurrent: () => current === turn };
      turn.signal = turn.controller.signal;
      current = turn;
      return turn;
    },
    /** @returns {boolean} whether there was a turn to abort. */
    abort() {
      if (!current) return false;
      current.controller.abort();
      current = null;
      return true;
    },
    end(turn) {
      if (current === turn) current = null;
    },
    get active() { return current !== null; },
  };
}

/**
 * @param {{ fetch?: typeof fetch, csrf?: string, base?: string }} [options]
 */
export function createAssistantApi({ fetch: fetchImpl = globalThis.fetch?.bind(globalThis), csrf = '', base = '/assistant' } = {}) {
  // X-Requested-With makes [RequireFeature] answer a locked workspace with a JSON 403 instead of a
  // redirect to the locked page's HTML. X-CSRF-TOKEN is the header Program.cs's antiforgery reads.
  const common = { 'X-Requested-With': 'XMLHttpRequest' };

  async function json(method, path, body) {
    let response;
    try {
      response = await fetchImpl(base + path, {
        method,
        credentials: 'same-origin',
        redirect: 'manual',
        headers: {
          ...common,
          Accept: 'application/json',
          ...(method === 'GET' ? {} : { 'X-CSRF-TOKEN': csrf }),
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch {
      const w = words('network');
      throw new AssistantRequestError(0, w.messageEn, w.messageFa);
    }

    if (!response.ok || response.type === 'opaqueredirect') {
      const failure = await describeFailure(response);
      throw new AssistantRequestError(failure.status, failure.messageEn, failure.messageFa, failure);
    }
    if (response.status === 204) return null;

    const text = await response.text();
    if (!text) return null;
    try {
      return JSON.parse(text);
    } catch {
      const w = words('unexpected');
      throw new AssistantRequestError(response.status, w.messageEn, w.messageFa);
    }
  }

  return {
    status: () => json('GET', '/status'),
    conversations: () => json('GET', '/conversations'),
    /**
     * `POST /assistant/conversations` → AgentConversationSummaryView of a new, empty conversation —
     * created before the first message so the pane knows (and remembers) its id before anything is
     * sent. A reload at any moment after that finds the conversation again.
     */
    createConversation: () => json('POST', '/conversations'),
    /**
     * `POST /assistant/secrets` (AgentSecretRequest) → AgentSecretView: the panel holds the value and
     * answers with the handle the message carries instead. The only request that ever contains a
     * secret value, and only from the lock button's form.
     */
    holdSecret: ({ name, value }) => json('POST', '/secrets', { name, value }),
    conversation: (id) => json('GET', `/conversations/${encodeURIComponent(id)}`),
    approve: (planId, body) => json('POST', `/plans/${encodeURIComponent(planId)}/approve`, body),
    reject: (planId) => json('POST', `/plans/${encodeURIComponent(planId)}/reject`),

    /**
     * Sends one message and reports the stream through `onEvent(name, data)` as it arrives. Never
     * throws for anything the network or the server did: those arrive as an `error` event shaped
     * like the server's own ({ messageEn, messageFa }), so the pane has one path for both.
     *
     * @returns {Promise<{ completed: boolean, aborted?: true }>} completed: `done` arrived.
     */
    async send({ conversationId, text, page, onEvent, signal }) {
      const target = conversationId ? encodeURIComponent(conversationId) : 'new';
      const aborted = () => signal?.aborted === true;

      // Stream events go through `deliver`: if the pane's handler throws, that is a fault in the
      // pane, and reading stops (the reader is cancelled) without blaming the connection. Errors the
      // pane reports itself go through `report`, which cannot make things worse by throwing.
      const deliver = (name, data) => {
        try { onEvent(name, data); } catch (cause) { throw new PaneFault(cause); }
      };
      const report = (failure) => {
        try { onEvent('error', failure); } catch (cause) { console.error('[assistant] could not show an error', cause); }
      };

      let response;
      try {
        response = await fetchImpl(`${base}/conversations/${target}/messages`, {
          method: 'POST',
          credentials: 'same-origin',
          redirect: 'manual',
          headers: {
            ...common,
            'Content-Type': 'application/json',
            Accept: 'text/event-stream',
            'X-CSRF-TOKEN': csrf,
          },
          body: JSON.stringify({ text, page: page ?? null }),
          signal,
        });
      } catch {
        if (aborted()) return { completed: false, aborted: true };
        report(words('network'));
        return { completed: false };
      }

      if (!response.ok || response.type === 'opaqueredirect') {
        const { messageEn, messageFa, fromServer } = await describeFailure(response);
        // 409: the conversation is still answering the previous message. Not a failure — the message
        // was simply not taken — so it is handed back as a notice for the pane to show gently, not
        // reported as an error.
        if (response.status === 409) {
          return { completed: false, busy: true, notice: fromServer ? { messageEn, messageFa } : words('stillAnswering') };
        }
        report({ messageEn, messageFa });
        return { completed: false };
      }

      const type = response.headers?.get?.('content-type') ?? '';
      if (!type.includes('text/event-stream') || !response.body) {
        response.body?.cancel?.().catch(() => {});
        report(words('unexpected'));
        return { completed: false };
      }

      let sawDone = false;
      try {
        await readEventStream(response.body, (frame) => {
          const event = parseAssistantFrame(frame);
          if (!event) return;
          if (event.unreadable) { deliver('error', words('unreadable')); return; }
          if (event.name === 'done') sawDone = true;
          deliver(event.name, event.data);
        });
      } catch (error) {
        if (error instanceof PaneFault) {
          console.error('[assistant] the pane failed to handle an event', error.cause);
          report(words('paneFault'));
          return { completed: false, paneFault: true };
        }
        if (aborted()) return { completed: false, aborted: true };
        report(words('connectionLost'));
        return { completed: false };
      }

      if (!sawDone) {
        report(words('endedEarly'));
        return { completed: false };
      }
      return { completed: true };
    },
  };
}
