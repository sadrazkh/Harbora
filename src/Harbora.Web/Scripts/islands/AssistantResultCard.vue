<script setup lang="ts">
import { computed } from 'vue';
import { Check, CircleHelp, ExternalLink, Minus, X } from 'lucide';
import { AssistantIcon } from './assistantIcon';
import { stepResultState, summarizeResult } from './assistantPlan.js';
import { safeHref } from './assistantFormat.js';

// What a plan actually did (AgentPlanResultView), from approve or from an `executed` event. A step
// is drawn as done only when its result says exactly "done" (stepResultState) — this card is the
// one place in the pane that tells somebody their database exists, so it says nothing it was not told.

const props = defineProps<{ result: any; plan?: any | null; fa: boolean }>();
const t = (en: string, persian: string) => (props.fa ? persian : en);

const summary = computed(() => summarizeResult(props.result));
const firstFailed = computed(() => {
  const steps = props.result?.steps ?? [];
  const at = steps.findIndex((s: any) => stepResultState(s) === 'failed');
  return at === -1 ? null : at + 1;
});

const heading = computed(() => {
  const s = summary.value;
  if (s.allDone) return t('Done', 'انجام شد');
  if (firstFailed.value !== null) return t(`Stopped — step ${firstFailed.value} failed`, `متوقف شد — گام ${firstFailed.value} ناموفق بود`);
  if (s.total === 0) return t('Nothing ran', 'هیچ کاری اجرا نشد');
  return t(`${s.done} of ${s.total} steps done`, `${s.done} از ${s.total} گام انجام شد`);
});

const planStep = (r: any) => props.plan?.steps?.find((s: any) => s.index === r.index) ?? null;
const title = (r: any, i: number) => {
  const s = planStep(r);
  const text = s ? (props.fa ? s.titleFa || s.titleEn : s.titleEn || s.titleFa) : null;
  return text || t(`Step ${i + 1}`, `گام ${i + 1}`);
};
const pick = (en?: string | null, persian?: string | null) => (props.fa ? persian || en : en || persian) || '';
const note = (r: any) => {
  switch (stepResultState(r)) {
    case 'done': return pick(r.summaryEn, r.summaryFa);
    case 'failed': return pick(r.errorEn, r.errorFa) || t('Failed.', 'ناموفق بود.');
    case 'skipped': return t('Skipped — an earlier step failed, so this did not run.', 'رد شد — گام قبلی ناموفق بود، پس این اجرا نشد.');
    default: return t('The result did not say whether this ran.', 'نتیجه نگفت که این اجرا شد یا نه.');
  }
};
const iconFor = (r: any) => ({ done: Check, failed: X, skipped: Minus } as Record<string, any>)[stepResultState(r)] ?? CircleHelp;
const iconClass = (r: any) => ({ done: 'bg-ok-soft text-ok', failed: 'bg-danger-soft text-danger', skipped: 'bg-surface-2 text-ink-faint' } as Record<string, string>)[stepResultState(r)] ?? 'bg-surface-2 text-ink-faint';
</script>

<template>
  <section class="rounded-xl border border-line bg-surface shadow-panel" data-assistant-result
           :data-result-status="result.status" role="group" :aria-label="heading">
    <header class="flex items-center gap-2 border-b border-line px-3 py-2.5">
      <span class="grid h-5 w-5 place-items-center rounded-full"
            :class="summary.allDone ? 'bg-ok-soft text-ok' : firstFailed !== null ? 'bg-danger-soft text-danger' : 'bg-surface-2 text-ink-faint'">
        <AssistantIcon :icon="summary.allDone ? Check : firstFailed !== null ? X : CircleHelp" :size="12" />
      </span>
      <strong class="text-sm font-semibold text-ink">{{ heading }}</strong>
    </header>
    <ol class="divide-y divide-line">
      <li v-for="(r, i) in result.steps" :key="r.index" class="flex items-start gap-2.5 px-3 py-2.5" :data-step-state="stepResultState(r)">
        <span class="mt-0.5 grid h-5 w-5 shrink-0 place-items-center rounded-full" :class="iconClass(r)">
          <AssistantIcon :icon="iconFor(r)" :size="12" />
        </span>
        <div class="min-w-0 flex-1 space-y-1">
          <p class="text-sm font-medium text-ink" dir="auto">{{ title(r, i) }}</p>
          <p v-if="note(r)" class="text-xs leading-5" :class="stepResultState(r) === 'failed' ? 'text-danger' : 'text-ink-muted'" dir="auto">{{ note(r) }}</p>
          <div v-if="r.links?.length" class="flex flex-wrap gap-1.5 pt-0.5">
            <template v-for="(link, li) in r.links" :key="li">
              <a v-if="safeHref(link.url)" :href="safeHref(link.url)!" class="inline-flex items-center gap-1 rounded-md bg-accent-soft px-2 py-1 text-[11px] font-medium text-accent-text hover:underline" dir="auto">
                {{ link.label }}<AssistantIcon :icon="ExternalLink" :size="11" />
              </a>
              <span v-else class="rounded-md bg-surface-2 px-2 py-1 text-[11px] text-ink-muted" dir="auto">{{ link.label }}</span>
            </template>
          </div>
        </div>
      </li>
    </ol>
  </section>
</template>
