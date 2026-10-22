import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { runDir } from './speckit-flow.mjs';
import { check, main, pending, result, revise, start, summarize } from './speckit-implement-flow.mjs';
import { toolingFingerprint } from './speckit-flow.mjs';
import { defaultGit } from './speckit-orchestrate.mjs';

const FOLDER = '20261021-120000-sample';
const TASKS = `specs/${FOLDER}/tasks.md`;
const TASKS_TEXT = '# Tasks\n\n## Phase 1: Setup\n\n- [x] T001 Create the project\n- [ ] T002 Add the greeting in src/app.txt\n';

// A repository with one spec (T001 done, T002 open) and src/app.txt, committed on main.
function repository() {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-implement-flow-'));
  const git = defaultGit(root);
  const run = (...args) => {
    const outcome = git(args);
    assert.equal(outcome.status, 0, `git ${args.join(' ')}: ${outcome.stderr}`);
    return outcome.stdout.trim();
  };
  run('init', '--quiet', '-b', 'main');
  run('config', 'user.name', 'test');
  run('config', 'user.email', 'test@example.com');
  run('config', 'core.autocrlf', 'false');
  const write = (file, text) => {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), text);
  };
  write(TASKS, TASKS_TEXT);
  write('src/app.txt', 'hello\n');
  write('README.md', '# Repo\n');
  // Like the repository: the run folders are ignored, which `git add` refuses to name.
  write('.specify/.gitignore', 'workflows/runs/\n');
  run('add', '.');
  run('commit', '--quiet', '-m', 'init');
  return { root, git, run, write, read: (file) => readFileSync(path.join(root, file), 'utf8'), cleanup: () => rmSync(root, { recursive: true, force: true }) };
}

// A run whose inputs.json holds `inputs`, as the workflow engine writes it.
function begin(repo, inputs, id = 'run1') {
  mkdirSync(runDir(repo.root, id), { recursive: true });
  writeFileSync(path.join(runDir(repo.root, id), 'inputs.json'), JSON.stringify({ inputs }));
  return { rootDir: repo.root, run: id, git: repo.git };
}

test('implement-task: start names the task, check accepts its change, and result records it', () => {
  const repo = repository();
  try {
    repo.write('README.md', '# Repo\n\nuncommitted before the run\n');
    const context = begin(repo, { folder: `specs/${FOLDER}`, task: 't002', guidance: 'Keep it short', mode: 'unattended' });
    const data = start({ ...context, workflow: 'implement-task' });
    assert.deepEqual({ ...data, extra: undefined }, { workflow: 'implement-task', folder: FOLDER, spec_dir: `specs/${FOLDER}`, tasks_path: TASKS, interactive: false, extra: undefined, task: 'T002', task_text: 'Add the greeting in src/app.txt' });
    assert.equal(data.extra, 'Guidance from the person who started this work: Keep it short');
    repo.write('src/app.txt', 'hello, world\n');
    repo.write('src/new.txt', 'new\n');
    repo.write(TASKS, TASKS_TEXT.replace('- [ ] T002', '- [x] T002'));
    const checked = check(context);
    assert.deepEqual(checked.files, [TASKS, 'src/app.txt', 'src/new.txt'], 'the README change from before the run is not part of it');
    assert.equal(checked.summary, 'changes 3 files');
    assert.match(readFileSync(path.join(runDir(repo.root, 'run1'), 'changes.diff'), 'utf8'), /\+hello, world/);
    assert.deepEqual(result(context), { format: 1, outcome: 'completed', files: checked.files, tooling: {}, workflow: 'implement-task', folder: FOLDER, archived: false, head: repo.run('rev-parse', 'HEAD'), changed: true, task: 'T002' });
    assert.deepEqual(JSON.parse(readFileSync(path.join(runDir(repo.root, 'run1'), 'result.json'), 'utf8')).task, 'T002');
    assert.deepEqual(repo.git(['status', '--porcelain', '--', 'src']).stdout.split('\n').filter(Boolean).sort(), [' M src/app.txt', '?? src/new.txt'], 'the change stays uncommitted');
    assert.equal(repo.run('diff', '--cached', '--name-only'), '', 'the real index stays as it was');
  } finally {
    repo.cleanup();
  }
});

test('implement-task: start refuses unknown, checked, and malformed tasks and missing folders', () => {
  const repo = repository();
  try {
    assert.throws(() => start({ ...begin(repo, { folder: FOLDER, task: 'T001' }), workflow: 'implement-task' }), /Task T001 is already checked/);
    assert.throws(() => start({ ...begin(repo, { folder: FOLDER, task: 'T009' }), workflow: 'implement-task' }), /Task T009 is not in tasks\.md/);
    assert.throws(() => start({ ...begin(repo, { folder: FOLDER, task: 'two' }), workflow: 'implement-task' }), /Input task must be a task ID/);
    assert.throws(() => start({ ...begin(repo, { folder: '20261021-120000-missing', task: 'T002' }), workflow: 'implement-task' }), /tasks\.md does not exist/);
    assert.throws(() => start({ ...begin(repo, { folder: '', task: 'T002' }), workflow: 'implement-task' }), /Input folder is required/);
    assert.throws(() => start({ ...begin(repo, { folder: FOLDER, task: 'T002', mode: 'auto' }), workflow: 'implement-task' }), /Input mode must be/);
    assert.throws(() => start({ ...begin(repo, { folder: FOLDER }), workflow: 'deploy' }), /start needs --workflow/);
  } finally {
    repo.cleanup();
  }
});

test('implement-task: check rejects other task changes and protected paths with every reason', () => {
  const repo = repository();
  try {
    const context = begin(repo, { folder: FOLDER, task: 'T002', mode: 'unattended' });
    start({ ...context, workflow: 'implement-task' });
    repo.write(TASKS, TASKS_TEXT.replace('Create the project', 'Create the whole project'));
    repo.write('.github/workflows/x.yml', 'name: x\n');
    assert.throws(() => check(context), (error) => /^The change cannot be accepted:\n- changed protected paths: \.github\/workflows\/x\.yml\n- `specs\/[\w-]+\/tasks\.md` may only change by checking T002$/.test(error.message));
  } finally {
    repo.cleanup();
  }
});

test('converge: waits for every task, accepts one appended convergence section, and lists its tasks', () => {
  const repo = repository();
  try {
    assert.throws(() => start({ ...begin(repo, { folder: FOLDER }), workflow: 'converge' }), /1 task\(s\) are still unchecked \(T002\): implement them before the convergence/);
    const done = TASKS_TEXT.replace('- [ ] T002', '- [x] T002');
    repo.write(TASKS, done);
    repo.run('commit', '--quiet', '-am', 'T002');
    const context = begin(repo, { folder: FOLDER, mode: 'interactive' }, 'run2');
    assert.equal(start({ ...context, workflow: 'converge' }).interactive, true);
    repo.write(TASKS, `${done}\n## Phase 2: Convergence\n\n- [ ] T003 Cover the greeting with a test\n- [ ] T004 Document the greeting\n`);
    const checked = check(context);
    assert.deepEqual(checked.appended, ['T003', 'T004']);
    assert.equal(checked.summary, 'appends 2 tasks: T003, T004');
    assert.deepEqual(result(context).appended, ['T003', 'T004']);

    const unchanged = begin(repo, { folder: FOLDER }, 'run3');
    repo.write(TASKS, done);
    start({ ...unchanged, workflow: 'converge' });
    assert.equal(check(unchanged).summary, 'found no remaining work');

    const rewritten = begin(repo, { folder: FOLDER }, 'run4');
    start({ ...rewritten, workflow: 'converge' });
    repo.write(TASKS, done.replace('Create the project', 'Create it'));
    assert.throws(() => check(rewritten), /convergence may only append to `specs\/[\w-]+\/tasks\.md`, not change existing lines/);
  } finally {
    repo.cleanup();
  }
});

test('repair: leaves tasks.md and protected paths alone; environment specs may change the environment actions', () => {
  const repo = repository();
  try {
    assert.throws(() => start({ ...begin(repo, { folder: FOLDER, evidence: '.speckit-repair-evidence' }), workflow: 'repair' }), /The failure evidence \.speckit-repair-evidence\/ does not exist/);
    assert.throws(() => start({ ...begin(repo, { folder: FOLDER, tasks: 'T001, nope' }), workflow: 'repair' }), /Input tasks must list task IDs/);
    repo.write('.speckit-repair-evidence/log.txt', 'failed\n');
    const context = begin(repo, { folder: FOLDER, tasks: 'T001,T002', scope: 'phase', evidence: '.speckit-repair-evidence', mode: 'unattended' });
    const data = start({ ...context, workflow: 'repair' });
    assert.equal(data.tasks, 'T001, T002');
    assert.equal(data.scope, 'phase');
    assert.equal(data.evidence, 'The failure evidence is in .speckit-repair-evidence/; inspect it directly.');
    assert.equal(data.protected, 'Do not change anything under `.github/`, `.specify/`, or `specs/`.');
    repo.write('src/app.txt', 'fixed\n');
    repo.write('.github/actions/environment-verify/action.yml', 'name: verify\n');
    repo.write(TASKS, TASKS_TEXT.replace('- [x] T001', '- [ ] T001'));
    assert.throws(() => check(context), new RegExp(`the repair changed protected files: \\.github/actions/environment-verify/action\\.yml, ${TASKS.replaceAll('/', '\\/').replaceAll('.', '\\.')}`));

    repo.write(TASKS, TASKS_TEXT);
    const environment = begin(repo, { folder: FOLDER, environment: 'true' }, 'run2');
    assert.match(start({ ...environment, workflow: 'repair' }).protected, /other than the environment actions/);
    repo.write('.github/actions/environment-verify/action.yml', 'name: verify again\n');
    repo.write('src/app.txt', 'fixed again\n');
    assert.deepEqual(check(environment).files, ['.github/actions/environment-verify/action.yml', 'src/app.txt']);
  } finally {
    repo.cleanup();
  }
});

// The implementation branch `speckit/<folder>` (T002 done, src/app.txt changed) with main merged into it, which
// conflicts in src/app.txt and in tasks.md.
function conflictedMerge() {
  const repo = repository();
  repo.run('checkout', '--quiet', '-b', `speckit/${FOLDER}`);
  repo.write('src/app.txt', 'hello from the implementation\n');
  repo.write('src/own.txt', 'own\n');
  repo.write(TASKS, TASKS_TEXT.replace('- [ ] T002', '- [x] T002'));
  repo.run('add', '.');
  repo.run('commit', '--quiet', '-m', 'implementation');
  repo.run('checkout', '--quiet', 'main');
  repo.write('src/app.txt', 'hello from main\n');
  repo.write('src/base.txt', 'base\n');
  repo.write(TASKS, TASKS_TEXT.replace('Create the project', 'Create the project skeleton'));
  repo.run('add', '.');
  repo.run('commit', '--quiet', '-m', 'main moves on');
  repo.run('checkout', '--quiet', `speckit/${FOLDER}`);
  assert.notEqual(repo.git(['merge', '--no-edit', 'main']).status, 0, 'the merge conflicts');
  return repo;
}

test('resolve: lists the conflicts, rejects markers and lost tasks, and reports base branch files it changed', () => {
  const repo = conflictedMerge();
  try {
    const context = begin(repo, { folder: FOLDER, base: 'main', mode: 'unattended' });
    const data = start({ ...context, workflow: 'resolve' });
    assert.equal(data.conflicts, `\`${TASKS}\`, \`src/app.txt\``);
    assert.equal(data.base, 'main');
    assert.match(data.fork_point, /^[0-9a-f]{40}$/);
    assert.throws(() => check(context), /conflict markers remain in specs\/[\w-]+\/tasks\.md, src\/app\.txt/);

    repo.write('src/app.txt', 'hello from both\n');
    repo.write(TASKS, '# Tasks\n\n## Phase 1: Setup\n\n- [x] T001 Create the project skeleton\n- [ ] T002 Add the greeting in src/app.txt\n');
    assert.throws(() => check(context), /the resolution unchecked task T002 of `specs\/[\w-]+\/tasks\.md`/);

    repo.write(TASKS, '# Tasks\n\n## Phase 1: Setup\n\n- [x] T001 Create the project skeleton\n- [x] T002 Add the greeting in src/app.txt\n');
    repo.write('src/own.txt', 'own, adjusted\n');
    repo.write('src/base.txt', 'base, adjusted for the implementation\n');
    const checked = check(context);
    assert.deepEqual(checked.base_owned, ['src/base.txt']);
    assert.equal(checked.summary, 'resolves 2 conflicted files and changes 2 other files, 1 of them owned by the base branch (a person reviews those)');
    assert.deepEqual(result(context).base_owned, ['src/base.txt']);
    assert.equal(repo.git(['rev-parse', '-q', '--verify', 'MERGE_HEAD']).status, 0, 'the merge stays in progress for the land job');
  } finally {
    repo.cleanup();
  }
});

test('resolve: start needs a merge with conflicts outside protected paths', () => {
  const repo = repository();
  try {
    assert.throws(() => start({ ...begin(repo, { folder: FOLDER }), workflow: 'resolve' }), /not in the middle of a merge/);
  } finally {
    repo.cleanup();
  }
});

test('revise stops the run with the command that runs it again with feedback; pending describes the run', () => {
  const repo = repository();
  try {
    const context = begin(repo, { folder: FOLDER, task: 'T002' });
    start({ ...context, workflow: 'implement-task' });
    repo.write('src/app.txt', 'hi\n');
    check(context);
    assert.throws(() => revise(context), (error) => error.message === [
      `Revision requested for implement-task of specs/${FOLDER}. Its changes stay in the working tree; run the workflow again with what should change:`,
      '',
      `  specify workflow run implement-task -i folder=${FOLDER} -i task=T002 -i feedback="what should change"`,
    ].join('\n'));
    const state = (fields) => writeFileSync(path.join(runDir(repo.root, 'run1'), 'state.json'), JSON.stringify(fields));
    state({ status: 'failed', current_step_id: 'stop-for-feedback', step_results: { 'stop-for-feedback': { output: { stderr: 'Revision requested for implement-task\nShell command exited with code 1.' } } } });
    assert.deepEqual(pending(context), { status: 'failed', step: 'stop-for-feedback', error: 'Revision requested for implement-task', workflow: 'implement-task', folder: FOLDER, revise: true });
    state({ status: 'paused', current_step_id: 'review-verdict', step_results: { 'review-verdict': { output: { message: 'The change for T002 changes 1 file.', options: ['accept', 'revise', 'abort'], show_file: 'changes.diff' } } } });
    assert.deepEqual(pending(context), { status: 'paused', kind: 'gate', step: 'review-verdict', input: 'review_verdict', message: 'The change for T002 changes 1 file.', options: ['accept', 'revise', 'abort'], recommended: 'accept', file: 'changes.diff', workflow: 'implement-task', folder: FOLDER });
    result(context);
    state({ status: 'completed', current_step_id: 'result' });
    assert.equal(pending(context).result.task, 'T002');
  } finally {
    repo.cleanup();
  }
});

test('the command line prints ASCII JSON and needs a run id', () => {
  const repo = repository();
  try {
    begin(repo, { folder: FOLDER, task: 'T002', guidance: 'Grüße' });
    let printed = '';
    assert.equal(main(['start', '--run', 'run1', '--workflow', 'implement-task'], { rootDir: repo.root, git: repo.git, log: (text) => { printed += text; } }), 0);
    assert.match(printed, /Gr\\u00fc\\u00dfe/);
    assert.equal(JSON.parse(printed).task, 'T002');
    assert.throws(() => main(['start'], { rootDir: repo.root, git: repo.git }), /Usage: speckit-implement-flow\.mjs <command> --run <run id>/);
    assert.throws(() => main(['deploy', '--run', 'run1'], { rootDir: repo.root, git: repo.git }), /Unknown command "deploy"/);
  } finally {
    repo.cleanup();
  }
});

test('the tooling guard: start records the helpers, later steps refuse changed helpers, pending still reads the run', () => {
  const repo = repository();
  try {
    begin(repo, { folder: FOLDER, task: 'T002', mode: 'unattended' });
    const silent = () => {};
    const helper = new URL('./speckit-implement-flow.mjs', import.meta.url);
    const run = (...argv) => main([...argv, '--run', 'run1'], { rootDir: repo.root, git: repo.git, log: silent });
    writeFileSync(path.join(runDir(repo.root, 'run1'), 'workflow.yml'), 'workflow:\n  id: implement-task\n  version: 1.1.0\n');
    run('start', '--workflow', 'implement-task');
    const recorded = JSON.parse(readFileSync(path.join(runDir(repo.root, 'run1'), 'tooling.json'), 'utf8'));
    assert.deepEqual(recorded, toolingFingerprint(repo.root, 'run1', helper));
    assert.equal(recorded.workflow_version, '1.1.0');
    repo.write('src/app.txt', 'hello, world\n');
    run('check');
    writeFileSync(path.join(runDir(repo.root, 'run1'), 'tooling.json'), JSON.stringify({ ...recorded, helpers: 'older' }));
    writeFileSync(path.join(runDir(repo.root, 'run1'), 'state.json'), JSON.stringify({ status: 'paused', workflow_id: 'implement-task', current_step_id: 'review-verdict', step_results: {} }));
    assert.throws(() => run('result'), /^Error: Run run1 started with other workflow helpers than the checkout has now[\s\S]*specify workflow run implement-task with the same inputs\); the changes of this run stay in the working tree\.$/);
    assert.throws(() => run('revise'), /started with other workflow helpers/);
    assert.equal(run('pending'), 0, 'pending only reads, so a person can still see the run');
    writeFileSync(path.join(runDir(repo.root, 'run1'), 'tooling.json'), JSON.stringify(recorded));
    run('result');
    assert.deepEqual(JSON.parse(readFileSync(path.join(runDir(repo.root, 'run1'), 'result.json'), 'utf8')).tooling, recorded);
  } finally {
    repo.cleanup();
  }
});

test('summarize describes each kind of change in one line', () => {
  assert.equal(summarize('implement-task', { files: [] }), 'changes no file');
  assert.equal(summarize('repair', { files: ['a'] }), 'changes 1 file');
  assert.equal(summarize('converge', { files: ['t'], appended: ['T009'] }), 'appends 1 task: T009');
  assert.equal(summarize('resolve', { files: ['a'], conflicts: ['a'] }), 'resolves 1 conflicted file and changes 0 other files');
});
