import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  formatMessage, blockText, inlineText, textDirection, createSteadyDirection, messageCopyText, codeCopyText, listStyleOf,
} from '../islands/assistantFormat.js';

// Round seven P3: bold, numbered lists, headings and tables — still without markup. The tree's leaves
// are plain strings that the template writes with {{ }}; these tests pin what the new blocks look like
// and that nothing a message says can become anything but characters.

const text = (t) => ({ type: 'text', text: t });
const bold = (t) => ({ type: 'text', text: t, bold: true });
const code = (t) => ({ type: 'code', text: t });
const para = (...nodes) => ({ type: 'p', lines: [nodes] });

/** The nodes of the only line of the only paragraph. */
function inlineOf(source) {
  const blocks = formatMessage(source);
  assert.equal(blocks.length, 1, source);
  assert.equal(blocks[0].type, 'p');
  assert.equal(blocks[0].lines.length, 1);
  return blocks[0].lines[0];
}

// ---- bold ---------------------------------------------------------------------------------------------

test('**bold** and __bold__ become bold runs inside a sentence', () => {
  assert.deepEqual(inlineOf('Created **shop-db** for you'), [text('Created '), bold('shop-db'), text(' for you')]);
  assert.deepEqual(inlineOf('Created __shop-db__.'), [text('Created '), bold('shop-db'), text('.')]);
  assert.deepEqual(inlineOf('**one** and **two**'), [bold('one'), text(' and '), bold('two')]);
  assert.deepEqual(inlineOf('**دیتابیس ساخته شد**'), [bold('دیتابیس ساخته شد')]);
});

test('a lone * or _ stays a character: products, snake_case names, file globs', () => {
  for (const line of [
    '2*3 is 6',
    'set snake_case_name to 1',
    'delete *.log and src/*.js',
    'a * b * c',
    'use _private_ and _x',
    'cost: 5 * 4 * 3',
  ]) {
    assert.deepEqual(inlineOf(line), [text(line)], line);
  }
});

test('a pair that does not enclose text on one line is not bold', () => {
  for (const line of [
    'a ** b ** c',      // spaces inside the pair
    '**not closed',
    'not opened**',
    '** x**',           // the opener is followed by a space
    '**x **',           // the closer is preceded by a space
    '2 ** 3 ** 2',
  ]) {
    assert.deepEqual(inlineOf(line), [text(line)], line);
  }
});

test('runs of three or more, and double underscores inside a word, are left alone', () => {
  assert.deepEqual(inlineOf('***x***'), [text('***x***')]);
  assert.deepEqual(inlineOf('fill in ____ here'), [text('fill in ____ here')]);
  assert.deepEqual(inlineOf('snake__case__name'), [text('snake__case__name')]);
  assert.deepEqual(inlineOf('call my__init__x now'), [text('call my__init__x now')]);
});

test('bold never reaches into code, and a marker inside code is only a character', () => {
  assert.deepEqual(inlineOf('`**x**`'), [code('**x**')]);
  assert.deepEqual(inlineOf('run `a**` then b**'), [text('run '), code('a**'), text(' then b**')]);
  assert.deepEqual(inlineOf('`__a__` and __b__'), [code('__a__'), text(' and '), bold('b')]);
});

test('bold may wrap code, and the code is bold too', () => {
  assert.deepEqual(inlineOf('Created **`shop-db`** now'), [text('Created '), { type: 'code', text: 'shop-db', bold: true }, text(' now')]);
  assert.deepEqual(inlineOf('**run `x` first**'), [bold('run '), { type: 'code', text: 'x', bold: true }, bold(' first')]);
});

test('bold stays on its own line', () => {
  assert.deepEqual(formatMessage('**one\ntwo**'), [{ type: 'p', lines: [[text('**one')], [text('two**')]] }]);
});

test('a secret handle is never read for markers, even though it can contain __', () => {
  const handle = '{{secret:AbCdEfGhIjKlMnOpQr__Uv}}';
  assert.deepEqual(inlineOf(`${handle} and __x__`), [text(`${handle} and `), bold('x')]);
  assert.deepEqual(inlineOf(`__${handle}__`), [bold(handle)]);
  assert.deepEqual(inlineOf(`${handle} ${handle}`), [text(`${handle} ${handle}`)]);
});

test('bold works inside list items, headings and table cells', () => {
  assert.deepEqual(formatMessage('- **a** b'), [{ type: 'ul', items: [[bold('a'), text(' b')]] }]);
  assert.deepEqual(formatMessage('## **a** b'), [{ type: 'heading', level: 2, nodes: [bold('a'), text(' b')] }]);
  const [table] = formatMessage('| **a** | b |\n|---|---|\n| **c** | d |');
  assert.deepEqual(table.head[0], [bold('a')]);
  assert.deepEqual(table.rows[0][0], [bold('c')]);
});

// ---- numbered lists -----------------------------------------------------------------------------------

test('numbered lists, with . or ), in their own numbering', () => {
  assert.deepEqual(formatMessage('1. create the database\n2. attach it\n3) redeploy'), [
    { type: 'ol', start: 1, digits: 'latin', items: [[text('create the database')], [text('attach it')], [text('redeploy')]] },
  ]);
  assert.deepEqual(formatMessage('3. third\n4. fourth'), [
    { type: 'ol', start: 3, digits: 'latin', items: [[text('third')], [text('fourth')]] },
  ]);
});

test('Persian and Arabic-Indic digits make numbered lists too, and keep their digits', () => {
  assert.deepEqual(formatMessage('۱. ساخت دیتابیس\n۲. وصل کردن'), [
    { type: 'ol', start: 1, digits: 'persian', items: [[text('ساخت دیتابیس')], [text('وصل کردن')]] },
  ]);
  assert.deepEqual(formatMessage('۱۲) الف'), [{ type: 'ol', start: 12, digits: 'persian', items: [[text('الف')]] }]);
  assert.deepEqual(formatMessage('٣. ج'), [{ type: 'ol', start: 3, digits: 'arabic-indic', items: [[text('ج')]] }]);
});

test('numbered and bulleted lists next to each other stay two lists', () => {
  assert.deepEqual(formatMessage('1. a\n2. b\n- c\n- d'), [
    { type: 'ol', start: 1, digits: 'latin', items: [[text('a')], [text('b')]] },
    { type: 'ul', items: [[text('c')], [text('d')]] },
  ]);
});

test('a blank line between items does not split a list; a paragraph does', () => {
  assert.deepEqual(formatMessage('1. a\n\n2. b'), [
    { type: 'ol', start: 1, digits: 'latin', items: [[text('a')], [text('b')]] },
  ]);
  assert.deepEqual(formatMessage('- a\n\n- b'), [{ type: 'ul', items: [[text('a')], [text('b')]] }]);
  assert.deepEqual(formatMessage('1. a\n\ntext\n\n2. b'), [
    { type: 'ol', start: 1, digits: 'latin', items: [[text('a')]] },
    para(text('text')),
    { type: 'ol', start: 2, digits: 'latin', items: [[text('b')]] },
  ]);
});

test('a code block between steps ends the list, and the next one carries on from its own number', () => {
  const blocks = formatMessage('1. run:\n```bash\nnpm ci\n```\n2. then:\n```\nnpm test\n```\n3. done');
  assert.deepEqual(blocks.map((b) => b.type), ['ol', 'pre', 'ol', 'pre', 'ol']);
  assert.deepEqual(blocks.filter((b) => b.type === 'ol').map((b) => b.start), [1, 2, 3]);
});

test('what only looks like a list item is not one', () => {
  assert.deepEqual(formatMessage('3.5 GB of memory'), [para(text('3.5 GB of memory'))]);
  assert.deepEqual(formatMessage('1234567890. too long a number'), [para(text('1234567890. too long a number'))]);
  assert.deepEqual(formatMessage('1.no space'), [para(text('1.no space'))]);
  assert.deepEqual(formatMessage('see step 2. it is next'), [para(text('see step 2. it is next'))]);
});

test('the list style follows the digits the model used, from a fixed set of names', () => {
  assert.equal(listStyleOf(formatMessage('1. a')[0]), 'decimal');
  assert.equal(listStyleOf(formatMessage('۱. الف')[0]), 'persian');
  assert.equal(listStyleOf(formatMessage('١. أ')[0]), 'arabic-indic');
  assert.equal(listStyleOf({ digits: 'url(javascript:x)' }), 'decimal', 'anything else is plain decimal');
});

// ---- headings -----------------------------------------------------------------------------------------

test('#, ## and ### at the start of a line are headings, with their level', () => {
  assert.deepEqual(formatMessage('# One\n## Two\n### Three\n#### Four'), [
    { type: 'heading', level: 1, nodes: [text('One')] },
    { type: 'heading', level: 2, nodes: [text('Two')] },
    { type: 'heading', level: 3, nodes: [text('Three')] },
    { type: 'heading', level: 4, nodes: [text('Four')] },
  ]);
});

test('a heading interrupts a paragraph and may close itself with #s; the text keeps its own #', () => {
  assert.deepEqual(formatMessage('before\n## Plan ##\nafter'), [
    para(text('before')),
    { type: 'heading', level: 2, nodes: [text('Plan')] },
    para(text('after')),
  ]);
  assert.deepEqual(formatMessage('### C#'), [{ type: 'heading', level: 3, nodes: [text('C#')] }]);
  assert.deepEqual(formatMessage('## `shop-db` ساخته شد'), [{ type: 'heading', level: 2, nodes: [code('shop-db'), text(' ساخته شد')] }]);
});

test('a # that is not followed by a space, or has nothing after it, is text', () => {
  for (const line of ['#hashtag', '#', '#   ', '####### seven', 'issue #12', '    ## indented four spaces']) {
    assert.deepEqual(formatMessage(line), [para(text(line))], line);
  }
});

test('a # line inside a code block is code', () => {
  assert.deepEqual(formatMessage('```\n# comment\n```'), [{ type: 'pre', text: '# comment' }]);
});

// ---- tables -------------------------------------------------------------------------------------------

const cell = (t) => [text(t)];

test('a header row, a separator and body rows make a table', () => {
  assert.deepEqual(formatMessage('| Name | Size |\n|------|------|\n| shop-db | small |\n| old-db | large |'), [{
    type: 'table',
    head: [cell('Name'), cell('Size')],
    rows: [[cell('shop-db'), cell('small')], [cell('old-db'), cell('large')]],
  }]);
});

test('the outer pipes and the alignment colons are optional decoration', () => {
  assert.deepEqual(formatMessage('a | b\n:--- | ---:\nc | d'), [{
    type: 'table', head: [cell('a'), cell('b')], rows: [[cell('c'), cell('d')]],
  }]);
  assert.deepEqual(formatMessage('| a |\n|:-:|\n| b |'), [{ type: 'table', head: [cell('a')], rows: [[cell('b')]] }]);
});

test('ragged rows are padded, and a longer row widens the table instead of losing a cell', () => {
  const [table] = formatMessage('| a | b |\n|---|---|\n| 1 |\n| 2 | 3 | 4 |');
  assert.deepEqual(table.head, [cell('a'), cell('b'), []]);
  assert.deepEqual(table.rows, [[cell('1'), [], []], [cell('2'), cell('3'), cell('4')]]);
  assert.ok([table.head, ...table.rows].every((row) => row.length === 3), 'every row is as wide as the widest');
});

test('a header with a separator and no rows is still a table', () => {
  assert.deepEqual(formatMessage('| a | b |\n|---|---|'), [{ type: 'table', head: [cell('a'), cell('b')], rows: [] }]);
});

test('a table ends at a blank line or a line without a pipe', () => {
  const blocks = formatMessage('| a |\n|---|\n| 1 |\n\n| 2 |\n\n| b |\n|---|\n| 3 |\nafter the table');
  assert.deepEqual(blocks.map((b) => b.type), ['table', 'p', 'table', 'p']);
  assert.deepEqual(blocks[0].rows, [[cell('1')]]);
  assert.deepEqual(blocks[2].rows, [[cell('3')]]);
});

test('a malformed table stays text', () => {
  // no separator
  assert.deepEqual(formatMessage('| a | b |\n| 1 | 2 |'), [{ type: 'p', lines: [[text('| a | b |')], [text('| 1 | 2 |')]] }]);
  // a separator of another width
  assert.deepEqual(formatMessage('| a | b |\n|---|\n| 1 | 2 |').map((b) => b.type), ['p']);
  // a separator that is not one
  assert.deepEqual(formatMessage('| a | b |\n| x | y |\n| 1 | 2 |').map((b) => b.type), ['p']);
  // a dash line with no pipe is not a delimiter row
  assert.deepEqual(formatMessage('a | b\n---').map((b) => b.type), ['p']);
  // a pipe in prose
  assert.deepEqual(formatMessage('use a | b to choose'), [para(text('use a | b to choose'))]);
  // nothing in the table at all
  assert.deepEqual(formatMessage('|\n|').map((b) => b.type), ['p']);
});

test('a pipe inside code is not a column break, and \\| is a literal pipe', () => {
  const [table] = formatMessage(String.raw`| cmd | note |
|---|---|
| ` + '`a | b`' + String.raw` | x\|y |`);
  assert.deepEqual(table.rows[0], [[code('a | b')], cell('x|y')]);
});

test('cells are inline nodes, and text in a cell votes for its own direction', () => {
  const [table] = formatMessage('| نام | Size |\n|---|---|\n| `shop-db` را بساز | small |');
  assert.deepEqual(table.rows[0][0], [code('shop-db'), text(' را بساز')]);
  assert.equal(textDirection(inlineText(table.head[0])), 'rtl');
  assert.equal(textDirection(inlineText(table.head[1])), 'ltr');
  assert.equal(textDirection(inlineText(table.rows[0][0])), 'rtl', 'code does not vote');
});

test('table-looking lines inside a code block stay code', () => {
  assert.deepEqual(formatMessage('```\n| a | b |\n|---|---|\n```'), [{ type: 'pre', text: '| a | b |\n|---|---|' }]);
});

test('a table between paragraphs and lists', () => {
  const blocks = formatMessage('Result:\n| a | b |\n|---|---|\n| 1 | 2 |\n- next');
  assert.deepEqual(blocks.map((b) => b.type), ['p', 'table', 'ul']);
});

// ---- direction and copying ----------------------------------------------------------------------------

test('every block\'s prose is available for direction, and code does not vote', () => {
  const [heading, ol, table] = formatMessage('## عنوان `x`\n1. اول\n2. دوم\n\n| الف | ب |\n|---|---|\n| ج | د |');
  assert.equal(blockText(heading), 'عنوان ');
  assert.equal(blockText(ol), 'اول\nدوم');
  assert.equal(blockText(table), 'الف ب', 'a table reads by its header row');
});

test('a Persian table of identifiers reads right to left, by its labels; a header without letters falls back to the cells', () => {
  const [persian] = formatMessage('| نام | نوع | وضعیت |\n|---|---|---|\n| shop-db | postgres 16 | running |\n| old-db | postgres 15 | stopped |');
  assert.equal(textDirection(blockText(persian)), 'rtl', 'the body has more Latin words than the header has Persian ones');
  const [english] = formatMessage('| Name | نوع |\n|---|---|\n| shop-db | پایگاه |');
  assert.equal(textDirection(blockText(english)), 'ltr');
  const [numbers] = formatMessage('| 1 | 2 |\n|---|---|\n| سه | چهار |');
  assert.equal(blockText(numbers), '1 2\nسه چهار');
  assert.equal(textDirection(blockText(numbers)), 'rtl');
});

test('while a message is being written, no block flips side once it has been decided', () => {
  const full = [
    '## وضعیت استقرار برنامه', '',
    '1. دیتابیس shop-db ساخته شد و آماده است', '2. به shop وصل شد', '',
    '| نام | نوع | وضعیت |', '|---|---|---|', '| shop-db | postgres 16 | running |', '',
    'در پایان همه چیز آماده است و کار تمام شد.',
  ].join('\n');
  const dir = createSteadyDirection();
  const seen = new Map();
  for (let end = 1; end <= full.length; end++) {
    formatMessage(full.slice(0, end)).forEach((block, index) => {
      const words = blockText(block).split(/\s+/).filter(Boolean).length;
      const now = dir(blockText(block), index);
      // Once a block has four words it is decided, and the answer it gave then is the one it keeps.
      if (words >= 4) {
        if (seen.has(index)) assert.equal(now, seen.get(index), `block ${index} flipped at ${end} characters`);
        seen.set(index, now);
      }
    });
  }
  assert.ok(seen.size >= 3, 'the heading, the list and the closing paragraph were all decided');
  assert.equal([...seen.values()].every((d) => d === 'rtl'), true);
});

test('the copy button under an answer takes the answer as written; secret handles stay handles', () => {
  const handle = '{{secret:AbCdEfGhIjKlMnOpQrStUv}}';
  const answer = `**Done**\r\n\r\n1. set DB_PASSWORD to ${handle}\r\n`;
  assert.equal(messageCopyText(answer), `**Done**\n\n1. set DB_PASSWORD to ${handle}`);
  assert.equal(messageCopyText('  \n '), null);
  assert.equal(messageCopyText(null), null);
  assert.equal(messageCopyText(undefined), null);
});

test('a code block copies its code exactly: no fences, no language tag, indentation kept', () => {
  const blocks = formatMessage('```bash\n  export A=1\n\nnpm ci\n```');
  assert.equal(codeCopyText(blocks[0]), '  export A=1\n\nnpm ci');
  assert.equal(codeCopyText(formatMessage('plain')[0]), null, 'only a code block has a code copy');
  assert.equal(codeCopyText(formatMessage('```\n```')[0]), null, 'an empty block has nothing to copy');
  assert.equal(codeCopyText(null), null);
});

// ---- nothing becomes markup ---------------------------------------------------------------------------

const KEYS = {
  text: ['bold', 'text', 'type'],
  code: ['bold', 'text', 'type'],
};

/** Every leaf of the tree must be a plain string in a known place; returns all the leaf strings. */
function leavesOf(blocks) {
  const leaves = [];
  const inline = (nodes) => {
    assert.ok(Array.isArray(nodes));
    for (const node of nodes) {
      assert.ok(node.type === 'text' || node.type === 'code', node.type);
      assert.equal(typeof node.text, 'string');
      assert.ok(Object.keys(node).every((k) => KEYS[node.type].includes(k)), JSON.stringify(node));
      if ('bold' in node) assert.equal(node.bold, true);
      leaves.push(node.text);
    }
  };
  for (const block of blocks) {
    switch (block.type) {
      case 'p': assert.deepEqual(Object.keys(block).sort(), ['lines', 'type']); block.lines.forEach(inline); break;
      case 'ul': assert.deepEqual(Object.keys(block).sort(), ['items', 'type']); block.items.forEach(inline); break;
      case 'ol':
        assert.deepEqual(Object.keys(block).sort(), ['digits', 'items', 'start', 'type']);
        assert.ok(['latin', 'persian', 'arabic-indic'].includes(block.digits));
        assert.ok(Number.isSafeInteger(block.start) && block.start >= 0);
        block.items.forEach(inline);
        break;
      case 'heading':
        assert.deepEqual(Object.keys(block).sort(), ['level', 'nodes', 'type']);
        assert.ok([1, 2, 3, 4, 5, 6].includes(block.level));
        inline(block.nodes);
        break;
      case 'table':
        assert.deepEqual(Object.keys(block).sort(), ['head', 'rows', 'type']);
        inline(block.head.flat());
        block.head.forEach(inline);
        for (const row of block.rows) { assert.equal(row.length, block.head.length); row.forEach(inline); }
        break;
      case 'pre': assert.deepEqual(Object.keys(block).sort(), ['text', 'type']); assert.equal(typeof block.text, 'string'); leaves.push(block.text); break;
      default: assert.fail(`unknown block type ${block.type}`);
    }
  }
  return leaves;
}

test('hostile text in every kind of block comes out as strings and only as strings', () => {
  const evil = [
    '<script>steal()</script>',
    '<img src=x onerror="alert(1)">',
    '[click](javascript:alert(1)) and ![x](https://evil.example/a.png)',
    '<a href="javascript:x">y</a>',
    '&lt;b&gt; &#x3C;i&#x3E;',
  ];
  const source = [
    ...evil,
    ...evil.map((e) => `**${e}**`),
    ...evil.map((e) => `# ${e}`),
    ...evil.map((e) => `1. ${e}`),
    ...evil.map((e) => `- ${e}`),
    `| ${evil[0]} | ${evil[1]} |`, '|---|---|', `| **${evil[2]}** | ${evil[3]} |`,
    '```html', ...evil, '```',
  ].join('\n');
  const blocks = formatMessage(source);
  const all = leavesOf(blocks).join('\n');
  for (const piece of evil) assert.ok(all.includes(piece), `kept as written: ${piece}`);
});

test('links stay as the characters the model wrote', () => {
  assert.deepEqual(inlineOf('[docs](https://example.test/a) and <https://example.test>'), [text('[docs](https://example.test/a) and <https://example.test>')]);
});

test('random markup soup never throws, and only ever yields characters the message had', () => {
  const alphabet = ['*', '*', '_', '_', '`', '|', '|', '-', '-', ':', '#', '#', ' ', ' ', ' ', '\n', '\n', '1', '2', '.', ')', '<', '>', 'a', 'b', 'ب', '۱', '\\', '{', '}', '!', '[', ']', '(', '/'];
  let seed = 20261015;
  const next = () => { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 2 ** 32; };
  for (let n = 0; n < 3000; n++) {
    const length = 1 + Math.floor(next() * 90);
    let source = '';
    for (let i = 0; i < length; i++) source += alphabet[Math.floor(next() * alphabet.length)];
    let blocks;
    assert.doesNotThrow(() => { blocks = formatMessage(source); }, JSON.stringify(source));
    const had = new Set(source);
    for (const leaf of leavesOf(blocks).join('')) assert.ok(had.has(leaf), `${JSON.stringify(leaf)} was not in ${JSON.stringify(source)}`);
  }
});

test('very long lines of markers and pipes are formatted without blowing up', () => {
  const started = Date.now();
  formatMessage('`|'.repeat(20000));
  formatMessage(`${'|a'.repeat(20000)}\n${'|-'.repeat(20000)}\n${'|b'.repeat(20000)}`);
  formatMessage('**a '.repeat(20000));
  formatMessage('__a**b '.repeat(10000));
  assert.ok(Date.now() - started < 3000, 'formatting is linear in the text, not quadratic');
});
