import assert from 'node:assert/strict';
import { mkdir, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';

import {
  createGitRunner,
  detectDependencyChanges,
  findNearestDependencyCheckpoint,
  pushDependencyNotes,
  readDependencyNote,
  writeDependencyCheckpoint,
} from './openspec-change-git-notes.mjs';
import { JSON_CONTRACTS } from './openspec-change-core.mjs';

const root = path.join(
  process.cwd(),
  '.github',
  'scripts',
  '.git-note-test-repositories',
);

async function createRepository(name) {
  const cwd = path.join(root, name);
  await rm(cwd, { recursive: true, force: true });
  await mkdir(cwd, { recursive: true });
  const git = createGitRunner({ cwd });
  await git('init', '--initial-branch=main');
  await git('config', 'user.name', 'OpenSpec tests');
  await git('config', 'user.email', 'openspec-tests@example.invalid');
  await writeFile(path.join(cwd, 'README.md'), 'initial\n');
  await git('add', '.');
  await git('commit', '-m', 'initial');
  return { cwd, git, initial: await git('rev-parse', 'HEAD') };
}

function checkpoint(commit, overrides = {}) {
  return {
    $schema: JSON_CONTRACTS.dependencyCheckpoint,
    commit,
    changes: [{
      ref: 'one',
      digest: '1'.repeat(64),
      summary: 'First change',
    }],
    managedEdges: [],
    inference: {
      mode: 'full',
      evaluatedRefs: ['one'],
      baseCommit: null,
      minimumConfidence: 0.85,
    },
    ...overrides,
  };
}

test('writes, reads, and finds the nearest non-forced dependency checkpoint', async (t) => {
  const repository = await createRepository('nearest');
  t.after(() => rm(repository.cwd, { recursive: true, force: true }));
  await writeDependencyCheckpoint({
    git: repository.git,
    object: repository.initial,
    checkpoint: checkpoint(repository.initial),
  });
  assert.deepEqual(
    await readDependencyNote({ git: repository.git, object: repository.initial }),
    checkpoint(repository.initial),
  );

  await writeFile(path.join(repository.cwd, 'README.md'), 'next\n');
  await repository.git('add', '.');
  await repository.git('commit', '-m', 'next');
  const nearest = await findNearestDependencyCheckpoint({ git: repository.git });
  assert.equal(nearest.commit, repository.initial);
  assert.equal(nearest.checkpoint.commit, repository.initial);
  await assert.rejects(
    writeDependencyCheckpoint({
      git: repository.git,
      object: repository.initial,
      checkpoint: checkpoint(repository.initial),
    }),
    /already exists/,
  );
});

test('detects incremental active change refs and supports full evaluation', async (t) => {
  const repository = await createRepository('changes');
  t.after(() => rm(repository.cwd, { recursive: true, force: true }));
  const changeDirectory = path.join(
    repository.cwd,
    'openspec',
    'changes',
    'change-two',
  );
  await mkdir(changeDirectory, { recursive: true });
  await writeFile(path.join(changeDirectory, 'proposal.md'), '# Change two\n');
  await repository.git('add', '.');
  await repository.git('commit', '-m', 'add change');
  assert.deepEqual(await detectDependencyChanges({
    git: repository.git,
    baseCommit: repository.initial,
    allRefs: ['change-one', 'change-two'],
  }), {
    mode: 'incremental',
    evaluatedRefs: ['change-two'],
    files: ['openspec/changes/change-two/proposal.md'],
  });
  assert.deepEqual(await detectDependencyChanges({
    git: repository.git,
    allRefs: ['change-two', 'change-one', 'change-two'],
  }), {
    mode: 'full',
    evaluatedRefs: ['change-one', 'change-two'],
    files: [],
  });
});

test('rejects a checkpoint attached to a different commit', async (t) => {
  const repository = await createRepository('mismatch');
  t.after(() => rm(repository.cwd, { recursive: true, force: true }));
  await assert.rejects(writeDependencyCheckpoint({
    git: repository.git,
    checkpoint: checkpoint('a'.repeat(40)),
  }), /does not match/);
});

test('full recovery replaces a malformed checkpoint on the target commit', async (t) => {
  const repository = await createRepository('replace-invalid');
  t.after(() => rm(repository.cwd, { recursive: true, force: true }));
  await repository.git(
    'notes',
    '--ref=refs/notes/openspec-change-dependencies',
    'add',
    '-m',
    '{"invalid":true}',
    repository.initial,
  );
  await assert.rejects(
    readDependencyNote({ git: repository.git, object: repository.initial }),
    /\$schema must be/,
  );
  await writeDependencyCheckpoint({
    git: repository.git,
    object: repository.initial,
    checkpoint: checkpoint(repository.initial),
    replaceInvalid: true,
  });
  assert.deepEqual(
    await readDependencyNote({ git: repository.git, object: repository.initial }),
    checkpoint(repository.initial),
  );
});

test('push refuses a changed remote notes tip before invoking git push', async () => {
  const calls = [];
  const git = async (...args) => {
    calls.push(args);
    if (args[0] === 'ls-remote') return `${'b'.repeat(40)}\trefs/notes/openspec-change-dependencies`;
    throw new Error('push must not be called');
  };
  await assert.rejects(pushDependencyNotes({
    git,
    expectedRemoteTip: 'a'.repeat(40),
  }), /Dependency notes changed/);
  assert.equal(calls.some(([command]) => command === 'push'), false);
});

test('push uses a plain non-force refspec after the remote lease check', async () => {
  const tip = 'a'.repeat(40);
  const calls = [];
  const git = async (...args) => {
    calls.push(args);
    if (args[0] === 'ls-remote') return `${tip}\trefs/notes/openspec-change-dependencies`;
    if (args[0] === 'push') return 'ok';
    if (args[0] === 'rev-parse') return 'c'.repeat(40);
    throw new Error(`Unexpected git call: ${args.join(' ')}`);
  };
  assert.equal(await pushDependencyNotes({
    git,
    expectedRemoteTip: tip,
  }), 'c'.repeat(40));
  const push = calls.find(([command]) => command === 'push');
  assert.deepEqual(push, [
    'push',
    '--porcelain',
    'origin',
    'refs/notes/openspec-change-dependencies:refs/notes/openspec-change-dependencies',
  ]);
});
