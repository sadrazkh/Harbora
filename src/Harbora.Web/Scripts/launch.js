// The launch pages' small script: the poll, the cost refresh, the address preview, the drop zone.
//
// Everything here is an improvement on a page that already works. /launch posts as an ordinary form,
// the progress page is a complete page with a refresh link, and the server refuses honestly whatever
// figure is on the screen. So a failure in here leaves what is showing alone rather than saying
// anything about the launch: the one thing this must never do is make a launch look further along,
// or more broken, than the server says it is.
//
// The decisions are plain functions taking plain values, so `node --test` exercises them with small
// fakes and no DOM (the same split as `mobileSidebar.js`). `initLaunch` is the thin wiring, and acts
// only when its elements are on the page, because main.ts runs it on every page.

export const POLL_MS = 2500;
export const QUOTE_DEBOUNCE_MS = 250;
export const SLOW_AFTER_MS = 60_000;

/**
 * What to do with one poll's answer.
 *
 * A redirect is the login page: the session ended, and swapping a sign-in form into this page would
 * be nonsense. A 404 is the launch gone. Anything else that is not an answer (a restart, a dropped
 * connection) is probably momentary, so the page keeps what it has and asks again.
 *
 * @param {{ ok: boolean, status?: number, redirected?: boolean, terminal?: boolean, reload?: boolean, recognised?: boolean }} answer
 * @returns {'swap' | 'swap-and-stop' | 'reload' | 'retry' | 'stop'}
 */
export function afterPoll({ ok, status, redirected, terminal, reload, recognised }) {
  if (redirected || status === 404 || status === 401 || status === 403) return 'stop';
  if (!ok || !recognised) return 'retry';
  if (reload) return 'reload';
  return terminal ? 'swap-and-stop' : 'swap';
}

/**
 * The poll itself. One request at a time, and the next only once this one has answered, so a slow
 * panel is asked less often rather than queued up behind.
 *
 * @param {{
 *   fetchProgress(): Promise<{ ok: boolean, status?: number, redirected?: boolean, html?: string }>,
 *   readState(html: string): { terminal: boolean, reload: boolean } | null,
 *   swap(html: string): void,
 *   reload(): void,
 *   tick?(): void,
 *   schedule(fn: () => void, ms: number): unknown,
 * }} deps
 */
export function createPoller({ fetchProgress, readState, swap, reload, tick, schedule }) {
  let last = null;
  let stopped = false;

  async function poll() {
    if (stopped) return;

    let next = 'retry';
    let html = null;
    try {
      const answer = await fetchProgress();
      html = answer.html ?? null;
      const state = answer.ok && html !== null ? readState(html) : null;
      next = afterPoll({
        ok: answer.ok,
        status: answer.status,
        redirected: answer.redirected,
        recognised: state !== null,
        terminal: state?.terminal,
        reload: state?.reload,
      });
    } catch {
      next = 'retry';
    }

    tick?.();

    if (next === 'reload') {
      stopped = true;
      reload();
      return;
    }
    if (next === 'stop') {
      stopped = true;
      return;
    }
    // An unchanged answer is not put back: replacing it would restart the spinner, close a <details>
    // somebody just opened and have a screen reader read the whole region out again.
    if ((next === 'swap' || next === 'swap-and-stop') && html !== last) {
      last = html;
      swap(html);
    }
    if (next === 'swap-and-stop') {
      stopped = true;
      return;
    }
    schedule(poll, POLL_MS);
  }

  return {
    start: () => schedule(poll, POLL_MS),
    /** Runs one poll now. For tests. */
    poll,
    isStopped: () => stopped,
  };
}

/** Whether the reading has taken long enough to say so. */
export function isSlow(startedAt, now) {
  return now - startedAt >= SLOW_AFTER_MS;
}

/**
 * Refreshes the figure when the form changes: debounced, because ticking three boxes is one question,
 * and ignoring any answer but the newest, because an answer about the form as it was is a wrong answer.
 *
 * A failed request changes nothing. The figure stays as it was and the server still refuses honestly.
 *
 * @param {{
 *   send(): Promise<string | null>,
 *   apply(html: string): void,
 *   schedule(fn: () => void, ms: number): unknown,
 *   cancel(handle: unknown): void,
 * }} deps
 */
export function createQuoteRefresher({ send, apply, schedule, cancel }) {
  let pending = null;
  let newest = 0;

  async function run() {
    pending = null;
    const mine = ++newest;
    try {
      const html = await send();
      if (mine === newest && html !== null) apply(html);
    } catch {
      /* the figure on the screen stands */
    }
  }

  return {
    trigger() {
      if (pending !== null) cancel(pending);
      pending = schedule(run, QUOTE_DEBOUNCE_MS);
    },
  };
}

/**
 * The form with every variable's value taken out. The figure does not depend on any of them, and an
 * answer to "what would this cost" has no business carrying somebody's API key along with it.
 *
 * @param {FormData} formData
 */
export function withoutValues(formData) {
  for (const key of [...formData.keys()]) {
    if (key.startsWith('Values[')) formData.delete(key);
  }
  return formData;
}

/**
 * The slug the server would derive (`AppProvisioningService.Slugify`), for the address shown beside
 * the name. Letters of any script survive, because they do there. The server's answer is the one that
 * counts: this is only so the address follows the typing.
 */
export function slugFor(name) {
  const slug = (name ?? '')
    .trim()
    .toLowerCase()
    .replace(/[^\p{L}\p{Nd}]/gu, '-')
    .replace(/-{2,}/g, '-')
    .replace(/^-+|-+$/g, '');
  const cut = slug.length > 50 ? slug.slice(0, 50).replace(/-+$/, '') : slug;
  return cut === '' ? 'app' : cut;
}

export function addressFor(name, slug, rootDomain) {
  const typed = (slug ?? '').trim();
  return `${typed === '' ? slugFor(name) : typed}.${rootDomain}`;
}

export function isTooBig(file, maxBytes) {
  return !!file && file.size > maxBytes;
}

function fragment(doc, html) {
  const template = doc.createElement('template');
  template.innerHTML = html;
  return template.content;
}

function contentChanged(doc) {
  // main.ts redraws icons on this: what was just swapped in holds only the empty <i> placeholders.
  doc.dispatchEvent(new CustomEvent('harbora:content-changed'));
}

function initPoll(doc, win) {
  const region = doc.querySelector('[data-launch-poll]');
  if (!region) return;

  const first = region.querySelector('[data-launch-state]');
  if (!first || first.dataset.terminal === 'true' || first.dataset.reload === 'true') return;

  const startedAt = win.Date.now();
  const paintSlow = () => {
    region.querySelector('[data-launch-slow]')?.classList.toggle('hidden', !isSlow(startedAt, win.Date.now()));
  };

  createPoller({
    fetchProgress: async () => {
      const response = await win.fetch(region.dataset.launchPoll, { headers: { Accept: 'text/html' } });
      return {
        ok: response.ok,
        status: response.status,
        redirected: response.redirected,
        html: response.ok ? await response.text() : undefined,
      };
    },
    readState: (html) => {
      const state = fragment(doc, html).querySelector('[data-launch-state]');
      return state ? { terminal: state.dataset.terminal === 'true', reload: state.dataset.reload === 'true' } : null;
    },
    swap: (html) => {
      region.innerHTML = html;
      contentChanged(doc);
    },
    reload: () => win.location.reload(),
    tick: paintSlow,
    schedule: (fn, ms) => win.setTimeout(fn, ms),
  }).start();
}

function initConfirm(doc, win) {
  const form = doc.querySelector('[data-launch-confirm]');
  if (!form) return;

  const cost = form.querySelector('[data-launch-cost]');
  const submit = form.querySelector('[data-launch-submit]');
  const name = form.querySelector('[data-launch-name]');
  const slug = form.querySelector('[data-launch-slug]');
  const kind = form.querySelector('[data-launch-kind]');
  const address = form.querySelector('[data-launch-address]');

  const refresher = createQuoteRefresher({
    send: async () => {
      const body = withoutValues(new win.FormData(form));
      const response = await win.fetch(form.dataset.quoteUrl, { method: 'POST', body, headers: { Accept: 'text/html' } });
      return response.ok && !response.redirected ? response.text() : null;
    },
    apply: (html) => {
      if (!cost) return;
      cost.innerHTML = html;
      const state = cost.querySelector('[data-launch-cost-state]');
      if (submit && state) submit.disabled = state.dataset.refused === 'true';
      contentChanged(doc);
    },
    schedule: (fn, ms) => win.setTimeout(fn, ms),
    cancel: (handle) => win.clearTimeout(handle),
  });

  form.addEventListener('change', (event) => {
    const field = event.target?.name;
    if (field === 'Services' || field === 'InstanceSizeKey' || field === 'ServerId' || field === 'Kind') refresher.trigger();
  });

  const paintAddress = () => {
    const root = form.dataset.rootDomain;
    if (address && root) address.textContent = addressFor(name?.value, slug?.value, root);
  };
  name?.addEventListener('input', paintAddress);
  slug?.addEventListener('input', paintAddress);

  // A class rather than the `hidden` attribute: these are .form-field blocks, whose display wins over it.
  const paintKind = () => {
    const worker = kind?.value === 'Worker';
    form.querySelectorAll('[data-launch-web-only]').forEach((el) => el.classList.toggle('hidden', worker));
  };
  kind?.addEventListener('change', paintKind);
  paintKind();
}

function initUpload(doc, win) {
  const form = doc.querySelector('[data-launch-upload]');
  if (!form) return;

  const input = form.querySelector('input[type=file]');
  const zone = form.querySelector('[data-launch-drop]');
  const error = form.querySelector('[data-launch-file-error]');
  const button = form.querySelector('[data-launch-upload-button]');
  const maxBytes = Number(form.dataset.maxBytes);
  const label = button?.textContent;

  const paintFile = () => {
    const tooBig = isTooBig(input?.files?.[0], maxBytes);
    if (error) {
      error.textContent = tooBig ? form.dataset.tooBig : '';
      error.classList.toggle('hidden', !tooBig);
    }
    if (button) button.disabled = tooBig;
    return !tooBig;
  };

  input?.addEventListener('change', paintFile);

  zone?.addEventListener('dragover', (event) => {
    event.preventDefault();
    zone.dataset.over = 'true';
  });
  zone?.addEventListener('dragleave', () => {
    delete zone.dataset.over;
  });
  zone?.addEventListener('drop', (event) => {
    event.preventDefault();
    delete zone.dataset.over;
    if (input && event.dataTransfer?.files?.length) {
      input.files = event.dataTransfer.files;
      paintFile();
    }
  });

  // A plain form post shows no progress of its own, and a big zip takes long enough to look like
  // nothing happened: this is what stops somebody pressing the button again.
  form.addEventListener('submit', (event) => {
    if (!paintFile()) {
      event.preventDefault();
      return;
    }
    if (button) {
      button.disabled = true;
      button.textContent = form.dataset.sending;
    }
  });

  // Back from the next page restores this page as it was left, button disabled and all.
  win.addEventListener('pageshow', () => {
    if (button) {
      button.disabled = false;
      if (label) button.textContent = label;
    }
    paintFile();
  });
}

export function initLaunch(doc = document, win = window) {
  initPoll(doc, win);
  initConfirm(doc, win);
  initUpload(doc, win);
}
