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
};

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

/** A failed JSON call, with the reason in both languages. */
export class AssistantRequestError extends Error {
  constructor(status, messageEn, messageFa) {
    super(messageEn);
    this.name = 'AssistantRequestError';
    this.status = status;
    this.messageEn = messageEn;
    this.messageFa = messageFa;
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
  // stream's error event), error/errorFa (AgentValidation, AgentToolResult), or ProblemDetails.
  const firstText = (...values) => values.find((v) => typeof v === 'string' && v.trim() !== '') ?? null;
  const en = firstText(body?.messageEn, body?.errorEn, body?.error, body?.detail, body?.title);
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

  return { status: response.status, ...(fromServer ?? fallback) };
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
      throw new AssistantRequestError(failure.status, failure.messageEn, failure.messageFa);
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
        onEvent('error', words('network'));
        return { completed: false };
      }

      if (!response.ok || response.type === 'opaqueredirect') {
        const { messageEn, messageFa } = await describeFailure(response);
        onEvent('error', { messageEn, messageFa });
        return { completed: false };
      }

      const type = response.headers?.get?.('content-type') ?? '';
      if (!type.includes('text/event-stream') || !response.body) {
        onEvent('error', words('unexpected'));
        return { completed: false };
      }

      let sawDone = false;
      try {
        await readEventStream(response.body, (frame) => {
          const event = parseAssistantFrame(frame);
          if (!event) return;
          if (event.unreadable) { onEvent('error', words('unreadable')); return; }
          if (event.name === 'done') sawDone = true;
          onEvent(event.name, event.data);
        });
      } catch {
        if (aborted()) return { completed: false, aborted: true };
        onEvent('error', words('connectionLost'));
        return { completed: false };
      }

      if (!sawDone) {
        onEvent('error', words('endedEarly'));
        return { completed: false };
      }
      return { completed: true };
    },
  };
}
