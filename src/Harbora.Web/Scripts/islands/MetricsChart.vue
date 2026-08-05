<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue';

/*
 * One measurement over time.
 *
 * `appId` / `serviceId` name a resource the signed-in person already has the right to see; the
 * server derives the container name from it. The container name was previously a query parameter,
 * which made it the key to any tenant's series for anyone who could guess one.
 *
 * `limit` is what the resource was allotted. Drawn as a line rather than folded into the scale, so
 * "we gave this app 2 GB, how much is it using" is answerable at a glance and a value over the
 * ceiling still looks like one.
 */
const props = defineProps<{
  name: string;
  label: string;
  color: string;
  appId?: string;
  serviceId?: string;
  limit?: number;
  unit?: 'bytes' | 'percent' | 'raw';
  height?: number;
}>();

interface Point { t: number; v: number; }
const points = ref<Point[]>([]);
const minutes = ref(60);
const loaded = ref(false);
const W = 800, PAD = 8;
const H = props.height ?? 120;
let timer: number | undefined;

const WINDOWS = [
  { minutes: 60, label: '1h' },
  { minutes: 60 * 6, label: '6h' },
  { minutes: 60 * 24, label: '24h' },
  { minutes: 60 * 24 * 7, label: '7d' },
  { minutes: 60 * 24 * 30, label: '30d' },
];

async function load() {
  const q = new URLSearchParams({ name: props.name, minutes: String(minutes.value) });
  if (props.appId) q.set('appId', props.appId);
  if (props.serviceId) q.set('serviceId', props.serviceId);
  try {
    const res = await fetch('/monitoring/metrics?' + q.toString());
    if (!res.ok) { points.value = []; loaded.value = true; return; }
    points.value = await res.json();
  } catch { /* keep the previous series rather than blanking the chart on one failed poll */ }
  loaded.value = true;
}

function pick(m: number) { minutes.value = m; load(); }

// The ceiling is part of the scale, so a series sitting at a tenth of its allocation looks like a
// tenth rather than filling the chart.
const top = computed(() => {
  const highest = Math.max(1, ...points.value.map(p => p.v));
  return props.limit && props.limit > 0 ? Math.max(highest, props.limit) : highest;
});

const sx = (x: number, minX: number, maxX: number) =>
  PAD + ((x - minX) / (maxX - minX || 1)) * (W - 2 * PAD);
const sy = (y: number) => H - PAD - (y / top.value) * (H - 2 * PAD);

const path = computed(() => {
  const pts = points.value;
  if (pts.length < 2) return '';
  const xs = pts.map(p => p.t);
  const minX = Math.min(...xs), maxX = Math.max(...xs);
  return pts.map((p, i) => `${i === 0 ? 'M' : 'L'}${sx(p.t, minX, maxX).toFixed(1)},${sy(p.v).toFixed(1)}`).join(' ');
});

const area = computed(() => path.value ? `${path.value} L${W - PAD},${H - PAD} L${PAD},${H - PAD} Z` : '');
const limitY = computed(() => props.limit && props.limit > 0 ? sy(props.limit) : null);

function format(value: number): string {
  if (props.unit === 'percent') return value.toFixed(1) + '%';
  if (props.unit !== 'bytes') return value.toFixed(1);
  if (value < 1024) return value.toFixed(0) + ' B';
  if (value < 1024 * 1024) return (value / 1024).toFixed(1) + ' KB';
  if (value < 1024 * 1024 * 1024) return (value / 1024 / 1024).toFixed(1) + ' MB';
  return (value / 1024 / 1024 / 1024).toFixed(2) + ' GB';
}

const latest = computed(() => points.value.at(-1)?.v);
const peak = computed(() => points.value.length ? Math.max(...points.value.map(p => p.v)) : undefined);

onMounted(() => { load(); timer = window.setInterval(load, 15000); });
onUnmounted(() => { if (timer) clearInterval(timer); });
</script>

<template>
  <div>
    <div class="mb-1 flex flex-wrap items-baseline justify-between gap-2">
      <span class="text-xs text-ink-muted">{{ label }}</span>
      <span class="flex items-center gap-1">
        <button v-for="w in WINDOWS" :key="w.minutes" type="button" @click="pick(w.minutes)"
                class="rounded px-1.5 py-0.5 text-[10px]"
                :class="minutes === w.minutes ? 'bg-accent-soft text-accent-text' : 'text-ink-faint hover:text-ink'">
          {{ w.label }}
        </button>
      </span>
    </div>

    <!-- Nothing measured is not nothing used. An empty chart says so rather than drawing a flat
         line along the bottom, which reads as an idle application. -->
    <div v-if="loaded && points.length < 2" class="text-xs text-ink-faint" :style="{ lineHeight: H + 'px' }">
      {{ points.length ? '···' : 'not measured yet' }}
    </div>
    <div v-else-if="!loaded" class="text-xs text-ink-faint" :style="{ lineHeight: H + 'px' }">···</div>

    <svg v-else :viewBox="`0 0 ${W} ${H}`" preserveAspectRatio="none" class="w-full" :style="{ height: H + 'px' }">
      <path :d="area" :fill="color" opacity="0.12" />
      <path :d="path" :stroke="color" fill="none" stroke-width="2" stroke-linejoin="round" />
      <line v-if="limitY !== null" :x1="PAD" :x2="W - PAD" :y1="limitY" :y2="limitY"
            stroke="currentColor" class="text-warn" stroke-width="1" stroke-dasharray="6 4" opacity="0.7" />
    </svg>

    <div class="mt-1 flex flex-wrap gap-x-3 text-[11px] text-ink-faint" dir="ltr">
      <span v-if="latest !== undefined">now {{ format(latest) }}</span>
      <span v-if="peak !== undefined">peak {{ format(peak) }}</span>
      <span v-if="limit && limit > 0">of {{ format(limit) }}</span>
    </div>
  </div>
</template>
