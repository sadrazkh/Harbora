<script setup lang="ts">
// The Functions code editor. Mounted over a plain <textarea> the server already rendered — see
// `EditFunction.cshtml` and the `function-code-editor` entry in `main.ts`. That textarea stays in
// charge of the actual form submission: this component only keeps its `.value` in sync as
// somebody types, so a post is correct whether or not any of this ever ran. If the chunk below
// fails to load — offline, blocked, a bad deploy — `main.ts` never calls `mount()`, and the plain
// textarea nobody touched is still sitting there, already working.
//
// CodeMirror 6, hand-assembled rather than `basicSetup`, and lazily importing exactly one grammar
// — see `loadLanguage` — because the entire reason this is CodeMirror rather than Prism is editing
// help (line numbers, indentation, bracket matching, its own undo history, a Tab that indents
// instead of leaving the field), and the entire reason it does not cost all three grammars is that
// no function app ever needs more than one: `FunctionRuntime` is fixed for the app this page
// belongs to.
import { onBeforeUnmount, onMounted, ref, shallowRef } from 'vue';
import { EditorState, type Extension } from '@codemirror/state';
import {
  EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter,
  highlightSpecialChars, drawSelection, dropCursor, rectangularSelection, crosshairCursor,
} from '@codemirror/view';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import {
  bracketMatching, indentOnInput, indentUnit, syntaxHighlighting, HighlightStyle, StreamLanguage,
} from '@codemirror/language';
import { closeBrackets, closeBracketsKeymap } from '@codemirror/autocomplete';
import { search, searchKeymap, openSearchPanel } from '@codemirror/search';
import { tags as t } from '@lezer/highlight';

const props = defineProps<{
  /** `FunctionRuntime`'s C# name — "CSharp" | "JavaScript" | "Python", never a number, so a
   *  fourth runtime added later fails loudly instead of silently loading the wrong grammar. */
  runtime: string;
  /** The panel's own language, for this component's two strings — never the code's language. */
  lang: string;
  initialCode: string;
  /** The name the shadow textarea posts under — copied off the textarea this replaced (`Code`). */
  fieldName: string;
}>();

const fa = props.lang === 'fa';

const host = ref<HTMLElement | null>(null);
const shadow = ref<HTMLTextAreaElement | null>(null);
const view = shallowRef<EditorView | null>(null);

/**
 * Only the grammar this page's runtime needs. A dynamic `import()` inside an already-lazy
 * component: Vite still emits one chunk per branch, so `@codemirror/lang-javascript` is never
 * fetched by a Python function app's editor, and vice versa.
 */
async function loadLanguage(runtime: string): Promise<Extension> {
  switch (runtime) {
    case 'JavaScript': {
      const { javascript } = await import('@codemirror/lang-javascript');
      return javascript();
    }
    case 'Python': {
      const { python } = await import('@codemirror/lang-python');
      return python();
    }
    case 'CSharp':
    default: {
      // CodeMirror 6 has no first-class (Lezer) C# grammar; the legacy stream-mode C-like parser
      // is what the measurements this island was sized against actually used. Good highlighting,
      // not Lezer-quality — and, of the three, the cheapest to load.
      const { csharp } = await import('@codemirror/legacy-modes/mode/clike');
      return StreamLanguage.define(csharp);
    }
  }
}

// Colours read the panel's own CSS custom properties (`app.css`), so this needs no dark-mode
// logic of its own: the same `html.dark` class toggle that repaints the rest of the panel
// repaints this, because these are the same variables every other surface in the panel uses.
const editorTheme = EditorView.theme({
  '&': {
    color: 'rgb(var(--text))',
    backgroundColor: 'rgb(var(--canvas))',
    height: '100%',
  },
  '.cm-content': {
    caretColor: 'rgb(var(--brand))',
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
    fontSize: '13px',
    lineHeight: '1.65',
    padding: '1rem 0.75rem',
  },
  '.cm-gutters': {
    backgroundColor: 'rgb(var(--canvas))',
    color: 'rgb(var(--text-faint))',
    border: 'none',
  },
  '.cm-activeLine': { backgroundColor: 'rgb(var(--surface-2))' },
  '.cm-activeLineGutter': { backgroundColor: 'rgb(var(--surface-2))' },
  '&.cm-focused': { outline: 'none' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': {
    backgroundColor: 'rgb(var(--brand) / 0.22) !important',
  },
  '.cm-matchingBracket, .cm-nonmatchingBracket': {
    backgroundColor: 'rgb(var(--brand) / 0.2)',
    outline: '1px solid rgb(var(--brand) / 0.4)',
  },
  '.cm-cursor': { borderLeftColor: 'rgb(var(--brand))' },
  // The search/replace panel `search()` opens on Ctrl/Cmd-F — themed to the panel's own tokens
  // rather than left at CodeMirror's generic default, which reads as a foreign control dropped
  // onto the page.
  '.cm-panels': {
    backgroundColor: 'rgb(var(--surface-2))',
    color: 'rgb(var(--text))',
    borderTop: '1px solid rgb(var(--border))',
  },
  '.cm-panel input, .cm-panel button': {
    color: 'rgb(var(--text))',
    backgroundColor: 'rgb(var(--surface))',
    border: '1px solid rgb(var(--border-strong))',
    borderRadius: '6px',
  },
  '.cm-searchMatch': { backgroundColor: 'rgb(var(--warn-soft))' },
  '.cm-searchMatch-selected': { backgroundColor: 'rgb(var(--warn) / 0.35)' },
});

// Token colours, mapped onto the same semantic tokens the rest of the panel uses rather than a
// hard-coded palette that would fight dark mode.
const highlightStyle = HighlightStyle.define([
  { tag: t.keyword, color: 'rgb(var(--brand-text))', fontWeight: '600' },
  { tag: [t.name, t.deleted, t.character, t.propertyName, t.macroName], color: 'rgb(var(--text))' },
  { tag: [t.function(t.variableName), t.labelName], color: 'rgb(var(--info))' },
  { tag: [t.color, t.constant(t.name), t.standard(t.name)], color: 'rgb(var(--brand-text))' },
  { tag: [t.definition(t.name), t.separator], color: 'rgb(var(--text))' },
  {
    tag: [t.typeName, t.className, t.number, t.changed, t.annotation, t.modifier, t.self, t.namespace],
    color: 'rgb(var(--warn))',
  },
  { tag: [t.operator, t.operatorKeyword], color: 'rgb(var(--text-muted))' },
  { tag: [t.url, t.escape, t.regexp, t.link], color: 'rgb(var(--ok))' },
  { tag: [t.meta, t.comment], color: 'rgb(var(--text-faint))', fontStyle: 'italic' },
  { tag: t.strong, fontWeight: 'bold' },
  { tag: t.emphasis, fontStyle: 'italic' },
  { tag: t.string, color: 'rgb(var(--ok))' },
  { tag: t.punctuation, color: 'rgb(var(--text-muted))' },
]);

function syncShadow(doc: string) {
  const el = shadow.value;
  if (!el) return;
  el.value = doc;
  // A real 'input' event, not a Vue-only signal — so a plain framework-agnostic dirty-tracking
  // script listening on the textarea (draft protection, in EditFunction.cshtml) works identically
  // whether the island mounted or somebody is typing straight into the fallback textarea.
  el.dispatchEvent(new Event('input', { bubbles: true }));
}

/**
 * CM6's own recommended `indentWithTab` traps keyboard focus — the price of making Tab do
 * something other than move focus, which is exactly what this editor was built to change (the
 * task's own "single most Notepad-like thing about a bare textarea"). Escape is the documented
 * way out: it does not collide with anything else bound here.
 */
const escapeToBlur = keymap.of([{
  key: 'Escape',
  run: (v) => { v.contentDOM.blur(); return true; },
}]);

onMounted(async () => {
  if (!host.value) return;

  const language = await loadLanguage(props.runtime);
  // The page may have navigated away while the grammar chunk was in flight; host.value would be
  // gone and mounting into it would throw.
  if (!host.value) return;

  const state = EditorState.create({
    doc: props.initialCode,
    extensions: [
      lineNumbers(),
      highlightActiveLineGutter(),
      highlightActiveLine(),
      highlightSpecialChars(),
      history(),
      drawSelection(),
      dropCursor(),
      rectangularSelection(),
      crosshairCursor(),
      indentOnInput(),
      bracketMatching(),
      closeBrackets(),
      search({ top: true }),
      syntaxHighlighting(highlightStyle, { fallback: true }),
      indentUnit.of('    '),
      EditorState.tabSize.of(4),
      EditorView.lineWrapping,
      // Code is LTR inside an RTL-first panel — forced on the DOM (see the wrapper's own
      // `dir="ltr"` in the template) and here too, so CodeMirror's own bidi handling never
      // second-guesses it from the page's ambient direction.
      EditorView.contentAttributes.of({ dir: 'ltr', spellcheck: 'false', 'aria-label': fa ? 'ویرایشگر کد' : 'Code editor' }),
      language,
      editorTheme,
      escapeToBlur,
      keymap.of([
        ...closeBracketsKeymap,
        ...defaultKeymap,
        ...searchKeymap,
        ...historyKeymap,
        indentWithTab,
      ]),
      EditorView.updateListener.of((update) => {
        if (update.docChanged) syncShadow(update.state.doc.toString());
      }),
    ],
  });

  view.value = new EditorView({ state, parent: host.value });
  syncShadow(props.initialCode);

  // The search icon above was inserted after lucide's own render pass already ran once at load —
  // the same reason every other island and the log poller re-fire this event after first paint.
  document.dispatchEvent(new CustomEvent('harbora:content-changed'));
});

onBeforeUnmount(() => {
  view.value?.destroy();
});

function openFind() {
  if (view.value) openSearchPanel(view.value);
}
</script>

<template>
  <div class="relative">
    <div class="mb-1.5 flex items-center justify-end">
      <button type="button" @click="openFind"
              class="inline-flex items-center gap-1 rounded-md px-2 py-1 text-[10px] font-medium text-ink-faint hover:bg-surface-2 hover:text-ink">
        <i data-lucide="search" class="h-3 w-3"></i>
        {{ fa ? 'جستجو و جایگزینی' : 'Find & replace' }}
      </button>
    </div>
    <div ref="host" dir="ltr"
         class="min-h-[28rem] w-full resize-y overflow-auto rounded-xl border border-line bg-canvas focus-within:border-accent"></div>
    <!-- Not rendered, but real: the browser reads this element's value at submit time exactly the
         way it always read the plain textarea, because this is still that field — same `name`,
         kept current by `syncShadow` on every change. -->
    <textarea ref="shadow" :name="fieldName" class="hidden" tabindex="-1" aria-hidden="true"></textarea>
  </div>
</template>
