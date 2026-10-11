// The assistant's message stream: `POST /assistant/conversations/{id}/messages` answers with
// `text/event-stream` (AgentViews.cs, AgentStreamEvents). It is a POST, so EventSource — GET only —
// cannot read it; the pane reads the body itself and this file is the parser.
//
// Plain JS rather than TypeScript so `node --test` can import it as it is, with no build step,
// exactly like mobileSidebar.js. The parser follows the WHATWG event-stream rules, because the
// network decides where a read ends: a frame, a line, a CRLF pair and a UTF-8 character can each
// arrive split across two reads, and every one of those splits has to come out the same as no split.

/** The event names the server sends, from `AgentStreamEvents`. Anything else is ignored. */
export const ASSISTANT_EVENTS = ['started', 'tool', 'delta', 'message', 'plan', 'executed', 'error', 'done'];

/**
 * @typedef {{ event: string, data: string, id: string }} SseFrame
 */

/**
 * An incremental event-stream parser. `push` takes decoded text in whatever pieces the network
 * produced; `onFrame` is called once per complete frame.
 *
 * @param {(frame: SseFrame) => void} onFrame
 */
export function createSseParser(onFrame) {
  let buffer = '';
  // A chunk that ended on '\r' may be the first half of a CRLF; the '\n' that opens the next chunk
  // then belongs to that same line ending and must not be read as an empty line of its own — which
  // would dispatch the frame early, with half its data.
  let skipLeadingLf = false;

  let eventName = '';
  let dataLines = [];
  let lastId = '';

  function dispatch() {
    // Per the standard, a frame with no data field is not an event (a bare "event: x" followed by a
    // blank line, or a comment block).
    if (dataLines.length > 0) {
      onFrame({ event: eventName || 'message', data: dataLines.join('\n'), id: lastId });
    }
    eventName = '';
    dataLines = [];
  }

  function line(text) {
    if (text === '') { dispatch(); return; }
    if (text.startsWith(':')) return; // comment / keep-alive

    const colon = text.indexOf(':');
    const field = colon === -1 ? text : text.slice(0, colon);
    let value = colon === -1 ? '' : text.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1); // exactly one space, not all of them

    if (field === 'event') eventName = value;
    else if (field === 'data') dataLines.push(value);
    else if (field === 'id' && !value.includes('\0')) lastId = value;
    // `retry` and unknown fields mean nothing to a POST body that is read once.
  }

  return {
    /** @param {string} text */
    push(text) {
      if (text === '') return; // keeps a pending CR's state for the chunk that actually follows
      if (skipLeadingLf && text.startsWith('\n')) text = text.slice(1);
      skipLeadingLf = false;
      buffer += text;

      let start = 0;
      for (let i = 0; i < buffer.length; i++) {
        const ch = buffer[i];
        if (ch !== '\n' && ch !== '\r') continue;
        line(buffer.slice(start, i));
        if (ch === '\r') {
          if (i + 1 < buffer.length) {
            if (buffer[i + 1] === '\n') i++;
          } else {
            skipLeadingLf = true;
          }
        }
        start = i + 1;
      }
      buffer = buffer.slice(start);
    },

    /**
     * The stream is over. A frame still being assembled is discarded, as the standard says: it was
     * never terminated, so its data may be half of what the server meant — and a truncated `done`
     * believed as a real one would let a dropped connection read as a finished turn.
     */
    end() {
      buffer = '';
      eventName = '';
      dataLines = [];
    },
  };
}

/**
 * Reads a fetch body to the end, calling `onFrame` for each frame as soon as it is complete —
 * a tool's "running" line has to appear while the tool runs, not when the turn is over.
 *
 * @param {ReadableStream<Uint8Array>} body
 * @param {(frame: SseFrame) => void} onFrame
 */
export async function readEventStream(body, onFrame) {
  const parser = createSseParser(onFrame);
  // `stream: true` holds back the first byte of a split multi-byte character until the rest arrives;
  // without it a Persian letter cut between two reads would decode as two replacement characters.
  const decoder = new TextDecoder('utf-8');
  const reader = body.getReader();
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      if (value) parser.push(decoder.decode(value, { stream: true }));
    }
    parser.push(decoder.decode());
  } catch (error) {
    // Cancelled, not merely released: a released reader leaves the body — and the request under it —
    // running, so a pane that gave up on a turn would go on downloading it. Cancelling closes the
    // connection, which is also how the server learns nobody is listening.
    try { await reader.cancel(error); } catch { /* already errored or closed */ }
    throw error;
  } finally {
    parser.end();
    try { reader.releaseLock(); } catch { /* cancel may already have released it */ }
  }
}

/**
 * Turns a raw frame into one of the contract's events.
 *
 * Returns null for an event name the contract does not define (a newer server, a proxy's
 * keep-alive): ignored rather than guessed at. A known event whose data is not a JSON object comes
 * back marked `unreadable` so the pane can say so — silently dropping a `plan` would leave the user
 * waiting for a card that already came and went.
 *
 * @param {SseFrame} frame
 * @returns {{ name: string, data: any, unreadable?: true } | null}
 */
export function parseAssistantFrame(frame) {
  if (!ASSISTANT_EVENTS.includes(frame.event)) return null;
  try {
    const data = JSON.parse(frame.data);
    if (data === null || typeof data !== 'object' || Array.isArray(data)) {
      return { name: frame.event, data: null, unreadable: true };
    }
    return { name: frame.event, data };
  } catch {
    return { name: frame.event, data: null, unreadable: true };
  }
}
