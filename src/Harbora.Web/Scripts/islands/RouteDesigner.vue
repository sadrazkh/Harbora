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
  customHeadersJson: string | null;
  redirectTo: string | null;
  isEnabled: boolean;
}
interface Target { label: string; service: string; port: number; appId: string; }

const props = defineProps<{ initialRoutes: Route[]; targets: Target[]; csrf: string; lang: string; }>();

const routes = reactive<Route[]>([...props.initialRoutes]);
const selected = ref<number>(routes.length ? 0 : -1);
const tab = ref<'map' | 'config' | 'validate'>('map');
const preview = ref('');
const validation = ref<{ isValid: boolean; errors: string[]; warnings: string[] } | null>(null);
const applyResult = ref<{ success: boolean; error: string | null; rolledBack: boolean } | null>(null);
const busy = ref(false);
const dirty = ref(false);
let dragIndex = -1;

// --- tiny bilingual dictionary (fa/en) ---
const dict: Record<string, [string, string]> = {
  rules: ['قوانین', 'Rules'], add: ['افزودن مسیر', 'Add route'],
  validate: ['اعتبارسنجی', 'Validate'], save: ['ذخیره و اعمال', 'Save & Apply'],
  map: ['نقشه مسیر', 'Route map'], config: ['کانفیگ تولیدشده', 'Generated config'],
  host: ['دامنه', 'Host'], path: ['مسیر (Path)', 'Path prefix'], target: ['مقصد', 'Target'],
  port: ['پورت', 'Port'], type: ['نوع', 'Type'], ssl: ['SSL', 'SSL'],
  forceHttps: ['ریدایرکت HTTP→HTTPS', 'Redirect HTTP→HTTPS'], ws: ['WebSocket', 'WebSocket'],
  basicAuth: ['احراز پایه', 'Basic auth'], headers: ['هدرهای سفارشی (JSON)', 'Custom headers (JSON)'],
  redirectTo: ['ریدایرکت به', 'Redirect to'], enabled: ['فعال', 'Enabled'],
  empty: ['هنوز مسیری نیست', 'No routes yet'], remove: ['حذف', 'Remove'],
  noConfig: ['برای دیدن کانفیگ، «کانفیگ تولیدشده» را بزنید.', 'Open “Generated config” to render.'],
  applied: ['کانفیگ با موفقیت اعمال شد.', 'Configuration applied.'],
  saveFailed: ['اعمال ناموفق بود', 'Apply failed'],
};
const t = (k: string) => dict[k]?.[props.lang === 'fa' ? 0 : 1] ?? k;

const enabledRoutes = computed(() => routes.filter(r => r.isEnabled));

function newRoute(): Route {
  return {
    id: null, appId: null, type: 'HostBased', host: '', pathPrefix: '/', priority: routes.length + 1,
    targetService: props.targets[0]?.service ?? '', targetPort: props.targets[0]?.port ?? 80,
    sslEnabled: true, redirectHttpToHttps: true, webSocketEnabled: false, basicAuthEnabled: false,
    customHeadersJson: null, redirectTo: null, isEnabled: true,
  };
}
function addRoute() { routes.push(newRoute()); selected.value = routes.length - 1; recomputePriorities(); markDirty(); }
function removeRoute(i: number) { routes.splice(i, 1); if (selected.value >= routes.length) selected.value = routes.length - 1; recomputePriorities(); markDirty(); }
function markDirty() { dirty.value = true; applyResult.value = null; }

function onTargetChange(r: Route, service: string) {
  const target = props.targets.find(x => x.service === service);
  if (target) { r.targetService = target.service; r.targetPort = target.port; r.appId = target.appId; }
  markDirty();
}

// --- drag to reorder = change priority (top wins) ---
function onDragStart(i: number) { dragIndex = i; }
function onDrop(i: number) {
  if (dragIndex < 0 || dragIndex === i) return;
  const [moved] = routes.splice(dragIndex, 1);
  routes.splice(i, 0, moved);
  selected.value = i; dragIndex = -1; recomputePriorities(); markDirty();
}
function recomputePriorities() { routes.forEach((r, i) => (r.priority = routes.length - i)); }

// --- API ---
async function api(path: string) {
  const res = await fetch('/routes/' + path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': props.csrf },
    body: JSON.stringify(routes),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}
async function doPreview() { busy.value = true; try { const r = await api('preview'); preview.value = r.content; tab.value = 'config'; } finally { busy.value = false; } }
async function doValidate() { busy.value = true; try { validation.value = await api('validate'); tab.value = 'validate'; } finally { busy.value = false; } }
async function doSave() {
  busy.value = true; applyResult.value = null;
  try {
    const r = await api('save');
    validation.value = r.validation;
    if (r.saved) { applyResult.value = r.apply; dirty.value = false; if (!r.apply.success) tab.value = 'validate'; }
    else tab.value = 'validate';
  } catch (e: any) { validation.value = { isValid: false, errors: [String(e.message ?? e)], warnings: [] }; tab.value = 'validate'; }
  finally { busy.value = false; }
}
</script>

<template>
  <div class="grid lg:grid-cols-12 gap-6">
    <!-- Rules list -->
    <section class="lg:col-span-5 space-y-3">
      <div class="flex items-center justify-between">
        <h2 class="font-semibold">{{ t('rules') }} <span class="text-slate-500 text-sm">({{ routes.length }})</span></h2>
        <button @click="addRoute" class="rounded-lg bg-brand-600 hover:bg-brand-500 px-3 py-1.5 text-sm font-semibold">+ {{ t('add') }}</button>
      </div>

      <p v-if="!routes.length" class="rounded-xl border border-dashed border-slate-700 p-8 text-center text-slate-400">{{ t('empty') }}</p>

      <div v-for="(r, i) in routes" :key="i" draggable="true"
           @dragstart="onDragStart(i)" @dragover.prevent @drop="onDrop(i)"
           @click="selected = i"
           :class="['rounded-xl border p-3 cursor-grab transition',
                    selected === i ? 'border-brand-500 bg-slate-900' : 'border-slate-800 bg-slate-900/60 hover:border-slate-700',
                    !r.isEnabled ? 'opacity-50' : '']">
        <div class="flex items-center justify-between">
          <div class="min-w-0">
            <div class="font-mono text-sm truncate">{{ r.host || '—' }}<span class="text-slate-500">{{ r.pathPrefix }}</span></div>
            <div class="text-xs text-slate-500 truncate">→ {{ r.targetService || '—' }}:{{ r.targetPort }}</div>
          </div>
          <div class="flex items-center gap-1 text-[10px]">
            <span v-if="r.sslEnabled" class="rounded bg-emerald-500/15 text-emerald-400 px-1.5 py-0.5">SSL</span>
            <span v-if="r.webSocketEnabled" class="rounded bg-sky-500/15 text-sky-400 px-1.5 py-0.5">WS</span>
            <span v-if="r.type === 'Redirect'" class="rounded bg-amber-500/15 text-amber-300 px-1.5 py-0.5">↪</span>
          </div>
        </div>

        <!-- Inline editor for the selected rule -->
        <div v-if="selected === i" class="mt-3 space-y-3 border-t border-slate-800 pt-3" @click.stop>
          <div class="flex items-center justify-between">
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" v-model="r.isEnabled" @change="markDirty"> {{ t('enabled') }}</label>
            <button @click="removeRoute(i)" class="text-xs text-red-400 hover:text-red-300">{{ t('remove') }}</button>
          </div>
          <div class="grid grid-cols-2 gap-2">
            <label class="text-xs text-slate-400">{{ t('type') }}
              <select v-model="r.type" @change="markDirty" class="mt-1 w-full rounded-lg bg-slate-800 border border-slate-700 px-2 py-1.5 text-sm">
                <option value="HostBased">Host</option><option value="PathBased">Path</option><option value="Redirect">Redirect</option>
              </select>
            </label>
            <label class="text-xs text-slate-400">{{ t('path') }}
              <input v-model="r.pathPrefix" @input="markDirty" class="mt-1 w-full rounded-lg bg-slate-800 border border-slate-700 px-2 py-1.5 text-sm" />
            </label>
          </div>
          <label class="text-xs text-slate-400 block">{{ t('host') }}
            <input v-model="r.host" @input="markDirty" placeholder="app.example.com" class="mt-1 w-full rounded-lg bg-slate-800 border border-slate-700 px-2 py-1.5 text-sm" />
          </label>

          <template v-if="r.type !== 'Redirect'">
            <div class="grid grid-cols-3 gap-2">
              <label class="text-xs text-slate-400 col-span-2">{{ t('target') }}
                <select :value="r.targetService" @change="onTargetChange(r, ($event.target as HTMLSelectElement).value)"
                        class="mt-1 w-full rounded-lg bg-slate-800 border border-slate-700 px-2 py-1.5 text-sm">
                  <option v-for="tg in targets" :key="tg.service" :value="tg.service">{{ tg.label }} ({{ tg.service }})</option>
                </select>
              </label>
              <label class="text-xs text-slate-400">{{ t('port') }}
                <input type="number" v-model.number="r.targetPort" @input="markDirty" class="mt-1 w-full rounded-lg bg-slate-800 border border-slate-700 px-2 py-1.5 text-sm" />
              </label>
            </div>
            <div class="grid grid-cols-2 gap-2 text-sm">
              <label class="flex items-center gap-2"><input type="checkbox" v-model="r.sslEnabled" @change="markDirty"> {{ t('ssl') }}</label>
              <label class="flex items-center gap-2"><input type="checkbox" v-model="r.redirectHttpToHttps" @change="markDirty"> {{ t('forceHttps') }}</label>
              <label class="flex items-center gap-2"><input type="checkbox" v-model="r.webSocketEnabled" @change="markDirty"> {{ t('ws') }}</label>
              <label class="flex items-center gap-2"><input type="checkbox" v-model="r.basicAuthEnabled" @change="markDirty"> {{ t('basicAuth') }}</label>
            </div>
            <label class="text-xs text-slate-400 block">{{ t('headers') }}
              <input v-model="r.customHeadersJson" @input="markDirty" placeholder='{"X-Frame-Options":"DENY"}' class="mt-1 w-full rounded-lg bg-slate-800 border border-slate-700 px-2 py-1.5 text-sm font-mono" />
            </label>
          </template>
          <label v-else class="text-xs text-slate-400 block">{{ t('redirectTo') }}
            <input v-model="r.redirectTo" @input="markDirty" placeholder="https://example.com/$1" class="mt-1 w-full rounded-lg bg-slate-800 border border-slate-700 px-2 py-1.5 text-sm" />
          </label>
        </div>
      </div>
    </section>

    <!-- Panel: map / config / validation -->
    <section class="lg:col-span-7">
      <div class="flex items-center justify-between mb-3">
        <div class="inline-flex rounded-lg bg-slate-800 p-1 text-sm">
          <button @click="tab = 'map'"   :class="tab==='map'   ? 'bg-slate-700 rounded-md px-3 py-1' : 'px-3 py-1'">{{ t('map') }}</button>
          <button @click="doPreview"     :class="tab==='config'? 'bg-slate-700 rounded-md px-3 py-1' : 'px-3 py-1'">{{ t('config') }}</button>
          <button @click="tab = 'validate'" :class="tab==='validate'? 'bg-slate-700 rounded-md px-3 py-1' : 'px-3 py-1'">{{ t('validate') }}</button>
        </div>
        <div class="flex gap-2">
          <button @click="doValidate" :disabled="busy" class="rounded-lg bg-slate-800 hover:bg-slate-700 px-3 py-1.5 text-sm">{{ t('validate') }}</button>
          <button @click="doSave" :disabled="busy" class="rounded-lg bg-brand-600 hover:bg-brand-500 px-4 py-1.5 text-sm font-semibold disabled:opacity-50">{{ t('save') }}</button>
        </div>
      </div>

      <div v-if="applyResult" :class="['mb-3 rounded-lg px-4 py-2 text-sm', applyResult.success ? 'bg-emerald-900/20 border border-emerald-700 text-emerald-300' : 'bg-red-900/20 border border-red-700 text-red-300']">
        <span v-if="applyResult.success">✓ {{ t('applied') }}</span>
        <span v-else>✗ {{ t('saveFailed') }}: {{ applyResult.error }}<span v-if="applyResult.rolledBack"> (rolled back)</span></span>
      </div>

      <!-- Route map -->
      <div v-show="tab === 'map'" class="rounded-xl border border-slate-800 bg-slate-900/60 p-4 space-y-2">
        <p v-if="!enabledRoutes.length" class="text-slate-400 text-sm">{{ t('empty') }}</p>
        <div v-for="(r, i) in enabledRoutes" :key="i" class="flex items-center gap-2 flex-wrap text-sm">
          <span class="rounded-lg bg-slate-800 px-2 py-1 font-mono">{{ r.sslEnabled ? '🔒' : '' }} {{ r.host || '—' }}</span>
          <span class="rounded-lg bg-slate-800 px-2 py-1 font-mono text-slate-300">{{ r.pathPrefix }}</span>
          <span class="text-brand-400">→</span>
          <span v-if="r.type === 'Redirect'" class="rounded-lg bg-amber-500/15 text-amber-300 px-2 py-1">↪ {{ r.redirectTo }}</span>
          <span v-else class="rounded-lg bg-brand-600/15 text-brand-300 px-2 py-1 font-mono">{{ r.targetService }}:{{ r.targetPort }}</span>
          <span v-if="r.webSocketEnabled" class="text-[10px] rounded bg-sky-500/15 text-sky-400 px-1.5 py-0.5">WS</span>
          <span v-if="r.basicAuthEnabled" class="text-[10px] rounded bg-slate-600/30 text-slate-300 px-1.5 py-0.5">AUTH</span>
        </div>
      </div>

      <!-- Generated config -->
      <div v-show="tab === 'config'" class="rounded-xl border border-slate-800 bg-black/60">
        <pre class="p-4 text-xs overflow-auto max-h-[60vh] whitespace-pre-wrap font-mono">{{ preview || t('noConfig') }}</pre>
      </div>

      <!-- Validation -->
      <div v-show="tab === 'validate'" class="rounded-xl border border-slate-800 bg-slate-900/60 p-4 space-y-2 text-sm">
        <p v-if="!validation" class="text-slate-400">—</p>
        <template v-else>
          <p :class="validation.isValid ? 'text-emerald-400' : 'text-red-400'">
            {{ validation.isValid ? '✓ valid' : '✗ invalid' }}
          </p>
          <div v-for="(e, i) in validation.errors" :key="'e'+i" class="text-red-400">• {{ e }}</div>
          <div v-for="(w, i) in validation.warnings" :key="'w'+i" class="text-amber-300">⚠ {{ w }}</div>
        </template>
      </div>
    </section>
  </div>
</template>
