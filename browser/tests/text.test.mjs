import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import test from 'node:test';
import { ENGLISH, localized, t, translatePage } from '../chromium/text.mjs';

const chromium = new URL('../chromium/', import.meta.url);
const read = async path => readFile(new URL(path, chromium), 'utf8');
const locales = (await readdir(new URL('_locales/', chromium))).sort();
const messages = Object.fromEntries(await Promise.all(locales.map(async locale =>
  [locale, JSON.parse(await read(`_locales/${locale}/messages.json`))])));
const placeholders = text => [...text.matchAll(/\$[1-9]/g)].map(match => match[0]).sort();

test('the extension carries the languages the UI ships that Chrome knows', () => {
  // Chrome names Tagalog fil and has one Serbian, in Cyrillic; it has no Cebuano.
  assert.deepEqual(locales, ['de', 'en', 'es', 'fil', 'it', 'sr']);
});

test('the English messages are the fallback table and every language has each of them', () => {
  assert.deepEqual(Object.fromEntries(Object.entries(messages.en).map(([key, value]) => [key, value.message])), ENGLISH);
  for (const locale of locales) {
    assert.deepEqual(Object.keys(messages[locale]).sort(), Object.keys(ENGLISH).sort(), locale);
    for (const [key, { message }] of Object.entries(messages[locale])) {
      assert.ok(message.trim(), `${locale} ${key} is empty`);
      assert.deepEqual(placeholders(message), placeholders(ENGLISH[key]), `${locale} ${key} keeps its values`);
      assert.doesNotMatch(message, /[—“”‘’]/, `${locale} ${key} has an em dash or curly quote`);
    }
  }
});

test('the manifest, pages and modules name only messages that exist', async () => {
  const manifest = JSON.parse(await read('manifest.json'));
  assert.equal(manifest.default_locale, 'en');
  const used = [...JSON.stringify(manifest).matchAll(/__MSG_(\w+)__/g)].map(match => match[1]);
  assert.deepEqual(used.sort(), ['actionTitle', 'commandOpen', 'extensionDescription', 'extensionName']);
  for (const page of ['popup.html', 'devices.html'])
    for (const [, key] of (await read(page)).matchAll(/data-i18n="(\w+)"/g)) assert.ok(key in ENGLISH, `${page} ${key}`);
  for (const module of ['popup.mjs', 'devices.mjs', 'worker.mjs', 'routing.mjs'])
    for (const [, key] of (await read(module)).matchAll(/\bt\('(\w+)'/g)) assert.ok(key in ENGLISH, `${module} ${key}`);
});

test('text falls back to English and an English error from the audio document is translated', () => {
  assert.equal(t('tooManyCaptures', 8), 'At most 8 tabs can be routed at once.');
  assert.equal(localized('At most 8 tabs can be routed at once.'), 'At most 8 tabs can be routed at once.');
  const italian = Object.fromEntries(Object.entries(messages.it).map(([key, value]) => [key, value.message]));
  globalThis.chrome = { i18n: {
    getMessage: (key, values) => italian[key]?.replace(/\$([1-9])/g, (_, n) => values[n - 1]) ?? '',
    getUILanguage: () => 'it' } };
  try {
    assert.equal(localized('At most 8 tabs can be routed at once.'), 'Si possono instradare al massimo 8 schede alla volta.');
    assert.equal(localized('The tab supplied no audio track.'), italian.noAudioTrack);
    assert.equal(localized('NotAllowedError: Permission denied'), 'NotAllowedError: Permission denied');
    assert.equal(localized(''), '');
    const elements = [{ dataset: { i18n: 'popupStart' }, textContent: 'Activate this tab' }];
    const document = { documentElement: { lang: 'en' }, querySelectorAll: () => elements };
    translatePage(document);
    assert.equal(elements[0].textContent, 'Attiva questa scheda');
    assert.equal(document.documentElement.lang, 'it');
  } finally { delete globalThis.chrome; }
});
