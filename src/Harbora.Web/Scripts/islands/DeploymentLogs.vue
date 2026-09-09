<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue';
import { HubConnectionBuilder, type HubConnection, LogLevel } from '@microsoft/signalr';

const props = defineProps<{ deploymentId: string; initialStatus: string }>();
interface LogLine { seq: number; stream: string; message: string; }
const fa = document.documentElement.lang.startsWith('fa');
const t = (en: string, persian: string) => fa ? persian : en;
const lines = ref<LogLine[]>([]);
const pending = ref<LogLine[]>([]);
const allLines = computed(() => [...lines.value, ...pending.value]);
let connection: HubConnection | null = null;
const status = ref(props.initialStatus);
const pre = ref<HTMLElement | null>(null);
const query = ref('');
const errorsOnly = ref(false);
const following = ref(true);
const loading = ref(true);
const error = ref('');
const terminal = ['Succeeded', 'Failed', 'Cancelled', 'RolledBack'];
const visible = computed(() => allLines.value.filter(line =>
  (!errorsOnly.value || /error|fail|exception|fatal/i.test(line.message) || line.stream === 'Stderr') &&
  line.message.toLocaleLowerCase().includes(query.value.toLocaleLowerCase())));
let lastSeq = -1;
let timer: ReturnType<typeof setTimeout> | undefined;
let disposed = false;
let busy = false;
let terminalReads = 0;
const abort = new AbortController();

function publish(next: string) {
  status.value = next;
  window.dispatchEvent(new CustomEvent('harbora:deployment-status', { detail: { status: next } }));
}
async function scrollToEnd() {
  await nextTick();
  if (following.value && pre.value) pre.value.scrollTop = pre.value.scrollHeight;
}
function trackScroll() {
  if (pre.value) following.value = pre.value.scrollHeight - pre.value.scrollTop - pre.value.clientHeight < 40;
}
function resume() { following.value = true; void scrollToEnd(); }

// Persisted sequence numbers are authoritative; reconcile provisional socket lines one occurrence
// at a time so legitimately repeated messages are retained. Polling also recovers reconnect gaps.
async function backfill() {
  if (disposed || busy) return;
  clearTimeout(timer);
  busy = true;
  try {
    const res = await fetch(`/deployments/${props.deploymentId}/logs?after=${lastSeq}`, {
      signal: abort.signal, headers: { Accept: 'application/json' }, cache: 'no-store'
    });
    if (!res.ok) throw new Error(String(res.status));
    const data: { status: string; lines: LogLine[] } = await res.json();
    if (disposed) return;
    for (const line of data.lines) {
      if (line.seq > lastSeq) {
        const index = pending.value.findIndex(item => item.message === line.message && item.stream === line.stream);
        if (index >= 0) pending.value.splice(index, 1);
        lines.value.push(line); lastSeq = line.seq;
      }
    }
    if (data.status) publish(data.status);
    error.value = '';
    terminalReads = terminal.includes(status.value) ? terminalReads + 1 : 0;
    await scrollToEnd();
  } catch {
    if (!disposed) error.value = t('Connection interrupted. Retrying; existing logs are preserved.', 'ارتباط قطع شد؛ دوباره تلاش می‌کنیم. لاگ‌های دریافت‌شده حفظ شده‌اند.');
  } finally {
    busy = false;
    loading.value = false;
    if (!disposed && (error.value || !terminal.includes(status.value) || terminalReads < 4)) {
      timer = setTimeout(backfill, error.value ? 5000 : 1500);
    }
  }
}
function download() {
  const url = URL.createObjectURL(new Blob([allLines.value.map(l => l.message).join('\n')], { type: 'text/plain;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url; link.download = `deployment-${props.deploymentId}.log`; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
onMounted(async () => {
  await backfill();
  if (disposed || terminal.includes(status.value)) return;
  connection = new HubConnectionBuilder().withUrl('/hubs/deployments').withAutomaticReconnect().configureLogging(LogLevel.Warning).build();
  connection.on('log', (payload: { line: string; stream: string }) => {
    pending.value.push({ seq: -1, message: payload.line, stream: payload.stream });
    void scrollToEnd();
  });
  connection.on('status', () => { void backfill(); });
  connection.onreconnected(async () => {
    try { await connection?.invoke('Subscribe', props.deploymentId); await backfill(); } catch { /* Polling continues. */ }
  });
  try {
    await connection.start();
    if (disposed) { await connection.stop(); return; }
    await connection.invoke('Subscribe', props.deploymentId);
    await backfill();
  } catch { /* The independent poller continues even when the socket cannot open. */ }
});
onUnmounted(() => { disposed = true; clearTimeout(timer); abort.abort(); void connection?.stop(); });
const statusClass = () => status.value === 'Succeeded' ? 'text-ok'
  : terminal.includes(status.value) ? 'text-danger' : 'text-accent-text animate-pulse';
</script>

<template>
  <div>
    <div class="flex flex-wrap items-center justify-between gap-3 px-4 py-3 border-b border-slate-800 text-slate-400">
      <strong>{{ t('Build & deploy logs', 'لاگ ساخت و استقرار') }}</strong>
      <span :class="statusClass()" role="status">● {{ status }}</span>
    </div>
    <div class="flex flex-wrap items-center gap-3 p-3 border-b border-slate-800 text-sm">
      <input v-model="query" type="search" class="form-control flex-1 min-w-0" :aria-label="t('Search logs', 'جست‌وجوی لاگ')" :placeholder="t('Search logs…', 'جست‌وجو در لاگ‌ها…')" />
      <label class="flex items-center gap-2"><input v-model="errorsOnly" type="checkbox" />{{ t('Errors only', 'فقط خطاها') }}</label>
      <button type="button" class="btn-secondary" :disabled="!allLines.length" @click="download">{{ t('Download logs', 'دریافت لاگ') }}</button>
      <button type="button" class="btn-secondary" :aria-pressed="following" @click="following ? following = false : resume()">{{ following ? t('Pause scrolling', 'توقف اسکرول') : t('Follow latest', 'دنبال‌کردن آخرین خط') }}</button>
    </div>
    <div v-if="error" role="status" class="p-3 text-warn flex items-center justify-between gap-2">
      {{ error }}<button type="button" class="btn-secondary" @click="backfill">{{ t('Retry now', 'تلاش دوباره') }}</button>
    </div>
    <p v-if="loading || !allLines.length" class="p-4 text-slate-400" role="status">{{ loading ? t('Loading logs…', 'دریافت لاگ‌ها…') : t('No logs yet.', 'هنوز لاگی ثبت نشده است.') }}</p>
    <p v-else-if="!visible.length" class="p-4 text-slate-400" role="status">{{ t('No matching lines. Change your filters.', 'خطی مطابق جست‌وجو نیست؛ فیلترها را تغییر دهید.') }}</p>
    <pre ref="pre" dir="ltr" tabindex="0" :aria-label="t('Deployment logs', 'لاگ استقرار')" class="p-4 max-h-[60vh] overflow-auto whitespace-pre-wrap scrollbar-thin text-left" @scroll="trackScroll">{{ visible.map(l => l.message).join('\n') }}</pre>
    <div class="px-4 py-2 text-xs text-slate-400 border-t border-slate-800">{{ visible.length }} / {{ allLines.length }} {{ t('lines', 'خط') }} · {{ terminal.includes(status) ? t('Deployment ended', 'استقرار پایان یافته') : t('Live updates', 'به‌روزرسانی زنده') }}</div>
  </div>
</template>
