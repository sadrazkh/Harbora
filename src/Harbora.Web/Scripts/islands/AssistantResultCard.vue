<script setup lang="ts">
import { computed, ref, useId } from 'vue';
import { Check, ChevronDown, CircleCheck, CircleHelp, ExternalLink, Minus, TriangleAlert, X } from 'lucide';
import { AssistantIcon } from './assistantIcon';
import { stepResultState, summarizeResult, resultNote } from './assistantPlan.js';
import { ranHeading, resultLinks } from './assistantActivity.js';
import { safeHref, textDirection } from './assistantFormat.js';

// What a plan actually did (AgentPlanResultView), from approve or from an `executed` event. A step
// is drawn as done only when its result says exactly "done" (stepResultState) — this card is the
// one place in the pane that tells somebody their database exists, so it says nothing it was not told.
//
// `compact`: a plan that ran with every step done (assistantActivity.js, ranCompactly) — its card and
// this one become one: "Plan ran · 2 steps", the resource links as chips that are always visible, and
// the steps behind "Details". Anything that did not fully run is never drawn this way.

const props = defineProps<{ result: any; plan?: any | null; fa: boolean; compact?: boolean }>();
const t = (en: string, persian: string) => (props.fa ? persian : en);

const summary = computed(() => summarizeResult(props.result));
const firstFailed = computed(() => {
  const steps = props.result?.steps ?? [];
  const at = steps.findIndex((s: any) => stepResultState(s) === 'failed');
  return at === -1 ? null : at + 1;
});

const heading = computed(() => {
  if (props.compact) return ranHeading(props.result, props.fa);
  const s = summary.value;
  if (s.allDone) return t('Done', 'انجام شد');
  if (firstFailed.value !== null) return t(`Stopped — step ${firstFailed.value} failed`, `متوقف شد — گام ${firstFailed.value} ناموفق بود`);
  if (s.total === 0) return t('Nothing ran', 'هیچ کاری اجرا نشد');
  return t(`${s.done} of ${s.total} steps done`, `${s.done} از ${s.total} گام انجام شد`);
});

const open = ref(false);
const stepsId = `assistant-result-steps-${useId()}`;
const links = computed(() => resultLinks(props.result));

const planStep = (r: any) => props.plan?.steps?.find((s: any) => s.index === r.index) ?? null;
const title = (r: any, i: number) => {
  const s = planStep(r);
  const text = s ? (props.fa ? s.titleFa || s.titleEn : s.titleEn || s.titleFa) : null;
  return text || t(`Step ${i + 1}`, `گام ${i + 1}`);
};
const note = (r: any) => resultNote(r, props.fa);
const iconFor = (r: any) => ({ done: Check, failed: X, skipped: Minus } as Record<string, any>)[stepResultState(r)] ?? CircleHelp;
const iconClass = (r: any) => ({ done: 'bg-ok-soft text-ok', failed: 'bg-danger-soft text-danger', skipped: 'bg-surface-2 text-ink-faint' } as Record<string, string>)[stepResultState(r)] ?? 'bg-surface-2 text-ink-faint';
</script>

<template>
  <section class="overflow-hidden rounded-xl border border-line bg-surface shadow-panel" data-assistant-result
           :data-result-status="result.status" :data-result-compact="compact ? 'true' : undefined" role="group" :aria-label="heading">
    <!-- A band of its own, tinted by the outcome, so the heading never reads as one more step. -->
    <header v-if="compact" class="flex items-center gap-2 bg-ok-soft px-3 py-2 text-ok" data-result-heading>
      <AssistantIcon :icon="CircleCheck" />
      <h3 class="min-w-0 flex-1 text-sm font-semibold">{{ heading }}</h3>
      <button type="button" class="inline-flex shrink-0 items-center gap-1 rounded-md px-1.5 py-0.5 text-[11px] font-medium hover:bg-ok/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ok/40"
              :aria-expanded="open ? 'true' : 'false'" :aria-controls="stepsId" data-result-details @click="open = !open">
        {{ t('Details', 'جزئیات') }}
        <AssistantIcon :icon="ChevronDown" :size="14" class="motion-safe:transition-transform" :class="open ? 'rotate-180' : ''" />
      </button>
    </header>
    <header v-else class="flex items-center gap-2 border-b border-line px-3 py-2.5" data-result-heading
            :class="summary.allDone ? 'bg-ok-soft text-ok' : firstFailed !== null ? 'bg-danger-soft text-danger' : 'bg-surface-2 text-ink-muted'">
      <AssistantIcon :icon="summary.allDone ? CircleCheck : firstFailed !== null ? TriangleAlert : CircleHelp" />
      <h3 class="text-sm font-semibold">{{ heading }}</h3>
    </header>

    <!-- The compact card's links: what was made, one click away, whether or not Details is open. -->
    <div v-if="compact && links.length" class="flex flex-wrap gap-1.5 px-3 py-2" data-result-links>
      <template v-for="(link, li) in links" :key="li">
        <a v-if="safeHref(link.url)" :href="safeHref(link.url)!" class="inline-flex max-w-full items-center gap-1 rounded-md bg-accent-soft px-2 py-1 text-[11px] font-medium text-accent-text hover:underline" :dir="textDirection(link.label)">
          <span class="truncate">{{ link.label }}</span><AssistantIcon :icon="ExternalLink" :size="11" />
        </a>
        <span v-else class="max-w-full truncate rounded-md bg-surface-2 px-2 py-1 text-[11px] text-ink-muted" :dir="textDirection(link.label)">{{ link.label }}</span>
      </template>
    </div>

    <ol v-show="!compact || open" :id="stepsId" class="divide-y divide-line" :class="compact ? 'border-t border-line' : ''">
      <li v-for="(r, i) in result.steps" :key="r.index" class="flex items-start gap-2.5 px-3 py-2.5" :data-step-state="stepResultState(r)">
        <span class="mt-0.5 grid h-5 w-5 shrink-0 place-items-center rounded-full" :class="iconClass(r)">
          <AssistantIcon :icon="iconFor(r)" :size="12" />
        </span>
        <div class="min-w-0 flex-1 space-y-1">
          <p class="text-sm text-ink" :dir="textDirection(title(r, i))">{{ title(r, i) }}</p>
          <p v-if="note(r)" class="text-sm leading-5" :class="stepResultState(r) === 'failed' ? 'text-danger' : 'text-ink-muted'" :dir="textDirection(note(r))">{{ note(r) }}</p>
          <!-- In the compact card the links are the chips above, already in view. -->
          <div v-if="!compact && r.links?.length" class="flex flex-wrap gap-1.5 pt-0.5">
            <template v-for="(link, li) in r.links" :key="li">
              <a v-if="safeHref(link.url)" :href="safeHref(link.url)!" class="inline-flex items-center gap-1 rounded-md bg-accent-soft px-2 py-1 text-[11px] font-medium text-accent-text hover:underline" :dir="textDirection(link.label)">
                {{ link.label }}<AssistantIcon :icon="ExternalLink" :size="11" />
              </a>
              <span v-else class="rounded-md bg-surface-2 px-2 py-1 text-[11px] text-ink-muted" :dir="textDirection(link.label)">{{ link.label }}</span>
            </template>
          </div>
        </div>
      </li>
    </ol>
  </section>
</template>
