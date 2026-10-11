import { h, type FunctionalComponent } from 'vue';

// Lucide glyphs for the assistant island, rendered by Vue rather than by lucide's createIcons().
//
// createIcons() finds `<i data-lucide>` elements and swaps them for <svg> after the fact. Inside a
// Vue tree that swap replaces nodes Vue still holds references to, and the chat is nothing but
// conditional and repeated markup — a spinner becoming a check, lines arriving while you watch — so
// the first re-render would patch elements that are no longer in the document. Here each icon is the
// glyph's own node list (lucide exports them as data: `[['path', { d }], …]`), drawn with h().
// Imported one by one in the island, like main.ts does, so only the glyphs used reach the chunk.

type IconNode = [tag: string, attrs: Record<string, string | number>][];

export const AssistantIcon: FunctionalComponent<{ icon: IconNode; size?: number; spin?: boolean }> = (props) =>
  h(
    'svg',
    {
      xmlns: 'http://www.w3.org/2000/svg',
      width: props.size ?? 16,
      height: props.size ?? 16,
      viewBox: '0 0 24 24',
      fill: 'none',
      stroke: 'currentColor',
      'stroke-width': 1.75,
      'stroke-linecap': 'round',
      'stroke-linejoin': 'round',
      'aria-hidden': 'true',
      class: ['shrink-0', props.spin ? 'animate-spin motion-reduce:animate-none' : ''],
    },
    props.icon.map(([tag, attrs]) => h(tag, attrs)),
  );

AssistantIcon.props = ['icon', 'size', 'spin'];
