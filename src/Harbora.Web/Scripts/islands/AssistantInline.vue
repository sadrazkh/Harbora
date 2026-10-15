<script setup lang="ts">
import AssistantSecretText from './AssistantSecretText.vue';

// One line of a message's inline nodes — text, `code`, and either of them bold (assistantFormat.js).
// Every leaf is written with {{ }}, as a text node; <strong> and <code> are chosen by the node's type,
// never by what the text says. A secret's handle inside a text node is drawn as a lock chip.
defineProps<{ nodes: any[]; nameOf?: (handle: string) => string | null; fa?: boolean }>();
</script>

<template>
  <template v-for="(node, n) in nodes" :key="n">
    <code v-if="node.type === 'code'" dir="ltr"
          class="rounded bg-surface-2 px-1 py-0.5 font-mono text-[0.85em] text-ink ring-1 ring-inset ring-line"
          :class="node.bold ? 'font-semibold' : ''">{{ node.text }}</code>
    <strong v-else-if="node.bold" class="font-semibold"><AssistantSecretText :text="node.text" :name-of="nameOf" :fa="fa" /></strong>
    <AssistantSecretText v-else :text="node.text" :name-of="nameOf" :fa="fa" />
  </template>
</template>
