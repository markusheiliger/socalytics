import assert from 'node:assert/strict';
import test from 'node:test';

import {
  CHANGESET_END,
  CHANGESET_START,
  calculateRunnableFrontier,
  canonicalizeChangeset,
  hashChangeset,
  parseChangesetIssue,
  renderChangesetIssue,
  renderMermaid,
  transitiveDependencies,
  validateChangeset,
} from './openspec-changeset-core.mjs';

const example = {
  version: 1,
  name: 'platform-foundation',
  changes: [
    { ref: 'add-recording-lineage-and-upload', dependsOn: ['add-club-identity-foundation'] },
    { ref: 'add-platform-persistence-foundation', dependsOn: [] },
    { ref: 'add-club-identity-foundation', dependsOn: ['add-platform-persistence-foundation'] },
    { ref: 'add-analyst-manager-registration', dependsOn: ['add-club-identity-foundation'] },
  ],
};

test('normalizes change and dependency ordering', () => {
  const result = validateChangeset(example);
  assert.deepEqual(result.changes.map((change) => change.ref), [
    'add-analyst-manager-registration',
    'add-club-identity-foundation',
    'add-platform-persistence-foundation',
    'add-recording-lineage-and-upload',
  ]);
});

test('round-trips a rendered issue', () => {
  assert.deepEqual(parseChangesetIssue(renderChangesetIssue(example)), validateChangeset(example));
});

test('requires exactly one ordered marker pair', () => {
  assert.throws(() => parseChangesetIssue('missing'), /exactly one/);
  assert.throws(
    () => parseChangesetIssue(`${CHANGESET_END}\n{}\n${CHANGESET_START}`),
    /end marker must follow/,
  );
  assert.throws(
    () => parseChangesetIssue(`${CHANGESET_START}\n{}\n${CHANGESET_END}\n${CHANGESET_START}`),
    /exactly one/,
  );
});

test('reports invalid JSON', () => {
  assert.throws(
    () => parseChangesetIssue(`${CHANGESET_START}\n{nope}\n${CHANGESET_END}`),
    /invalid JSON/,
  );
});

test('rejects unsupported versions and unknown fields', () => {
  assert.throws(() => validateChangeset({ ...example, version: 2 }), /version must be 1/);
  assert.throws(() => validateChangeset({ ...example, extra: true }), /unknown field/);
  assert.throws(
    () => validateChangeset({ ...example, changes: [{ ref: 'one', dependsOn: [], title: 'One' }] }),
    /unknown field/,
  );
});

test('rejects duplicate refs and dependencies', () => {
  assert.throws(
    () => validateChangeset({ ...example, changes: [{ ref: 'one', dependsOn: [] }, { ref: 'one', dependsOn: [] }] }),
    /duplicate change ref/,
  );
  assert.throws(
    () => validateChangeset({ ...example, changes: [{ ref: 'one', dependsOn: ['two', 'two'] }, { ref: 'two', dependsOn: [] }] }),
    /duplicate dependency/,
  );
});

test('rejects missing and self dependencies', () => {
  assert.throws(
    () => validateChangeset({ ...example, changes: [{ ref: 'one', dependsOn: ['missing'] }] }),
    /missing change/,
  );
  assert.throws(
    () => validateChangeset({ ...example, changes: [{ ref: 'one', dependsOn: ['one'] }] }),
    /depend on itself/,
  );
});

test('rejects dependency cycles with their path', () => {
  assert.throws(
    () => validateChangeset({
      ...example,
      changes: [
        { ref: 'one', dependsOn: ['three'] },
        { ref: 'two', dependsOn: ['one'] },
        { ref: 'three', dependsOn: ['two'] },
      ],
    }),
    /one -> three -> two -> one/,
  );
});

test('canonical JSON and hash ignore input ordering', () => {
  const normalized = validateChangeset(example);
  assert.equal(canonicalizeChangeset(example), `${JSON.stringify(normalized, null, 2)}\n`);
  assert.equal(hashChangeset(example), hashChangeset(normalized));
  assert.match(hashChangeset(example), /^[a-f0-9]{64}$/);
});

test('calculates parallel runnable frontiers', () => {
  assert.deepEqual(calculateRunnableFrontier(example), ['add-platform-persistence-foundation']);
  assert.deepEqual(
    calculateRunnableFrontier(example, ['add-platform-persistence-foundation']),
    ['add-club-identity-foundation'],
  );
  assert.deepEqual(
    calculateRunnableFrontier(example, ['add-platform-persistence-foundation', 'add-club-identity-foundation']),
    ['add-analyst-manager-registration', 'add-recording-lineage-and-upload'],
  );
});

test('does not redispatch active changes', () => {
  assert.deepEqual(calculateRunnableFrontier(example, [], ['add-platform-persistence-foundation']), []);
  assert.throws(() => calculateRunnableFrontier(example, ['outside']), /outside the changeset/);
});

test('collects transitive dependencies', () => {
  assert.deepEqual(transitiveDependencies(example, ['add-recording-lineage-and-upload']), [
    'add-club-identity-foundation',
    'add-platform-persistence-foundation',
  ]);
});

test('renders deterministic dependency direction', () => {
  assert.equal(renderMermaid(example), [
    'graph TD',
    '    n0["add-analyst-manager-registration"]',
    '    n1["add-club-identity-foundation"]',
    '    n2["add-platform-persistence-foundation"]',
    '    n3["add-recording-lineage-and-upload"]',
    '    n1 --> n0',
    '    n2 --> n1',
    '    n1 --> n3',
    '',
  ].join('\n'));
});