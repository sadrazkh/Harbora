<script setup lang="ts">
import { computed } from 'vue';
import { formatMessage, textDirection, blockText } from './assistantFormat.js';

// A message's text with light formatting. Every leaf is rendered with {{ }}, which Vue writes as a
// text node: the formatter decides where paragraphs, lists and code go, and nothing the model wrote
// is ever parsed as markup. See assistantFormat.js.
const props = defineProps<{ text: string }>();
const blocks = computed(() => formatMessage(props.text));
</script>

<template>
  <div class="space-y-2 break-words leading-6">
    <template v-for="(block, b) in blocks" :key="b">
      <p v-if="block.type === 'p'" :dir="textDirection(blockText(block))">
        <template v-for="(line, l) in block.lines" :key="l">
          <br v-if="l > 0" />
          <template v-for="(node, n) in line" :key="n">
            <code v-if="node.type === 'code'" dir="ltr" class="rounded bg-surface-2 px-1 py-0.5 font-mono text-[0.85em] text-ink ring-1 ring-inset ring-line">{{ node.text }}</code>
            <template v-else>{{ node.text }}</template>
          </template>
        </template>
      </p>
      <ul v-else-if="block.type === 'ul'" class="list-disc space-y-1 ps-5" :dir="textDirection(blockText(block))">
        <li v-for="(item, i) in block.items" :key="i">
          <template v-for="(node, n) in item" :key="n">
            <code v-if="node.type === 'code'" dir="ltr" class="rounded bg-surface-2 px-1 py-0.5 font-mono text-[0.85em] text-ink ring-1 ring-inset ring-line">{{ node.text }}</code>
            <template v-else>{{ node.text }}</template>
          </template>
        </li>
      </ul>
      <pre v-else-if="block.type === 'pre'" dir="ltr" class="overflow-x-auto rounded-lg bg-code p-3 font-mono text-sm leading-5 text-code-ink">{{ block.text }}</pre>
    </template>
  </div>
</template>
