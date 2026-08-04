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

/**
 * Announces the status to the rest of the page.
 *
 * The staged progress bar is Razor's and sits outside this island; the socket is the island's.
 * Publishing over the DOM keeps that boundary — for a long time the island received every status
 * and used it for nothing but the coloured dot above the log pane, while the bar it was documented
 * to drive never moved.
 */
function publish(next: string) {
  status.value = next;
  window.dispatchEvent(new CustomEvent('harbora:deployment-status', { detail: { status: next } }));
}

async function backfill() {
  const res = await fetch(`/deployments/${props.deploymentId}/logs?after=${lastSeq}`);
  const data: { status: string; lines: LogLine[] } = await res.json();
  for (const l of data.lines) { lines.value.push(l.message); lastSeq = l.seq; }

  // The status comes back with the lines, so the polling fallback below finishes and the bar
  // reaches its last step even when the socket never opened.
  if (data.status) publish(data.status);
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
    publish(payload.status);
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

// Design tokens, not raw palette values. text-brand-300 was a class from the retired colour ramp
// and had not resolved to anything since the redesign, so the "in progress" state rendered with no
// colour at all.
const statusClass = () =>
  status.value === 'Succeeded' ? 'text-ok'
    : terminal.includes(status.value) ? 'text-danger'
      : 'text-accent-text animate-pulse';
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
