// Light formatting for the assistant's messages: paragraphs, line breaks, `inline code`, fenced code
// blocks and simple "-" lists — and nothing that could become markup.
//
// Model text is untrusted. It can repeat whatever a user, a resource name or a log line put in front
// of the model, so it is never handed to the DOM as HTML. Instead it is cut into a small tree whose
// leaves are plain strings; AssistantPanel.vue renders the leaves with `{{ }}` interpolation, which
// Vue writes as text nodes. "<script>" in a message is therefore shown as eight characters. There is
// no escaping step to forget, because nothing here produces HTML in the first place.

/**
 * @typedef {{ type: 'text' | 'code', text: string }} InlineNode
 * @typedef {{ type: 'p', lines: InlineNode[][] } | { type: 'ul', items: InlineNode[][] } | { type: 'pre', text: string }} Block
 */

const BULLET = /^\s*[-*•]\s+(.*)$/;
const FENCE = /^\s*```/;

/** @param {string} line @returns {InlineNode[]} */
function inline(line) {
  /** @type {InlineNode[]} */
  const nodes = [];
  let rest = line;
  for (;;) {
    const open = rest.indexOf('`');
    const close = open === -1 ? -1 : rest.indexOf('`', open + 1);
    // An unmatched backtick is just a backtick: everything from here on is text.
    if (open === -1 || close === -1) {
      if (rest) nodes.push({ type: 'text', text: rest });
      return nodes;
    }
    if (open > 0) nodes.push({ type: 'text', text: rest.slice(0, open) });
    const body = rest.slice(open + 1, close);
    if (body) nodes.push({ type: 'code', text: body });
    rest = rest.slice(close + 1);
  }
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

  const close = () => { open = null; };

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

    if (line.trim() === '') { close(); continue; }

    const bullet = BULLET.exec(line);
    if (bullet) {
      if (!open || open.type !== 'ul') { open = { type: 'ul', items: [] }; blocks.push(open); }
      open.items.push(inline(bullet[1]));
      continue;
    }

    if (!open || open.type !== 'p') { open = { type: 'p', lines: [] }; blocks.push(open); }
    open.lines.push(inline(line));
  }

  return blocks;
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
