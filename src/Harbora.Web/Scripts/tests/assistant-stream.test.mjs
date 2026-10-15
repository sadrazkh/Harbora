import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createSseParser, readEventStream, parseAssistantFrame, ASSISTANT_EVENTS } from '../islands/assistantStream.js';

// The message endpoint is a POST, so the pane cannot use EventSource and parses the stream itself.
// Everything below is about the ways a hand-written parser goes wrong on a real network: frames cut
// at arbitrary byte offsets, CRLF split between two reads, multi-byte Persian split mid-character.

function collect() {
  const frames = [];
  const parser = createSseParser((frame) => frames.push(frame));
  return { frames, parser };
}

function streamOf(chunks) {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(typeof chunk === 'string' ? encoder.encode(chunk) : chunk);
      controller.close();
    },
  });
}

test('one complete frame is dispatched with its event name and data', () => {
  const { frames, parser } = collect();
  parser.push('event: started\ndata: {"conversationId":"c1"}\n\n');
  assert.deepEqual(frames, [{ event: 'started', data: '{"conversationId":"c1"}', id: '' }]);
});

test('a frame split at every possible offset is reassembled identically', () => {
  const wire = 'event: message\ndata: {"text":"hello"}\n\nevent: done\ndata: {"remainingToday":3}\n\n';
  for (let cut = 1; cut < wire.length; cut++) {
    const { frames, parser } = collect();
    parser.push(wire.slice(0, cut));
    parser.push(wire.slice(cut));
    assert.equal(frames.length, 2, `cut at ${cut}`);
    assert.equal(frames[0].event, 'message');
    assert.equal(frames[0].data, '{"text":"hello"}');
    assert.equal(frames[1].event, 'done');
  }
});

test('CRLF line endings split between two chunks do not produce a phantom blank line', () => {
  const { frames, parser } = collect();
  // If the trailing \r of the first chunk were taken as a line end and the \n that starts the next
  // chunk as a second, empty one, the frame would be dispatched early with only half its data.
  parser.push('event: message\r\ndata: one\r');
  parser.push('\ndata: two\r\n\r\n');
  assert.equal(frames.length, 1);
  assert.equal(frames[0].data, 'one\ntwo');
});

test('lone CR line endings are honoured too', () => {
  const { frames, parser } = collect();
  parser.push('event: done\rdata: {}\r\r');
  assert.equal(frames.length, 1);
  assert.equal(frames[0].event, 'done');
});

test('multi-line data fields are joined with a newline', () => {
  const { frames, parser } = collect();
  parser.push('event: message\ndata: {"text":\ndata: "two lines"}\n\n');
  assert.equal(frames[0].data, '{"text":\n"two lines"}');
  assert.deepEqual(JSON.parse(frames[0].data), { text: 'two lines' });
});

test('only one leading space is stripped from a field value', () => {
  const { frames, parser } = collect();
  parser.push('data:  indented\n\ndata:tight\n\n');
  assert.equal(frames[0].data, ' indented');
  assert.equal(frames[1].data, 'tight');
});

test('comments and frames with no data are not dispatched', () => {
  const { frames, parser } = collect();
  parser.push(': keep-alive\n\nevent: tool\n\nevent: done\ndata: {}\n\n');
  assert.deepEqual(frames.map((f) => f.event), ['done']);
});

test('a frame with no event field is a "message" frame, per the SSE standard', () => {
  const { frames, parser } = collect();
  parser.push('data: x\n\n');
  assert.equal(frames[0].event, 'message');
});

test('a frame cut off by the end of the stream is never dispatched', () => {
  const { frames, parser } = collect();
  // Its JSON may be half of what the server meant to send; believing a truncated "done" would let a
  // dropped connection read as a finished turn.
  parser.push('event: done\ndata: {"remainingToday":');
  parser.end();
  assert.equal(frames.length, 0);
});

test('readEventStream decodes Persian split in the middle of a UTF-8 character', async () => {
  const bytes = new TextEncoder().encode('event: message\ndata: {"text":"سلام دنیا"}\n\n');
  // Split inside the two-byte sequence of the first Persian letter.
  const firstLetter = bytes.indexOf(0xd8);
  assert.ok(firstLetter > 0);
  const frames = [];
  await readEventStream(streamOf([bytes.slice(0, firstLetter + 1), bytes.slice(firstLetter + 1)]), (f) => frames.push(f));
  assert.equal(frames.length, 1);
  assert.equal(JSON.parse(frames[0].data).text, 'سلام دنیا');
});

test('readEventStream delivers frames as they arrive, not only at the end', async () => {
  const seen = [];
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  const encoder = new TextEncoder();
  const body = new ReadableStream({
    async start(controller) {
      controller.enqueue(encoder.encode('event: tool\ndata: {"name":"get_app","state":"running"}\n\n'));
      await gate;
      controller.enqueue(encoder.encode('event: done\ndata: {}\n\n'));
      controller.close();
    },
  });
  const reading = readEventStream(body, (f) => {
    seen.push(f.event);
    if (f.event === 'tool') release();
  });
  await reading;
  assert.deepEqual(seen, ['tool', 'done']);
});

test('a frame handler that throws cancels the body instead of leaving it downloading', async () => {
  const encoder = new TextEncoder();
  let cancelledWith = null;
  let pulls = 0;
  const body = new ReadableStream({
    pull(controller) {
      pulls++;
      controller.enqueue(encoder.encode('event: message\ndata: {}\n\n'));
    },
    cancel(reason) { cancelledWith = reason; },
  });
  const boom = new Error('handler bug');
  await assert.rejects(readEventStream(body, () => { throw boom; }), (err) => err === boom);
  assert.equal(cancelledWith, boom, 'the stream must be cancelled with the error');
  assert.ok(pulls <= 2, `stopped reading (pulled ${pulls} times)`);
});

test('parseAssistantFrame returns the parsed JSON for every event the contract names', () => {
  for (const name of ASSISTANT_EVENTS) {
    const parsed = parseAssistantFrame({ event: name, data: '{"a":1}', id: '' });
    assert.deepEqual(parsed, { name, data: { a: 1 } });
  }
  assert.deepEqual(ASSISTANT_EVENTS, ['started', 'tool', 'delta', 'message', 'plan', 'executed', 'error', 'stopped', 'done']);
});

test('a stopped frame is read as the stopped event, with the server\'s words', () => {
  const { frames, parser } = collect();
  parser.push('event: stopped\ndata: {"messageEn":"Stopped.","messageFa":"متوقف شد."}\n\nevent: done\ndata: {"remainingToday":3}\n\n');
  assert.deepEqual(frames.map(parseAssistantFrame), [
    { name: 'stopped', data: { messageEn: 'Stopped.', messageFa: 'متوقف شد.' } },
    { name: 'done', data: { remainingToday: 3 } },
  ]);
});

test('unknown events are ignored rather than guessed at', () => {
  assert.equal(parseAssistantFrame({ event: 'ping', data: '{}', id: '' }), null);
  assert.equal(parseAssistantFrame({ event: 'Plan', data: '{}', id: '' }), null);
});

test('a known event whose data is not JSON is reported as unreadable, not dropped', () => {
  assert.deepEqual(parseAssistantFrame({ event: 'plan', data: '{not json', id: '' }), { name: 'plan', data: null, unreadable: true });
  assert.deepEqual(parseAssistantFrame({ event: 'done', data: '42', id: '' }), { name: 'done', data: null, unreadable: true });
});
