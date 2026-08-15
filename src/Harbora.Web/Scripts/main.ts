import './app.css';
import { createApp } from 'vue';
import DeploymentLogs from './islands/DeploymentLogs.vue';
import RouteDesigner from './islands/RouteDesigner.vue';
import MetricsChart from './islands/MetricsChart.vue';
import TerminalIsland from './islands/Terminal.vue';
import { mountDeployProgress } from './deployProgress';

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
  'app-terminal': (el) => {
    createApp(TerminalIsland, {
      appId: el.dataset.appId!,
      lang: el.dataset.lang || 'en',
    }).mount(el);
  },
  'metrics-chart': (el) => {
    createApp(MetricsChart, {
      name: el.dataset.name!,
      label: el.dataset.label || '',
      color: el.dataset.color || '#818cf8',
      // A resource the caller may already see. The container name used to be passed here and put
      // straight into the query, which made it the key to any tenant's series.
      appId: el.dataset.appId,
      serviceId: el.dataset.serviceId,
      limit: el.dataset.limit ? Number(el.dataset.limit) : undefined,
      unit: (el.dataset.unit as 'bytes' | 'percent' | 'raw' | undefined),
      height: el.dataset.height ? Number(el.dataset.height) : undefined,
      // Set only by pages with a range control of their own (the usage tabs); everything else keeps
      // the island's own default window and per-chart picker, unchanged.
      minutes: el.dataset.minutes ? Number(el.dataset.minutes) : undefined,
      lang: (el.dataset.lang as 'en' | 'fa' | undefined),
    }).mount(el);
  },
};

for (const [id, mount] of Object.entries(islands)) {
  // By id for the pages that have one of something, and by attribute for the pages that have
  // several — an application shows a chart per measurement, and ids are unique.
  const el = document.getElementById(id);
  if (el) mount(el);
  document.querySelectorAll<HTMLElement>(`[data-island="${id}"]`).forEach(mount);
}

// Razor owns the progress bar; the island owns the socket. This wires the two through the DOM so
// neither has to know about the other.
mountDeployProgress();

// ---- icons ----
// Declared as `data-lucide` attributes in Razor rather than inlined SVG, so a partial stays
// readable. Re-rendered after dynamic updates because the Vue islands and the deployment log
// poller both insert markup after first paint.
//
// Imported one by one, deliberately. Pulling in lucide's whole `icons` barrel took the bundle from
// 138 kB to 821 kB — six times the weight of the entire panel, for a set of glyphs, on every page
// load. Adding an icon to a view means adding it here too, which is the point.
import {
    createIcons, Activity, AlertTriangle, Archive, ArrowRight, ArrowUpLeft, ArrowUpRight, Bell,
    BookOpen, Box, Boxes, Building2, Check, CheckCircle2, ChevronDown, ChevronRight, ChevronUp,
    CircleCheck, CircleCheckBig, CircleHelp, CloudUpload, Code, Coins, Container, Copy, CornerLeftUp, Cpu, CreditCard,
    Cuboid, Database, DatabaseZap, Download, ExternalLink, File, FileCode2, FileJson2, Folder,
    FolderLock, FolderOpen, Gauge, GitBranch, Globe, Globe2, HardDrive, History, Info, KeyRound,
    Languages, Layers, Layers3, LayoutDashboard, Link, Lock, LockKeyhole, LogOut, Mail, Menu, Monitor, Moon, Network,
    PanelLeftClose, PanelRight, Pause, Pencil, Play, Plug, PlugZap, Plus, RefreshCw, Rocket, RotateCw, Route,
    Ruler, ScrollText, Search, SearchX, Server, Settings, Settings2, Shapes, ShieldAlert,
    ShieldCheck, SlidersHorizontal, Sparkles, SquareArrowOutUpRight, SquareTerminal, Star, Sun,
    ServerCog, Table2, Terminal, TerminalSquare, ToggleRight, Trash2, TrendingUp, TriangleAlert,
    UploadCloud, UserPlus, Users, Wallet,
} from 'lucide';

const usedIcons = {
    Activity, AlertTriangle, Archive, ArrowRight, ArrowUpLeft, ArrowUpRight, Bell, BookOpen, Box,
    Boxes, Building2, Check, CheckCircle2, ChevronDown, ChevronRight, ChevronUp, CircleCheck,
    CircleCheckBig, CircleHelp, CloudUpload, Code, Coins, Container, Copy, CornerLeftUp, Cpu, CreditCard, Cuboid,
    Database, DatabaseZap, Download, ExternalLink, File, FileCode2, FileJson2, Folder, FolderLock,
    FolderOpen, Gauge, GitBranch, Globe, Globe2, HardDrive, History, Info, KeyRound, Languages,
    Layers, Layers3, LayoutDashboard, Link, Lock, LockKeyhole, LogOut, Mail, Menu, Monitor, Moon, Network,
    PanelLeftClose, PanelRight, Pause, Pencil, Play, Plug, PlugZap, Plus, RefreshCw, Rocket, RotateCw, Route,
    Ruler, ScrollText, Search, SearchX, Server, Settings, Settings2, Shapes, ShieldAlert,
    ShieldCheck, SlidersHorizontal, Sparkles, SquareArrowOutUpRight, SquareTerminal, Star, Sun,
    ServerCog, Table2, Terminal, TerminalSquare, ToggleRight, Trash2, TrendingUp, TriangleAlert,
    UploadCloud, UserPlus, Users, Wallet,
};

function renderIcons() {
    createIcons({ icons: usedIcons, attrs: { 'stroke-width': '1.75' } });
}

renderIcons();
document.addEventListener('harbora:content-changed', renderIcons);

// ---- app shell interactions ----
// Dropdowns share one small behaviour: only one is open, outside click and Escape both close it.
const menuRoots = Array.from(document.querySelectorAll<HTMLElement>('[data-menu-root]'));
function closeMenus(except?: HTMLElement) {
  for (const root of menuRoots) {
    if (root === except) continue;
    root.querySelector<HTMLElement>('[data-menu]')?.classList.add('hidden');
    root.querySelector<HTMLElement>('[data-menu-trigger]')?.setAttribute('aria-expanded', 'false');
  }
}
for (const root of menuRoots) {
  const trigger = root.querySelector<HTMLElement>('[data-menu-trigger]');
  const menu = root.querySelector<HTMLElement>('[data-menu]');
  trigger?.addEventListener('click', (event) => {
    event.stopPropagation();
    const opening = menu?.classList.contains('hidden') ?? false;
    closeMenus(root);
    menu?.classList.toggle('hidden', !opening);
    trigger.setAttribute('aria-expanded', opening ? 'true' : 'false');
  });
}
document.addEventListener('click', () => closeMenus());
document.addEventListener('keydown', (event) => { if (event.key === 'Escape') closeMenus(); });

// Desktop compact mode persists per browser. Mobile remains an off-canvas panel.
const collapseButton = document.querySelector<HTMLElement>('[data-collapse-sidebar]');
const collapsed = localStorage.getItem('harbora-sidebar') === 'collapsed';
document.documentElement.classList.toggle('sidebar-collapsed', collapsed);
collapseButton?.addEventListener('click', () => {
  const next = !document.documentElement.classList.contains('sidebar-collapsed');
  document.documentElement.classList.toggle('sidebar-collapsed', next);
  localStorage.setItem('harbora-sidebar', next ? 'collapsed' : 'expanded');
});

// Command palette: the top-bar search is intentionally a button, because a text field that accepts
// input and does nothing is worse than no search. The palette filters only routes the current user
// is authorised to see because its entries come from the same navigation map as the sidebar.
const palette = document.querySelector<HTMLElement>('[data-command-palette]');
const commandInput = palette?.querySelector<HTMLInputElement>('[data-command-input]');
const commandItems = Array.from(palette?.querySelectorAll<HTMLAnchorElement>('[data-command-item]') ?? []);
let selected = 0;

function visibleCommandItems() {
  return commandItems.filter((item) => !item.classList.contains('hidden'));
}

function paintCommandSelection(index: number) {
  const visible = visibleCommandItems();
  if (visible.length === 0) return;
  selected = (index + visible.length) % visible.length;
  visible.forEach((item, i) => item.classList.toggle('is-selected', i === selected));
  visible[selected].scrollIntoView({ block: 'nearest' });
}

function openPalette(open: boolean) {
  if (!palette) return;
  palette.classList.toggle('hidden', !open);
  palette.setAttribute('aria-hidden', open ? 'false' : 'true');
  document.body.classList.toggle('overflow-hidden', open);
  if (open) {
    commandInput?.focus();
    commandInput?.select();
    selected = 0;
    paintCommandSelection(0);
  }
}

document.querySelectorAll('[data-command-open]').forEach((button) => button.addEventListener('click', () => openPalette(true)));
palette?.querySelector('[data-command-backdrop]')?.addEventListener('click', () => openPalette(false));
document.addEventListener('keydown', (event) => {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault();
    openPalette(true);
    return;
  }
  if (!palette || palette.classList.contains('hidden')) return;
  if (event.key === 'Escape') openPalette(false);
  else if (event.key === 'ArrowDown') { event.preventDefault(); paintCommandSelection(selected + 1); }
  else if (event.key === 'ArrowUp') { event.preventDefault(); paintCommandSelection(selected - 1); }
  else if (event.key === 'Enter' && document.activeElement === commandInput) {
    event.preventDefault();
    visibleCommandItems()[selected]?.click();
  }
});

commandInput?.addEventListener('input', () => {
  const query = commandInput.value.trim().toLocaleLowerCase();
  for (const item of commandItems) {
    const haystack = (item.dataset.search || item.textContent || '').toLocaleLowerCase();
    item.classList.toggle('hidden', query.length > 0 && !haystack.includes(query));
  }
  selected = 0;
  paintCommandSelection(0);
});
