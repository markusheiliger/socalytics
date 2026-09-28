import assert from 'node:assert/strict';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';

import {
  compareDependencyInventory,
  inventoryActiveChanges,
  prepareDependencyReconciliation,
  reconcileDependencyState,
} from './openspec-change-reconciliation.mjs';
import {
  JSON_CONTRACTS,
  serializeDependencyCheckpoint,
} from './openspec-change-core.mjs';

const testRoot = path.join(
  process.cwd(),
  '.github',
  'scripts',
  '.reconciliation-test-repositories',
);

async function writeChange(root, ref, content, delta = null) {
  const directory = path.join(root, 'openspec', 'changes', ref);
  await mkdir(directory, { recursive: true });
  await writeFile(path.join(directory, 'proposal.md'), content);
  if (delta !== null) {
    const specDirectory = path.join(directory, 'specs', 'capability');
    await mkdir(specDirectory, { recursive: true });
    await writeFile(path.join(specDirectory, 'spec.md'), delta);
  }
}

test('digests authoritative artifacts and detects added, modified, and archived refs', async (t) => {
  const root = path.join(testRoot, 'digest');
  t.after(() => rm(root, { recursive: true, force: true }));
  await rm(root, { recursive: true, force: true });
  await writeChange(root, 'one', 'before\n', 'delta before\n');
  const before = await inventoryActiveChanges({ root });
  await writeChange(root, 'one', 'after\n', 'delta before\n');
  await writeChange(root, 'two', 'new\n');
  const after = await inventoryActiveChanges({ root });
  assert.notEqual(before[0].digest, after.find(({ ref }) => ref === 'one').digest);
  assert.deepEqual(compareDependencyInventory(after, {
    changes: [
      { ...before[0], summary: 'One' },
      { ref: 'old', digest: 'f'.repeat(64), summary: 'Old' },
    ],
  }), {
    added: ['two'],
    modified: ['one'],
    archived: ['old'],
  });
  assert.deepEqual(
    after.find(({ ref }) => ref === 'one').artifacts,
    [
      'openspec/changes/one/proposal.md',
      'openspec/changes/one/specs/capability/spec.md',
    ],
  );
});

test('prepare uses the exact prior note, chooses incremental, and dry-run is read-only', async (t) => {
  const root = path.join(testRoot, 'prepare');
  const contextPath = path.join(root, 'context.json');
  t.after(() => rm(root, { recursive: true, force: true }));
  await rm(root, { recursive: true, force: true });
  await writeChange(root, 'one', 'before\n');
  const previousInventory = await inventoryActiveChanges({ root });
  await writeChange(root, 'one', 'after\n');
  await writeChange(root, 'two', 'new\n');
  const target = 'b'.repeat(40);
  const base = 'a'.repeat(40);
  const notesTip = 'c'.repeat(40);
  const checkpoint = {
    $schema: JSON_CONTRACTS.dependencyCheckpoint,
    commit: base,
    changes: [
      { ref: 'old', digest: 'f'.repeat(64), summary: 'Archived change' },
      {
        ref: 'one',
        digest: previousInventory[0].digest,
        summary: 'Cached one',
      },
    ],
    managedEdges: [{
      changeRef: 'one',
      dependsOn: 'old',
      confidence: 0.95,
      evidence: ['old dependency'],
    }],
    inference: {
      mode: 'full',
      evaluatedRefs: ['old', 'one'],
      baseCommit: null,
      minimumConfidence: 0.85,
    },
  };
  const calls = [];
  const git = async (...args) => {
    calls.push(args);
    if (args[0] === 'rev-parse' && args[1] === 'HEAD^{commit}') return target;
    if (args[0] === 'rev-parse') return notesTip;
    if (args[0] === 'rev-list') return `${target}\n${base}`;
    if (args[0] === 'notes' && args.at(-1) === target) {
      throw Object.assign(new Error('missing'), { stderr: 'no note found' });
    }
    if (args[0] === 'notes' && args.at(-1) === base) {
      return serializeDependencyCheckpoint(checkpoint);
    }
    throw new Error(`Unexpected git call: ${args.join(' ')}`);
  };
  const result = await prepareDependencyReconciliation({
    root,
    contextPath,
    dryRun: true,
    git,
  });
  assert.equal(result.context.mode, 'incremental');
  assert.deepEqual(result.context.changes, {
    added: ['two'],
    modified: ['one'],
    archived: ['old'],
  });
  assert.deepEqual(result.context.evaluatedRefs, ['one', 'two']);
  await assert.rejects(readFile(contextPath), /ENOENT/);
  assert.equal(calls.some(([command]) => command === 'fetch'), false);
});

function reconciliationFixture() {
  const head = 'b'.repeat(40);
  const base = 'a'.repeat(40);
  const prior = {
    $schema: JSON_CONTRACTS.dependencyCheckpoint,
    commit: base,
    changes: [{
      ref: 'one',
      digest: '1'.repeat(64),
      summary: 'Cached one',
    }],
    managedEdges: [],
    inference: {
      mode: 'full',
      evaluatedRefs: ['one'],
      baseCommit: null,
      minimumConfidence: 0.85,
    },
  };
  return {
    head,
    prior,
    context: {
      $schema: JSON_CONTRACTS.dependencyReconciliationContext,
      targetHead: head,
      notesTip: 'c'.repeat(40),
      prior: { noteCommit: base, checkpoint: prior },
      mode: 'incremental',
      evaluatedRefs: ['two'],
      inventory: [
        {
          ref: 'one',
          digest: '1'.repeat(64),
          artifacts: [],
          cachedSummary: 'Cached one',
        },
        {
          ref: 'two',
          digest: '2'.repeat(64),
          artifacts: [],
          cachedSummary: null,
        },
      ],
    },
  };
}

test('reconcile writes and pushes a checkpoint only after GitHub mutations succeed', async () => {
  const { context, head, prior } = reconciliationFixture();
  const order = [];
  const result = await reconcileDependencyState({
    context,
    output: {
      $schema: JSON_CONTRACTS.dependencyGraphPatch,
      evaluationMode: 'incremental',
      evaluatedRefs: ['two'],
      summaries: [{ ref: 'two', summary: 'Generated two' }],
      upsert: [],
      remove: [],
    },
    client: {},
    git: async () => head,
    fetchNotes: async () => order.push('fetch'),
    verifyPriorNote: async () => {
      order.push('verify');
      return prior;
    },
    reconcile: async () => {
      order.push('github');
      return {
        accepted: [],
        review: [],
        changes: { add: [], remove: [], update: [] },
        evaluationMode: 'incremental',
        evaluatedRefs: ['two'],
        summaries: [{ ref: 'two', summary: 'Generated two' }],
      };
    },
    writeCheckpoint: async ({ checkpoint }) => {
      order.push('write');
      assert.deepEqual(checkpoint.changes, [
        { ref: 'one', digest: '1'.repeat(64), summary: 'Cached one' },
        { ref: 'two', digest: '2'.repeat(64), summary: 'Generated two' },
      ]);
    },
    pushNotes: async ({ expectedRemoteTip }) => {
      order.push('push');
      assert.equal(expectedRemoteTip, context.notesTip);
    },
  });
  assert.deepEqual(order, ['fetch', 'verify', 'github', 'write', 'push']);
  assert.equal(result.checkpoint.commit, head);
});

test('reconcile failure never writes or pushes a checkpoint', async () => {
  const { context, head, prior } = reconciliationFixture();
  const order = [];
  await assert.rejects(reconcileDependencyState({
    context,
    output: {
      $schema: JSON_CONTRACTS.dependencyGraphPatch,
      evaluationMode: 'incremental',
      evaluatedRefs: ['two'],
      summaries: [{ ref: 'two', summary: 'Generated two' }],
      upsert: [],
      remove: [],
    },
    client: {},
    git: async () => head,
    verifyPriorNote: async () => prior,
    reconcile: async () => {
      order.push('github');
      throw new Error('mutation failed');
    },
    writeCheckpoint: async () => order.push('write'),
    pushNotes: async () => order.push('push'),
  }), /mutation failed/);
  assert.deepEqual(order, ['github']);
});

test('checkpoint write failure prevents a notes push', async () => {
  const { context, head, prior } = reconciliationFixture();
  const order = [];
  await assert.rejects(reconcileDependencyState({
    context,
    output: {
      $schema: JSON_CONTRACTS.dependencyGraphPatch,
      evaluationMode: 'incremental',
      evaluatedRefs: ['two'],
      summaries: [{ ref: 'two', summary: 'Generated two' }],
      upsert: [],
      remove: [],
    },
    client: {},
    git: async () => head,
    verifyPriorNote: async () => prior,
    reconcile: async () => ({
      accepted: [],
      review: [],
      changes: { add: [], remove: [], update: [] },
      evaluationMode: 'incremental',
      evaluatedRefs: ['two'],
      summaries: [{ ref: 'two', summary: 'Generated two' }],
    }),
    writeCheckpoint: async () => {
      order.push('write');
      throw new Error('note failed');
    },
    pushNotes: async () => order.push('push'),
  }), /note failed/);
  assert.deepEqual(order, ['write']);
});

test('scope mismatch is rejected before dependency reconciliation', async () => {
  const { context, head, prior } = reconciliationFixture();
  let reconciled = false;
  await assert.rejects(reconcileDependencyState({
    context,
    output: {
      $schema: JSON_CONTRACTS.dependencyGraphPatch,
      evaluationMode: 'incremental',
      evaluatedRefs: [],
      summaries: [],
      upsert: [],
      remove: [],
    },
    client: {},
    git: async () => head,
    verifyPriorNote: async () => prior,
    reconcile: async () => {
      reconciled = true;
    },
  }), /scope does not match/);
  assert.equal(reconciled, false);
});
