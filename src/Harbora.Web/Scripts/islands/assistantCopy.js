// Copying text out of the pane: an answer, or one code block of it.
//
// What text a button takes is decided in assistantFormat.js (messageCopyText, codeCopyText). This is
// the doing of it, and what the button says afterwards: the Clipboard API where the page may use it,
// and a hidden textarea with execCommand('copy') where it may not — a panel served over plain http
// (an intranet address is not a "secure context", and navigator.clipboard is then missing), or a
// browser that refuses the permission. Both can fail, and a copy button that says nothing when it
// failed is worse than none, so the outcome is always reported: «کپی شد» or «کپی نشد».
//
// Plain JS with the clipboard, the document and the timers injected, so node --test drives it.

/**
 * The fallback: select the text in an off-screen textarea and ask the browser to copy the selection.
 * The element that had the keyboard focus gets it back — a copy button must not move the person.
 *
 * @param {string} text
 * @param {Document | undefined} doc
 */
function copyWithTextarea(text, doc) {
  if (!doc?.body || typeof doc.createElement !== 'function' || typeof doc.execCommand !== 'function') return false;
  const previous = doc.activeElement;
  const area = doc.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  area.setAttribute('aria-hidden', 'true');
  area.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;padding:0;border:0;opacity:0;pointer-events:none';
  doc.body.appendChild(area);
  try {
    area.select();
    area.setSelectionRange?.(0, text.length);
    return doc.execCommand('copy') === true;
  } catch {
    return false;
  } finally {
    area.remove();
    previous?.focus?.();
  }
}

/**
 * Puts `text` on the clipboard. Resolves to whether it got there; never rejects.
 *
 * @param {string} text
 * @param {{ clipboard?: { writeText?: (text: string) => Promise<void> } | null, doc?: Document }} [env]
 */
export async function copyText(text, {
  clipboard = globalThis.navigator?.clipboard,
  doc = globalThis.document,
} = {}) {
  if (typeof text !== 'string' || text === '') return false;
  if (typeof clipboard?.writeText === 'function') {
    try {
      await clipboard.writeText(text);
      return true;
    } catch {
      // Refused (no permission, the page not focused): the textarea may still be allowed.
    }
  }
  return copyWithTextarea(text, doc);
}

/**
 * What the copy buttons of one message show. One state for all of them — a message's button and each
 * of its code blocks' — keyed by which button was pressed: `status` is 'copied' or 'failed' for
 * `holdMs`, then 'idle' again. Pressing another one in the meantime takes over.
 *
 * `wrap` is how the state object becomes reactive in the component (`reactive`), the same way
 * assistantSecrets.js does it; the module changes only what `wrap` returned.
 *
 * @param {{
 *   copy?: (text: string) => Promise<boolean>,
 *   wrap?: <T extends object>(state: T) => T,
 *   timers?: { setTimeout: Function, clearTimeout: Function },
 *   holdMs?: number,
 * }} [deps]
 */
export function createCopyFeedback({
  copy = (text) => copyText(text),
  wrap = (state) => state,
  timers = { setTimeout: globalThis.setTimeout.bind(globalThis), clearTimeout: globalThis.clearTimeout.bind(globalThis) },
  holdMs = 1500,
} = {}) {
  const state = wrap({ key: /** @type {string | null} */ (null), status: /** @type {'idle' | 'copied' | 'failed'} */ ('idle') });
  let timer = null;
  let disposed = false;

  const clear = () => {
    if (timer !== null) timers.clearTimeout(timer);
    timer = null;
  };

  const show = (key, status) => {
    clear();
    state.key = key;
    state.status = status;
    timer = timers.setTimeout(() => {
      timer = null;
      state.key = null;
      state.status = 'idle';
    }, holdMs);
  };

  return {
    state,
    /** The status of one button: 'idle' unless it is the one just pressed. */
    statusOf: (key) => (state.key === key ? state.status : 'idle'),
    /** Copies `text` for the button `key`; resolves to whether it worked. A null text is "nothing to copy". */
    async copy(key, text) {
      if (text === null || text === undefined) return false;
      let ok = false;
      try { ok = (await copy(text)) === true; } catch { ok = false; }
      if (!disposed) show(key, ok ? 'copied' : 'failed');
      return ok;
    },
    /** The component is going away: nothing more is shown, and no timer is left running. */
    dispose() {
      disposed = true;
      clear();
    },
  };
}
