import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { GroupsUsageError, main } from './speckit-tasks-groups.mjs';

const TASKS = [
  '## Phase 1: Setup', '- [x] T001 a', '',
  '## Phase 2: Story', '### Providers', '- [ ] T002 [P] b', '- [ ] T003 [P] c', '- [ ] T004 d', '',
].join('\n');

function withSpec(body) {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-groups-'));
  mkdirSync(path.join(root, 'specs', 'f'), { recursive: true });
  writeFileSync(path.join(root, 'specs', 'f', 'tasks.md'), TASKS);
  try {
    return body(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('prints the stages of every phase for a spec folder', () => withSpec((root) => {
  let output = '';
  assert.equal(main(['f'], { cwd: root, log: (text) => { output += text; } }), 0);
  assert.equal(output, [
    '## Phase 1: Setup (0 open, 1 done)',
    '- Nothing open',
    '',
    '## Phase 2: Story (3 open, 0 done)',
    '- Stage 1: T002, T003 in parallel (under "Providers")',
    '- Stage 2: T004 (under "Providers")',
    '',
    'Warnings: none',
    '',
  ].join('\n'));
}));

test('accepts a folder path or a tasks.md path and prints JSON', () => withSpec((root) => {
  for (const target of ['specs/f', 'specs/f/tasks.md']) {
    let output = '';
    main([target, '--json'], { cwd: root, log: (text) => { output += text; } });
    const groups = JSON.parse(output);
    assert.deepEqual(groups.phases[1].stages[0], { parallel: true, heading: 'Providers', tasks: ['T002', 'T003'] });
  }
  assert.throws(() => main(['missing'], { cwd: root }), GroupsUsageError);
  assert.throws(() => main([], { cwd: root }), /Usage/);
}));
