import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createUploadLimit, isTooBig } from '../uploadLimit.js';

// Plain fakes, not a DOM: the core only needs a form's dataset and listeners, a file input's
// `files`, an error element's text and `hidden` class, and buttons' `disabled`.
function listening(extra = {}) {
  const listeners = {};
  return {
    ...extra,
    addEventListener: (type, fn) => { (listeners[type] ??= []).push(fn); },
    fire: (type, event = {}) => (listeners[type] ?? []).forEach((fn) => fn(event)),
  };
}

function rig({ maxBytes = '2048', size } = {}) {
  const form = listening({ dataset: { maxBytes, tooBig: 'This file is larger than 2 KB.' } });
  const input = listening({ files: size === undefined ? [] : [{ size }] });
  const hidden = new Set(['hidden']);
  const error = {
    textContent: '',
    classList: { toggle: (cls, force) => (force ? hidden.add(cls) : hidden.delete(cls)) },
    isHidden: () => hidden.has('hidden'),
  };
  const button = { disabled: false };
  createUploadLimit({ form, input, error, buttons: [button] });
  return { form, input, error, button };
}

test('a file over the ceiling is said and its submit is stopped before anything is sent', () => {
  const { form, input, error, button } = rig({ size: 4096 });

  input.fire('change');
  assert.equal(error.textContent, 'This file is larger than 2 KB.');
  assert.equal(error.isHidden(), false);
  assert.equal(button.disabled, true);

  let prevented = false;
  form.fire('submit', { preventDefault: () => { prevented = true; } });
  assert.equal(prevented, true);
});

test('a file within the ceiling, exactly at it included, goes', () => {
  const { form, input, error, button } = rig({ size: 2048 });

  input.fire('change');
  assert.equal(error.textContent, '');
  assert.equal(error.isHidden(), true);
  assert.equal(button.disabled, false);

  let prevented = false;
  form.fire('submit', { preventDefault: () => { prevented = true; } });
  assert.equal(prevented, false);
});

test('choosing a smaller file after a too big one clears the refusal', () => {
  const { input, error, button } = rig({ size: 4096 });
  input.fire('change');

  input.files = [{ size: 10 }];
  input.fire('change');

  assert.equal(error.isHidden(), true);
  assert.equal(button.disabled, false);
});

test('no file, or a form with no usable ceiling, is never refused here', () => {
  assert.equal(isTooBig(undefined, 2048), false);
  assert.equal(isTooBig({ size: 10 ** 12 }, Number('')), false);
  assert.equal(isTooBig({ size: 10 ** 12 }, Number.NaN), false);
  assert.equal(isTooBig({ size: 3 }, 2), true);
});
