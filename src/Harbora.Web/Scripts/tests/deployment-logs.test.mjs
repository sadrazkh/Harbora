import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import vm from 'node:vm';

function mount() {
  const source = readFileSync(new URL('../islands/DeploymentLogs.vue', import.meta.url), 'utf8')
    .split('<script setup lang="ts">')[1].split('</script>')[0].replace(/^import .*;$/gm, '');
  let dispose;
  const scheduled = [];
  const context = vm.createContext({
    ref: value => ({ value }), computed: fn => ({ get value() { return fn(); } }),
    defineProps: () => ({ deploymentId: 'test', initialStatus: 'Building' }),
    document: { documentElement: { lang: 'en' } },
    window: { dispatchEvent() {} }, CustomEvent: class {},
    onMounted() {}, onUnmounted: fn => { dispose = fn; }, nextTick: async () => {},
    AbortController, setTimeout: (fn, delay) => { scheduled.push({ fn, delay }); return scheduled.length; }, clearTimeout() {},
    fetch: async () => ({ ok: true, json: async () => ({ status: 'Building', lines: [] }) }),
  });
  vm.runInContext(stripTypeScriptTypes(source), context);
  return { context, scheduled, dispose: () => dispose(), run: code => vm.runInContext(code, context) };
}
test('backfill reconciles repeated live messages without removing legitimate repetitions', async () => {
  const h = mount();
  h.run("pending.value = [{ seq: -1, stream: 'Build', message: 'same' }, { seq: -1, stream: 'Build', message: 'same' }]");
  h.context.fetch = async () => ({ ok: true, json: async () => ({ status: 'Building', lines: [{ seq: 0, stream: 'Build', message: 'same' }] }) });
  await h.run('backfill()');
  assert.equal(h.run('lines.value.length'), 1);
  assert.equal(h.run('pending.value.length'), 1);
  await h.run('backfill()');
  assert.equal(h.run('allLines.value.length'), 2);
});
test('failed initial request preserves logs and schedules recovery', async () => {
  const h = mount();
  h.context.fetch = async () => { throw new Error('offline'); };
  await h.run('backfill()');
  assert.ok(h.run('error.value'));
  assert.equal(h.scheduled.at(-1).delay, 5000);
  h.context.fetch = async () => ({ ok: true, json: async () => ({ status: 'Building', lines: [{ seq: 1, stream: 'Build', message: 'recovered' }] }) });
  await h.run('backfill()');
  assert.equal(h.run('error.value'), '');
  assert.equal(h.run('lines.value[0].message'), 'recovered');
});
test('unmount prevents further polling', async () => {
  const h = mount();
  h.dispose();
  await h.run('backfill()');
  assert.equal(h.scheduled.length, 0);
});
test('completed deployments receive bounded trailing-log checks', async () => {
  const h = mount();
  h.context.fetch = async () => ({ ok: true, json: async () => ({ status: 'Succeeded', lines: [] }) });
  for (let i = 0; i < 4; i++) await h.run('backfill()');
  assert.equal(h.scheduled.length, 3);
});
