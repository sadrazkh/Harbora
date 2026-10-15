<script setup lang="ts">
import { computed, ref, useId } from 'vue';
import { Ban, Check, ChevronDown, CircleCheck, Clock, ListChecks, LoaderCircle, ShieldAlert, TriangleAlert } from 'lucide';
import { AssistantIcon } from './assistantIcon';
import {
  kindLabel, needsPhrase, isRememberable, planIsActionable, planIsExpired, planHeading,
} from './assistantPlan.js';
import { planCardLine } from './assistantActivity.js';
import { textDirection } from './assistantFormat.js';

// One plan waiting for the person's consent (AgentPlanView). Nothing on this card has run: that is
// said on the card itself, because "create the database" in a list reads like a report unless
// something says otherwise. The rules — who needs a typed name, what may be remembered, what the
// approve body contains — live in assistantPlan.js; this file only draws them.
//
// Whether the plan is still open is the server's word (its status), never this browser's clock: a
// late approve is refused by the server, and its refusal — with the step it names — is shown here.
//
// A cancelled or expired plan is one muted line — nothing ran — with its steps behind "Details"
// (assistantActivity.js, planCardLine); a refusal on it stays in view. A plan that ran with every step
// done is not drawn here at all: the result card draws the two as one compact card.

interface PlanItem {
  plan: any;
  form: { phrases: Record<number, string>; remember: Record<string, boolean> };
  mismatched: number[];
  busy: boolean;
  error: { messageEn: string; messageFa: string } | null;
  errorStep: number | null;
  superseded: boolean;
  resulted: boolean;
  notRun?: boolean;
}

// `locked`: a turn is streaming. The server expires a waiting plan when it receives a new message,
// so Run is held until it has answered rather than racing that.
const props = defineProps<{ item: PlanItem; fa: boolean; locked?: boolean }>();
const emit = defineEmits<{ (e: 'run'): void; (e: 'cancel'): void }>();
const t = (en: string, persian: string) => (props.fa ? persian : en);

const plan = computed(() => props.item.plan);
const actionable = computed(() => planIsActionable(plan.value));
const expired = computed(() => planIsExpired(plan.value));
const hasDestructive = computed(() => (plan.value.steps ?? []).some((s: any) => s.kind === 'destructive'));

// The server's expiry, shown as a time of day — information, not a countdown this card enforces.
const validUntil = computed(() => {
  const at = new Date(plan.value.expiresAt);
  return Number.isNaN(at.getTime()) ? null : new Intl.DateTimeFormat(props.fa ? 'fa-IR' : 'en-GB', { timeStyle: 'short' }).format(at);
});

const heading = computed(() => planHeading(props.item, props.fa));
const line = computed(() => planCardLine(props.item, props.fa));
const open = ref(false);
const detailsId = `assistant-plan-details-${useId()}`;

// The heading is a band of its own — tint, icon and weight — so it never reads as one more step.
const band = computed(() => {
  switch (plan.value.status) {
    case 'pending': return hasDestructive.value
      ? { cls: 'bg-danger-soft text-danger', icon: ShieldAlert }
      : { cls: 'bg-accent-soft text-accent-text', icon: ListChecks };
    case 'executing': return { cls: 'bg-accent-soft text-accent-text', icon: LoaderCircle };
    case 'executed': return { cls: 'bg-ok-soft text-ok', icon: CircleCheck };
    case 'failed': return { cls: 'bg-danger-soft text-danger', icon: TriangleAlert };
    case 'rejected': return { cls: 'bg-surface-2 text-ink-muted', icon: Ban };
    case 'expired': return { cls: 'bg-surface-2 text-ink-muted', icon: Clock };
    default: return { cls: 'bg-surface-2 text-ink', icon: ListChecks };
  }
});

const kindClass = (kind: string) => ({
  safe: 'bg-ok-soft text-ok',
  dangerous: 'bg-warn-soft text-warn',
  destructive: 'bg-danger-soft text-danger',
} as Record<string, string>)[kind] ?? 'bg-surface-2 text-ink-muted';

const title = (s: any) => (props.fa ? s.titleFa || s.titleEn : s.titleEn || s.titleFa) || s.tool;
const detail = (s: any) => (props.fa ? s.detailFa || s.detailEn : s.detailEn || s.detailFa);
const warnings = (s: any): string[] => (props.fa ? (s.warningsFa?.length ? s.warningsFa : s.warningsEn) : (s.warningsEn?.length ? s.warningsEn : s.warningsFa)) ?? [];
const inputId = (s: any) => `assistant-phrase-${plan.value.id}-${s.index}`;
const rememberId = (s: any) => `assistant-remember-${plan.value.id}-${s.index}`;
const errorId = computed(() => `assistant-plan-error-${plan.value.id}`);
const errorText = computed(() => (props.item.error ? (props.fa ? props.item.error.messageFa || props.item.error.messageEn : props.item.error.messageEn || props.item.error.messageFa) : ''));
const refused = (s: any) => props.item.errorStep !== null && props.item.errorStep === s.index;
</script>

<template>
  <section class="overflow-hidden rounded-xl border bg-surface shadow-panel"
           :class="actionable && hasDestructive ? 'border-danger/30' : 'border-line'"
           data-assistant-plan :data-plan-status="plan.status" :data-superseded="item.superseded ? 'true' : undefined"
           :data-plan-compact="line ? 'true' : undefined" role="group" :aria-label="line || heading">
    <header v-if="line" class="flex items-center gap-2 bg-surface-2 px-3 py-2 text-ink-muted" data-plan-heading>
      <AssistantIcon :icon="band.icon" :size="14" />
      <h3 class="min-w-0 flex-1 text-sm">{{ line }}</h3>
      <button type="button" class="inline-flex shrink-0 items-center gap-1 rounded-md px-1.5 py-0.5 text-[11px] font-medium hover:bg-surface hover:text-ink focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/40"
              :aria-expanded="open ? 'true' : 'false'" :aria-controls="detailsId" data-plan-details @click="open = !open">
        {{ t('Details', 'جزئیات') }}
        <AssistantIcon :icon="ChevronDown" :size="14" class="motion-safe:transition-transform" :class="open ? 'rotate-180' : ''" />
      </button>
    </header>
    <header v-else class="flex items-center gap-2 px-3 py-2.5" :class="band.cls" data-plan-heading>
      <AssistantIcon :icon="band.icon" :spin="plan.status === 'executing'" />
      <h3 class="flex-1 text-sm font-semibold">{{ heading }}</h3>
      <span v-if="actionable && validUntil" class="text-[11px]">{{ t(`valid until ${validUntil}`, `معتبر تا ${validUntil}`) }}</span>
    </header>

    <!-- The server's refusal on a card folded to one line ("this plan expired") stays in view. -->
    <p v-if="line && item.error" :id="errorId" class="alert-danger mx-3 my-2.5 py-2 text-sm" role="alert" :dir="textDirection(errorText)" data-plan-error>{{ errorText }}</p>

    <div v-show="!line || open" :id="detailsId">
      <ol class="divide-y divide-line border-t border-line">
        <li v-for="(step, i) in plan.steps" :key="step.index" class="space-y-2 px-3 py-3"
            :class="refused(step) ? 'bg-danger-soft/50 ring-2 ring-inset ring-danger/40' : ''"
            :data-step-kind="step.kind" :data-step-refused="refused(step) ? 'true' : undefined"
            :aria-describedby="refused(step) ? errorId : undefined">
          <div class="flex items-start gap-2.5">
            <span class="mt-0.5 grid h-5 w-5 shrink-0 place-items-center rounded-full bg-surface-2 text-[11px] font-semibold text-ink-muted" dir="ltr">{{ i + 1 }}</span>
            <div class="min-w-0 flex-1 space-y-1">
              <div class="flex flex-wrap items-center gap-1.5">
                <span class="status-pill text-[11px]" :class="kindClass(step.kind)" data-step-kind-badge>{{ kindLabel(step.kind, fa) }}</span>
                <span class="text-sm text-ink" :dir="textDirection(title(step))">{{ title(step) }}</span>
              </div>
              <p v-if="detail(step)" class="text-sm leading-5 text-ink-muted" :dir="textDirection(detail(step))">{{ detail(step) }}</p>
              <ul v-if="warnings(step).length" class="space-y-1">
                <li v-for="(w, wi) in warnings(step)" :key="wi" class="flex items-start gap-1.5 text-sm leading-5 text-warn" :dir="textDirection(w)">
                  <AssistantIcon :icon="TriangleAlert" :size="14" class="mt-0.5" /><span>{{ w }}</span>
                </li>
              </ul>
              <p v-if="step.autoApproved" class="flex items-center gap-1.5 text-[11px] text-ink-muted">
                <AssistantIcon :icon="Check" :size="12" />
                {{ t('You said not to ask again for this.', 'گفته بودید برای این کار دوباره نپرسم.') }}
              </p>
            </div>
          </div>

          <label v-if="actionable && isRememberable(step)" :for="rememberId(step)" class="flex cursor-pointer items-center gap-2 ps-7 text-sm text-ink-muted">
            <input :id="rememberId(step)" v-model="item.form.remember[step.tool]" type="checkbox" class="h-4 w-4 rounded border-line-strong accent-[rgb(var(--brand))]" data-remember-tool />
            {{ t("Don't ask again for this", 'دیگر برای این کار نپرس') }}
          </label>

          <div v-if="actionable && needsPhrase(step)" class="space-y-1 ps-7">
            <label :for="inputId(step)" class="block text-sm text-ink-muted">
              {{ t('To confirm, type', 'برای تأیید بنویسید') }}
              <code dir="ltr" class="rounded bg-danger-soft px-1 py-0.5 font-mono text-danger">{{ step.confirmationPhrase }}</code>
            </label>
            <input :id="inputId(step)" v-model="item.form.phrases[step.index]" type="text" :dir="textDirection(item.form.phrases[step.index])" autocomplete="off" spellcheck="false"
                   class="form-control py-1.5 font-mono text-sm" data-confirm-phrase
                   :aria-invalid="item.mismatched.includes(step.index) ? 'true' : 'false'"
                   :aria-describedby="item.mismatched.includes(step.index) ? inputId(step) + '-error' : undefined" />
            <p v-if="item.mismatched.includes(step.index)" :id="inputId(step) + '-error'" class="field-error text-sm" role="alert">
              <template v-if="step.confirmationPhrase">{{ t('That does not match. Type the name exactly as shown.', 'با نام نمایش‌داده‌شده یکی نیست. آن را دقیقاً همان‌طور بنویسید.') }}</template>
              <template v-else>{{ t('This step cannot be confirmed: no name to type was sent.', 'این گام قابل تأیید نیست: نامی برای نوشتن فرستاده نشده است.') }}</template>
            </p>
          </div>
        </li>
      </ol>

      <!-- The server's refusal, whatever the card's state now: "expired" is shown in its own words. -->
      <p v-if="item.error && !line" :id="errorId" class="alert-danger mx-3 mb-0 mt-2.5 py-2 text-sm" role="alert" :dir="textDirection(errorText)" data-plan-error>{{ errorText }}</p>

      <footer v-if="actionable" class="space-y-2 border-t border-line px-3 py-2.5" :class="item.error ? 'mt-2.5' : ''">
        <p class="text-sm font-medium text-ink-muted" data-plan-safety>{{ t('Nothing has run yet.', 'هنوز هیچ کاری اجرا نشده است.') }}</p>
        <div class="flex items-center gap-2">
          <button type="button" :disabled="item.busy || locked" data-plan-run
                  :class="hasDestructive ? 'btn-danger inline-flex items-center gap-2 py-1.5 disabled:opacity-50' : 'btn-primary py-1.5 disabled:opacity-50'"
                  @click="emit('run')">
            <AssistantIcon v-if="item.busy" :icon="LoaderCircle" spin />
            {{ t('Run', 'اجرا') }}
          </button>
          <button type="button" class="btn-secondary py-1.5 disabled:opacity-50" :disabled="item.busy || locked" data-plan-cancel @click="emit('cancel')">
            {{ t('Cancel', 'لغو') }}
          </button>
        </div>
      </footer>
      <footer v-else-if="expired" class="border-t border-line px-3 py-2.5 text-sm text-ink-muted" :class="item.error && !line ? 'mt-2.5' : ''">
        <template v-if="item.superseded">{{ t('Nothing ran. The assistant was told this plan was not carried out.', 'هیچ کاری اجرا نشد. به دستیار گفته شد که این برنامه انجام نشده است.') }}</template>
        <template v-else>{{ t('Nothing ran. Ask again if you still want this.', 'هیچ کاری اجرا نشد. اگر هنوز این را می‌خواهید، دوباره بپرسید.') }}</template>
      </footer>
      <div v-else-if="item.error && !line" class="h-2.5"></div>
    </div>
  </section>
</template>
