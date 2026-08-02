import './app.css';
import { createApp } from 'vue';
import DeploymentLogs from './islands/DeploymentLogs.vue';
import RouteDesigner from './islands/RouteDesigner.vue';
import MetricsChart from './islands/MetricsChart.vue';

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
  'route-designer': (el) => {
    createApp(RouteDesigner, {
      initialRoutes: JSON.parse(el.dataset.routes || '[]'),
      targets: JSON.parse(el.dataset.targets || '[]'),
      csrf: el.dataset.csrf || '',
      lang: el.dataset.lang || 'en',
    }).mount(el);
  },
  'metrics-chart': (el) => {
    createApp(MetricsChart, {
      name: el.dataset.name!,
      label: el.dataset.label || '',
      color: el.dataset.color || '#818cf8',
      resource: el.dataset.resource,
      height: el.dataset.height ? Number(el.dataset.height) : undefined,
    }).mount(el);
  },
};

for (const [id, mount] of Object.entries(islands)) {
  const el = document.getElementById(id);
  if (el) mount(el);
}

// ---- icons ----
// Declared as `data-lucide` attributes in Razor rather than inlined SVG, so a partial stays
// readable. Re-rendered after dynamic updates because the Vue islands and the deployment log
// poller both insert markup after first paint.
//
// Imported one by one, deliberately. Pulling in lucide's whole `icons` barrel took the bundle from
// 138 kB to 821 kB — six times the weight of the entire panel, for a set of glyphs, on every page
// load. Adding an icon to a view means adding it here too, which is the point.
import {
    createIcons,
    Activity, Archive, Boxes, Building2, CreditCard, GitBranch, Globe, Layers, LayoutDashboard,
    Menu, Monitor, Moon, Network, Rocket, Route, ScrollText, Search, Server, Settings, Shapes, Sun,
} from 'lucide';

const usedIcons = {
    Activity, Archive, Boxes, Building2, CreditCard, GitBranch, Globe, Layers, LayoutDashboard,
    Menu, Monitor, Moon, Network, Rocket, Route, ScrollText, Search, Server, Settings, Shapes, Sun,
};

function renderIcons() {
    createIcons({ icons: usedIcons, attrs: { 'stroke-width': '1.75' } });
}

renderIcons();
document.addEventListener('harbora:content-changed', renderIcons);
