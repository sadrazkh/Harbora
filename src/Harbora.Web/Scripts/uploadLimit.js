// A file over a form's stated ceiling is refused here, before the browser sends any of it.
//
// The server refuses one too, with the same sentence (`UploadLimit.TooLarge`), but by then the browser
// is still sending: a server that answers a large upload before it has finished arriving is often
// shown as a dropped connection instead of its answer, and somebody who waited ten minutes to send
// three gigabytes deserves to hear "too big" before the first byte, not after the last.
//
// A form opts in with `data-upload-limit`, carries its ceiling in `data-max-bytes` and its refusal,
// already in the reader's language, in `data-too-big`; an element with `data-upload-limit-error`
// inside it is where the refusal is shown. `createUploadLimit` is the testable core and takes plain
// element-like objects; `initUploadLimits` is the wiring `main.ts` calls.

/** Whether the chosen file is over the ceiling. No file, or no usable ceiling, is never too big. */
export function isTooBig(file, maxBytes) {
  return !!file && Number.isFinite(maxBytes) && maxBytes > 0 && file.size > maxBytes;
}

/**
 * @param {{
 *   form: { dataset: Record<string, string | undefined>, addEventListener(type: string, fn: Function): void },
 *   input: { files?: ArrayLike<{ size: number }> | null, addEventListener(type: string, fn: Function): void } | null,
 *   error?: { textContent: string, classList: { toggle(cls: string, force?: boolean): void } } | null,
 *   buttons?: Array<{ disabled: boolean }>,
 * }} parts
 */
export function createUploadLimit({ form, input, error = null, buttons = [] }) {
  const maxBytes = Number(form.dataset.maxBytes);

  const check = () => {
    const tooBig = isTooBig(input?.files?.[0], maxBytes);
    if (error) {
      error.textContent = tooBig ? (form.dataset.tooBig ?? '') : '';
      error.classList.toggle('hidden', !tooBig);
    }
    buttons.forEach((button) => { button.disabled = tooBig; });
    return !tooBig;
  };

  input?.addEventListener('change', check);
  form.addEventListener('submit', (event) => {
    if (!check()) event.preventDefault();
  });

  return { check };
}

export function initUploadLimits(doc = document) {
  doc.querySelectorAll('form[data-upload-limit]').forEach((form) => {
    createUploadLimit({
      form,
      input: form.querySelector('input[type=file]'),
      error: form.querySelector('[data-upload-limit-error]'),
      buttons: Array.from(form.querySelectorAll('button')),
    });
  });
}
