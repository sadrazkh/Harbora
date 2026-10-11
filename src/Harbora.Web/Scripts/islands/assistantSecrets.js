// Secrets as black boxes (u-brief S; AgentSecretContracts.cs). The owner's rule: «سکرت رو بگیره ولی
// برای مدل به عنوان بلک باکس بفرسته و بگیره اینور اعمال کنه» — take the secret, send it to the model as
// a black box, and apply it on this side.
//
// The person types a secret into the lock button's form, never into the message. The form posts it to
// `POST /assistant/secrets` and gets back a handle, `{{secret:<22 url-safe chars>}}`; the draft gets a
// placeholder naming it (`[🔒 DB_PASSWORD]`), which becomes the handle only when the message is sent.
// So the value exists in exactly one place in the browser — the open form — and is gone from there as
// soon as it has been posted: never in the draft, the message, the outbox or storage. The model sees the
// handle and passes it on; the server's tools resolve it.

/** The contract's handle pattern (AgentSecretHandles.Pattern), global: use with matchAll/replace. */
export const SECRET_HANDLE = /\{\{secret:[A-Za-z0-9_-]{22}\}\}/g;

const ONE_HANDLE = /^\{\{secret:[A-Za-z0-9_-]{22}\}\}$/;

/** True when the whole value (surrounding whitespace allowed) is exactly one handle. */
export function isHandle(value) {
  return typeof value === 'string' && ONE_HANDLE.test(value.trim());
}

/** A message as text and handles, in order — for drawing each handle as a lock chip. */
export function splitSecrets(text) {
  const source = String(text ?? '');
  const parts = [];
  let at = 0;
  for (const match of source.matchAll(SECRET_HANDLE)) {
    if (match.index > at) parts.push({ type: 'text', text: source.slice(at, match.index) });
    parts.push({ type: 'secret', handle: match[0] });
    at = match.index + match[0].length;
  }
  if (at < source.length) parts.push({ type: 'text', text: source.slice(at) });
  return parts;
}

// What a secret typed straight into a message tends to look like. A gentle heuristic for a warning the
// person can ignore — never a block — so it leans towards the obvious: a secret-ish key followed by
// `:`/`=` and a value, or a token in a well-known provider format.
const SECRET_LOOKS = [
  // A few words may sit between the key and the colon: "password for shop-db: …", «رمز دیتابیس: …».
  /(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key)[^\n:=]{0,25}?["']?\s*[:=]\s*["']?[^\s"']{3,}/i,
  /(رمز|گذرواژه|کلمه\s*عبور)[^\n:=]{0,25}?[:=]\s*\S{3,}/,
  /\bsk-[A-Za-z0-9_-]{20,}/,
  /\bghp_[A-Za-z0-9]{30,}/,
  /\bgithub_pat_[A-Za-z0-9_]{20,}/,
  /\bxox[abprs]-[A-Za-z0-9-]{10,}/,
  /\bAKIA[0-9A-Z]{16}\b/,
  /-----BEGIN [A-Z ]*PRIVATE KEY-----/,
];

/** Whether a draft seems to carry a secret in plain text. Handles do not count: they are the safe form. */
export function looksLikeSecret(text) {
  // Each handle (and each placeholder, should a raw draft be passed) becomes a lone quote mark: a
  // character no "value" in the patterns may contain, so `password for shop-db: [🔒 DB_PASSWORD]
  // please` — the safe form — is not mistaken for a key followed by a value.
  const plain = String(text ?? '').replace(SECRET_HANDLE, ' " ').replace(PLACEHOLDER, ' " ');
  return SECRET_LOOKS.some((pattern) => pattern.test(plain));
}

/** A placeholder as this pane writes it into the draft. */
const PLACEHOLDER = /\[🔒 [^\]\n]*\]/g;

/**
 * The text without its secrets, for deciding which way it reads: a draft that opens with
 * `[🔒 DB_PASSWORD]`, or a message with a handle in it, says nothing about the language around it.
 */
export function withoutSecrets(text) {
  return String(text ?? '').replace(SECRET_HANDLE, ' ').replace(PLACEHOLDER, ' ');
}

/**
 * The secrets held in this pane, by handle: their names and the placeholders that stand for them in
 * the draft. Handles and names only — the book is never given a value.
 */
export function createSecretBook() {
  const byHandle = new Map(); // handle → { name, placeholder }
  const byPlaceholder = new Map(); // placeholder → handle

  return {
    /** Records a held secret (AgentSecretView) and returns the placeholder for the draft. */
    add(view) {
      const name = String(view?.name ?? '').trim() || 'secret';
      let placeholder = `[🔒 ${name}]`;
      for (let n = 2; byPlaceholder.has(placeholder); n++) placeholder = `[🔒 ${name} ${n}]`;
      byHandle.set(view.handle, { name, placeholder });
      byPlaceholder.set(placeholder, view.handle);
      return placeholder;
    },

    /** The draft with `placeholder` inserted at `cursor`, and where the cursor goes after it. */
    insert(draft, cursor, placeholder) {
      const text = String(draft ?? '');
      const at = Number.isInteger(cursor) && cursor >= 0 && cursor <= text.length ? cursor : text.length;
      return { draft: text.slice(0, at) + placeholder + text.slice(at), cursor: at + placeholder.length };
    },

    /** For sending: every placeholder this book made becomes its handle. Anything else stays as typed. */
    expand(draft) {
      let text = String(draft ?? '');
      // Longest first, so a placeholder that happens to contain a shorter one is replaced whole.
      const entries = [...byPlaceholder].sort((a, b) => b[0].length - a[0].length);
      for (const [placeholder, handle] of entries) text = text.split(placeholder).join(handle);
      return text;
    },

    /** For words given back to the composer: handles this book knows become their placeholders again. */
    collapse(text) {
      return String(text ?? '').replace(SECRET_HANDLE, (handle) => byHandle.get(handle)?.placeholder ?? handle);
    },

    nameOf(handle) {
      return byHandle.get(handle)?.name ?? null;
    },

    toJSON() {
      return { secrets: [...byHandle.values()].map((s) => s.name) };
    },
  };
}

const MAX_NAME = 100;
const MAX_VALUE = 8000;

/**
 * The lock button's form. `wrap` lets the island make the state reactive (Vue's `reactive`) while the
 * form mutates it through the proxy; tests pass nothing and read the plain object.
 */
export function createSecretForm({ api, book, wrap = (s) => s }) {
  const blank = () => ({ open: false, name: '', value: '', reveal: false, busy: false, error: null });
  const state = wrap(blank());
  // Every open and close starts a new generation: a post still in flight when the form was cancelled
  // (or cancelled and opened again) finds itself stale and touches nothing — no placeholder, no reset
  // of a form the person has started filling in again.
  let generation = 0;

  function reset() {
    generation++;
    Object.assign(state, blank());
  }

  function refuse(messageEn, messageFa) {
    state.error = { messageEn, messageFa };
    return null;
  }

  return {
    state,
    open() {
      reset();
      state.open = true;
    },
    /** Closing forgets what was typed — the value most of all. */
    close: reset,

    /** Posts the secret. Returns the placeholder for the draft, or null (the reason is in `state.error`). */
    async submit() {
      if (state.busy) return null;
      const name = state.name.trim();
      if (!name || name.length > MAX_NAME) {
        return refuse(`Give the secret a name of 1–${MAX_NAME} characters, for example DATABASE_PASSWORD.`,
          `یک نام ۱ تا ${MAX_NAME} نویسه‌ای برای آن بنویسید، مثلاً DATABASE_PASSWORD.`);
      }
      // The name ends up inside "[🔒 …]" in the draft; a bracket in it would end the placeholder early.
      if (/[[\]]/.test(name)) {
        return refuse('A name cannot contain [ or ].', 'نام نمی‌تواند [ یا ] داشته باشد.');
      }
      if (!state.value || state.value.length > MAX_VALUE) {
        return refuse(`The secret must be 1–${MAX_VALUE} characters.`, `مقدار باید ۱ تا ${MAX_VALUE} نویسه باشد.`);
      }
      state.busy = true;
      state.error = null;
      const mine = generation;
      try {
        const view = await api.holdSecret({ name, value: state.value });
        if (mine !== generation) return null; // cancelled meanwhile
        const placeholder = book.add({ handle: view.handle, name: view.name ?? name });
        reset();
        return placeholder;
      } catch (error) {
        if (mine !== generation) return null;
        state.busy = false;
        return refuse(error?.messageEn ?? String(error), error?.messageFa ?? error?.messageEn ?? String(error));
      }
    },
  };
}
