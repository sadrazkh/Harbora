<script setup lang="ts">
import { ref, reactive, computed } from 'vue';

interface Route {
  id: string | null;
  appId: string | null;
  type: string;            // HostBased | PathBased | Redirect
  host: string;
  pathPrefix: string;
  priority: number;
  targetService: string;
  targetPort: number;
  sslEnabled: boolean;
  redirectHttpToHttps: boolean;
  webSocketEnabled: boolean;
  basicAuthEnabled: boolean;
  basicAuthUser: string | null;
  basicAuthPassword: string | null;
  basicAuthConfigured: boolean;
  customHeadersJson: string | null;
  redirectTo: string | null;
  isEnabled: boolean;
}
interface Target { label: string; service: string; port: number; appId: string; }

const props = defineProps<{ initialRoutes: Route[]; targets: Target[]; csrf: string; lang: string; }>();

const routes = reactive<Route[]>([...props.initialRoutes]);
const selected = ref<number>(routes.length ? 0 : -1);

// A snapshot taken the moment a card opens, so "Discard" can restore it exactly and "Keep draft"
// can just close the card - every keystroke already lands straight in `routes[i]` either way.
const editSnapshot = ref<Route | null>(
  selected.value >= 0 ? (JSON.parse(JSON.stringify(routes[selected.value])) as Route) : null,
);

const tab = ref<'map' | 'config' | 'validate'>('map');
const preview = ref('');
const validation = ref<{ isValid: boolean; errors: string[]; warnings: string[] } | null>(null);
const applyResult = ref<{ success: boolean; error: string | null; rolledBack: boolean } | null>(null);
const busy = ref(false);
let dragIndex = -1;

// What is actually saved (and, as far as this island knows, applied). Compared against `routes`
// to answer "what is still a draft" - see `isRouteDirty` below - instead of a single hand-set
// flag, which used to stay true even after undoing every edit back to what was already on disk.
const baseline = ref<Route[]>(JSON.parse(JSON.stringify(props.initialRoutes)) as Route[]);

// --- tiny bilingual dictionary (fa/en) ---
const dict: Record<string, [string, string]> = {
  rules: ['قوانین', 'Rules'], add: ['افزودن مسیر', 'Add route'],
  validate: ['اعتبارسنجی', 'Validate'], save: ['ذخیره و اعمال', 'Save & Apply'],
  map: ['نقشه مسیر', 'Route map'], config: ['کانفیگ تولیدشده', 'Generated config'],
  host: ['دامنه', 'Host'], path: ['مسیر (Path)', 'Path prefix'], target: ['مقصد', 'Target'],
  match: ['تطبیق', 'Match'],
  matchHost: ['دامنه', 'Host'], matchPath: ['مسیر', 'Path'], matchRedirect: ['ریدایرکت', 'Redirect'],
  port: ['پورت', 'Port'], type: ['نوع', 'Type'], ssl: ['گواهی SSL', 'SSL certificate'],
  forceHttps: ['ریدایرکت HTTP→HTTPS', 'Redirect HTTP→HTTPS'], ws: ['WebSocket', 'WebSocket'],
  basicAuth: ['احراز پایه', 'Basic auth'], headers: ['هدرهای سفارشی', 'Custom headers'],
  authUser: ['نام کاربری', 'Username'], authPass: ['گذرواژه', 'Password'], authSet: ['•••••• (تنظیم‌شده)', '•••••• (set)'],
  redirectTo: ['ریدایرکت به', 'Redirect to'], enabled: ['فعال', 'Enabled'],
  empty: ['هنوز مسیری نیست', 'No routes yet'], remove: ['حذف', 'Remove'],
  noConfig: ['برای دیدن کانفیگ، «کانفیگ تولیدشده» را بزنید.', 'Open “Generated config” to render.'],
  applied: ['کانفیگ با موفقیت اعمال شد.', 'Configuration applied.'],
  saveFailed: ['اعمال ناموفق بود', 'Apply failed'], rolledBack: ['برگردانده شد', 'rolled back'],
  resolvesTo: ['معادل است با', 'resolves to'],
  discard: ['رها کردن', 'Discard'], keepDraft: ['نگه‌داشتن پیش‌نویس', 'Keep draft'],
  ruleLabel: ['قانون', 'Rule'], draft: ['پیش‌نویس', 'Draft'],
  edit: ['ویرایش', 'Edit'], editing: ['در حال ویرایش', 'Editing'],
  unappliedChange: ['تغییر اعمال‌نشده', 'unapplied change'],
};
const t = (k: string) => dict[k]?.[props.lang === 'fa' ? 0 : 1] ?? k;

const enabledRoutes = computed(() => routes.filter(r => r.isEnabled));

function newRoute(): Route {
  return {
    id: null, appId: null, type: 'HostBased', host: '', pathPrefix: '/', priority: routes.length + 1,
    targetService: props.targets[0]?.service ?? '', targetPort: props.targets[0]?.port ?? 80,
    sslEnabled: true, redirectHttpToHttps: true, webSocketEnabled: false, basicAuthEnabled: false,
    basicAuthUser: '', basicAuthPassword: '', basicAuthConfigured: false,
    customHeadersJson: null, redirectTo: null, isEnabled: true,
  };
}
function addRoute() {
  routes.push(newRoute());
  selectRoute(routes.length - 1);
  recomputePriorities();
  markDirty();
}
function removeRoute(i: number) {
  routes.splice(i, 1);
  if (selected.value >= routes.length) selected.value = routes.length - 1;
  editSnapshot.value = null;
  recomputePriorities();
  markDirty();
}
/** Clears the stale apply banner on any further edit. Draft/dirty state itself is derived below
 *  from real content, not set here - see `isRouteDirty`. */
function markDirty() { applyResult.value = null; }

function onTargetChange(r: Route, service: string) {
  const target = props.targets.find(x => x.service === service);
  if (target) { r.targetService = target.service; r.targetPort = target.port; r.appId = target.appId; }
  markDirty();
}

function selectRoute(i: number) {
  if (selected.value === i) return;
  selected.value = i;
  editSnapshot.value = routes[i] ? (JSON.parse(JSON.stringify(routes[i])) as Route) : null;
}
function discardEdit(i: number) {
  if (editSnapshot.value) Object.assign(routes[i], editSnapshot.value);
  editSnapshot.value = null;
  selected.value = -1;
  applyResult.value = null;
}
function keepDraft() {
  editSnapshot.value = null;
  selected.value = -1;
}

// --- drag to reorder = change priority (top wins) ---
function onDragStart(i: number) { dragIndex = i; }
function onDrop(i: number) {
  if (dragIndex < 0 || dragIndex === i) return;
  const [moved] = routes.splice(dragIndex, 1);
  routes.splice(i, 0, moved);
  selected.value = i;
  editSnapshot.value = routes[i] ? (JSON.parse(JSON.stringify(routes[i])) as Route) : null;
  dragIndex = -1;
  recomputePriorities();
  markDirty();
}
function recomputePriorities() { routes.forEach((r, i) => (r.priority = routes.length - i)); }

// --- draft tracking: derived from real content rather than a hand-set flag, so undoing an edit
// (or discarding it) clears the badge on its own instead of leaving it stuck on. ---
function isRouteDirty(r: Route): boolean {
  if (r.id) {
    const base = baseline.value.find(b => b.id === r.id);
    return !base || JSON.stringify(r) !== JSON.stringify(base);
  }
  // Never saved. This reads as "clean" only right after a save just re-synced the baseline to
  // match `routes` exactly, id-less rows included - matched by content, since a freshly created
  // route has no id for `baseline` to key on yet.
  return !baseline.value.some(b => !b.id && JSON.stringify(b) === JSON.stringify(r));
}
const changedCount = computed(() => {
  const currentIds = new Set(routes.filter(r => r.id).map(r => r.id as string));
  const removed = baseline.value.filter(b => b.id && !currentIds.has(b.id as string)).length;
  return routes.filter(r => isRouteDirty(r)).length + removed;
});
const isDirty = computed(() => changedCount.value > 0);
const unappliedLabel = computed(() => props.lang === 'fa'
  ? `${changedCount.value} ${t('unappliedChange')}`
  : `${changedCount.value} unapplied change${changedCount.value === 1 ? '' : 's'}`);

// --- API ---
async function api(path: string, body: Route[] = routes) {
  const res = await fetch('/routes/' + path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': props.csrf },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}

// --- config diff: the baseline preview is fetched once (and re-fetched after a save) and diffed
// line by line against the live preview, so "Config diff" shows only what this draft would
// change - not the whole generated file re-typed in green. ---
const baselinePreview = ref<string | null>(null);
async function ensureBaselinePreview() {
  if (baselinePreview.value !== null) return;
  try {
    const r = await api('preview', baseline.value);
    baselinePreview.value = r.content as string;
  } catch {
    // Leave it null - the diff below degrades to a plain, uncoloured listing rather than
    // guessing at a "before" it never actually received.
  }
}

interface DiffLine { text: string; kind: 'context' | 'add' | 'remove'; }

/** A small line-level LCS diff. Configs here are a handful of routes - never large enough for the
 *  O(n·m) table to matter. */
function diffLines(before: string, after: string): DiffLine[] {
  const a = before.split('\n');
  const b = after.split('\n');
  const n = a.length, m = b.length;
  const dp: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    }
  }
  const out: DiffLine[] = [];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) { out.push({ text: a[i], kind: 'context' }); i++; j++; }
    else if (dp[i + 1][j] >= dp[i][j + 1]) { out.push({ text: a[i], kind: 'remove' }); i++; }
    else { out.push({ text: b[j], kind: 'add' }); j++; }
  }
  while (i < n) { out.push({ text: a[i], kind: 'remove' }); i++; }
  while (j < m) { out.push({ text: b[j], kind: 'add' }); j++; }
  return out;
}
const configDiffLines = computed(() => diffLines(baselinePreview.value ?? preview.value, preview.value));

async function doPreview() {
  busy.value = true;
  try {
    const r = await api('preview');
    preview.value = r.content;
    tab.value = 'config';
    await ensureBaselinePreview();
  } finally { busy.value = false; }
}
async function doValidate() { busy.value = true; try { validation.value = await api('validate'); tab.value = 'validate'; } finally { busy.value = false; } }
async function doSave() {
  busy.value = true; applyResult.value = null;
  try {
    const r = await api('save');
    validation.value = r.validation;
    if (r.saved) {
      // The draft just became what is on disk (and, if the apply below succeeded, what is live) -
      // re-sync the baseline so `isDirty` reflects that instead of staying stuck on.
      baseline.value = JSON.parse(JSON.stringify(routes));
      baselinePreview.value = null;
      applyResult.value = r.apply;
      if (!r.apply.success) tab.value = 'validate';
    } else {
      tab.value = 'validate';
    }
  } catch (e: any) { validation.value = { isValid: false, errors: [String(e.message ?? e)], warnings: [] }; tab.value = 'validate'; }
  finally { busy.value = false; }
}
</script>

<template>
  <div>
    <!-- Header actions: the unapplied-change badge + Validate + Save & Apply, always visible at
         the top of the designer so a draft's status is never more than a glance away. -->
    <div class="mb-4 flex flex-wrap items-center justify-end gap-2.5">
      <span v-if="isDirty" class="inline-flex min-h-[44px] items-center rounded-lg border border-warn/25 bg-warn-soft px-3 text-xs font-medium text-warn">
        {{ unappliedLabel }}
      </span>
      <button type="button" @click="doValidate" :disabled="busy" class="btn-secondary min-h-[44px] text-sm">{{ t('validate') }}</button>
      <button type="button" @click="doSave" :disabled="busy" class="btn-primary min-h-[44px] text-sm disabled:opacity-50">{{ t('save') }}</button>
    </div>

    <div v-if="applyResult" :class="['mb-4 rounded-xl border px-4 py-3 text-sm', applyResult.success ? 'border-ok/20 bg-ok-soft text-ok' : 'border-danger/20 bg-danger-soft text-danger']">
      <span v-if="applyResult.success">✓ {{ t('applied') }}</span>
      <span v-else>✗ {{ t('saveFailed') }}: {{ applyResult.error }}<span v-if="applyResult.rolledBack"> ({{ t('rolledBack') }})</span></span>
    </div>

    <div class="grid gap-[18px] lg:grid-cols-12">
      <!-- Rules list -->
      <section class="min-w-0 lg:col-span-7 flex flex-col gap-3">
        <h2 class="font-semibold text-ink">{{ t('rules') }} <span class="text-ink-faint text-sm font-normal">({{ routes.length }})</span></h2>

        <p v-if="!routes.length" class="rounded-xl border border-dashed border-line-strong p-8 text-center text-ink-muted">{{ t('empty') }}</p>

        <div v-for="(r, i) in routes" :key="i" draggable="true"
             @dragstart="onDragStart(i)" @dragover.prevent @drop="onDrop(i)"
             @click="selectRoute(i)"
             :class="['cursor-grab rounded-xl border p-3.5 transition-colors duration-150',
                      selected === i ? 'border-[1.5px] border-accent bg-surface shadow-panel-hover' : 'border-line bg-surface hover:border-line-strong',
                      !r.isEnabled ? 'opacity-50' : '']">
          <div class="flex items-center gap-3.5">
            <span class="shrink-0 font-mono text-[13px] text-ink-faint" aria-hidden="true">⠿</span>
            <span class="shrink-0 rounded px-1.5 py-1 font-mono text-[10px] font-medium"
                  :class="selected === i ? 'border border-accent/20 bg-accent-soft text-accent-text' : 'border border-line text-ink-muted'">{{ r.priority }}</span>
            <div class="min-w-0 flex-1">
              <div class="truncate font-mono text-sm font-medium text-ink" dir="ltr">{{ r.host || '—' }}<span class="text-accent-text">{{ r.pathPrefix }}</span></div>
              <div class="mt-1.5 truncate font-mono text-[10.5px] text-ink-faint" dir="ltr">
                → {{ r.targetService || '—' }}:{{ r.targetPort }}<span v-if="r.basicAuthEnabled"> · {{ t('basicAuth') }}</span>
              </div>
            </div>
            <div class="ms-auto flex shrink-0 items-center gap-1.5 text-[10px]">
              <span v-if="r.sslEnabled" class="rounded bg-ok-soft px-1.5 py-0.5 font-medium uppercase text-ok">SSL</span>
              <span v-if="r.webSocketEnabled" class="rounded bg-info-soft px-1.5 py-0.5 font-medium uppercase text-info">WS</span>
              <span v-if="r.type === 'Redirect'" class="rounded bg-warn-soft px-1.5 py-0.5 font-medium text-warn">↪</span>
              <span v-if="isRouteDirty(r)" class="rounded bg-warn-soft px-1.5 py-0.5 font-medium uppercase text-warn">{{ t('draft') }}</span>
              <span class="ms-1 text-ink-faint">{{ selected === i ? t('editing') : t('edit') }}</span>
            </div>
          </div>

          <!-- Grouped edit panel for the selected rule: Match / Target / switches / Custom
               headers, then a Discard / Keep draft footer. Discard restores the snapshot taken
               when this card opened; Keep draft just closes it - every keystroke above already
               landed in `r` directly. -->
          <div v-if="selected === i" class="mt-3.5 space-y-3.5 border-t border-line pt-3.5" @click.stop>
            <div class="flex items-center justify-between">
              <span class="text-sm font-semibold text-ink">{{ t('ruleLabel') }} {{ i + 1 }}</span>
              <button type="button" @click="removeRoute(i)" class="-m-2 rounded-md p-2 text-xs font-medium text-danger hover:opacity-80">{{ t('remove') }}</button>
            </div>

            <div class="space-y-2">
              <div class="form-label mb-0">{{ t('match') }}</div>
              <div class="grid grid-cols-2 gap-2">
                <select v-model="r.type" @change="markDirty" class="form-control">
                  <option value="HostBased">{{ t('matchHost') }}</option>
                  <option value="PathBased">{{ t('matchPath') }}</option>
                  <option value="Redirect">{{ t('matchRedirect') }}</option>
                </select>
                <input v-model="r.pathPrefix" @input="markDirty" class="form-control font-mono" dir="ltr" />
              </div>
              <input v-model="r.host" @input="markDirty" placeholder="app.example.com" class="form-control font-mono" dir="ltr" />
            </div>

            <template v-if="r.type !== 'Redirect'">
              <div class="space-y-2 border-t border-line pt-3.5">
                <div class="form-label mb-0">{{ t('target') }}</div>
                <div class="grid grid-cols-[1fr_84px] gap-2">
                  <select :value="r.targetService" @change="onTargetChange(r, ($event.target as HTMLSelectElement).value)" class="form-control">
                    <option v-for="tg in targets" :key="tg.service" :value="tg.service">{{ tg.label }}</option>
                  </select>
                  <input type="number" v-model.number="r.targetPort" @input="markDirty" class="form-control font-mono" dir="ltr" />
                </div>
                <p class="font-mono text-[10.5px] text-ink-faint" dir="ltr">{{ t('resolvesTo') }} {{ r.targetService || '—' }}:{{ r.targetPort }}</p>
              </div>

              <div class="space-y-1 border-t border-line pt-3.5">
                <label class="flex min-h-[44px] items-center justify-between gap-3">
                  <span class="text-sm text-ink">{{ t('enabled') }}</span>
                  <span class="relative inline-flex h-[19px] w-[34px] shrink-0 items-center rounded-full transition-colors" :class="r.isEnabled ? 'bg-accent' : 'bg-line-strong'">
                    <input type="checkbox" v-model="r.isEnabled" @change="markDirty" class="absolute inset-0 h-full w-full cursor-pointer opacity-0" />
                    <span class="pointer-events-none inline-block h-[15px] w-[15px] rounded-full bg-white shadow transition-transform"
                          :class="r.isEnabled ? 'translate-x-[17px] rtl:-translate-x-[17px]' : 'translate-x-[2px] rtl:-translate-x-[2px]'"></span>
                  </span>
                </label>
                <label class="flex min-h-[44px] items-center justify-between gap-3">
                  <span class="text-sm text-ink">{{ t('ssl') }}</span>
                  <span class="relative inline-flex h-[19px] w-[34px] shrink-0 items-center rounded-full transition-colors" :class="r.sslEnabled ? 'bg-accent' : 'bg-line-strong'">
                    <input type="checkbox" v-model="r.sslEnabled" @change="markDirty" class="absolute inset-0 h-full w-full cursor-pointer opacity-0" />
                    <span class="pointer-events-none inline-block h-[15px] w-[15px] rounded-full bg-white shadow transition-transform"
                          :class="r.sslEnabled ? 'translate-x-[17px] rtl:-translate-x-[17px]' : 'translate-x-[2px] rtl:-translate-x-[2px]'"></span>
                  </span>
                </label>
                <label class="flex min-h-[44px] items-center justify-between gap-3">
                  <span class="text-sm text-ink">{{ t('forceHttps') }}</span>
                  <span class="relative inline-flex h-[19px] w-[34px] shrink-0 items-center rounded-full transition-colors" :class="r.redirectHttpToHttps ? 'bg-accent' : 'bg-line-strong'">
                    <input type="checkbox" v-model="r.redirectHttpToHttps" @change="markDirty" class="absolute inset-0 h-full w-full cursor-pointer opacity-0" />
                    <span class="pointer-events-none inline-block h-[15px] w-[15px] rounded-full bg-white shadow transition-transform"
                          :class="r.redirectHttpToHttps ? 'translate-x-[17px] rtl:-translate-x-[17px]' : 'translate-x-[2px] rtl:-translate-x-[2px]'"></span>
                  </span>
                </label>
                <label class="flex min-h-[44px] items-center justify-between gap-3">
                  <span class="text-sm text-ink">{{ t('ws') }}</span>
                  <span class="relative inline-flex h-[19px] w-[34px] shrink-0 items-center rounded-full transition-colors" :class="r.webSocketEnabled ? 'bg-accent' : 'bg-line-strong'">
                    <input type="checkbox" v-model="r.webSocketEnabled" @change="markDirty" class="absolute inset-0 h-full w-full cursor-pointer opacity-0" />
                    <span class="pointer-events-none inline-block h-[15px] w-[15px] rounded-full bg-white shadow transition-transform"
                          :class="r.webSocketEnabled ? 'translate-x-[17px] rtl:-translate-x-[17px]' : 'translate-x-[2px] rtl:-translate-x-[2px]'"></span>
                  </span>
                </label>
                <label class="flex min-h-[44px] items-center justify-between gap-3">
                  <span class="text-sm text-ink">{{ t('basicAuth') }}</span>
                  <span class="relative inline-flex h-[19px] w-[34px] shrink-0 items-center rounded-full transition-colors" :class="r.basicAuthEnabled ? 'bg-accent' : 'bg-line-strong'">
                    <input type="checkbox" v-model="r.basicAuthEnabled" @change="markDirty" class="absolute inset-0 h-full w-full cursor-pointer opacity-0" />
                    <span class="pointer-events-none inline-block h-[15px] w-[15px] rounded-full bg-white shadow transition-transform"
                          :class="r.basicAuthEnabled ? 'translate-x-[17px] rtl:-translate-x-[17px]' : 'translate-x-[2px] rtl:-translate-x-[2px]'"></span>
                  </span>
                </label>
                <div v-if="r.basicAuthEnabled" class="grid grid-cols-2 gap-2 pb-1">
                  <input v-model="r.basicAuthUser" @input="markDirty" :placeholder="t('authUser')" class="form-control font-mono" dir="ltr" />
                  <input v-model="r.basicAuthPassword" @input="markDirty" type="password"
                         :placeholder="r.basicAuthConfigured ? t('authSet') : t('authPass')"
                         class="form-control font-mono" dir="ltr" />
                </div>
              </div>

              <div class="border-t border-line pt-3.5">
                <div class="mb-2 flex items-center justify-between">
                  <span class="form-label mb-0">{{ t('headers') }}</span>
                  <span class="text-xs font-medium text-accent-text">JSON</span>
                </div>
                <input v-model="r.customHeadersJson" @input="markDirty" placeholder='{"X-Frame-Options":"DENY"}' class="form-control font-mono text-xs" dir="ltr" />
              </div>
            </template>
            <div v-else class="border-t border-line pt-3.5">
              <label class="form-label">{{ t('redirectTo') }}</label>
              <input v-model="r.redirectTo" @input="markDirty" placeholder="https://example.com/$1" class="form-control font-mono" dir="ltr" />
            </div>

            <div class="flex items-center gap-2 border-t border-line pt-3.5">
              <button type="button" @click="discardEdit(i)" class="btn-secondary min-h-[44px] flex-1 justify-center">{{ t('discard') }}</button>
              <button type="button" @click="keepDraft" class="min-h-[44px] flex-1 rounded-lg bg-ink px-4 text-sm font-semibold text-canvas transition-opacity hover:opacity-90">{{ t('keepDraft') }}</button>
            </div>
          </div>
        </div>

        <button type="button" @click="addRoute" class="min-h-[44px] rounded-xl border border-dashed border-line-strong px-4 py-3.5 text-center text-xs font-medium text-ink-muted transition-colors hover:border-accent/40 hover:text-ink">
          + {{ t('add') }}
        </button>
      </section>

      <!-- Panel: map / config (with the config diff) / validation -->
      <section class="min-w-0 lg:col-span-5">
        <div class="mb-3 inline-flex rounded-lg bg-surface-2 p-1 text-sm">
          <button type="button" @click="tab = 'map'" class="inline-flex min-h-[44px] items-center justify-center rounded-md px-3" :class="tab==='map' ? 'bg-surface font-medium text-ink shadow-sm' : 'text-ink-muted'">{{ t('map') }}</button>
          <button type="button" @click="doPreview" class="inline-flex min-h-[44px] items-center justify-center rounded-md px-3" :class="tab==='config' ? 'bg-surface font-medium text-ink shadow-sm' : 'text-ink-muted'">{{ t('config') }}</button>
          <button type="button" @click="tab = 'validate'" class="inline-flex min-h-[44px] items-center justify-center rounded-md px-3" :class="tab==='validate' ? 'bg-surface font-medium text-ink shadow-sm' : 'text-ink-muted'">{{ t('validate') }}</button>
        </div>

        <!-- Route map -->
        <div v-show="tab === 'map'" class="space-y-2 rounded-xl border border-line bg-surface p-4">
          <p v-if="!enabledRoutes.length" class="text-ink-muted text-sm">{{ t('empty') }}</p>
          <div v-for="(r, i) in enabledRoutes" :key="i" class="flex flex-wrap items-center gap-2 text-sm" dir="ltr">
            <span class="rounded-lg bg-surface-2 px-2 py-1 font-mono">{{ r.sslEnabled ? '🔒 ' : '' }}{{ r.host || '—' }}</span>
            <span class="rounded-lg bg-surface-2 px-2 py-1 font-mono text-ink-muted">{{ r.pathPrefix }}</span>
            <span class="text-accent-text">→</span>
            <span v-if="r.type === 'Redirect'" class="rounded-lg bg-warn-soft px-2 py-1 text-warn">↪ {{ r.redirectTo }}</span>
            <span v-else class="rounded-lg bg-accent-soft px-2 py-1 font-mono text-accent-text">{{ r.targetService }}:{{ r.targetPort }}</span>
            <span v-if="r.webSocketEnabled" class="rounded bg-info-soft px-1.5 py-0.5 text-[10px] font-medium uppercase text-info">WS</span>
            <span v-if="r.basicAuthEnabled" class="rounded bg-surface-2 px-1.5 py-0.5 text-[10px] font-medium text-ink-muted">AUTH</span>
          </div>
        </div>

        <!-- Generated config + diff: one block element per line, so a long YAML line wraps in
             place instead of forcing the panel to scroll sideways. -->
        <div v-show="tab === 'config'" class="overflow-hidden rounded-xl border border-line bg-code">
          <div class="flex items-center justify-between border-b border-line-strong/60 px-4 py-2.5">
            <span class="text-xs font-semibold text-code-ink">{{ t('config') }}</span>
            <span class="font-mono text-[10.5px] text-ink-faint" dir="ltr">traefik · dynamic.yml</span>
          </div>
          <div class="max-h-[60vh] overflow-y-auto p-3">
            <p v-if="!preview" class="p-2 text-sm text-ink-faint">{{ t('noConfig') }}</p>
            <div v-else class="font-mono text-[11px] leading-[1.9]" dir="ltr">
              <div v-for="(line, li) in configDiffLines" :key="li"
                   class="whitespace-pre-wrap break-all rounded px-2"
                   :class="line.kind === 'add' ? 'bg-ok-soft text-ok' : line.kind === 'remove' ? 'bg-danger-soft text-danger' : 'text-code-ink/70'">{{ line.kind === 'add' ? '+ ' : line.kind === 'remove' ? '- ' : '  ' }}{{ line.text }}</div>
            </div>
          </div>
        </div>

        <!-- Validation -->
        <div v-show="tab === 'validate'" class="space-y-2 rounded-xl border border-line bg-surface p-4 text-sm">
          <p v-if="!validation" class="text-ink-faint">—</p>
          <template v-else>
            <p :class="validation.isValid ? 'text-ok' : 'text-danger'">
              {{ validation.isValid ? '✓ valid' : '✗ invalid' }}
            </p>
            <div v-for="(e, i) in validation.errors" :key="'e'+i" class="text-danger">• {{ e }}</div>
            <div v-for="(w, i) in validation.warnings" :key="'w'+i" class="text-warn">⚠ {{ w }}</div>
          </template>
        </div>
      </section>
    </div>
  </div>
</template>
