<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue';

const props = defineProps<{ name: string; label: string; color: string; resource?: string }>();

interface Point { t: number; v: number; }
const points = ref<Point[]>([]);
const W = 800, H = 120, PAD = 8;
let timer: number | undefined;

async function load() {
  const q = new URLSearchParams({ name: props.name, minutes: '60' });
  if (props.resource) q.set('resource', props.resource);
  try {
    const res = await fetch('/monitoring/metrics?' + q.toString());
    points.value = await res.json();
  } catch { /* keep previous */ }
}

const path = computed(() => {
  const pts = points.value;
  if (pts.length < 2) return '';
  const xs = pts.map(p => p.t), ys = pts.map(p => p.v);
  const minX = Math.min(...xs), maxX = Math.max(...xs);
  const maxY = Math.max(1, ...ys);
  const sx = (x: number) => PAD + ((x - minX) / (maxX - minX || 1)) * (W - 2 * PAD);
  const sy = (y: number) => H - PAD - (y / maxY) * (H - 2 * PAD);
  return pts.map((p, i) => `${i === 0 ? 'M' : 'L'}${sx(p.t).toFixed(1)},${sy(p.v).toFixed(1)}`).join(' ');
});

const area = computed(() => path.value ? `${path.value} L${W - PAD},${H - PAD} L${PAD},${H - PAD} Z` : '');
const latest = computed(() => points.value.at(-1)?.v ?? 0);

onMounted(() => { load(); timer = window.setInterval(load, 15000); });
onUnmounted(() => { if (timer) clearInterval(timer); });
</script>

<template>
  <div>
    <div v-if="points.length < 2" class="text-slate-500 text-sm py-8 text-center">
      Collecting data… (samples every 30s)
    </div>
    <svg v-else :viewBox="`0 0 ${W} ${H}`" preserveAspectRatio="none" class="w-full" :style="{ height: H + 'px' }">
      <path :d="area" :fill="color" opacity="0.12" />
      <path :d="path" :stroke="color" fill="none" stroke-width="2" stroke-linejoin="round" />
    </svg>
    <div class="text-xs text-slate-400 mt-1">{{ label }}: {{ latest.toFixed(1) }}</div>
  </div>
</template>
