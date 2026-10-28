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
  assert.deepEqual(combineShards(source, target), {
    shards: 3, outcome: 'failure', checks: 'Markdown check,platform build and tests', failed: 'platform build and tests',
    uncovered: '', flaky: 'Ns.B,Ns.C,Ns.A', problems: [],
  });
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
  assert.equal(main(['combine', 'a', 'b', '--plan', '{}'], { env: {}, log: () => {} }), 2);
});

const PLANNED = { include: [{ name: 'platform, shared checks', shard: '1/3' }, { name: 'web', shard: '2/3' }, { name: 'manager', shard: '3/3' }] };

function plannedShard(source, index, label, files) {
  shard(source, index, { 'shard.txt': `${label}\n`, 'verification.log': `${label}\n`, ...files });
}

test('passes a planned verification only when every planned shard passed once, with checks, uncovered paths, and flaky tests of all', () => {
  const source = path.join(temporary, `source-${counter++}`);
  plannedShard(source, 0, '1/3', { 'outcome.txt': 'success', 'checks.txt': 'Markdown check,platform build and tests', 'uncovered.txt': 'docs/a.txt', 'flaky.txt': 'Ns.A' });
  plannedShard(source, 1, '2/3', { 'outcome.txt': 'success', 'checks.txt': 'Web UI build and tests', 'uncovered.txt': 'docs/a.txt,docs/b.txt', 'flaky.txt': 'Ns.A,Ns.B' });
  plannedShard(source, 2, '3/3', { 'outcome.txt': 'success', 'checks.txt': 'Analyst Manager build and tests', 'uncovered.txt': '' });
  assert.deepEqual(combineShards(source, path.join(temporary, `target-${counter++}`), { expect: PLANNED }), {
    shards: 3,
    outcome: 'success',
    checks: 'Markdown check,platform build and tests,Web UI build and tests,Analyst Manager build and tests',
    failed: '',
    uncovered: 'docs/a.txt,docs/b.txt',
    flaky: 'Ns.A,Ns.B',
    problems: [],
  });
});

test('names every failed check of a planned verification and every shard whose evidence is missing, repeated, unplanned, or unfinished', () => {
  const source = path.join(temporary, `source-${counter++}`);
  plannedShard(source, 0, '1/3', { 'outcome.txt': 'failure', 'checks.txt': 'Markdown check,platform build and tests' });
  plannedShard(source, 1, '2/3', { 'outcome.txt': 'failure', 'checks.txt': 'Web UI build and tests' });
  plannedShard(source, 5, '2/3', { 'outcome.txt': 'success', 'checks.txt': 'Web UI build and tests' });
  plannedShard(source, 7, '4/3', { 'outcome.txt': 'success', 'checks.txt': 'x' });
  const result = combineShards(source, path.join(temporary, `target-${counter++}`), { expect: PLANNED });
  assert.equal(result.outcome, 'failure');
  assert.equal(result.failed, 'platform build and tests,Web UI build and tests');
  assert.deepEqual(result.problems, [
    'verification shard 2/3 (web) reported 2 times',
    'verification shard 3/3 (manager) did not report',
    'verification shard 4/3 was not planned',
  ]);

  const unfinished = path.join(temporary, `source-${counter++}`);
  plannedShard(unfinished, 0, '1/3', { 'outcome.txt': 'success', 'checks.txt': 'Markdown check' });
  plannedShard(unfinished, 1, '2/3', { 'outcome.txt': 'skipped', 'checks.txt': '' });
  plannedShard(unfinished, 2, '3/3', { 'outcome.txt': 'failure', 'checks.txt': '' });
  const stopped = combineShards(unfinished, path.join(temporary, `target-${counter++}`), { expect: PLANNED });
  assert.equal(stopped.outcome, 'failure');
  assert.equal(stopped.failed, '');
  assert.deepEqual(stopped.problems, ['verification shard 2/3 (web) did not finish (skipped)', 'verification shard 3/3 (manager) did not finish (failure)']);
  assert.deepEqual(combineShards(path.join(temporary, 'none'), path.join(temporary, `target-${counter++}`), { expect: { include: [] } }).problems, ['the verification planned no shard']);
});

test('writes the combined outcome, checks, failures, uncovered paths, and problems only for a planned verification', () => {
  const source = path.join(temporary, `source-${counter++}`);
  plannedShard(source, 0, '1/1', { 'outcome.txt': 'failure', 'checks.txt': 'Markdown check' });
  const output = path.join(temporary, `output-${counter++}`);
  writeFileSync(output, '');
  const matrix = JSON.stringify({ include: [{ name: 'shared checks', shard: '1/1' }, { name: 'web', shard: '2/1' }] });
  assert.equal(main(['combine', source, path.join(temporary, `target-${counter++}`), '--expect', matrix], { env: { GITHUB_OUTPUT: output }, log: () => {} }), 0);
  assert.equal(readFileSync(output, 'utf8'), [
    'shards=1', 'flaky=', 'outcome=failure', 'checks=Markdown check', 'failed=Markdown check', 'uncovered=',
    'problems<<SPECKIT_SHARD_PROBLEMS_END', 'verification shard 2/1 (web) did not report', 'SPECKIT_SHARD_PROBLEMS_END', '',
  ].join('\n'));
  const invalid = path.join(temporary, `output-${counter++}`);
  writeFileSync(invalid, '');
  main(['combine', source, path.join(temporary, `target-${counter++}`), '--expect', 'not json'], { env: { GITHUB_OUTPUT: invalid }, log: () => {} });
  assert.match(readFileSync(invalid, 'utf8'), /outcome=failure\n[\s\S]*the verification planned no shard/);
});
