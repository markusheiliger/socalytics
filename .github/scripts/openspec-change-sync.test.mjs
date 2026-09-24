import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';

import {
  inventoryRepositoryChanges,
  renderTwinSection,
  synchronizeTwins,
  updateManagedTwinBody,
} from './openspec-change-sync.mjs';

function fixtureRoot() {
  const root = mkdtempSync(join(tmpdir(), 'openspec-sync-'));
  mkdirSync(join(root, 'openspec', 'changes', 'active-one', 'specs'), { recursive: true });
  writeFileSync(join(root, 'openspec', 'changes', 'active-one', 'proposal.md'), '# Proposal');
  mkdirSync(
    join(root, 'openspec', 'changes', 'archive', '2026-09-24-archived-one'),
    { recursive: true },
  );
  writeFileSync(
    join(root, 'openspec', 'changes', 'archive', '2026-09-24-archived-one', 'tasks.md'),
    '# Tasks',
  );
  return root;
}

test('inventories active and dated archived changes with existing artifacts', () => {
  const inventory = inventoryRepositoryChanges({
    root: fixtureRoot(),
    activeChanges: [{ name: 'active-one' }],
  });
  assert.deepEqual(inventory, [
    {
      ref: 'active-one',
      lifecycle: 'active',
      path: 'openspec/changes/active-one',
      artifacts: [
        ['Proposal', 'openspec/changes/active-one/proposal.md'],
        ['Specifications', 'openspec/changes/active-one/specs'],
      ],
    },
    {
      ref: 'archived-one',
      lifecycle: 'archived',
      path: 'openspec/changes/archive/2026-09-24-archived-one',
      artifacts: [
        ['Tasks', 'openspec/changes/archive/2026-09-24-archived-one/tasks.md'],
      ],
    },
  ]);
});

test('fails when a canonical ref is both active and archived', () => {
  const root = fixtureRoot();
  assert.throws(
    () => inventoryRepositoryChanges({
      root,
      activeChanges: [{ name: 'archived-one' }],
    }),
    /both active and archived/,
  );
});

test('renders lifecycle-aware visible links and a machine marker', () => {
  const section = renderTwinSection({
    ref: 'active-one',
    lifecycle: 'active',
    path: 'openspec/changes/active-one',
    artifacts: [['Proposal', 'openspec/changes/active-one/proposal.md']],
  }, 'markusheiliger/socalytics', 'main');
  assert.match(section, /Lifecycle:\*\* active/);
  assert.match(section, /blob\/main\/openspec\/changes\/active-one\/proposal\.md/);
  assert.match(section, /"ref":"active-one"/);
});

test('preserves unmanaged issue body content when inserting or replacing the section', () => {
  const first = updateManagedTwinBody('Human notes.', 'MANAGED');
  assert.equal(first, 'Human notes.\n\nMANAGED\n');
  const second = updateManagedTwinBody(
    `Before\n<!-- openspec-twin:v1:start -->\nold\n<!-- openspec-twin:v1:end -->\nAfter`,
    '<!-- openspec-twin:v1:start -->\nnew\n<!-- openspec-twin:v1:end -->',
  );
  assert.match(second, /^Before\n<!-- openspec-twin:v1:start -->\nnew/);
  assert.match(second, /After$/);
  assert.throws(
    () => updateManagedTwinBody(
      `<!-- openspec-twin:v1:end -->\ntext\n<!-- openspec-twin:v1:start -->`,
      'managed',
    ),
    /reversed managed twin section/,
  );
});

test('creates missing twins and idempotently updates existing twins', async () => {
  const created = [];
  const updated = [];
  const inventory = [{
    ref: 'active-one',
    lifecycle: 'active',
    path: 'openspec/changes/active-one',
    artifacts: [],
  }];
  const section = renderTwinSection(inventory[0], 'markusheiliger/socalytics', 'main');
  const client = {
    ensureLabel: async () => {},
    listIssueTwins: async () => [],
    createIssue: async (issue) => created.push(issue),
  };
  assert.deepEqual(await synchronizeTwins({
    client,
    inventory,
    repository: 'markusheiliger/socalytics',
  }), [{ action: 'create', ref: 'active-one' }]);
  assert.equal(created.length, 1);

  const existingClient = {
    ensureLabel: async () => {},
    listIssueTwins: async () => [{
      number: 10,
      title: 'OpenSpec change: active-one',
      body: `${section}\n`,
    }],
    updateIssue: async (...args) => updated.push(args),
  };
  assert.deepEqual(await synchronizeTwins({
    client: existingClient,
    inventory,
    repository: 'markusheiliger/socalytics',
  }), [{ action: 'unchanged', ref: 'active-one', issueNumber: 10 }]);
  assert.equal(updated.length, 0);
});

test('does not create twins for historical archives', async () => {
  const created = [];
  assert.deepEqual(await synchronizeTwins({
    client: {
      ensureLabel: async () => {},
      listIssueTwins: async () => [],
      createIssue: async (issue) => created.push(issue),
    },
    inventory: [{
      ref: 'archived-one',
      lifecycle: 'archived',
      path: 'openspec/changes/archive/2026-09-24-archived-one',
      artifacts: [],
    }],
    repository: 'markusheiliger/socalytics',
  }), [{ action: 'ignored-archive', ref: 'archived-one' }]);
  assert.equal(created.length, 0);
});

test('closes an existing twin when its archive is observed on main', async () => {
  const updated = [];
  const archived = {
    ref: 'archived-one',
    lifecycle: 'archived',
    path: 'openspec/changes/archive/2026-09-24-archived-one',
    artifacts: [],
  };
  const body = `${renderTwinSection({
    ...archived,
    lifecycle: 'active',
    path: 'openspec/changes/archived-one',
  }, 'markusheiliger/socalytics', 'main')}\n`;
  await synchronizeTwins({
    client: {
      ensureLabel: async () => {},
      listIssueTwins: async () => [{
        number: 10,
        state: 'open',
        title: 'OpenSpec change: archived-one',
        body,
      }],
      updateIssue: async (...args) => updated.push(args),
    },
    inventory: [archived],
    repository: 'markusheiliger/socalytics',
  });
  assert.equal(updated.length, 1);
  assert.equal(updated[0][1].state, 'closed');
  assert.equal(updated[0][1].state_reason, 'completed');
});

test('fails visibly on duplicate twins', async () => {
  const body = `${renderTwinSection({
    ref: 'active-one',
    lifecycle: 'active',
    path: 'openspec/changes/active-one',
    artifacts: [],
  }, 'markusheiliger/socalytics', 'main')}\n`;
  await assert.rejects(synchronizeTwins({
    client: {
      ensureLabel: async () => {},
      listIssueTwins: async () => [
        { number: 1, body },
        { number: 2, body },
      ],
    },
    inventory: [],
    repository: 'markusheiliger/socalytics',
  }), /Duplicate issue twins/);
});

test('dry run performs no label or issue mutation', async () => {
  const mutations = [];
  await synchronizeTwins({
    client: {
      ensureLabel: async (...args) => mutations.push(['label', ...args]),
      listIssueTwins: async () => [],
      createIssue: async (...args) => mutations.push(['issue', ...args]),
    },
    inventory: [{
      ref: 'active-one',
      lifecycle: 'active',
      path: 'openspec/changes/active-one',
      artifacts: [],
    }],
    repository: 'markusheiliger/socalytics',
    dryRun: true,
  });
  assert.deepEqual(mutations, []);
});
