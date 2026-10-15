<script setup lang="ts">
import { computed, inject, onBeforeUnmount, reactive } from 'vue';
import { Check, Copy, TriangleAlert } from 'lucide';
import {
  formatMessage, textDirection, createSteadyDirection, blockText, inlineText, listStyleOf, messageCopyText, codeCopyText,
} from './assistantFormat.js';
import { createCopyFeedback, copyNotice } from './assistantCopy.js';
import { ANNOUNCER } from './assistantAnnouncer.js';
import { AssistantIcon } from './assistantIcon';
import AssistantInline from './AssistantInline.vue';

// A message's text with light formatting. Every leaf is rendered with {{ }}, which Vue writes as a
// text node: the formatter decides where paragraphs, lists, headings, tables and code go, and nothing
// the model wrote is ever parsed as markup. See assistantFormat.js.
//
// `live`: text still being written by delta events — each paragraph's direction is decided once and held
// (createSteadyDirection), so the bubble does not flip sides as words arrive. `streamed`: such a bubble
// after its message arrived — decided paragraphs keep their side, short ones are settled by the whole
// text. A secret's handle in the text is drawn as a lock chip (AssistantSecretText), named by `nameOf`.
//
// Copying: once the message is finished (not `live`) it has a copy button under it, and each code block
// a button of its own in its corner. What they take is decided in assistantFormat.js, how it is put on
// the clipboard and what the button says afterwards in assistantCopy.js; the words a screen reader hears
// go to the pane's announcer (assistantAnnouncer.js), outside the conversation log.
const props = defineProps<{ text: string; live?: boolean; streamed?: boolean; nameOf?: (handle: string) => string | null; fa?: boolean }>();
const t = (en: string, persian: string) => (props.fa ? persian : en);
const blocks = computed(() => formatMessage(props.text));
const steadyDirection = createSteadyDirection();
const dirOf = (block: any, index: number) => {
  const text = blockText(block);
  if (props.live) return steadyDirection(text, index);
  if (props.streamed) return steadyDirection.settle(text, index);
  return textDirection(text);
};
// A cell with letters reads the way they do; one with only numbers or symbols takes the table's own side.
const cellDir = (cell: any[]) => {
  const dir = textDirection(inlineText(cell));
  return dir === 'auto' ? undefined : dir;
};

const feedback = createCopyFeedback({ wrap: reactive });
onBeforeUnmount(() => feedback.dispose());
const messageText = computed(() => messageCopyText(props.text));
// «کپی شد» / «کپی نشد» go to the pane's one announcer (assistantAnnouncer.js), not to a region per message.
const announcer = inject(ANNOUNCER, null);
async function copy(key: string, text: string | null) {
  if (text === null) return;
  announcer?.say(copyNotice(await feedback.copy(key, text), props.fa === true));
}
const iconFor = (key: string) => ({ copied: Check, failed: TriangleAlert } as Record<string, any>)[feedback.statusOf(key)] ?? Copy;
const iconClassFor = (key: string) => ({ copied: 'text-ok', failed: 'text-danger' } as Record<string, string>)[feedback.statusOf(key)] ?? '';
</script>

<template>
  <div>
    <div class="space-y-2 break-words leading-6">
      <template v-for="(block, b) in blocks" :key="b">
        <p v-if="block.type === 'p'" :dir="dirOf(block, b)">
          <template v-for="(line, l) in block.lines" :key="l">
            <br v-if="l > 0" />
            <AssistantInline :nodes="line" :name-of="nameOf" :fa="fa" />
          </template>
        </p>
        <!-- A heading is a bold paragraph, not an <h*>: the pane's outline belongs to the panel around it. -->
        <p v-else-if="block.type === 'heading'" class="pt-2 font-semibold text-ink first:pt-0" :dir="dirOf(block, b)">
          <AssistantInline :nodes="block.nodes" :name-of="nameOf" :fa="fa" />
        </p>
        <ul v-else-if="block.type === 'ul'" class="list-disc space-y-1 ps-5" :dir="dirOf(block, b)">
          <li v-for="(item, i) in block.items" :key="i">
            <AssistantInline :nodes="item" :name-of="nameOf" :fa="fa" />
          </li>
        </ul>
        <ol v-else-if="block.type === 'ol'" class="space-y-1 ps-6" :start="block.start" :style="{ listStyleType: listStyleOf(block) }" :dir="dirOf(block, b)">
          <li v-for="(item, i) in block.items" :key="i">
            <AssistantInline :nodes="item" :name-of="nameOf" :fa="fa" />
          </li>
        </ol>
        <!-- The table scrolls inside its wrapper; the pane never scrolls sideways. -->
        <div v-else-if="block.type === 'table'" class="max-w-full overflow-x-auto rounded-lg border border-line" :dir="dirOf(block, b)" data-assistant-table>
          <table class="min-w-full border-collapse text-sm leading-5">
            <thead class="bg-surface-2">
              <tr>
                <th v-for="(cell, c) in block.head" :key="c" scope="col" :dir="cellDir(cell)" class="border-b border-line px-2.5 py-1.5 text-start align-top font-semibold">
                  <AssistantInline :nodes="cell" :name-of="nameOf" :fa="fa" />
                </th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(row, r) in block.rows" :key="r" class="border-t border-line first:border-t-0">
                <td v-for="(cell, c) in row" :key="c" :dir="cellDir(cell)" class="px-2.5 py-1.5 text-start align-top">
                  <AssistantInline :nodes="cell" :name-of="nameOf" :fa="fa" />
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <div v-else-if="block.type === 'pre'" class="relative" dir="ltr">
          <pre class="overflow-x-auto rounded-lg bg-code p-3 font-mono text-sm leading-5 text-code-ink">{{ block.text }}</pre>
          <button v-if="!live && codeCopyText(block) !== null" type="button"
                  class="absolute end-1.5 top-1.5 grid h-7 w-7 place-items-center rounded-md text-code-ink/70 hover:bg-code-ink/10 hover:text-code-ink"
                  :aria-label="t('Copy code', 'کپی کد')" :title="t('Copy code', 'کپی کد')" data-assistant-copy-code
                  @click="copy('code-' + b, codeCopyText(block))">
            <AssistantIcon :icon="iconFor('code-' + b)" :size="14" :class="iconClassFor('code-' + b)" />
          </button>
        </div>
      </template>
    </div>
    <div v-if="!live && messageText !== null" class="mt-1">
      <button type="button" class="grid h-7 w-7 place-items-center rounded-md text-ink-faint hover:bg-surface-2 hover:text-ink"
              :aria-label="t('Copy answer', 'کپی پاسخ')" :title="t('Copy answer', 'کپی پاسخ')" data-assistant-copy
              @click="copy('message', messageText)">
        <AssistantIcon :icon="iconFor('message')" :size="14" :class="iconClassFor('message')" />
      </button>
    </div>
  </div>
</template>
