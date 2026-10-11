import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  SECRET_HANDLE, isHandle, splitSecrets, looksLikeSecret, createSecretBook, createSecretForm, withoutSecrets,
} from '../islands/assistantSecrets.js';
import { createAssistantApi } from '../islands/assistantApi.js';
import { createAssistantSession } from '../islands/assistantSession.js';
import { createTimeline } from '../islands/assistantTimeline.js';
import { createAssistantPrefs } from '../assistantDrawer.js';

// Secrets as black boxes (u-brief S, owner decision 2026-10-12): the person gives a secret through the
// lock button, the panel holds it, and the message — and so the model — only ever carries its handle,
// `{{secret:<22 url-safe chars>}}` (AgentSecretContracts.cs). The value is in one place only: the form,
// until it is posted.

const HANDLE = '{{secret:AbCdEfGhIjKlMnOpQrStUv}}';
const HANDLE2 = '{{secret:0123456789abcdefghij-_}}';
const VALUE = 'Sup3r-S3cret-Passw0rd!';

function fakeApi({ fail = null } = {}) {
  const calls = [];
  return {
    calls,
    async holdSecret(body) {
      calls.push(body);
      if (fail) throw fail;
      return { handle: calls.length === 1 ? HANDLE : HANDLE2, name: body.name, expiresAt: '2026-10-12T10:30:00Z' };
    },
  };
}

test('the handle pattern is the contract\'s: {{secret: + 22 URL-safe characters + }}', () => {
  assert.equal(isHandle(HANDLE), true);
  assert.equal(isHandle(` ${HANDLE2} `), true);
  assert.equal(isHandle('{{secret:short}}'), false);
  assert.equal(isHandle('{{secret:AbCdEfGhIjKlMnOpQrStU!}}'), false);
  assert.equal(isHandle(`x ${HANDLE}`), false, 'the whole value, not a part of it');
  assert.ok(SECRET_HANDLE.global);
});

test('a message is split into text and handles, in order', () => {
  assert.deepEqual(splitSecrets(`set DB_PASSWORD to ${HANDLE} and TOKEN to ${HANDLE2}.`), [
    { type: 'text', text: 'set DB_PASSWORD to ' }, { type: 'secret', handle: HANDLE },
    { type: 'text', text: ' and TOKEN to ' }, { type: 'secret', handle: HANDLE2 }, { type: 'text', text: '.' },
  ]);
  assert.deepEqual(splitSecrets('no secrets here'), [{ type: 'text', text: 'no secrets here' }]);
  assert.deepEqual(splitSecrets(''), []);
  // Called repeatedly: a global regex keeps lastIndex between calls unless it is reset.
  assert.equal(splitSecrets(HANDLE).length, 1);
  assert.equal(splitSecrets(HANDLE).length, 1);
});

test('the warning notices what looks like a secret typed into the message', () => {
  for (const text of [
    'password=hunter22', 'DB_PASSWORD: s3cr3t!', 'my token: abc123def', 'api_key=xyz', 'API-KEY: 123',
    'use sk-proj-abcdefghijklmnopqrstuvwx1234', 'ghp_abcdefghijklmnopqrstuvwxyz0123456789',
    'github_pat_11ABCDEFG0123456789_abcdefghij', 'AKIAIOSFODNN7EXAMPLE', '-----BEGIN OPENSSH PRIVATE KEY-----',
    'رمز: 12345678', 'گذرواژه = abc', 'رمز عبور: qwerty',
    'رمز دیتابیس: hunter22 را تنظیم کن', 'the password for shop-db: hunter22',
  ]) assert.equal(looksLikeSecret(text), true, text);
});

test('the warning stays quiet for ordinary requests, and for handles', () => {
  for (const text of [
    'reset the password for shop-db', 'what is my token limit?', 'create an api key page', 'رمز دیتابیس را عوض کن',
    `set DB_PASSWORD to ${HANDLE}`, `password=${HANDLE}`, 'sk-short', '',
  ]) assert.equal(looksLikeSecret(text), false, text);
});

test('a held secret goes into the draft as a placeholder naming it, at the cursor', () => {
  const book = createSecretBook();
  const placeholder = book.add({ handle: HANDLE, name: 'DB_PASSWORD' });
  assert.equal(placeholder, '[🔒 DB_PASSWORD]');
  const { draft, cursor } = book.insert('set it to  please', 10, placeholder);
  assert.equal(draft, 'set it to [🔒 DB_PASSWORD] please');
  assert.equal(cursor, 10 + placeholder.length);
});

test('two secrets with the same name get distinct placeholders', () => {
  const book = createSecretBook();
  assert.equal(book.add({ handle: HANDLE, name: 'TOKEN' }), '[🔒 TOKEN]');
  assert.equal(book.add({ handle: HANDLE2, name: 'TOKEN' }), '[🔒 TOKEN 2]');
});

test('on send, placeholders become their handles; anything else is left as typed', () => {
  const book = createSecretBook();
  book.add({ handle: HANDLE, name: 'DB_PASSWORD' });
  assert.equal(book.expand('set DB_PASSWORD to [🔒 DB_PASSWORD]'), `set DB_PASSWORD to ${HANDLE}`);
  // A placeholder the person half-deleted, or typed themselves, is not a secret and stays text.
  assert.equal(book.expand('[🔒 DB_PASSWOR] and [🔒 NOPE]'), '[🔒 DB_PASSWOR] and [🔒 NOPE]');
});

test('words given back to the composer show placeholders again, and the name is known for chips', () => {
  const book = createSecretBook();
  book.add({ handle: HANDLE, name: 'DB_PASSWORD' });
  assert.equal(book.collapse(`set to ${HANDLE} and ${HANDLE2}`), `set to [🔒 DB_PASSWORD] and ${HANDLE2}`);
  assert.equal(book.nameOf(HANDLE), 'DB_PASSWORD');
  assert.equal(book.nameOf(HANDLE2), null);
});

test('submitting the form posts the value once, then forgets it; the draft only ever gets the placeholder', async () => {
  const api = fakeApi();
  const book = createSecretBook();
  const form = createSecretForm({ api, book });
  form.open();
  form.state.name = 'DB_PASSWORD';
  form.state.value = VALUE;
  form.state.reveal = true;
  const placeholder = await form.submit();
  assert.deepEqual(api.calls, [{ name: 'DB_PASSWORD', value: VALUE }]);
  assert.equal(placeholder, '[🔒 DB_PASSWORD]');
  assert.equal(placeholder.includes(VALUE), false);
  assert.deepEqual({ ...form.state }, { open: false, name: '', value: '', reveal: false, busy: false, error: null });
  assert.equal(book.expand(placeholder), HANDLE);
  assert.equal(JSON.stringify(book).includes(VALUE), false, 'the book holds handles and names, never values');
});

test('closing the form forgets what was typed', () => {
  const form = createSecretForm({ api: fakeApi(), book: createSecretBook() });
  form.open();
  form.state.name = 'X';
  form.state.value = VALUE;
  form.close();
  assert.equal(form.state.value, '');
  assert.equal(form.state.name, '');
  assert.equal(form.state.open, false);
});

test('an empty or oversized name or value is refused before anything is posted', async () => {
  const api = fakeApi();
  const form = createSecretForm({ api, book: createSecretBook() });
  form.open();
  form.state.name = '';
  form.state.value = VALUE;
  assert.equal(await form.submit(), null);
  assert.match(form.state.error.messageEn, /name/i);
  form.state.name = 'X'.repeat(101);
  assert.equal(await form.submit(), null);
  form.state.name = 'OK';
  form.state.value = '';
  assert.equal(await form.submit(), null);
  form.state.value = 'v'.repeat(8001);
  assert.equal(await form.submit(), null);
  assert.equal(api.calls.length, 0);
});

test('a refused post keeps the form open with the server\'s words, and still holds the value for a retry', async () => {
  const api = fakeApi({ fail: Object.assign(new Error('x'), { status: 400, messageEn: 'The name is too long.', messageFa: 'نام بیش از حد بلند است.' }) });
  const form = createSecretForm({ api, book: createSecretBook() });
  form.open();
  form.state.name = 'DB';
  form.state.value = VALUE;
  assert.equal(await form.submit(), null);
  assert.equal(form.state.open, true);
  assert.equal(form.state.error.messageEn, 'The name is too long.');
  assert.equal(form.state.busy, false);
  assert.equal(form.state.value, VALUE, 'kept only in the open form, so the person can try again');
});

// ---- end to end inside the pane: form → draft → send ------------------------------------------------


test('holdSecret posts { name, value } to /assistant/secrets with the token and returns the view', async () => {
  const calls = [];
  const view = { handle: HANDLE, name: 'DB_PASSWORD', expiresAt: '2026-10-12T10:30:00Z' };
  const api = createAssistantApi({
    csrf: 'tok',
    fetch: async (url, init) => { calls.push({ url, init }); return new Response(JSON.stringify(view), { headers: { 'content-type': 'application/json' } }); },
  });
  assert.deepEqual(await api.holdSecret({ name: 'DB_PASSWORD', value: VALUE }), view);
  assert.equal(calls[0].url, '/assistant/secrets');
  assert.equal(calls[0].init.method, 'POST');
  assert.equal(calls[0].init.headers['X-CSRF-TOKEN'], 'tok');
  assert.deepEqual(JSON.parse(calls[0].init.body), { name: 'DB_PASSWORD', value: VALUE });
});

test('the value never reaches the draft, the message, the outbox or storage — only the handle travels', async () => {
  const data = {};
  const storage = { getItem: (k) => (k in data ? data[k] : null), setItem: (k, v) => { data[k] = String(v); }, removeItem: (k) => { delete data[k]; } };
  const sent = [];
  const outboxDuringSend = [];
  const prefs = createAssistantPrefs(storage);
  const api = {
    ...fakeApi(),
    async createConversation() { return { id: '0199d0c4-6a1e-7000-8000-0000000000c1' }; },
    async send(args) {
      sent.push(args.text);
      outboxDuringSend.push(JSON.stringify(data));
      args.onEvent('started', { conversationId: args.conversationId });
      args.onEvent('done', { conversationId: args.conversationId, remainingToday: 1 });
      return { completed: true };
    },
  };
  const book = createSecretBook();
  const form = createSecretForm({ api, book });
  const timeline = createTimeline();
  const session = createAssistantSession({ api, prefs, timeline, now: () => 0 });

  form.open();
  form.state.name = 'DB_PASSWORD';
  form.state.value = VALUE;
  const placeholder = await form.submit();
  const { draft } = book.insert('set DB_PASSWORD on shop to ', 27, placeholder);
  assert.equal(draft.includes(VALUE), false);

  await session.send(book.expand(draft));
  assert.deepEqual(sent, [`set DB_PASSWORD on shop to ${HANDLE}`]);
  assert.equal(outboxDuringSend[0].includes(HANDLE), true, 'the outbox kept the handle while the message was on its way');
  for (const where of [sent.join(), outboxDuringSend.join(), JSON.stringify(data), JSON.stringify(timeline.items), draft]) {
    assert.equal(where.includes(VALUE), false);
  }
  assert.equal(form.state.value, '');
});

test('a placeholder in the draft does not set off the warning: it is checked as the handle it stands for', () => {
  const book = createSecretBook();
  const placeholder = book.add({ handle: HANDLE, name: 'DB_PASSWORD' });
  const draft = `DB_PASSWORD=${placeholder}`;
  assert.equal(looksLikeSecret(draft), false, 'read raw, the placeholder is not a value');
  assert.equal(looksLikeSecret(book.expand(draft)), false, 'read as it will be sent, it is a handle');
});

test('the safe form stays quiet even with words after the placeholder or the handle', () => {
  const book = createSecretBook();
  const placeholder = book.add({ handle: HANDLE, name: 'DB_PASSWORD' });
  for (const draft of [
    `password for shop-db: ${placeholder} please`,
    `password for shop-db: ${placeholder} and restart it`,
    `رمز دیتابیس: ${placeholder} را تنظیم کن`,
    `token = ${placeholder}, thanks`,
  ]) {
    assert.equal(looksLikeSecret(draft), false, draft);
    assert.equal(looksLikeSecret(book.expand(draft)), false, book.expand(draft));
  }
  // A real value after the key still counts, even with a placeholder elsewhere in the draft.
  assert.equal(looksLikeSecret(`set ${placeholder} and password: hunter22`), true);
});

test('a name with a bracket is refused, since it would end its placeholder early', async () => {
  const api = fakeApi();
  const form = createSecretForm({ api, book: createSecretBook() });
  form.open();
  form.state.name = 'DB]PASS';
  form.state.value = VALUE;
  assert.equal(await form.submit(), null);
  assert.match(form.state.error.messageEn, /\[ or \]/);
  assert.equal(api.calls.length, 0);
});

test('secrets with related names each expand to their own handle', () => {
  const book = createSecretBook();
  book.add({ handle: HANDLE, name: 'A' });
  book.add({ handle: HANDLE2, name: 'A 2' });
  assert.equal(book.expand('[🔒 A] [🔒 A 2]'), `${HANDLE} ${HANDLE2}`);
});

test('cancelling while the post is in flight inserts nothing and leaves a reopened form alone', async () => {
  let release;
  const api = { calls: [], holdSecret(body) { this.calls.push(body); return new Promise((resolve) => { release = resolve; }); } };
  const book = createSecretBook();
  const form = createSecretForm({ api, book });
  form.open();
  form.state.name = 'DB';
  form.state.value = VALUE;
  const posting = form.submit();
  form.close();
  form.open();
  form.state.name = 'TYPING_AGAIN';
  release({ handle: HANDLE, name: 'DB' });
  assert.equal(await posting, null, 'no placeholder after a cancel');
  assert.equal(book.nameOf(HANDLE), null);
  assert.equal(form.state.open, true);
  assert.equal(form.state.name, 'TYPING_AGAIN', 'the reopened form is not wiped');
});

test('the text without its secrets, for direction: placeholders and handles drop out', () => {
  assert.equal(withoutSecrets(`[🔒 DB_PASSWORD] را تنظیم کن`).trim(), 'را تنظیم کن');
  assert.equal(withoutSecrets(`${HANDLE} است`).trim(), 'است');
});

test('the lock form is not a login form: no password field, no form submit, password managers told to stay out', () => {
  // Chrome and Edge offered to save "DB_PASSWORD" + the secret as a Harbora login once the form went
  // away after a successful post — and then filled it into the real sign-in page. A text field masked
  // by CSS, outside any <form>, with autocomplete off and the managers' opt-outs, is not offered.
  const source = readFileSync(new URL('../islands/AssistantPanel.vue', import.meta.url), 'utf8');
  const start = source.indexOf('data-assistant-secret-form');
  const end = source.indexOf('<!-- Composer -->', start);
  assert.ok(start > 0 && end > start, 'the lock form is found in the island');
  const form = source.slice(source.lastIndexOf('<', start), end);
  assert.equal(/type="password"/.test(form) || /'password'/.test(form), false, 'no password-type field');
  assert.equal(/<form\b/.test(form), false, 'not a <form>: Enter is handled by the island');
  assert.equal(/new-password|current-password/.test(form), false);
  const inputs = form.match(/<input\b[^>]*>/g) ?? [];
  assert.equal(inputs.length, 2);
  for (const input of inputs) {
    for (const attr of ['autocomplete="off"', 'data-1p-ignore', 'data-lpignore="true"', 'data-bwignore', 'data-form-type="other"']) {
      assert.ok(input.includes(attr), `${attr} on ${input.slice(0, 60)}…`);
    }
    assert.ok(input.includes('type="text"'), 'a text field');
  }
  assert.ok(/assistant-secret-mask/.test(form), 'masked with CSS');
  // The class alone hides nothing: deleting its rule would leave the secret readable with the markup
  // assertions above still green.
  const css = readFileSync(new URL('../app.css', import.meta.url), 'utf8');
  assert.match(css, /\.assistant-secret-mask\s*\{[^}]*text-security:\s*disc/, 'app.css masks the field');
});

test('a draft that opens with a placeholder takes the direction of the words around it', async () => {
  const { firstStrongDirection } = await import('../islands/assistantFormat.js');
  assert.equal(firstStrongDirection('[🔒 DB_PASSWORD] را تنظیم کن', 'rtl'), 'ltr', 'raw, the name decides — wrongly');
  assert.equal(firstStrongDirection(withoutSecrets('[🔒 DB_PASSWORD] را تنظیم کن'), 'rtl'), 'rtl');
  assert.equal(firstStrongDirection(withoutSecrets('[🔒 DB_PASSWORD]'), 'rtl'), 'rtl', 'only a placeholder: the panel\'s side');
});
