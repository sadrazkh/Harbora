import './app.css';
import { createApp } from 'vue';
import DeploymentLogs from './islands/DeploymentLogs.vue';

// "Islands" pattern: Razor renders the page; we hydrate only the interactive nodes.
// Each island is a mount point identified by id/selector — like initialising a jQuery plugin,
// but with Vue's reactivity + a SignalR connection for live data.
type IslandMounter = (el: HTMLElement) => void;

const islands: Record<string, IslandMounter> = {
  'deployment-logs': (el) => {
    createApp(DeploymentLogs, {
      deploymentId: el.dataset.deploymentId!,
      initialStatus: el.dataset.status!,
    }).mount(el);
    // Tell the Razor fallback poller to stand down — the island owns the stream now.
    (window as any).__harboraLogsMounted = true;
  },
};

for (const [id, mount] of Object.entries(islands)) {
  const el = document.getElementById(id);
  if (el) mount(el);
}
