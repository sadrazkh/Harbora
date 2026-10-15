// Simple tables: a header row, a `|---|` delimiter row and body rows → `{ type: 'table', head, rows }`
// with every cell a run of inline nodes (assistantInline.js). Part of the formatter
// (assistantFormat.js explains why nothing here may ever become markup): the leaves are plain strings.

import { codeSpans, inline } from './assistantInline.js';

/**
 * A line as table cells, or null when it has no `|` that separates anything. A `|` inside a code span
 * does not separate, and `\|` is a literal pipe wherever it is. A leading and a trailing `|` are
 * decoration, not empty cells.
 *
 * @param {string} line @returns {string[] | null}
 */
export function tableCells(line) {
  const text = line.trim();
  const code = new Uint8Array(text.length);
  for (const [start, end] of codeSpans(text)) code.fill(1, start, end);
  const cells = [];
  let cell = '';
  let separators = 0;
  let endedOnSeparator = false;
  for (let i = 0; i < text.length; i++) {
    endedOnSeparator = false;
    if (text[i] === '\\' && text[i + 1] === '|') { cell += '|'; i++; continue; }
    if (text[i] === '|' && code[i] !== 1) {
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
 * width (`|---|---|`), then rows for as long as the lines have a `|` (and are not a code fence, which
 * `isFence` recognises). A row shorter than the table is padded with empty cells; a longer one widens
 * the table rather than dropping what the model wrote. Without the delimiter row, or with one of
 * another width, it is not a table and stays text.
 *
 * @param {string[]} lines @param {number} at @param {(line: string) => boolean} isFence
 * @returns {{ block: { type: 'table', head: import('./assistantInline.js').InlineNode[][], rows: import('./assistantInline.js').InlineNode[][][] }, next: number } | null}
 */
export function parseTable(lines, at, isFence) {
  const head = tableCells(lines[at]);
  if (!head || head.length === 0 || at + 1 >= lines.length || !isDelimiterRow(lines[at + 1], head.length)) return null;
  const rows = [];
  let next = at + 2;
  for (; next < lines.length && !isFence(lines[next]); next++) {
    const cells = tableCells(lines[next]);
    if (!cells) break;
    rows.push(cells);
  }
  const width = Math.max(head.length, ...rows.map((r) => r.length));
  const row = (cells) => Array.from({ length: width }, (_, i) => inline(cells[i] ?? ''));
  return { block: { type: 'table', head: row(head), rows: rows.map(row) }, next };
}
