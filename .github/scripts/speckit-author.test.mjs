import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { collectFacts, countClarificationSessions, main, nextAuthoringStep, publishBlockers } from './speckit-author.mjs';
import { UsageError } from './speckit-orchestrate.mjs';

const SPEC = ['# Feature Specification: F', '', '## Clarifications', '', '### Session 2026-10-12', '', '- Q: a → A: b', '', '## User Scenarios', ''].join('\n');
const TASKS = ['## Phase 1: Setup', '- [ ] T001 a', '- [ ] T002 b', ''].join('\n');

function withRepo(files, body) {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-author-'));
  for (const [file, content] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), content);
  }
  try {
    return body(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function fakeGit({ branch = 'main', status = '', upstream = 'origin/main', log = '', head = 'origin/main' } = {}) {
  return (args) => {
    const key = args.join(' ');
    if (key === 'rev-parse --abbrev-ref HEAD') return { status: 0, stdout: `${branch}\n`, stderr: '' };
    if (key.startsWith('status --porcelain')) return { status: 0, stdout: status, stderr: '' };
    if (key === 'rev-parse --abbrev-ref @{u}') return upstream ? { status: 0, stdout: `${upstream}\n`, stderr: '' } : { status: 128, stdout: '', stderr: 'no upstream' };
    if (args[0] === 'log') return { status: 0, stdout: log, stderr: '' };
    if (args[0] === 'symbolic-ref') return { status: 0, stdout: `${head}\n`, stderr: '' };
    throw new Error(`unexpected git ${key}`);
  };
}

test('counts clarification sessions only inside the Clarifications section', () => {
  assert.equal(countClarificationSessions(SPEC), 1);
  assert.equal(countClarificationSessions('# F\n\n## Other\n\n### Session x\n'), 0);
});

test('follows the Spec Kit order: specify, clarify, plan, tasks, analyze', () => {
  const base = { folder: 'f', archived: false, hasSpec: true, hasPlan: false, hasTasks: false, artifacts: ['spec.md'], tasksMarkdown: null, clarificationSessions: 0, openClarificationMarkers: 0 };
  assert.equal(nextAuthoringStep({ ...base, hasSpec: false, artifacts: [] }), 'specify');
  assert.equal(nextAuthoringStep(base), 'clarify');
  assert.equal(nextAuthoringStep({ ...base, clarificationSessions: 1, openClarificationMarkers: 2 }), 'clarify');
  assert.equal(nextAuthoringStep({ ...base, clarificationSessions: 1 }), 'plan');
  assert.equal(nextAuthoringStep({ ...base, hasPlan: true, artifacts: ['spec.md', 'plan.md'] }), 'tasks');
  const tasked = { ...base, hasPlan: true, hasTasks: true, artifacts: ['spec.md', 'plan.md', 'tasks.md'], tasksMarkdown: TASKS };
  assert.equal(nextAuthoringStep(tasked), 'analyze');
  assert.equal(nextAuthoringStep({ ...tasked, git: { published: true } }), 'published');
  assert.equal(nextAuthoringStep({ ...tasked, tasksMarkdown: TASKS.replace('[ ] T001', '[x] T001') }), 'done');
  assert.equal(nextAuthoringStep({ ...tasked, archived: true }), 'done');
});

test('reports publish blockers for stage, checklists, markers, and the specs overview', () => {
  const facts = { folder: 'f', hasSpec: true, artifacts: ['spec.md', 'plan.md'], tasksMarkdown: null, openChecklistItems: 2, openClarificationMarkers: 1, listedInSpecsReadme: false };
  const reasons = publishBlockers(facts);
  assert.equal(reasons.length, 4);
  assert.match(reasons[0], /stage `planned`/);
  assert.match(reasons[1], /2 checklist item/);
  assert.match(reasons[2], /NEEDS CLARIFICATION/);
  assert.match(reasons[3], /specs\/README\.md/);
  assert.deepEqual(publishBlockers({ ...facts, artifacts: ['spec.md', 'plan.md', 'tasks.md'], tasksMarkdown: TASKS, openChecklistItems: 0, openClarificationMarkers: 0, listedInSpecsReadme: true }), []);
});

test('collects facts from the folder, its checklists, specs/README.md, and git', () => withRepo({
  'specs/README.md': '| [F](f/spec.md) |\n',
  'specs/f/spec.md': SPEC,
  'specs/f/plan.md': '# Plan\n',
  'specs/f/tasks.md': TASKS,
  'specs/f/checklists/requirements.md': '- [x] a\n- [ ] b\n',
}, (root) => {
  const facts = collectFacts({ rootDir: root, folder: 'f', git: fakeGit({ status: ' M specs/f/tasks.md\n', log: '' }) });
  assert.deepEqual(facts.artifacts, ['spec.md', 'plan.md', 'tasks.md']);
  assert.equal(facts.clarificationSessions, 1);
  assert.deepEqual(facts.checklists, [{ name: 'requirements.md', open: 1 }]);
  assert.equal(facts.listedInSpecsReadme, true);
  assert.deepEqual(facts.git, { currentBranch: 'main', defaultBranch: 'main', upstream: 'origin/main', uncommitted: ['specs/f/tasks.md'], unpushedCommits: 0, published: false });
}));

test('prints the status of an archived spec and of the active feature', () => withRepo({
  'specs/README.md': '',
  'specs/archive/a/spec.md': SPEC,
  'specs/archive/a/plan.md': '# Plan\n',
  'specs/archive/a/tasks.md': TASKS.replaceAll('[ ]', '[x]'),
  'specs/n/spec.md': '# Feature Specification: N\n\n[NEEDS CLARIFICATION: x]\n',
  '.specify/feature.json': JSON.stringify({ feature_directory: 'specs/n' }),
}, (root) => {
  let output = '';
  const log = (text) => { output += text; };
  assert.equal(main(['status', 'a'], { rootDir: root, env: {}, git: fakeGit({ upstream: null }), log }), 0);
  assert.match(output, /Spec folder: specs\/archive\/a/);
  assert.match(output, /Next Spec Kit step: done/);
  assert.match(output, /Publish blockers: none/);
  assert.match(output, /no upstream/);

  output = '';
  main(['status', '--json'], { rootDir: root, env: {}, git: fakeGit(), log });
  const status = JSON.parse(output);
  assert.equal(status.folder, 'n');
  assert.equal(status.nextStep, 'clarify');
  assert.equal(status.openClarificationMarkers, 1);

  assert.throws(() => main(['other'], { rootDir: root, env: {}, git: fakeGit(), log }), UsageError);
}));
