const assert = require('node:assert/strict');
const { test } = require('node:test');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

const load = () => import(pathToFileURL(path.resolve(__dirname, '../ui/timer-presets.js')));

// Ported from WidgetTests.cs: the same lengths and lines the C# parser reads.
test('durations are read as typed', async () => {
  const { parseDuration } = await load();
  for (const [text, seconds] of [
    ['12', 720], ['1.5', 90], [' 1:30 ', 90], ['1:02:03', 3723], ['90s', 90], ['45m', 2700], ['1h20m', 4800],
    ['1 hr 20 min', 4800], ['2 Hours', 7200], ['1m30s', 90], ['24h', 86400],
  ]) {
    assert.equal(parseDuration(text), seconds, text);
  }
});

test('durations that make no sense are rejected', async () => {
  const { parseDuration } = await load();
  for (const text of [null, '', 'soon', '0', '-5', '1h 20', '1:2:3:4', '1:xx', '25h', '0.001s']) {
    assert.equal(parseDuration(text), null, String(text));
  }
});

test('durations are described in a form that reads back', async () => {
  const { describeDuration, parseDuration } = await load();
  for (const [seconds, text] of [[300, '5m'], [5400, '1h30m'], [90, '1m30s'], [45, '45s'], [3600, '1h']]) {
    assert.equal(describeDuration(seconds), text);
    assert.equal(parseDuration(text), seconds);
  }
});

test('preset lines have an optional name', async () => {
  const { parseTimerPreset, timerPresetLine } = await load();
  for (const [line, name, seconds] of [
    ['15m', '', 900], ['Tea 3m', 'Tea', 180], ['Long walk 1h 20m', 'Long walk', 4800],
    ['Round 2 1:30', 'Round 2', 90], ['A very long preset name 10m', 'A very lon', 600],
  ]) {
    const preset = parseTimerPreset(line);
    assert.deepEqual(preset, { name, seconds }, line);
    assert.deepEqual(parseTimerPreset(timerPresetLine(preset)), preset);
  }
});

test('preset lines without a length are rejected', async () => {
  const { parseTimerPreset, readTimerPresets } = await load();
  for (const line of ['Tea', 'Tea soon', '']) {
    assert.equal(parseTimerPreset(line), null, line);
  }
  assert.deepEqual(readTimerPresets('5m\n\nTea soon\n1h'), { bad: 'Tea soon' });
  assert.deepEqual(readTimerPresets('1m\n2m\n3m\n4m\n5m\n6m\nnot read'), { lines: ['1m', '2m', '3m', '4m', '5m', '6m'] });
});
