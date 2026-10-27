import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, test } from 'node:test';

import { combineShards, main } from './speckit-verify-shards.mjs';

const temporary = mkdtempSync(path.join(tmpdir(), 'speckit-verify-shards-'));
after(() => rmSync(temporary, { recursive: true, force: true }));
let counter = 0;

function shard(source, number, files) {
  const folder = path.join(source, `speckit-phase-verification-shard-${number}`);
  mkdirSync(folder, { recursive: true });
  for (const [name, content] of Object.entries(files)) writeFileSync(path.join(folder, name), content);
}

test('combines the shard verifications in shard order with every log, the change list, and the flaky tests once', () => {
  const source = path.join(temporary, `source-${counter++}`);
  const target = path.join(temporary, `target-${counter++}`);
  shard(source, 10, { 'verification.log': 'tenth\n', 'outcome.txt': 'success\n', 'checks.txt': '', 'flaky.txt': '' });
  shard(source, 2, {
    'verification.log': 'second log\n', 'outcome.txt': 'failure', 'checks.txt': 'platform build and tests',
    'flaky.txt': 'Ns.A,Ns.B', 'changed-files.txt': 'src/a.cs\n', 'verification-plan.json': '{}',
  });
  shard(source, 1, { 'verification.log': 'first log\n', 'outcome.txt': 'success', 'checks.txt': 'Markdown check', 'flaky.txt': 'Ns.B,Ns.C', 'changed-files.txt': 'src/a.cs\n' });
  mkdirSync(path.join(source, 'unrelated'));
  assert.deepEqual(combineShards(source, target), { shards: 3, flaky: 'Ns.B,Ns.C,Ns.A' });
  assert.equal(readFileSync(path.join(target, 'verification.log'), 'utf8'), [
    '=== Shard 1 (success): Markdown check ===\nfirst log\n',
    '=== Shard 2 (failure): platform build and tests ===\nsecond log\n',
    '=== Shard 10 (success): no check ===\ntenth\n',
  ].join('\n'));
  assert.equal(readFileSync(path.join(target, 'changed-files.txt'), 'utf8'), 'src/a.cs\n');
  assert.equal(readFileSync(path.join(target, 'shards', '2', 'verification-plan.json'), 'utf8'), '{}');
});

test('names and orders the shards by their recorded shard input, not by the artifact name', () => {
  const source = path.join(temporary, `source-${counter++}`);
  const target = path.join(temporary, `target-${counter++}`);
  shard(source, 0, { 'shard.txt': '4/4\n', 'verification.log': 'web\n', 'outcome.txt': 'success', 'checks.txt': 'Web UI build and tests' });
  shard(source, 1, { 'shard.txt': '1/4\n', 'verification.log': 'shared\n', 'outcome.txt': 'success', 'checks.txt': 'Markdown check' });
  combineShards(source, target);
  assert.equal(readFileSync(path.join(target, 'verification.log'), 'utf8'), [
    '=== Shard 1/4 (success): Markdown check ===\nshared\n',
    '=== Shard 4/4 (success): Web UI build and tests ===\nweb\n',
  ].join('\n'));
  assert.equal(readFileSync(path.join(target, 'shards', '4', 'verification.log'), 'utf8'), 'web\n');
});

test('reports no shard when nothing was downloaded, and writes the outputs', () => {
  const output = path.join(temporary, `output-${counter++}`);
  writeFileSync(output, '');
  const lines = [];
  assert.equal(main(['combine', path.join(temporary, 'missing'), path.join(temporary, `target-${counter++}`)], { env: { GITHUB_OUTPUT: output }, log: (line) => lines.push(line) }), 0);
  assert.equal(readFileSync(output, 'utf8'), 'shards=0\nflaky=\n');
  assert.deepEqual(lines, ['Combined 0 shard verification(s).']);
  assert.equal(main(['merge'], { env: {}, log: () => {} }), 2);
});
