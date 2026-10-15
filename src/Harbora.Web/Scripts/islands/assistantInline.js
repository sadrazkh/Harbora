// One line of a message → inline nodes: text, `code`, and either of them bold. Part of the formatter
// (assistantFormat.js explains why nothing here may ever become markup): every node is
// `{ type, text }` with a plain string, plus `bold: true` on a run inside a pair of `**`.

/**
 * @typedef {{ type: 'text' | 'code', text: string, bold?: true }} InlineNode
 */

/** `[start, end)` of every `code` span in a line, backticks included. An unmatched backtick is no span. */
export function codeSpans(line) {
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

// Bold is `**` and nothing else. `__bold__` is not supported on purpose: dunder names (`__init__.py`,
// `__main__`) are everyday words in this product's chats. A marker is a run of exactly two asterisks;
// a single `*` is never one (`2*3`, `*.js`) and neither is a longer run (`***`).
const RUN = /\*+/g;
const SPACE = /\s/;
// What may stand in front of an opening `**` besides a space or the start of the line.
const OPENING_PUNCTUATION = new Set(['(', '[', '{', '«', '"', "'", '“', '‘']);
// What a closing `**` may not directly follow: the same, less the straight quotes (they open and close).
const OPENING_BRACKETS = new Set(['(', '[', '{', '«', '“', '‘']);

/** @param {string} text @param {Array<{ kind: string, text: string }>} tokens */
function scanPlain(text, tokens) {
  let at = 0;
  for (const match of text.matchAll(RUN)) {
    if (match[0] !== '**') continue; // a lone or long run: ordinary text
    if (match.index > at) tokens.push({ kind: 'text', text: text.slice(at, match.index) });
    tokens.push({ kind: 'mark', text: '**' });
    at = match.index + 2;
  }
  if (at < text.length) tokens.push({ kind: 'text', text: text.slice(at) });
}

/**
 * One line → text and code nodes, with `bold: true` on the runs inside a pair of `**`.
 *
 * The pair counts only on this one line and outside code, and by flanking rules that keep globs and
 * arithmetic literal:
 *  - an opening `**` stands at the start of the line, or after a space or an opening bracket or quote
 *    (`( [ { « " ' “ ‘`), and is followed by something that is neither a space nor a `/`;
 *  - a closing `**` is preceded by something that is neither a space, a `/`, nor an opening bracket
 *    (`( [ { « “ ‘` — not the straight quotes, since `**"x"**` closes), and is not followed by a `/` or
 *    a `*`. The bracket clause keeps Python's `f(**kw1) and g(**kw2)` from pairing up.
 * So `ignore **\/node_modules\/** please`, `see **\/bin\/** and **\/obj\/**` and `src/**\/a and lib/**\/b` are
 * text, while `**اپ**ها` (a Persian plural written straight on), `**اپ**‌ها` (with the half-space),
 * `**a**=1` and `**متن**،` are bold.
 * Anything left unpaired stays a literal character. A code span is never scanned, but a pair may wrap
 * one (`**\`shop-db\`**` is bold code).
 *
 * @param {string} line @returns {InlineNode[]}
 */
export function inline(line) {
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

  // The character on either side of a marker; code counts as a backtick (solid, and not a slash).
  const edge = (token, last) => (token.kind === 'code' ? '`' : last ? token.text.at(-1) : token.text[0]);
  const before = (i) => (i > 0 ? edge(tokens[i - 1], true) : undefined);
  const after = (i) => (i + 1 < tokens.length ? edge(tokens[i + 1], false) : undefined);
  const solid = (ch) => ch !== undefined && !SPACE.test(ch);

  let opener = -1;
  /** @type {Array<[number, number]>} */
  const pairs = [];
  tokens.forEach((token, i) => {
    if (token.kind !== 'mark') return;
    const prev = before(i);
    const next = after(i);
    const canOpen = solid(next) && next !== '/'
      && (prev === undefined || SPACE.test(prev) || OPENING_PUNCTUATION.has(prev));
    const canClose = solid(prev) && prev !== '/' && !OPENING_BRACKETS.has(prev) && next !== '/' && next !== '*';
    if (opener >= 0 && canClose) {
      pairs.push([opener, i]);
      opener = -1;
    } else if (canOpen) {
      opener = i;
    }
  });

  const bold = tokens.map(() => false);
  const paired = new Set();
  for (const [from, to] of pairs) {
    paired.add(from);
    paired.add(to);
    for (let i = from + 1; i < to; i++) bold[i] = true;
  }

  /** @type {InlineNode[]} */
  const nodes = [];
  tokens.forEach((token, i) => {
    if (paired.has(i)) return;
    const last = nodes.at(-1);
    if (token.kind === 'code') {
      nodes.push(bold[i] ? { type: 'code', text: token.text, bold: true } : { type: 'code', text: token.text });
    } else if (last && last.type === 'text' && (last.bold === true) === bold[i]) {
      last.text += token.text; // text, or a marker that found no partner
    } else {
      nodes.push(bold[i] ? { type: 'text', text: token.text, bold: true } : { type: 'text', text: token.text });
    }
  });
  return nodes;
}

/** The prose of a run of nodes — what votes on direction; code does not. @param {InlineNode[]} nodes */
export function inlineText(nodes) {
  return nodes.filter((n) => n.type === 'text').map((n) => n.text).join('');
}
