<script setup lang="ts">
import { computed } from 'vue';
import { Check, Clock, LoaderCircle, TriangleAlert } from 'lucide';
import { AssistantIcon } from './assistantIcon';
import {
  kindLabel, needsPhrase, isRememberable, planIsActionable, planIsExpired,
} from './assistantPlan.js';

// One plan waiting for the person's consent (AgentPlanView). Nothing on this card has run: that is
// said on the card itself, because "create the database" in a list reads like a report unless
// something says otherwise. The rules — who needs a typed name, what may be remembered, what the
// approve body contains — live in assistantPlan.js; this file only draws them.

interface PlanItem {
  plan: any;
  form: { phrases: Record<number, string>; remember: Record<string, boolean> };
  mismatched: number[];
  busy: boolean;
  error: { messageEn: string; messageFa: string } | null;
}

const props = defineProps<{ item: PlanItem; fa: boolean; now: number }>();
const emit = defineEmits<{ (e: 'run'): void; (e: 'cancel'): void }>();
const t = (en: string, persian: string) => (props.fa ? persian : en);

const plan = computed(() => props.item.plan);
const actionable = computed(() => planIsActionable(plan.value, props.now));
const expired = computed(() => planIsExpired(plan.value, props.now));
const hasDestructive = computed(() => (plan.value.steps ?? []).some((s: any) => s.kind === 'destructive'));
const minutesLeft = computed(() => {
  const ms = Date.parse(plan.value.expiresAt) - props.now;
  return Number.isFinite(ms) ? Math.max(1, Math.ceil(ms / 60000)) : null;
});

const heading = computed(() => {
  switch (plan.value.status) {
    case 'pending': return expired.value ? t('This plan expired', 'این برنامه منقضی شد') : t('Plan — waiting for your approval', 'برنامه — منتظر تأیید شما');
    case 'executing': return t('Running…', 'در حال اجرا…');
    case 'executed': return t('Plan ran — see the result below', 'برنامه اجرا شد — نتیجه در پایین');
    case 'failed': return t('Plan stopped — see the result below', 'برنامه متوقف شد — نتیجه در پایین');
    case 'rejected': return t('Cancelled — nothing ran', 'لغو شد — هیچ کاری اجرا نشد');
    case 'expired': return t('This plan expired', 'این برنامه منقضی شد');
    default: return t('Plan', 'برنامه');
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
</script>

<template>
  <section class="rounded-xl border bg-surface shadow-panel"
           :class="actionable && hasDestructive ? 'border-danger/30' : 'border-line'"
           data-assistant-plan :data-plan-status="expired ? 'expired' : plan.status"
           role="group" :aria-label="heading">
    <header class="flex items-center gap-2 border-b border-line px-3 py-2.5">
      <AssistantIcon v-if="expired" :icon="Clock" class="text-ink-faint" />
      <strong class="flex-1 text-sm font-semibold text-ink">{{ heading }}</strong>
      <span v-if="actionable && minutesLeft !== null" class="text-[11px] text-ink-faint">
        {{ t(`expires in ${minutesLeft} min`, `تا ${minutesLeft} دقیقهٔ دیگر معتبر است`) }}
      </span>
    </header>

    <ol class="divide-y divide-line">
      <li v-for="(step, i) in plan.steps" :key="step.index" class="space-y-2 px-3 py-3" :data-step-kind="step.kind">
        <div class="flex items-start gap-2.5">
          <span class="mt-0.5 grid h-5 w-5 shrink-0 place-items-center rounded-full bg-surface-2 text-[11px] font-semibold text-ink-muted" dir="ltr">{{ i + 1 }}</span>
          <div class="min-w-0 flex-1 space-y-1">
            <div class="flex flex-wrap items-center gap-1.5">
              <span class="status-pill" :class="kindClass(step.kind)" data-step-kind-badge>{{ kindLabel(step.kind, fa) }}</span>
              <span class="text-sm font-medium text-ink" dir="auto">{{ title(step) }}</span>
            </div>
            <p v-if="detail(step)" class="text-xs leading-5 text-ink-muted" dir="auto">{{ detail(step) }}</p>
            <ul v-if="warnings(step).length" class="space-y-1">
              <li v-for="(w, wi) in warnings(step)" :key="wi" class="flex items-start gap-1.5 text-xs leading-5 text-warn" dir="auto">
                <AssistantIcon :icon="TriangleAlert" :size="14" class="mt-0.5" /><span>{{ w }}</span>
              </li>
            </ul>
            <p v-if="step.autoApproved" class="flex items-center gap-1.5 text-[11px] text-ink-faint">
              <AssistantIcon :icon="Check" :size="12" />
              {{ t('You said not to ask again for this.', 'گفته بودید برای این کار دوباره نپرسم.') }}
            </p>
          </div>
        </div>

        <label v-if="actionable && isRememberable(step)" :for="rememberId(step)" class="flex cursor-pointer items-center gap-2 ps-7 text-xs text-ink-muted">
          <input :id="rememberId(step)" v-model="item.form.remember[step.tool]" type="checkbox" class="h-4 w-4 rounded border-line-strong accent-[rgb(var(--brand))]" data-remember-tool />
          {{ t("Don't ask again for this", 'دیگر برای این کار نپرس') }}
        </label>

        <div v-if="actionable && needsPhrase(step)" class="space-y-1 ps-7">
          <label :for="inputId(step)" class="block text-xs text-ink-muted">
            {{ t('To confirm, type', 'برای تأیید بنویسید') }}
            <code dir="ltr" class="rounded bg-danger-soft px-1 py-0.5 font-mono text-danger">{{ step.confirmationPhrase }}</code>
          </label>
          <input :id="inputId(step)" v-model="item.form.phrases[step.index]" type="text" dir="auto" autocomplete="off" spellcheck="false"
                 class="form-control py-1.5 font-mono text-xs" data-confirm-phrase
                 :aria-invalid="item.mismatched.includes(step.index) ? 'true' : 'false'"
                 :aria-describedby="item.mismatched.includes(step.index) ? inputId(step) + '-error' : undefined" />
          <p v-if="item.mismatched.includes(step.index)" :id="inputId(step) + '-error'" class="field-error" role="alert">
            <template v-if="step.confirmationPhrase">{{ t('That does not match. Type the name exactly as shown.', 'با نام نمایش‌داده‌شده یکی نیست. آن را دقیقاً همان‌طور بنویسید.') }}</template>
            <template v-else>{{ t('This step cannot be confirmed: no name to type was sent.', 'این گام قابل تأیید نیست: نامی برای نوشتن فرستاده نشده است.') }}</template>
          </p>
        </div>
      </li>
    </ol>

    <footer v-if="actionable" class="space-y-2 border-t border-line px-3 py-2.5">
      <p class="text-[11px] text-ink-faint">{{ t('Nothing has run yet.', 'هنوز هیچ کاری اجرا نشده است.') }}</p>
      <p v-if="item.error" class="alert-danger py-2 text-xs" role="alert" dir="auto">{{ fa ? item.error.messageFa : item.error.messageEn }}</p>
      <div class="flex items-center gap-2">
        <button type="button" :disabled="item.busy" data-plan-run
                :class="hasDestructive ? 'btn-danger inline-flex items-center gap-2 py-1.5' : 'btn-primary py-1.5'"
                @click="emit('run')">
          <AssistantIcon v-if="item.busy" :icon="LoaderCircle" spin />
          {{ t('Run', 'اجرا') }}
        </button>
        <button type="button" class="btn-secondary py-1.5" :disabled="item.busy" data-plan-cancel @click="emit('cancel')">
          {{ t('Cancel', 'لغو') }}
        </button>
      </div>
    </footer>
    <footer v-else-if="expired" class="border-t border-line px-3 py-2.5 text-xs text-ink-muted">
      {{ t('Nothing ran. Ask again if you still want this.', 'هیچ کاری اجرا نشد. اگر هنوز این را می‌خواهید، دوباره بپرسید.') }}
    </footer>
  </section>
</template>
