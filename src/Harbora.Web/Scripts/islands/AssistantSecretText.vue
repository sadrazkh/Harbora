<script setup lang="ts">
import { computed } from 'vue';
import { Lock } from 'lucide';
import { AssistantIcon } from './assistantIcon';
import { splitSecrets } from './assistantSecrets.js';

// Text in which a secret's handle (`{{secret:…}}`) is drawn as a lock chip with the secret's name —
// the name this pane gave it, when it was held here, or «secret» when it was not (a reloaded page, an
// older conversation). The handle is what the model saw; the value was never in the message at all.
// Everything is rendered with {{ }}, as text.
const props = defineProps<{ text: string; nameOf?: (handle: string) => string | null; fa?: boolean; inverse?: boolean }>();
const parts = computed(() => splitSecrets(props.text));
const label = (handle: string) => props.nameOf?.(handle) || (props.fa ? 'سکرت' : 'secret');
</script>

<template>
  <template v-for="(part, i) in parts" :key="i">
    <span v-if="part.type === 'secret'" dir="auto" data-secret-chip
          class="mx-0.5 inline-flex items-center gap-1 rounded-md px-1.5 py-px align-baseline text-[0.9em] font-medium"
          :class="inverse ? 'bg-white/20 text-white ring-1 ring-inset ring-white/30' : 'bg-surface-2 text-ink ring-1 ring-inset ring-line'"
          :title="fa ? 'مقداری که خصوصی وارد شد؛ هوش مصنوعی آن را نمی‌بیند' : 'Entered privately; the AI never sees the value'">
      <AssistantIcon :icon="Lock" :size="11" />{{ label(part.handle) }}
    </span>
    <template v-else>{{ part.text }}</template>
  </template>
</template>
