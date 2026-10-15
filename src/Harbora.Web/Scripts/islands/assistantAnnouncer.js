// The pane's one announcer: a single polite live region, outside the conversation log, that says short
// things to a screen reader — «کپی شد» after a copy — instead of every message carrying a region of its
// own. (Inside the log it would also be held back: the log is aria-busy while an answer is coming, and
// screen readers wait for a busy region to finish before they read changes in it.)
//
// The panel makes one (`createAnnouncer`), draws its `state.text` in a visually hidden
// `role="status"` element, and provides it to the components below it under `ANNOUNCER`.
// Plain JS with the timers injected, so node --test drives it.

/** The `provide`/`inject` key the panel provides the announcer under. */
export const ANNOUNCER = Symbol('harbora-assistant-announcer');

/**
 * @param {{
 *   wrap?: <T extends object>(state: T) => T,
 *   timers?: { setTimeout: Function, clearTimeout: Function },
 *   holdMs?: number,
 * }} [deps]
 *   `wrap` makes the state reactive in the component (`reactive`); `holdMs` is how long a line stays in
 *   the region before it is emptied, so that the same line said again later is a change, and is read.
 */
export function createAnnouncer({
  wrap = (state) => state,
  timers = { setTimeout: globalThis.setTimeout.bind(globalThis), clearTimeout: globalThis.clearTimeout.bind(globalThis) },
  holdMs = 1500,
} = {}) {
  const state = wrap({ text: '' });
  let timer = null;

  const clear = () => {
    if (timer !== null) timers.clearTimeout(timer);
    timer = null;
  };

  return {
    state,
    /** Says `text` (a later line replaces an earlier one); empty or missing text says nothing. */
    say(text) {
      const line = typeof text === 'string' ? text : '';
      clear();
      state.text = line;
      if (line) {
        timer = timers.setTimeout(() => {
          timer = null;
          state.text = '';
        }, holdMs);
      }
    },
    /** The pane is going away: nothing more is said, and no timer is left running. */
    dispose() {
      clear();
      state.text = '';
    },
  };
}
