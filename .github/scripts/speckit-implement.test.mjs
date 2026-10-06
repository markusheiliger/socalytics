import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import { TWIN_LABEL } from './speckit-prepare-core.mjs';
import { runSync } from './speckit-prepare.mjs';
import { UsageError, main, resolveFolder, runRequest, runSelect } from './speckit-implement.mjs';
import { FakeGitHub, SPEC_TEMPLATE, envFor, makeRepo, silent } from './speckit-test-helpers.mjs';

const IMPLEMENT = 'speckit:stage:implement';
const TASKS_OPEN = '- [ ] T001 one\n- [ ] T002 two\n';

async function twinsFor(folders) {
  const root = makeRepo(folders);
  const github = new FakeGitHub();
  await runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent });
  github.updates = [];
  return { root, github };
}

function flag(github, issue, login = 'dev') {
  issue.labels = issue.labels.filter((label) => !label.name.startsWith('speckit:stage:'));
  github.humanLabel(issue.number, IMPLEMENT, login);
}

// Fake git that serves files of one ref from a map of "path" -> content.
function fakeGit(files, { fetchStatus = 0 } = {}) {
  const calls = [];
  const git = (args) => {
    calls.push(args);
    if (args[0] === 'fetch') return { status: fetchStatus, stdout: '', stderr: fetchStatus ? 'network down' : '' };
    if (args[0] === 'show') {
      const file = args[1].replace(/^origin\/main:/, '');
      return file in files ? { status: 0, stdout: files[file], stderr: '' } : { status: 128, stdout: '', stderr: 'missing' };
    }
    if (args[0] === 'ls-tree') {
      assert.deepEqual(args.slice(0, 4), ['ls-tree', '-z', '--full-tree', '--name-only']);
      const prefix = args[6];
      const names = Object.keys(files).filter((file) => file.startsWith(prefix));
      return { status: 0, stdout: names.map((name) => `${name}\0`).join(''), stderr: '' };
    }
    throw new Error(`unexpected git ${args.join(' ')}`);
  };
  git.calls = calls;
  return git;
}

function remoteFiles(folder, { plan = true, tasks = TASKS_OPEN, checklists = { 'requirements.md': '- [x] ok\n' } } = {}) {
  const files = { [`specs/${folder}/spec.md`]: SPEC_TEMPLATE(folder) };
  if (plan) files[`specs/${folder}/plan.md`] = '# plan\n';
  if (tasks !== null) files[`specs/${folder}/tasks.md`] = tasks;
  for (const [name, content] of Object.entries(checklists)) files[`specs/${folder}/checklists/${name}`] = content;
  return files;
}

test('select reports ready, blocked, and inconsistent flagged twins', async () => {
  const { root, github } = await twinsFor([
    { folder: 'a', plan: true, tasks: TASKS_OPEN },
    { folder: 'b', plan: true, tasks: TASKS_OPEN },
    { folder: 'c', plan: true },
    { folder: 'd', plan: true, tasks: TASKS_OPEN },
    { folder: 'e', plan: true, tasks: TASKS_OPEN },
  ]);
  try {
    github.permissions.dev = 'write';
    github.permissions.outsider = 'triage';
    const [a, b, c, , e] = github.issues;
    for (const issue of [a, b, c]) flag(github, issue);
    flag(github, e, 'outsider');
    github.edges.push([b.number, github.issues[3].number]);
    const result = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.deepEqual(result.ready, [{ number: a.number, folder: 'a' }]);
    assert.deepEqual(result.blocked.map((twin) => [twin.folder, twin.blockers.map((blocker) => blocker.number)]), [['b', [github.issues[3].number]]]);
    assert.deepEqual(result.inconsistent.map((twin) => twin.folder), ['c', 'e']);
    assert.match(result.inconsistent[1].reasons[0], /@outsider needs at least write access/);
    const output = readFileSync(path.join(root, 'output.txt'), 'utf8');
    assert.match(output, /count=1/);
    assert.match(output, new RegExp(`matrix=\\{"include":\\[\\{"number":${a.number},"folder":"a"\\}\\]\\}`));

    github.find(github.issues[3].number).state = 'closed';
    const unblocked = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.deepEqual(unblocked.ready.map((twin) => twin.folder), ['a', 'b']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('select reports nothing when no twin is flagged', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: TASKS_OPEN }]);
  try {
    const result = await runSelect({ client: github, rootDir: root, env: envFor(root), log: silent });
    assert.equal(result.ready.length + result.blocked.length + result.inconsistent.length, 0);
    assert.match(readFileSync(path.join(root, 'summary.md'), 'utf8'), /No spec twin is flagged/);
    assert.match(readFileSync(path.join(root, 'output.txt'), 'utf8'), /matrix=\{"include":\[\]\}/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('resolves the folder from the argument, environment, or feature.json', () => {
  const root = makeRepo([]);
  try {
    assert.equal(resolveFolder({ folder: 'specs/x/', env: {}, rootDir: root }), 'x');
    assert.equal(resolveFolder({ env: { SPECIFY_FEATURE_DIRECTORY: 'specs\\y' }, rootDir: root }), 'y');
    assert.throws(() => resolveFolder({ env: {}, rootDir: root }), UsageError);
    mkdirSync(path.join(root, '.specify'));
    writeFileSync(path.join(root, '.specify', 'feature.json'), '{ "feature_directory": "specs/z" }');
    assert.equal(resolveFolder({ env: {}, rootDir: root }), 'z');
    writeFileSync(path.join(root, '.specify', 'feature.json'), '{ nope');
    assert.throws(() => resolveFolder({ env: {}, rootDir: root }), /not valid JSON/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('request flags a tasked twin with complete checklists in one update', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: TASKS_OPEN }]);
  try {
    const lines = [];
    const git = fakeGit(remoteFiles('a'));
    const result = await runRequest({ client: github, git, rootDir: root, env: {}, folder: 'a', log: (line) => lines.push(line) });
    assert.equal(result.exitCode, 0);
    const issue = github.issues[0];
    assert.deepEqual(issue.labels.map((label) => label.name), [TWIN_LABEL, 'speckit:deps-pending', IMPLEMENT]);
    assert.equal(github.updates.length, 1);
    assert.deepEqual(git.calls[0], ['fetch', '--quiet', 'origin', 'main']);
    assert.match(lines.join('\n'), /Requested: .*speckit:stage:implement[\s\S]*No open blockers/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('request reports blockers, already flagged twins, and failed pre-checks', async () => {
  const { root, github } = await twinsFor([{ folder: 'a', plan: true, tasks: TASKS_OPEN }, 'b']);
  try {
    const [a, b] = github.issues;
    github.edges.push([a.number, b.number]);
    const run = async (files, folder = 'a') => {
      const lines = [];
      const result = await runRequest({ client: github, git: fakeGit(files), rootDir: root, env: {}, folder, log: (line) => lines.push(line) });
      return { code: result.exitCode, text: lines.join('\n') };
    };

    const checklist = await run(remoteFiles('a', { checklists: { 'ux.md': '- [ ] open\n' } }));
    assert.equal(checklist.code, 1);
    assert.match(checklist.text, /1 checklist item\(s\)/);

    const planned = await run(remoteFiles('a', { tasks: null }));
    assert.equal(planned.code, 1);
    assert.match(planned.text, /stage `planned`/);

    const notMerged = await run({});
    assert.equal(notMerged.code, 1);
    assert.match(notMerged.text, /not merged to main/);

    const noTwin = await run(remoteFiles('zz'), 'zz');
    assert.equal(noTwin.code, 1);
    assert.match(noTwin.text, /no open spec twin/);

    const requested = await run(remoteFiles('a'));
    assert.equal(requested.code, 0);
    assert.match(requested.text, new RegExp(`Open blockers: #${b.number}`));

    github.updates = [];
    const again = await run(remoteFiles('a'));
    assert.equal(again.code, 0);
    assert.match(again.text, /Already requested/);
    assert.equal(github.updates.length, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('request surfaces fetch failures and main validates commands', async () => {
  const root = makeRepo([]);
  try {
    const github = new FakeGitHub();
    await assert.rejects(
      () => runRequest({ client: github, git: fakeGit({}, { fetchStatus: 1 }), rootDir: root, env: {}, folder: 'a', log: silent }),
      (error) => error instanceof UsageError && /network down/.test(error.message),
    );
    await assert.rejects(() => main(['nope'], { env: {}, client: github, git: fakeGit({}) }), UsageError);
    await assert.rejects(() => main(['request', '--bogus'], { env: {}, client: github, git: fakeGit({}) }), /Unknown argument/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
