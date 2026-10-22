import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  FlowError,
  MAX_FOLDER_CLOCK_SKEW_MS,
  answers,
  approve,
  begin,
  changedArchitecture,
  check,
  created,
  environment,
  existingFolder,
  findings,
  main,
  markdownCheck,
  pending,
  pick,
  proposal,
  publish,
  questions,
  readState,
  ready,
  result,
  revisedStage,
  runDir,
  stage,
  start,
} from './speckit-author-flow.mjs';

const RUN = 'r1';
const FOLDER = '20261021-101500-fleet-status';
const UTC = { TZ: 'UTC' };
const SPEC = ['# Feature Specification: Fleet status', '', '## Clarifications', '', '### Session 2026-10-21', '', '- Q: a → A: b', ''].join('\n');
const PLAN = ['# Plan', '', '## Architecture Updates', '', 'None: no architectural effect.', ''].join('\n');
const PLAN_WITH_UPDATES = ['# Plan', '', '## Architecture Updates', '', '- analyst-manager.md: fleet status read model', ''].join('\n');
const TASKS = ['## Phase 1: Setup', '', '- [ ] T001 [P] a', '- [ ] T002 [P] b', '- [ ] T003 c', ''].join('\n');
const QUESTIONS = [
  { question: 'Who sees offline Managers?', options: [{ key: 'A', text: 'Club Admins' }, { key: 'B', text: 'Everyone' }], recommended: 'A', reason: 'least privilege' },
  { question: 'How fresh?', options: [{ key: 'A', text: '1 minute' }, { key: 'B', text: '5 minutes' }, { key: 'C', text: '15 minutes' }], recommended: 'B' },
];

function withRepo(files, body) {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-author-flow-'));
  for (const [file, content] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), typeof content === 'string' ? content : `${JSON.stringify(content, null, 2)}\n`);
  }
  try {
    return body(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const inputsFile = (inputs) => ({ [`.specify/workflows/runs/${RUN}/inputs.json`]: { inputs } });
const runFile = (name, content) => ({ [`.specify/workflows/runs/${RUN}/${name}`]: content });
const stateFile = (state) => runFile('author.json', state);
const specFiles = (extra = {}) => ({ [`specs/${FOLDER}/spec.md`]: SPEC, [`specs/${FOLDER}/checklists/requirements.md`]: '- [x] ok\n', ...extra });

// A git double: answers the status queries of speckit-author.mjs and the publish commands, and records every call.
function fakeGit({ upstream = 'origin/main', published = false, committed = false, fail = {}, staged = true, files = '', branch = 'main', architecture = '' } = {}) {
  const calls = [];
  const git = (args) => {
    const key = args.join(' ');
    calls.push(key);
    if (fail[args[0]]) return { status: 1, stdout: '', stderr: fail[args[0]] };
    if (key === 'rev-parse --abbrev-ref HEAD') return { status: 0, stdout: `${branch}\n`, stderr: '' };
    if (key.startsWith('status --porcelain') && key.includes('docs/architecture')) return { status: 0, stdout: architecture, stderr: '' };
    if (key.startsWith('status --porcelain')) return { status: 0, stdout: published || committed ? '' : `?? specs/${FOLDER}/\n`, stderr: '' };
    if (key === 'rev-parse --abbrev-ref @{u}') return upstream ? { status: 0, stdout: `${upstream}\n`, stderr: '' } : { status: 128, stdout: '', stderr: 'no upstream' };
    if (args[0] === 'log') return { status: 0, stdout: committed ? 'abc1234\n' : '', stderr: '' };
    if (args[0] === 'symbolic-ref') return { status: 0, stdout: 'origin/main\n', stderr: '' };
    if (args[0] === 'ls-files') return { status: 0, stdout: files, stderr: '' };
    if (args[0] === 'diff') return { status: staged ? 1 : 0, stdout: '', stderr: '' };
    return { status: 0, stdout: '', stderr: '' };
  };
  return { git, calls };
}

const TWINS = [
  { number: 154, title: 'Member Invitations', state: 'OPEN', labels: [{ name: 'speckit:spec' }, { name: 'speckit:stage:implementing' }], body: '**Spec**: [`specs/20261017-202624-member-invitations`](https://example)' },
];
const fakeGh = (status = 0) => () => ({ status, stdout: status === 0 ? JSON.stringify(TWINS) : '', stderr: '' });

test('start requires TZ=UTC, because /speckit-specify names the folder after the current time', () => withRepo(inputsFile({ feature: 'x' }), (rootDir) => {
  assert.throws(() => start({ rootDir, run: RUN, git: fakeGit().git, env: {} }), /TZ=UTC/);
}));

test('start validates the mode, stage, and stamp inputs', () => {
  for (const [inputs, message] of [[{ mode: 'later' }, /interactive" or "unattended/], [{ stage: 'specify' }, /stage must be one of/], [{ stamp: '2026-10-21' }, /YYYYMMDD-HHMMSS/]]) {
    withRepo(inputsFile(inputs), (rootDir) => assert.throws(() => start({ rootDir, run: RUN, git: fakeGit().git, env: UTC }), message));
  }
});

test('start and begin: a feature description is specified; empty input fetches, lists the twins, and discovers', () => {
  withRepo(inputsFile({ feature: 'let club admins see offline Managers' }), (rootDir) => {
    assert.deepEqual(start({ rootDir, run: RUN, git: fakeGit().git, env: UTC }), { active: false, folder: '' });
    assert.deepEqual(begin({ rootDir, run: RUN, git: fakeGit().git, env: UTC, gh: fakeGh() }), { kind: 'feature', folder: '', from: 'specify', discover: false, specify: true, twins: null });
    assert.deepEqual(pick({ rootDir, run: RUN }), { feature: 'let club admins see offline Managers' });
  });
  withRepo(inputsFile({ mode: 'unattended' }), (rootDir) => {
    const { git, calls } = fakeGit();
    start({ rootDir, run: RUN, git, env: UTC });
    assert.deepEqual(begin({ rootDir, run: RUN, git, env: UTC, gh: fakeGh() }), { kind: 'discover', folder: '', from: 'specify', discover: true, specify: true, twins: 1 });
    assert.ok(calls.includes('fetch --quiet'), 'the discovery compares with the remote');
    assert.deepEqual(JSON.parse(readFileSync(path.join(runDir(rootDir, RUN), 'twins.json'), 'utf8')), [{ number: 154, title: 'Member Invitations', state: 'OPEN', stage: 'implementing', folder: '20261017-202624-member-invitations' }]);
  });
  withRepo(inputsFile({}), (rootDir) => {
    start({ rootDir, run: RUN, git: fakeGit().git, env: UTC });
    assert.equal(begin({ rootDir, run: RUN, git: fakeGit().git, env: UTC, gh: fakeGh(4) }).twins, null, 'without gh the discovery works from the repository');
    assert.equal(existsSync(path.join(runDir(rootDir, RUN), 'twins.json')), false);
  });
});

test('start asks before continuing the active feature; begin continues it or discovers', () => {
  const files = { ...specFiles({ [`specs/${FOLDER}/plan.md`]: PLAN }), '.specify/feature.json': { feature_directory: `specs/${FOLDER}` }, ...inputsFile({}) };
  withRepo(files, (rootDir) => {
    assert.deepEqual(start({ rootDir, run: RUN, git: fakeGit().git, env: UTC }), { active: true, folder: FOLDER });
    assert.deepEqual(begin({ rootDir, run: RUN, git: fakeGit().git, env: UTC, gh: fakeGh(), choice: 'continue' }), { kind: 'continue', folder: FOLDER, from: 'tasks', discover: false, specify: false, twins: null });
    start({ rootDir, run: RUN, git: fakeGit().git, env: UTC });
    assert.equal(begin({ rootDir, run: RUN, git: fakeGit().git, env: UTC, gh: fakeGh(), choice: '' }).kind, 'continue', 'unattended runs take the recommended continue');
    start({ rootDir, run: RUN, git: fakeGit().git, env: UTC });
    assert.equal(begin({ rootDir, run: RUN, git: fakeGit().git, env: UTC, gh: fakeGh(), choice: 'propose' }).kind, 'discover');
    writeFileSync(path.join(rootDir, 'specs', FOLDER, 'tasks.md'), TASKS);
    assert.equal(start({ rootDir, run: RUN, git: fakeGit({ published: true }).git, env: UTC }).active, false, 'a pushed spec is not offered');
    assert.equal(start({ rootDir, run: RUN, git: fakeGit({ committed: true }).git, env: UTC }).active, false, 'a committed spec that only waits for its push is not offered, so --repeat proposes the next one');
    assert.equal(start({ rootDir, run: RUN, git: fakeGit({ committed: true, upstream: null }).git, env: UTC }).active, false, 'also on a branch without upstream');
    assert.equal(start({ rootDir, run: RUN, git: fakeGit().git, env: UTC }).active, true, 'an uncommitted tasked spec is still offered');
  });
});

test('start continues a spec folder at its next step; feedback and stage choose where it starts again', () => withRepo({ ...specFiles({ [`specs/${FOLDER}/plan.md`]: PLAN, [`specs/${FOLDER}/tasks.md`]: TASKS }), ...inputsFile({ feature: `specs/${FOLDER}` }) }, (rootDir) => {
  const run = (inputs) => {
    writeFileSync(path.join(runDir(rootDir, RUN), 'inputs.json'), JSON.stringify({ inputs: { feature: FOLDER, ...inputs } }));
    start({ rootDir, run: RUN, git: fakeGit().git, env: UTC });
    return begin({ rootDir, run: RUN, git: fakeGit().git, env: UTC, gh: fakeGh() }).from;
  };
  assert.equal(existingFolder(rootDir, `specs/${FOLDER}/`), FOLDER);
  assert.equal(existingFolder(rootDir, 'a feature idea'), null);
  assert.equal(run({}), 'analyze');
  assert.equal(run({ feedback: 'Split T003.' }), 'tasks', 'feedback revises the tasks, the stage reviewed before analyze');
  assert.equal(run({ stage: 'clarify', feedback: 'Club Admins only.' }), 'clarify', 'the stage input reworks the spec of a complete spec');
  assert.deepEqual(stage({ rootDir, run: RUN, name: 'clarify' }), { run: true, folder: FOLDER, feedback: 'Club Admins only.', revise: true });
  assert.deepEqual(stage({ rootDir, run: RUN, name: 'plan' }), { run: true, folder: FOLDER, feedback: '', revise: false }, 'later stages run again without the feedback');
  assert.equal(run({ stage: 'plan' }), 'plan', 'a stage without feedback reruns from there');
  assert.throws(() => run({ stage: 'analyze', feedback: 'x' }), /Feedback reworks the stage clarify, plan, or tasks/);
  assert.deepEqual(['clarify', 'plan', 'tasks', 'analyze', 'published'].map(revisedStage), ['clarify', 'clarify', 'plan', 'tasks', null]);
}));

test('start refuses folders it cannot continue', () => {
  withRepo(inputsFile({ feature: 'idea', feedback: 'more' }), (rootDir) => {
    assert.throws(() => start({ rootDir, run: RUN, git: fakeGit().git, env: UTC }), /pass its folder/);
  });
  withRepo({ ...specFiles({ [`specs/${FOLDER}/plan.md`]: PLAN, [`specs/${FOLDER}/tasks.md`]: TASKS }), ...inputsFile({ feature: FOLDER }) }, (rootDir) => {
    assert.throws(() => start({ rootDir, run: RUN, git: fakeGit({ published: true }).git, env: UTC }), /already pushed/);
  });
  withRepo({ [`specs/${FOLDER}/checklists/requirements.md`]: '- [ ] x\n', ...inputsFile({ feature: FOLDER }) }, (rootDir) => {
    assert.throws(() => start({ rootDir, run: RUN, git: fakeGit().git, env: UTC }), /has no spec\.md, so there is nothing to continue/);
  });
  withRepo({ ...specFiles(), ...inputsFile({ feature: FOLDER, stage: 'tasks' }) }, (rootDir) => {
    assert.throws(() => start({ rootDir, run: RUN, git: fakeGit().git, env: UTC }), /cannot restart at tasks: its next step is plan/);
  });
});

test('discovery: validates and renders the proposal, and picks the chosen or recommended candidate', () => {
  const candidate = (title) => ({ title, description: `${title} description`, architecture: ['docs/architecture/analyst-manager.md#fleet'], why: 'unblocks' });
  withRepo({ ...inputsFile({}), ...runFile('proposal.json', { candidates: [candidate('One'), candidate('Two')] }) }, (rootDir) => {
    start({ rootDir, run: RUN, git: fakeGit().git, env: UTC });
    begin({ rootDir, run: RUN, git: fakeGit().git, env: UTC, gh: fakeGh(4) });
    const rendered = proposal({ rootDir, run: RUN });
    assert.equal(rendered.count, 2);
    assert.match(readFileSync(path.join(rootDir, rendered.file), 'utf8'), /^1\. One \(recommended\)\n {3}Why next: unblocks/);
    assert.match(pick({ rootDir, run: RUN, choice: '' }).feature, /^One description\n\nArchitecture sections: docs\/architecture/);
    assert.match(pick({ rootDir, run: RUN, choice: '2' }).feature, /^Two description/);
    assert.throws(() => pick({ rootDir, run: RUN, choice: '3' }), /no candidate 3/);
  });
  withRepo({ ...inputsFile({}), ...runFile('proposal.json', { candidates: [{ title: 'x' }] }) }, (rootDir) => {
    assert.throws(() => proposal({ rootDir, run: RUN }), /has no description/);
  });
});

test('created: takes the folder of .specify/feature.json and requires its UTC prefix', () => withRepo({ ...specFiles(), ...inputsFile({ feature: 'idea' }), '.specify/feature.json': { feature_directory: `specs/${FOLDER}` } }, (rootDir) => {
  start({ rootDir, run: RUN, git: fakeGit().git, env: UTC });
  const madeAt = Date.UTC(2026, 9, 21, 10, 15, 0);
  assert.deepEqual(created({ rootDir, run: RUN, git: fakeGit().git, now: madeAt + 60_000 }), { folder: FOLDER, nextStep: 'plan' });
  assert.equal(readState(rootDir, RUN).folder, FOLDER);
  assert.throws(() => created({ rootDir, run: RUN, git: fakeGit().git, now: madeAt + MAX_FOLDER_CLOCK_SKEW_MS + 1 }), /current UTC time/);
}));

test('created: the stamp input renames the new folder, its mentions, and the active feature', () => withRepo({ ...specFiles({ [`specs/${FOLDER}/spec.md`]: `# Feature Specification: Fleet status\n\n**Feature Branch**: \`${FOLDER}\`\n` }), ...inputsFile({ feature: 'idea', stamp: '20261020-080000' }), '.specify/feature.json': { feature_directory: `specs/${FOLDER}`, other: 1 } }, (rootDir) => {
  start({ rootDir, run: RUN, git: fakeGit().git, env: UTC });
  const renamed = '20261020-080000-fleet-status';
  assert.equal(created({ rootDir, run: RUN, git: fakeGit().git, now: Date.UTC(2026, 9, 25) }).folder, renamed, 'a stamp is not held to the current time');
  assert.equal(existsSync(path.join(rootDir, 'specs', FOLDER)), false);
  assert.match(readFileSync(path.join(rootDir, 'specs', renamed, 'spec.md'), 'utf8'), new RegExp(`\`${renamed}\``));
  assert.deepEqual(JSON.parse(readFileSync(path.join(rootDir, '.specify', 'feature.json'), 'utf8')), { feature_directory: `specs/${renamed}`, other: 1 });
}));

test('clarify: one rendered gate per question, the answers it records, and the final scan', () => withRepo({ ...inputsFile({}), ...runFile('questions.json', QUESTIONS), ...stateFile({ folder: FOLDER }) }, (rootDir) => {
  const asked = questions({ rootDir, run: RUN });
  assert.deepEqual([asked.has, asked.count, asked.has_q2, asked.has_q3, asked.q3], [true, 2, true, false, '']);
  assert.equal(asked.q1, asked.items[0].text, 'one flat field per question for the top-level gates');
  assert.equal(asked.items[0].text, 'Question 1 of 2: Who sees offline Managers?\n\n  A) Club Admins  (recommended)\n  B) Everyone\n\nWhy A: least privilege');
  assert.deepEqual(answers({ rootDir, run: RUN, choices: 'B,recommended' }), { count: 2, text: 'Q1: Who sees offline Managers? = B) Everyone; Q2: How fresh? = B) 5 minutes' });
  assert.match(answers({ rootDir, run: RUN, choices: 'B,,,,' }).text, /= B\) Everyone; Q2: .* = B\) 5 minutes$/, 'empty positions take the recommended option');
  assert.throws(() => answers({ rootDir, run: RUN, choices: 'A,B,C' }), /answers for 3 questions, but only 2 were asked/);
  assert.throws(() => answers({ rootDir, run: RUN, choices: 'A,E' }), /Question 2 has no option E/);
  assert.throws(() => questions({ rootDir, run: RUN, final: true }), (error) => error instanceof FlowError && /2 open clarification question\(s\) after three rounds:\n- Q1: Who sees offline Managers\?/.test(error.message) && error.message.includes(`-i feature=${FOLDER} -i stage=clarify`));
  writeFileSync(path.join(runDir(rootDir, RUN), 'questions.json'), '[]');
  assert.deepEqual([questions({ rootDir, run: RUN, final: true }).has, questions({ rootDir, run: RUN }).q1], [false, '']);
  for (const [bad, message] of [
    [[{ question: 'q', options: [{ key: 'B', text: 'x' }, { key: 'A', text: 'y' }], recommended: 'A' }], /key "A"/],
    [[{ question: 'q', options: [{ key: 'A', text: 'x' }, { key: 'B', text: 'y' }], recommended: 'C' }], /recommends no listed option/],
    [Array.from({ length: 6 }, () => QUESTIONS[0]), /at most 5/],
  ]) {
    writeFileSync(path.join(runDir(rootDir, RUN), 'questions.json'), JSON.stringify(bad));
    assert.throws(() => questions({ rootDir, run: RUN }), message);
  }
}));

test('check: repairs a missing Architecture Updates section, a missing documentation task, and [P] group warnings', () => {
  const state = stateFile({ folder: FOLDER });
  withRepo({ ...specFiles({ [`specs/${FOLDER}/plan.md`]: '# Plan\n' }), ...state }, (rootDir) => {
    assert.deepEqual(check({ rootDir, run: RUN, after: 'plan', git: fakeGit().git }), { repair: true, architecture: false });
    assert.throws(() => check({ rootDir, run: RUN, after: 'plan', git: fakeGit().git, strict: true }), /still has no `## Architecture Updates`/);
    assert.throws(() => check({ rootDir, run: RUN, after: 'tasks', git: fakeGit().git }), /did not write .*tasks\.md/);
  });
  const repeated = ['## Phase 1', '', '### Shared', '', '- [ ] T001 [P] a', '', '### Shared', '', '- [ ] T002 [P] b', ''].join('\n');
  withRepo({ ...specFiles({ [`specs/${FOLDER}/plan.md`]: PLAN, [`specs/${FOLDER}/tasks.md`]: repeated }), ...state }, (rootDir) => {
    const result = check({ rootDir, run: RUN, after: 'tasks', git: fakeGit().git });
    assert.deepEqual([result.refine, result.docs_task, result.repair], [true, false, true]);
    assert.match(result.warnings[0], /T001, T002 form one group/);
    assert.match(readFileSync(path.join(rootDir, result.file), 'utf8'), /Phase 1 \(2 tasks\)\n {2}- T001, T002 in parallel\n\nWarnings:/);
    assert.throws(() => check({ rootDir, run: RUN, after: 'tasks', git: fakeGit().git, strict: true }), /still have warnings/);
  });
  withRepo({ ...specFiles({ [`specs/${FOLDER}/plan.md`]: PLAN_WITH_UPDATES, [`specs/${FOLDER}/tasks.md`]: TASKS }), ...state }, (rootDir) => {
    assert.deepEqual(Object.values(check({ rootDir, run: RUN, after: 'tasks', git: fakeGit().git })).slice(0, 3), [false, true, true], 'Architecture Updates need a docs/architecture task');
    assert.throws(() => check({ rootDir, run: RUN, after: 'tasks', git: fakeGit().git, strict: true }), /no task that references docs\/architecture/);
  });
  withRepo({ ...specFiles({ [`specs/${FOLDER}/plan.md`]: PLAN_WITH_UPDATES, [`specs/${FOLDER}/tasks.md`]: `${TASKS}- [ ] T004 Update docs/architecture/analyst-manager.md\n` }), ...state }, (rootDir) => {
    assert.equal(check({ rootDir, run: RUN, after: 'tasks', git: fakeGit().git, strict: true }).repair, false);
  });
});

test('environment: an unattended run stops when the plan needs new tooling, unless the spec is the environment spec', () => {
  withRepo({ ...inputsFile({ mode: 'interactive' }), ...runFile('environment.json', { needed: true, what: 'Rust toolchain' }) }, (rootDir) => {
    assert.deepEqual(environment({ rootDir, run: RUN }), { needed: true, what: 'Rust toolchain', environment_spec: false });
  });
  withRepo({ ...inputsFile({ mode: 'unattended' }), ...runFile('environment.json', { needed: true, what: 'Rust toolchain' }) }, (rootDir) => {
    assert.throws(() => environment({ rootDir, run: RUN }), /Rust toolchain[\s\S]*environment spec first/);
  });
  withRepo({ ...inputsFile({ mode: 'unattended' }), ...runFile('environment.json', { needed: true, what: 'Rust toolchain', environment_spec: true }) }, (rootDir) => {
    assert.deepEqual(environment({ rootDir, run: RUN }), { needed: false, what: '', environment_spec: true });
  });
  withRepo({ ...inputsFile({}), ...runFile('environment.json', { needed: 'no' }) }, (rootDir) => {
    assert.throws(() => environment({ rootDir, run: RUN }), /boolean "needed"/);
  });
});

const FINDINGS = [
  { id: 'D1', severity: 'LOW', summary: 'wording', fix: null },
  { id: 'U1', severity: 'MEDIUM', summary: 'FR-1 is vague', fix: 'Say offline for 5 minutes' },
  { id: 'C1', severity: 'HIGH', summary: 'FR-3 has no task', fix: 'Add a task for FR-3' },
];

test('findings: one gate per fixable finding, the most severe first; CRITICAL and HIGH findings block', () => withRepo({ ...inputsFile({}), ...runFile('findings.json', { findings: FINDINGS }) }, (rootDir) => {
  const result = findings({ rootDir, run: RUN });
  assert.deepEqual([result.count, result.fixable, result.blocking, result.has_f2, result.has_f3], [3, true, true, true, false]);
  assert.equal(result.f1, 'Finding 1 of 2: HIGH C1 FR-3 has no task\n\nFix: Add a task for FR-3');
  assert.match(readFileSync(path.join(rootDir, result.file), 'utf8'), /^- LOW D1 wording\n- MEDIUM U1 FR-1 is vague\n {2}Fix: Say offline for 5 minutes\n- HIGH C1/);
  const many = Array.from({ length: 7 }, (_, index) => ({ id: `M${index}`, severity: 'MEDIUM', summary: `m${index}`, fix: 'x' }));
  writeFileSync(path.join(runDir(rootDir, RUN), 'findings.json'), JSON.stringify({ findings: many }));
  const capped = findings({ rootDir, run: RUN });
  assert.deepEqual([capped.has_f5, capped.f5.endsWith('2 more fixable finding(s) come up in the next round.')], [true, true]);
  writeFileSync(path.join(runDir(rootDir, RUN), 'findings.json'), JSON.stringify({ findings: [{ severity: 'SEVERE', summary: 'x' }] }));
  assert.throws(() => findings({ rootDir, run: RUN }), /no severity/);
}));

test('approve: remediates the findings a person applied, or every fixable one unattended', () => {
  withRepo({ ...inputsFile({}), ...runFile('findings.json', { findings: FINDINGS }) }, (rootDir) => {
    assert.deepEqual(approve({ rootDir, run: RUN, choices: 'skip,apply,,,' }), { any: true, count: 1, skipped: 1 });
    assert.deepEqual(JSON.parse(readFileSync(path.join(runDir(rootDir, RUN), 'remediation.json'), 'utf8')).findings.map((finding) => finding.id), ['U1']);
    assert.deepEqual(approve({ rootDir, run: RUN, choices: 'skip,skip' }), { any: false, count: 0, skipped: 2 });
  });
  withRepo({ ...inputsFile({ mode: 'unattended' }), ...runFile('findings.json', { findings: FINDINGS }) }, (rootDir) => {
    assert.deepEqual(approve({ rootDir, run: RUN, choices: ',,,,' }), { any: true, count: 2, skipped: 0 });
  });
});

test('changedArchitecture lists changed, new, and renamed files under docs/architecture/', () => {
  const { git } = fakeGit({ architecture: ' M docs/architecture/analyst-manager.md\n?? docs/architecture/new.md\nR  docs/architecture/a.md -> docs/architecture/b.md\n' });
  assert.deepEqual(changedArchitecture(git), ['docs/architecture/analyst-manager.md', 'docs/architecture/new.md', 'docs/architecture/b.md']);
});

// A Markdown check double that runs a real process, so the capture of its output is exercised.
const failingMarkdown = (calls) => ({ files, spawn }) => {
  calls.push(files);
  spawn(process.execPath, ['-e', 'console.error("specs/x/spec.md:3 MD032/blanks-around-lists")'], { stdio: 'inherit' });
  throw new Error('Markdown diagnostics failed with exit code 1.');
};

test('markdown: checks the spec folder and the changed architecture, and collects the findings for the formatting step', () => withRepo({ ...specFiles(), ...stateFile({ folder: FOLDER }) }, (rootDir) => {
  const calls = [];
  const { git } = fakeGit({ files: `specs/${FOLDER}/spec.md\nspecs/${FOLDER}/contracts/api.yaml\n`, architecture: ' M docs/architecture/analyst-manager.md\n' });
  const result = markdownCheck({ rootDir, run: RUN, git, markdown: failingMarkdown(calls) });
  assert.deepEqual([result.clean, result.folder], [false, FOLDER]);
  assert.deepEqual(calls[0], [`specs/${FOLDER}/spec.md`, 'docs/architecture/analyst-manager.md']);
  assert.match(readFileSync(path.join(rootDir, result.file), 'utf8'), /MD032\/blanks-around-lists[\s\S]*Markdown diagnostics failed/);
  assert.equal(markdownCheck({ rootDir, run: RUN, git, markdown: () => {} }).clean, true);
  assert.throws(() => markdownCheck({ rootDir, run: RUN, git, markdown: () => { throw new Error('Markdown diagnostics requires "markdownlint-cli2". Install it.'); } }), FlowError);
}));

test('pending: says which gate a paused run waits for, and asks a round of questions or findings at once', () => {
  const paused = (step, output) => ({ status: 'paused', current_step_id: step, step_results: { [step]: { status: 'paused', output } } });
  withRepo(runFile('state.json', paused('publish-verdict', { message: 'Push it, commit only, or leave it uncommitted?', options: ['push', 'commit', 'none'] })), (rootDir) => {
    assert.deepEqual(pending({ rootDir, run: RUN }), { status: 'paused', kind: 'gate', step: 'publish-verdict', input: 'publish_verdict', message: 'Push it, commit only, or leave it uncommitted?', options: ['push', 'commit', 'none'], recommended: 'push', file: null });
  });
  withRepo({ ...runFile('state.json', paused('answer-2-1', { message: 'q', options: ['recommended', 'A'] })), ...runFile('questions.json', QUESTIONS) }, (rootDir) => {
    const result = pending({ rootDir, run: RUN });
    assert.deepEqual([result.kind, result.round, result.questions.map((question) => [question.input, question.recommended])], ['questions', 2, [['answer_2_1', 'A'], ['answer_2_2', 'B']]]);
    assert.deepEqual(result.questions[1].options.map((option) => option.key), ['A', 'B', 'C']);
  });
  withRepo({ ...runFile('state.json', paused('fix-3-1', { message: 'f', options: ['apply', 'skip', 'abort'] })), ...runFile('findings.json', { findings: FINDINGS }) }, (rootDir) => {
    const result = pending({ rootDir, run: RUN });
    assert.deepEqual([result.kind, result.round, result.options, result.findings.map((finding) => [finding.input, finding.id])], ['findings', 3, ['apply', 'skip', 'abort'], [['fix_3_1', 'C1'], ['fix_3_2', 'U1']]]);
  });
  withRepo({ ...runFile('state.json', { status: 'completed', current_step_id: 'handover-interactive', step_results: { 'publish-chosen': { output: { data: { action: 'push', pushed: true } } } } }), ...stateFile({ folder: FOLDER }) }, (rootDir) => {
    assert.deepEqual(pending({ rootDir, run: RUN }), { status: 'completed', step: 'handover-interactive', folder: FOLDER, publish: { action: 'push', pushed: true } });
  });
  withRepo({ ...runFile('state.json', { status: 'failed', current_step_id: 'ready', error: 'Shell command exited with code 1.', step_results: { ready: { output: { stderr: 'specs/x is not ready to implement:\n- a blocker\nShell command exited with code 1.' } } } }), ...stateFile({ folder: FOLDER }) }, (rootDir) => {
    assert.deepEqual(pending({ rootDir, run: RUN }), { status: 'failed', step: 'ready', folder: FOLDER, error: 'specs/x is not ready to implement:\n- a blocker' });
  });
});

test('ready: publish blockers, blocking findings, and Markdown findings stop the run; it names what is published and where', () => {
  const state = stateFile({ folder: FOLDER });
  const tasked = specFiles({ [`specs/${FOLDER}/plan.md`]: PLAN, [`specs/${FOLDER}/tasks.md`]: TASKS });
  withRepo({ ...specFiles({ [`specs/${FOLDER}/plan.md`]: PLAN }), ...state }, (rootDir) => {
    assert.throws(() => ready({ rootDir, run: RUN, git: fakeGit().git, markdown: () => {} }), /not ready to implement:\n- /);
  });
  withRepo({ ...tasked, ...state, ...runFile('findings.json', { findings: [{ severity: 'CRITICAL', summary: 'x' }] }) }, (rootDir) => {
    assert.throws(() => ready({ rootDir, run: RUN, git: fakeGit().git, markdown: () => {} }), /CRITICAL or HIGH/);
  });
  withRepo({ ...tasked, ...state }, (rootDir) => {
    assert.throws(() => ready({ rootDir, run: RUN, git: fakeGit({ files: `specs/${FOLDER}/spec.md\n` }).git, markdown: failingMarkdown([]) }), /the Markdown check still fails:\n[\s\S]*MD032/);
  });
  withRepo({ ...tasked, ...state }, (rootDir) => {
    const result = ready({ rootDir, run: RUN, git: fakeGit({ branch: 'feature/x', architecture: ' M docs/architecture/analyst-manager.md\n' }).git, markdown: () => {} });
    assert.deepEqual([result.title, result.architecture, result.branch, result.defaultBranch], ['Fleet status', ['docs/architecture/analyst-manager.md'], 'feature/x', 'main']);
    assert.equal(result.summary, `specs/${FOLDER} is ready to implement and is published with its 1 change(s) to docs/architecture/ (docs/architecture/analyst-manager.md). Note: the checkout is on feature/x, not main; the spec twin is only created once the spec reaches main.`);
    assert.deepEqual(readState(rootDir, RUN).architecture, ['docs/architecture/analyst-manager.md']);
  });
  withRepo({ ...tasked, ...state }, (rootDir) => {
    assert.equal(ready({ rootDir, run: RUN, git: fakeGit().git, markdown: () => {} }).summary, `specs/${FOLDER} is ready to implement.`);
  });
});

test('publish: commits the spec folder and its architecture changes as Spec: <title>, fast-forwards, and pushes; never more', () => {
  withRepo(stateFile({ folder: FOLDER, title: 'Fleet status', architecture: ['docs/architecture/analyst-manager.md'] }), (rootDir) => {
    const pushed = fakeGit();
    assert.deepEqual(publish({ rootDir, run: RUN, choice: 'push', git: pushed.git }), { action: 'push', folder: FOLDER, committed: true, pushed: true, architecture: ['docs/architecture/analyst-manager.md'] });
    const paths = `specs/${FOLDER} docs/architecture/analyst-manager.md`;
    assert.deepEqual(pushed.calls.filter((call) => !call.startsWith('rev-parse')), [
      'fetch --quiet',
      'merge --ff-only --quiet @{u}',
      `add -A -- ${paths}`,
      `diff --cached --quiet -- ${paths}`,
      `commit --quiet -m Spec: Fleet status -m Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com> -- ${paths}`,
      'push --quiet',
    ]);
  });
  withRepo(stateFile({ folder: FOLDER, title: 'Fleet status' }), (rootDir) => {
    const committed = fakeGit();
    assert.equal(publish({ rootDir, run: RUN, choice: 'commit', git: committed.git }).pushed, false);
    assert.ok(!committed.calls.some((call) => call.startsWith('push') || call.startsWith('fetch')));
    const untouched = fakeGit();
    assert.deepEqual(publish({ rootDir, run: RUN, choice: 'ask', git: untouched.git }), { action: 'none', folder: FOLDER, committed: false, pushed: false }, 'unattended runs leave ask uncommitted');
    assert.deepEqual(untouched.calls, []);
    const fresh = fakeGit({ upstream: null });
    publish({ rootDir, run: RUN, choice: 'push', git: fresh.git });
    assert.ok(fresh.calls.includes('push --quiet -u origin main'), 'a branch without upstream gets one');
    assert.throws(() => publish({ rootDir, run: RUN, choice: 'push', git: fakeGit({ fail: { merge: 'diverged' } }).git }), /Fast-forwarding .* diverged/);
    assert.throws(() => publish({ rootDir, run: RUN, choice: 'force', git: fakeGit().git }), /push, commit, or none/);
    const detached = fakeGit({ branch: 'HEAD' });
    assert.throws(() => publish({ rootDir, run: RUN, choice: 'commit', git: detached.git }), /detached HEAD/);
    assert.ok(!detached.calls.some((call) => call.startsWith('add') || call.startsWith('commit')), 'nothing is committed on a detached HEAD');
  });
});

test('result: writes result.json in the common shape with the folder, the title, and what the handover did', () => {
  withRepo(stateFile({ folder: FOLDER, title: 'Fleet status' }), (rootDir) => {
    publish({ rootDir, run: RUN, choice: 'none', git: fakeGit().git });
    const written = result({ rootDir, run: RUN, git: fakeGit().git });
    assert.deepEqual(written, { format: 1, outcome: 'completed', files: [`specs/${FOLDER}/`], tooling: {}, workflow: 'author', folder: FOLDER, title: 'Fleet status', publish: { action: 'none', folder: FOLDER, committed: false, pushed: false } });
    assert.deepEqual(JSON.parse(readFileSync(path.join(runDir(rootDir, RUN), 'result.json'), 'utf8')), written);
  });
  withRepo(stateFile({}), (rootDir) => assert.throws(() => result({ rootDir, run: RUN, git: fakeGit().git }), /The run has no spec folder/));
});

test('main prints one ASCII-only JSON object per command and rejects unknown ones', () => withRepo({ ...inputsFile({}), ...runFile('questions.json', [{ ...QUESTIONS[0], question: 'Wer sieht Manager offline? ←' }]) }, (rootDir) => {
  let printed = '';
  main(['questions', '--run', RUN], { rootDir, git: fakeGit().git, log: (text) => { printed += text; } });
  assert.match(printed, /^[\x00-\x7f]*$/, 'the workflow engine decodes shell output with the system code page on Windows');
  assert.equal(JSON.parse(printed).items[0].text, questions({ rootDir, run: RUN }).items[0].text);
  assert.match(JSON.parse(printed).items[0].text, /Wer sieht Manager offline\? ←/);
  assert.throws(() => main(['questions'], { rootDir }), /--run <run id>/);
  assert.throws(() => main(['nope', '--run', RUN], { rootDir }), /Unknown command "nope"/);
}));

test('drift: the installed copies of every gha workflow match their source, and each is registered', () => {
  const repo = (file) => new URL(`../../${file}`, import.meta.url);
  const read = (file) => readFileSync(repo(file), 'utf8').replaceAll('\r\n', '\n');
  const reinstall = 'reinstall the extension and re-register the workflows (see .specify/extension-src/gha/README.md)';
  const ids = readdirSync(repo('.specify/extension-src/gha/workflows/'), { withFileTypes: true }).filter((entry) => entry.isDirectory()).map((entry) => entry.name).sort();
  assert.deepEqual(ids, ['author', 'converge', 'implement-task', 'repair', 'resolve']);
  const registry = JSON.parse(read('.specify/workflows/workflow-registry.json')).workflows ?? {};
  assert.deepEqual(Object.keys(registry).sort(), ids, `the registry lists exactly the extension's workflows: ${reinstall}`);
  for (const id of ids) {
    const source = read(`.specify/extension-src/gha/workflows/${id}/workflow.yml`);
    assert.match(source, new RegExp(`^\\s+id: "${id}"$`, 'm'), `${id}: the workflow id matches its folder`);
    for (const copy of [`.specify/extensions/gha/workflows/${id}/workflow.yml`, `.specify/workflows/${id}/workflow.yml`]) {
      assert.equal(read(copy), source, `${copy} differs from the source: ${reinstall}`);
    }
    const version = source.match(/^\s+version:\s*"?([\w.-]+)"?\s*$/m)?.[1];
    assert.equal(registry[id].version, version, `${id}: the registry lists version ${registry[id].version}, the source ${version}: ${reinstall}`);
    assert.equal(registry[id].source, `.specify/extensions/gha/workflows/${id}`, `${id}: the registry names the installed extension folder with forward slashes`);
  }
});
