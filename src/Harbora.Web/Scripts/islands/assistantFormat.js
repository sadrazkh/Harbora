// Light formatting for the assistant's messages: paragraphs, line breaks, **bold**, `inline code`,
// fenced code blocks, "-" and numbered lists, "#" headings and simple tables — and nothing that
// could become markup.
//
// Model text is untrusted. It can repeat whatever a user, a resource name or a log line put in front
// of the model, so it is never handed to the DOM as HTML. Instead it is cut into a small tree whose
// leaves are plain strings; AssistantText.vue renders the leaves with `{{ }}` interpolation, which
// Vue writes as text nodes. "<script>" in a message is therefore shown as eight characters. There is
// no escaping step to forget, because nothing here produces HTML in the first place. The richer
// blocks keep that: a table is rows of cells whose leaves are strings, a link stays the characters
// the model wrote, and the only thing the tree adds to a string is the flag that a run of it is bold.
//
// This file cuts a message into blocks; the line-level scanner (code spans, bold) is assistantInline.js
// and the table parser assistantTable.js.

import { SECRET_HANDLE } from './assistantSecrets.js';
import { inline, inlineText } from './assistantInline.js';
import { parseTable } from './assistantTable.js';

export { inlineText };

/**
 * @typedef {import('./assistantInline.js').InlineNode} InlineNode
 * @typedef {{ type: 'p', lines: InlineNode[][] }
 *   | { type: 'ul', items: InlineNode[][] }
 *   | { type: 'ol', start: number, digits: 'latin' | 'persian' | 'arabic-indic', items: InlineNode[][] }
 *   | { type: 'heading', level: number, nodes: InlineNode[] }
 *   | { type: 'table', head: InlineNode[][], rows: InlineNode[][][] }
 *   | { type: 'pre', text: string }} Block
 */

const BULLET = /^\s*[-*•]\s+(.*)$/;
// 1. or 1) in Latin, Persian (۱) or Arabic-Indic (١) digits. Capped at nine digits, as Markdown caps
// it, so a long number that ends a sentence at the start of a line is not a list.
const NUMBERED = /^\s*([0-9۰-۹٠-٩]{1,9})[.)]\s+(.*)$/;
const HEADING = /^\s{0,3}(#{1,6})\s+(\S.*)$/;
const FENCE = /^\s*```/;
const isFence = (line) => FENCE.test(line);

/**
 * A `#`…`######` heading line → `{ level, text }`, or null. The optional closing run of `#`s (`## Plan ##`)
 * is cut off by scanning back from the end rather than by a pattern: `(.*?)(\s+#+)?\s*$` backtracks
 * quadratically on a long run of spaces, and this runs on every line of a message that is re-formatted
 * with every word that arrives.
 *
 * @param {string} line
 */
function parseHeading(line) {
  const match = HEADING.exec(line);
  if (!match) return null;
  let text = match[2].trimEnd();
  let end = text.length;
  while (end > 0 && text[end - 1] === '#') end--;
  if (end > 0 && end < text.length && /\s/.test(text[end - 1])) text = text.slice(0, end).trimEnd();
  return { level: match[1].length, text };
}

// ---- blocks ----------------------------------------------------------------------------------------

/** The first code point of each non-Latin digit set, by the name `list-style-type` knows it by. */
const DIGIT_SETS = [['persian', 0x06F0], ['arabic-indic', 0x0660]];

/** `۱۲` → `{ value: 12, digits: 'persian' }`; only NUMBERED's character classes ever reach this. */
function readNumber(text) {
  let digits = 'latin';
  let value = 0;
  for (const ch of text) {
    const code = ch.codePointAt(0);
    const set = DIGIT_SETS.find(([, zero]) => code >= zero && code <= zero + 9);
    if (set) digits = set[0];
    value = value * 10 + (set ? code - set[1] : code - 0x30);
  }
  return { value, digits };
}

/**
 * @param {string | null | undefined} source
 * @returns {Block[]}
 */
export function formatMessage(source) {
  if (!source) return [];
  const lines = String(source).replace(/\r\n?/g, '\n').split('\n');

  /** @type {Block[]} */
  const blocks = [];
  /** @type {Block | null} */
  let open = null;
  // A blank line inside a list does not end it when the next line is another item of the same kind:
  // "1. a" and "2. b" with a blank line between them are one list, not two.
  const close = () => { open = null; };
  const list = (type, extra) => {
    if (!open || open.type !== type) { open = { type, ...extra, items: [] }; blocks.push(open); }
    return open;
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    if (isFence(line)) {
      // Everything up to the closing fence (or the end of the message, if the model never closed
      // it) is kept verbatim. The text after the opening ``` is a language tag, not content.
      const body = [];
      i++;
      while (i < lines.length && !isFence(lines[i])) body.push(lines[i++]);
      close();
      blocks.push({ type: 'pre', text: body.join('\n') });
      continue;
    }

    if (line.trim() === '') {
      if (!open || (open.type !== 'ul' && open.type !== 'ol')) close();
      continue;
    }

    const heading = parseHeading(line);
    if (heading) {
      close();
      blocks.push({ type: 'heading', level: heading.level, nodes: inline(heading.text) });
      continue;
    }

    const table = parseTable(lines, i, isFence);
    if (table) {
      close();
      blocks.push(table.block);
      i = table.next - 1;
      continue;
    }

    const bullet = BULLET.exec(line);
    if (bullet) {
      list('ul', {}).items.push(inline(bullet[1]));
      continue;
    }

    const numbered = NUMBERED.exec(line);
    if (numbered) {
      const { value, digits } = readNumber(numbered[1]);
      list('ol', { start: value, digits }).items.push(inline(numbered[2]));
      continue;
    }

    if (!open || open.type !== 'p') { open = { type: 'p', lines: [] }; blocks.push(open); }
    open.lines.push(inline(line));
  }

  return blocks;
}

const RTL_LETTER = /[\u0590-\u08FF\uFB1D-\uFDFF\uFE70-\uFEFC]/;
const LTR_LETTER = /[A-Za-z\u00C0-\u024F]/;

/**
 * The direction a piece of text reads in: "rtl", "ltr", or "auto" when it has no letters at all.
 *
 * Decided by which script has more words, not by the first letter — which is what `dir="auto"`
 * does, and why it gets this panel's sentences wrong: a Persian sentence that opens with a resource
 * name ("shop-db ساخته شد.") would be laid out left to right, its full stop on the wrong side. Words,
 * not letters, because a slug is one long Latin word inside a short Persian sentence ("وصل کردن
 * shop-db به shop"). Inline code does not vote at all. A tie goes to the first word that has letters,
 * the browser's own rule.
 *
 * @param {string | null | undefined} text
 * @returns {'rtl' | 'ltr' | 'auto'}
 */
export function textDirection(text) {
  if (!text) return 'auto';
  let rtl = 0;
  let ltr = 0;
  let first = null;
  // A secret's handle ({{secret:…}}) is Latin letters that say nothing about the sentence around it.
  for (const word of String(text).replace(/`[^`]*`/g, ' ').replace(SECRET_HANDLE, ' ').split(/\s+/)) {
    const kind = RTL_LETTER.test(word) ? 'rtl' : LTR_LETTER.test(word) ? 'ltr' : null;
    if (kind === 'rtl') rtl++;
    else if (kind === 'ltr') ltr++;
    first ??= kind;
  }
  if (rtl === ltr) return first ?? 'auto';
  return rtl > ltr ? 'rtl' : 'ltr';
}

/**
 * The direction of the first letter, "rtl" or "ltr", or `fallback` when there is none yet.
 *
 * For the composer, where textDirection() would be wrong in a different way: recomputed on every
 * keystroke it flips the field — and the caret — the moment a Persian sentence gains one Latin word
 * too many. The first letter typed is the person saying which way they are writing, and it does not
 * change while the rest of the sentence is typed. The fallback is the panel's own direction, so an
 * empty field and its placeholder sit on the side the person reads from.
 *
 * @param {string | null | undefined} text
 * @param {'rtl' | 'ltr' | 'auto'} [fallback]
 * @returns {'rtl' | 'ltr' | 'auto'}
 */
export function firstStrongDirection(text, fallback = 'auto') {
  for (const ch of String(text ?? '').replace(SECRET_HANDLE, ' ')) {
    if (RTL_LETTER.test(ch)) return 'rtl';
    if (LTR_LETTER.test(ch)) return 'ltr';
  }
  return fallback;
}

/**
 * Directions for text that is still being written (a streamed bubble), one per paragraph index, that
 * do not flip as words arrive. Until a paragraph has `latchWords` words with letters it follows its
 * first letter; then it is decided once by textDirection (which script has more words) and held.
 * First letter alone would lay out "shop-db ساخته شد…" left to right for good; counting words on
 * every delta would flip the bubble back and forth while it is read.
 */
export function createSteadyDirection(latchWords = 4) {
  const held = new Map();
  const steady = (text, index) => {
    if (held.has(index)) return held.get(index);
    const words = String(text ?? '').replace(SECRET_HANDLE, ' ').split(/\s+/)
      .filter((w) => RTL_LETTER.test(w) || LTR_LETTER.test(w)).length;
    if (words < latchWords) return firstStrongDirection(text);
    const decided = textDirection(text);
    held.set(index, decided);
    return decided;
  };
  /**
   * The bubble is final (its `message` arrived): a paragraph that was decided keeps that direction —
   * no flip at the very end — and one that never reached `latchWords` words is decided now, by the
   * whole text, rather than staying with its first letter for good.
   */
  steady.settle = (text, index) => {
    if (held.has(index)) return held.get(index);
    const decided = textDirection(text);
    held.set(index, decided);
    return decided;
  };
  return steady;
}

/**
 * A block's prose, for deciding which way it reads: code does not vote. A table is read by its header
 * row — the labels are in the message's own language, where the cells below are often identifiers
 * ("shop-db", "postgres 16") in a Persian table — and by all its cells when the header has no letters.
 * (Each cell still lays its own text out its own way; this decides the order of the columns.)
 *
 * @param {Block} block
 */
export function blockText(block) {
  switch (block.type) {
    case 'pre': return block.text;
    case 'heading': return inlineText(block.nodes);
    case 'table': {
      const head = block.head.map(inlineText).join(' ');
      if (textDirection(head) !== 'auto') return head;
      return [block.head, ...block.rows].map((row) => row.map(inlineText).join(' ')).join('\n');
    }
    default: return (block.type === 'p' ? block.lines : block.items).map(inlineText).join('\n');
  }
}

/**
 * What the copy button under a finished answer takes: the answer as the model wrote it — its `**`,
 * its `|`, its secret handles (`{{secret:…}}`, which are placeholders, not the secrets) — with
 * Windows line endings made plain and the blank lines at either end dropped. Null when there is
 * nothing to copy.
 *
 * @param {string | null | undefined} source
 */
export function messageCopyText(source) {
  const text = String(source ?? '').replace(/\r\n?/g, '\n').trim();
  return text === '' ? null : text;
}

/**
 * What a code block's own copy button takes: the code, verbatim — no fences, no language tag, and
 * not trimmed, since leading whitespace can be the point of a snippet. Null for anything that is
 * not a code block, and for an empty one.
 *
 * @param {Block | null | undefined} block
 */
export function codeCopyText(block) {
  return block?.type === 'pre' && block.text !== '' ? block.text : null;
}

/**
 * The CSS `list-style-type` for a numbered list, in the digits the model wrote it with. A fixed set
 * of names — never text from the message.
 *
 * @param {{ digits?: string }} block
 */
export function listStyleOf(block) {
  return block.digits === 'persian' ? 'persian' : block.digits === 'arabic-indic' ? 'arabic-indic' : 'decimal';
}

/**
 * The href for a result link (AgentResourceLink.url), or null when it should not be followed. Panel
 * paths and http(s) addresses only: a `javascript:` or `data:` URL in a link is script with a label
 * on it, and `//host` or `/\host` leaves the panel while looking like a path.
 *
 * @param {string | null | undefined} url
 */
export function safeHref(url) {
  if (typeof url !== 'string') return null;
  const value = url.trim();
  if (value.startsWith('/')) {
    return value.startsWith('//') || value.startsWith('/\\') ? null : value;
  }
  return /^https?:\/\//i.test(value) ? value : null;
}
