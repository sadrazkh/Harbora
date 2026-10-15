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

import { SECRET_HANDLE } from './assistantSecrets.js';

/**
 * @typedef {{ type: 'text' | 'code', text: string, bold?: true }} InlineNode
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
const HEADING = /^\s{0,3}(#{1,6})\s+(\S.*?)(?:\s+#+)?\s*$/;
const FENCE = /^\s*```/;

/** `[start, end)` of every `code` span in a line, backticks included. An unmatched backtick is no span. */
function codeSpans(line) {
  const spans = [];
  let at = 0;
  for (;;) {
    const open = line.indexOf('`', at);
    const close = open === -1 ? -1 : line.indexOf('`', open + 1);
    if (open === -1 || close === -1) return spans;
    spans.push([open, close + 1]);
    at = close + 1;
  }
}

// Outside code, what can matter to bold: a run of asterisks or underscores, and a secret's handle. A
// marker is a run of exactly two. A single `*` or `_` is never one (`2*3`, `snake_case_name`, `*.js`),
// and neither is a longer run (`***`, `____`). A handle can contain `__` (it is base64url), so it is cut
// out whole and never scanned.
const PLAIN_TOKEN = new RegExp(`${SECRET_HANDLE.source}|\\*+|_+`, 'g');
const SPACE = /\s/;
const WORD = /[\p{L}\p{N}]/u;

/** @param {string} text @param {Array<{ kind: string, text: string }>} tokens */
function scanPlain(text, tokens) {
  let at = 0;
  for (const match of text.matchAll(PLAIN_TOKEN)) {
    const found = match[0];
    const mark = found === '**' || found === '__';
    if (!mark && !found.startsWith('{')) continue; // a lone or long run: ordinary text
    if (match.index > at) tokens.push({ kind: 'text', text: text.slice(at, match.index) });
    tokens.push({ kind: mark ? 'mark' : 'text', text: found });
    at = match.index + found.length;
  }
  if (at < text.length) tokens.push({ kind: 'text', text: text.slice(at) });
}

/**
 * One line → text and code nodes, with `bold: true` on the runs inside a pair of `**` or `__`.
 * A pair counts only on this one line, outside code, when the opener is followed and the closer
 * preceded by something that is not a space; `__` also must not sit inside a word (`snake__case__name`
 * is a name). Anything left unpaired stays a literal character.
 *
 * @param {string} line @returns {InlineNode[]}
 */
function inline(line) {
  /** @type {Array<{ kind: 'text' | 'code' | 'mark', text: string }>} */
  const tokens = [];
  let at = 0;
  for (const [start, end] of codeSpans(line)) {
    if (start > at) scanPlain(line.slice(at, start), tokens);
    const body = line.slice(start + 1, end - 1);
    if (body) tokens.push({ kind: 'code', text: body });
    at = end;
  }
  if (at < line.length) scanPlain(line.slice(at), tokens);

  // The character on either side of a marker; code counts as a solid, non-word character.
  const edge = (token, last) => (token.kind === 'code' ? '`' : last ? token.text.at(-1) : token.text[0]);
  const before = (i) => (i > 0 ? edge(tokens[i - 1], true) : undefined);
  const after = (i) => (i + 1 < tokens.length ? edge(tokens[i + 1], false) : undefined);
  const solid = (ch) => ch !== undefined && !SPACE.test(ch);
  const inWord = (ch) => ch !== undefined && WORD.test(ch);

  const opener = { '**': -1, __: -1 };
  /** @type {Array<[number, number]>} */
  const pairs = [];
  tokens.forEach((token, i) => {
    if (token.kind !== 'mark') return;
    const mark = token.text;
    const other = mark === '**' ? '__' : '**';
    const canClose = solid(before(i)) && (mark === '**' || !inWord(after(i)));
    const canOpen = solid(after(i)) && (mark === '**' || !inWord(before(i)));
    if (opener[mark] >= 0 && canClose) {
      pairs.push([opener[mark], i]);
      if (opener[other] > opener[mark]) opener[other] = -1; // would cross this pair
      opener[mark] = -1;
    } else if (canOpen) {
      opener[mark] = i;
    }
  });

  const depth = tokens.map(() => 0);
  const paired = new Set();
  for (const [from, to] of pairs) {
    paired.add(from);
    paired.add(to);
    for (let i = from + 1; i < to; i++) depth[i]++;
  }

  /** @type {InlineNode[]} */
  const nodes = [];
  tokens.forEach((token, i) => {
    if (paired.has(i)) return;
    const bold = depth[i] > 0;
    const last = nodes.at(-1);
    if (token.kind === 'code') {
      nodes.push(bold ? { type: 'code', text: token.text, bold: true } : { type: 'code', text: token.text });
    } else if (last && last.type === 'text' && (last.bold === true) === bold) {
      last.text += token.text; // text, a handle, or a marker that found no partner
    } else {
      nodes.push(bold ? { type: 'text', text: token.text, bold: true } : { type: 'text', text: token.text });
    }
  });
  return nodes;
}

/** The prose of a run of nodes — what votes on direction; code does not. @param {InlineNode[]} nodes */
export function inlineText(nodes) {
  return nodes.filter((n) => n.type === 'text').map((n) => n.text).join('');
}

// ---- tables ----------------------------------------------------------------------------------------

/**
 * A line as table cells, or null when it has no `|` that separates anything. A `|` inside a code span
 * does not separate, and `\|` is a literal pipe wherever it is. A leading and a trailing `|` are
 * decoration, not empty cells.
 *
 * @param {string} line @returns {string[] | null}
 */
function tableCells(line) {
  const text = line.trim();
  const code = new Uint8Array(text.length);
  for (const [start, end] of codeSpans(text)) code.fill(1, start, end);
  const inCode = (i) => code[i] === 1;
  const cells = [];
  let cell = '';
  let separators = 0;
  let endedOnSeparator = false;
  for (let i = 0; i < text.length; i++) {
    endedOnSeparator = false;
    if (text[i] === '\\' && text[i + 1] === '|') { cell += '|'; i++; continue; }
    if (text[i] === '|' && !inCode(i)) {
      cells.push(cell);
      cell = '';
      separators++;
      endedOnSeparator = true;
      continue;
    }
    cell += text[i];
  }
  if (separators === 0) return null;
  if (text.startsWith('|')) cells.shift();
  if (!endedOnSeparator) cells.push(cell);
  return cells.map((c) => c.trim());
}

const DELIMITER_CELL = /^:?-+:?$/;

/** @param {string} line @param {number} width */
function isDelimiterRow(line, width) {
  const cells = tableCells(line);
  return cells !== null && cells.length === width && cells.every((c) => DELIMITER_CELL.test(c));
}

/**
 * The table that starts at `lines[at]`, if there is one: a header row, a delimiter row of the same
 * width (`|---|---|`), then rows for as long as the lines have a `|`. A row shorter than the table is
 * padded with empty cells; a longer one widens the table rather than dropping what the model wrote.
 * Without the delimiter row, or with one of another width, it is not a table and stays text.
 *
 * @param {string[]} lines @param {number} at
 * @returns {{ block: Block, next: number } | null}
 */
function parseTable(lines, at) {
  const head = tableCells(lines[at]);
  if (!head || head.length === 0 || at + 1 >= lines.length || !isDelimiterRow(lines[at + 1], head.length)) return null;
  const rows = [];
  let next = at + 2;
  for (; next < lines.length && !FENCE.test(lines[next]); next++) {
    const cells = tableCells(lines[next]);
    if (!cells) break;
    rows.push(cells);
  }
  const width = Math.max(head.length, ...rows.map((r) => r.length));
  const row = (cells) => Array.from({ length: width }, (_, i) => inline(cells[i] ?? ''));
  return { block: { type: 'table', head: row(head), rows: rows.map(row) }, next };
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

    if (FENCE.test(line)) {
      // Everything up to the closing fence (or the end of the message, if the model never closed
      // it) is kept verbatim. The text after the opening ``` is a language tag, not content.
      const body = [];
      i++;
      while (i < lines.length && !FENCE.test(lines[i])) body.push(lines[i++]);
      close();
      blocks.push({ type: 'pre', text: body.join('\n') });
      continue;
    }

    if (line.trim() === '') {
      if (!open || (open.type !== 'ul' && open.type !== 'ol')) close();
      continue;
    }

    const heading = HEADING.exec(line);
    if (heading) {
      close();
      blocks.push({ type: 'heading', level: heading[1].length, nodes: inline(heading[2]) });
      continue;
    }

    const table = parseTable(lines, i);
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
 * A block's prose, for deciding which way it reads: code does not vote, and a table is its cells.
 * @param {Block} block
 */
export function blockText(block) {
  switch (block.type) {
    case 'pre': return block.text;
    case 'heading': return inlineText(block.nodes);
    case 'table': return [block.head, ...block.rows].map((row) => row.map(inlineText).join(' ')).join('\n');
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
