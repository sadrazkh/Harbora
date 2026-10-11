import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { formatMessage, safeHref } from '../islands/assistantFormat.js';

// Model text is untrusted: it can repeat back anything a user, a resource name or a log line put in
// front of the model. The pane formats it into a tree of plain-text nodes that the template renders
// with text interpolation — so HTML in a message is shown as characters, never parsed.

const text = (t) => ({ type: 'text', text: t });
const code = (t) => ({ type: 'code', text: t });

test('HTML in a message stays literal text, in a text node', () => {
  const blocks = formatMessage('<img src=x onerror="alert(1)"> and <script>steal()</script>');
  assert.deepEqual(blocks, [{ type: 'p', lines: [[text('<img src=x onerror="alert(1)"> and <script>steal()</script>')]] }]);
});

test('HTML inside inline code and code blocks is literal too', () => {
  assert.deepEqual(formatMessage('run `<b>x</b>`'), [{ type: 'p', lines: [[text('run '), code('<b>x</b>')]] }]);
  assert.deepEqual(formatMessage('```\n<div onclick=x>\n```'), [{ type: 'pre', text: '<div onclick=x>' }]);
});

test('the formatter only ever produces text-bearing node types', () => {
  const blocks = formatMessage('a\n\n- b `c`\n- <i>d</i>\n\n```html\n<p>e</p>\n```\n&amp; f');
  const allowedBlocks = new Set(['p', 'ul', 'pre']);
  const allowedInline = new Set(['text', 'code']);
  for (const block of blocks) {
    assert.ok(allowedBlocks.has(block.type), block.type);
    for (const line of block.lines ?? block.items ?? []) {
      for (const node of line) {
        assert.ok(allowedInline.has(node.type), node.type);
        assert.equal(typeof node.text, 'string');
        assert.deepEqual(Object.keys(node).sort(), ['text', 'type']);
      }
    }
  }
  // Entities are not decoded either: "&amp;" is what the model wrote, so it is what is shown.
  assert.deepEqual(blocks.at(-1), { type: 'p', lines: [[text('&amp; f')]] });
});

test('the island renders model text with interpolation only — no v-html, no innerHTML', () => {
  const islands = new URL('../islands/', import.meta.url);
  const files = readdirSync(islands).filter((f) => /^Assistant.*\.vue$/.test(f) || /^assistant.*\.(js|ts)$/.test(f));
  // Guards the check below: a pattern that matched no files would pass having checked nothing.
  assert.ok(files.includes('AssistantPanel.vue') && files.includes('AssistantText.vue') && files.length >= 8, files.join(', '));
  for (const file of files) {
    const source = readFileSync(new URL(file, islands), 'utf8');
    assert.equal(/v-html/.test(source), false, `${file}: v-html would parse model text as markup`);
    assert.equal(/\.(innerHTML|outerHTML)\s*=|insertAdjacentHTML/.test(source), false, `${file}: writes HTML`);
  }
});

test('blank lines separate paragraphs and single newlines become line breaks', () => {
  assert.deepEqual(formatMessage('first line\nsecond line\n\nnext paragraph'), [
    { type: 'p', lines: [[text('first line')], [text('second line')]] },
    { type: 'p', lines: [[text('next paragraph')]] },
  ]);
});

test('Windows line endings and surrounding blank lines change nothing', () => {
  assert.deepEqual(formatMessage('\r\n\r\none\r\ntwo\r\n\r\n'), [{ type: 'p', lines: [[text('one')], [text('two')]] }]);
});

test('lines starting with "- " form a list, and the list ends at the first ordinary line', () => {
  assert.deepEqual(formatMessage('I will:\n- create `shop-db`\n- attach it to shop\nThen redeploy.'), [
    { type: 'p', lines: [[text('I will:')]] },
    { type: 'ul', items: [[text('create '), code('shop-db')], [text('attach it to shop')]] },
    { type: 'p', lines: [[text('Then redeploy.')]] },
  ]);
});

test('"* " and "• " bullets are lists too; a dash with no space is just a dash', () => {
  assert.deepEqual(formatMessage('* one\n• دو'), [{ type: 'ul', items: [[text('one')], [text('دو')]] }]);
  assert.deepEqual(formatMessage('-5 degrees'), [{ type: 'p', lines: [[text('-5 degrees')]] }]);
});

test('an unmatched backtick is shown as a backtick', () => {
  assert.deepEqual(formatMessage('a ` b `c` d'), [{ type: 'p', lines: [[text('a '), code(' b '), text('c` d')]] }]);
  assert.deepEqual(formatMessage('just ` one'), [{ type: 'p', lines: [[text('just ` one')]] }]);
});

test('a fenced block keeps its lines verbatim and drops the language tag', () => {
  assert.deepEqual(formatMessage('Set:\n```bash\nexport A=1\n  indented\n```\ndone'), [
    { type: 'p', lines: [[text('Set:')]] },
    { type: 'pre', text: 'export A=1\n  indented' },
    { type: 'p', lines: [[text('done')]] },
  ]);
});

test('an unclosed fence runs to the end of the message rather than swallowing nothing', () => {
  assert.deepEqual(formatMessage('```\nline'), [{ type: 'pre', text: 'line' }]);
});

test('empty and missing text produce no blocks', () => {
  assert.deepEqual(formatMessage(''), []);
  assert.deepEqual(formatMessage(null), []);
  assert.deepEqual(formatMessage(undefined), []);
});

test('result links are followed only to panel paths and http(s) addresses', () => {
  assert.equal(safeHref('/databases/0199d0c4-6a1e-7000-8000-000000000001'), '/databases/0199d0c4-6a1e-7000-8000-000000000001');
  assert.equal(safeHref('https://shop.example.test/'), 'https://shop.example.test/');
  assert.equal(safeHref('http://shop.example.test'), 'http://shop.example.test');
  assert.equal(safeHref('javascript:alert(1)'), null);
  assert.equal(safeHref(' JavaScript:alert(1)'), null);
  assert.equal(safeHref('data:text/html,<b>x</b>'), null);
  assert.equal(safeHref('//evil.example/path'), null, 'protocol-relative leaves the panel');
  assert.equal(safeHref('/\\evil.example'), null, 'browsers read /\\ as //');
  assert.equal(safeHref(''), null);
  assert.equal(safeHref(null), null);
});
