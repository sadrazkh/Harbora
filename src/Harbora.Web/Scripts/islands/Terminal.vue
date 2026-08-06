<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef } from 'vue';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';

const props = defineProps<{
  appId: string;
  lang: string;
}>();

const host = ref<HTMLElement | null>(null);
const term = shallowRef<Terminal | null>(null);
const socket = shallowRef<WebSocket | null>(null);
const state = ref<'connecting' | 'open' | 'closed'>('connecting');

const fa = props.lang === 'fa';
const say = (en: string, faText: string) => (fa ? faText : en);

const fit = new FitAddon();
let resizeObserver: ResizeObserver | null = null;

// The terminal is always left-to-right, whatever the panel's direction is. A shell draws its own
// screen with escape sequences that assume columns run one way, and mirroring it puts the cursor
// somewhere other than where the text is.
function connect() {
  const t = new Terminal({
    convertEol: false,
    cursorBlink: true,
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
    fontSize: 13,
    theme: { background: '#0b1020' },
  });
  t.loadAddon(fit);
  t.open(host.value!);
  fit.fit();
  term.value = t;

  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  const url = `${scheme}://${location.host}/apps/${props.appId}/terminal/ws`
    + `?cols=${t.cols}&rows=${t.rows}`;

  const ws = new WebSocket(url);
  ws.binaryType = 'arraybuffer';
  socket.value = ws;

  ws.onopen = () => {
    state.value = 'open';
    t.focus();
  };

  // Bytes, not text: a shell's output is not UTF-8 line by line, and decoding it as it arrives
  // splits multi-byte characters across chunks. xterm takes the raw bytes and handles that.
  ws.onmessage = (e) => {
    if (e.data instanceof ArrayBuffer) t.write(new Uint8Array(e.data));
    else t.write(e.data);
  };

  ws.onclose = (e) => {
    state.value = 'closed';
    // The server's reason, when it gave one. "Disconnected" on its own leaves somebody guessing
    // whether they were idle, whether the app stopped, or whether this never worked.
    const reason = e.reason || say('The session ended.', 'نشست تمام شد.');
    t.write(`\r\n\x1b[33m${reason}\x1b[0m\r\n`);
  };

  ws.onerror = () => { state.value = 'closed'; };

  t.onData((data) => {
    if (ws.readyState !== WebSocket.OPEN) return;
    ws.send(new TextEncoder().encode(data));   // binary frame: keystrokes
  });

  resizeObserver = new ResizeObserver(() => {
    fit.fit();
    if (ws.readyState === WebSocket.OPEN)
      ws.send(JSON.stringify({ cols: t.cols, rows: t.rows }));   // text frame: a resize
  });
  resizeObserver.observe(host.value!);
}

function reconnect() {
  socket.value?.close();
  term.value?.dispose();
  state.value = 'connecting';
  connect();
}

onMounted(connect);

onBeforeUnmount(() => {
  resizeObserver?.disconnect();
  socket.value?.close();
  term.value?.dispose();
});
</script>

<template>
  <div>
    <div class="mb-2 flex items-center gap-3 text-xs">
      <span v-if="state === 'connecting'" class="text-ink-faint">{{ say('Connecting…', 'در حال اتصال…') }}</span>
      <span v-else-if="state === 'open'" class="text-success">{{ say('Connected', 'وصل') }}</span>
      <template v-else>
        <span class="text-ink-faint">{{ say('Disconnected', 'قطع شد') }}</span>
        <button type="button" class="text-accent-text" @click="reconnect">
          {{ say('Reconnect', 'اتصال دوباره') }}
        </button>
      </template>
    </div>

    <div ref="host" dir="ltr" class="h-[70vh] rounded-xl border border-line overflow-hidden"></div>
  </div>
</template>
