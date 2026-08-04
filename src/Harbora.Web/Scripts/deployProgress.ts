/**
 * Moves the staged progress bar as a deployment runs.
 *
 * It used to be rendered once from the status at page load and never touched again: the logs
 * streamed, the deployment progressed, and the steps sat still — which reads as a broken page far
 * more than no bar at all would.
 *
 * The status→step mapping is not repeated here. It is serialised into the element by the server, so
 * this file knows nothing about Pushing or HealthChecking and cannot drift from the rule when a
 * status is added.
 */

type Json = Record<string, number>;

export function mountDeployProgress(): void {
  const root = document.querySelector<HTMLElement>('[data-deploy-progress]');
  if (!root) return;

  const map: Json = safeParse(root.dataset.stepMap, {});
  const terminal: string[] = safeParse(root.dataset.terminalStatuses, []);
  const steps = Array.from(root.querySelectorAll<HTMLElement>('.deploy-step'));
  const count = steps.length;

  function apply(status: string): void {
    if (!status) return;
    root!.dataset.status = status;

    const failed = status === 'Failed';
    const cancelled = status === 'Cancelled';
    const rolledBack = status === 'RolledBack';
    const succeeded = status === 'Succeeded';
    const active = Object.prototype.hasOwnProperty.call(map, status) ? map[status] : null;

    steps.forEach((step, i) => {
      let state = 'pending';

      if (succeeded) state = 'done';
      else if (rolledBack) state = i < count - 1 ? 'done' : 'failed';
      else if (failed || cancelled) state = i === 0 ? (failed ? 'failed' : 'cancelled') : 'pending';
      else if (active !== null) state = i < active ? 'done' : i === active ? 'active' : 'pending';

      step.dataset.state = state;
    });

    // The notes are rendered by the server and toggled here, so their wording stays in one place
    // and stays translated.
    root!.querySelectorAll<HTMLElement>('[data-deploy-note]').forEach((note) => {
      note.hidden = note.dataset.deployNote !== status;
    });

    if (terminal.includes(status)) finish(status);
  }

  function finish(status: string): void {
    const panel = document.querySelector<HTMLElement>('[data-deploy-outcome]');
    if (!panel) return;

    // Only the panel matching this ending is shown. Both are server-rendered, so the wording and
    // the link are the same whether somebody watched it happen or opened the page afterwards.
    document.querySelectorAll<HTMLElement>('[data-deploy-outcome]').forEach((el) => {
      el.hidden = el.dataset.deployOutcome !== (status === 'Succeeded' ? 'Succeeded' : 'Ended');
    });
  }

  // Published by the logs island over the DOM rather than wired directly: the bar is Razor's, the
  // socket is the island's, and neither needs to know the other exists.
  window.addEventListener('harbora:deployment-status', (event) => {
    apply((event as CustomEvent<{ status: string }>).detail?.status);
  });

  // Re-applied on load so a page opened mid-deploy agrees with itself, and so the outcome panel
  // appears for a deployment that finished before anyone looked.
  apply(root.dataset.status || '');
}

function safeParse<T>(value: string | undefined, fallback: T): T {
  if (!value) return fallback;
  try {
    return JSON.parse(value) as T;
  } catch {
    // A malformed attribute must not take the page down with it: without the map the bar simply
    // stops moving, which is what it did before this existed.
    return fallback;
  }
}
