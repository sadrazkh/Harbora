<script setup lang="ts">
import { ref, onMounted, onUnmounted, nextTick } from 'vue';
import { HubConnectionBuilder, HubConnection, LogLevel } from '@microsoft/signalr';

const props = defineProps<{ deploymentId: string; initialStatus: string }>();

interface LogLine { seq: number; stream: string; message: string; }

const lines = ref<string[]>([]);
const status = ref(props.initialStatus);
const pre = ref<HTMLElement | null>(null);
let connection: HubConnection | null = null;
let lastSeq = -1;

const terminal = ['Succeeded', 'Failed', 'Cancelled', 'RolledBack'];

async function scrollToEnd() {
  await nextTick();
  if (pre.value) pre.value.scrollTop = pre.value.scrollHeight;
}

async function backfill() {
  const res = await fetch(`/deployments/${props.deploymentId}/logs?after=${lastSeq}`);
  const data: LogLine[] = await res.json();
  for (const l of data) { lines.value.push(l.message); lastSeq = l.seq; }
  await scrollToEnd();
}

onMounted(async () => {
  await backfill();

  connection = new HubConnectionBuilder()
    .withUrl('/hubs/deployments')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  connection.on('log', async (payload: { line: string }) => {
    lines.value.push(payload.line);
    await scrollToEnd();
  });
  connection.on('status', (payload: { status: string }) => {
    status.value = payload.status;
  });

  try {
    await connection.start();
    await connection.invoke('Subscribe', props.deploymentId);
    // Catch any lines emitted between backfill and subscribe.
    await backfill();
  } catch {
    // If the socket can't open, fall back to polling.
    const poll = setInterval(async () => {
      await backfill();
      if (terminal.includes(status.value)) clearInterval(poll);
    }, 1500);
  }
});

onUnmounted(() => { connection?.stop(); });

const statusClass = () =>
  status.value === 'Succeeded' ? 'text-emerald-400'
    : terminal.includes(status.value) ? 'text-red-400'
      : 'text-brand-300 animate-pulse';
</script>

<template>
  <div>
    <div class="flex items-center justify-between px-4 py-2 border-b border-slate-800 text-slate-400">
      <span>build &amp; deploy logs</span>
      <span :class="statusClass()">● {{ status }}</span>
    </div>
    <pre ref="pre" class="p-4 max-h-[60vh] overflow-auto whitespace-pre-wrap scrollbar-thin">{{ lines.join('\n') }}</pre>
  </div>
</template>
