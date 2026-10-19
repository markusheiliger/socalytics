import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { archiveBlockers, archiveRewrite, archiveSpec, archivedFolders, defaultGit, main, refreshArchivedReferences, renderArchiveReport } from './speckit-archive.mjs';

const A = '20260101-000000-alpha';
const B = '20260101-000001-beta';

function git(cwd, ...args) {
  const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true });
  if (result.status !== 0) throw new Error(`git ${args.join(' ')} failed: ${result.stderr}`);
  return result.stdout;
}

function write(root, file, text) {
  mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
  writeFileSync(path.join(root, file), text);
}

const read = (root, file) => readFileSync(path.join(root, file), 'utf8');

function withRepo(fn, { tasks = '- [x] T001 Done\n- [X] T002 Done too\n' } = {}) {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-archive-'));
  try {
    git(root, 'init', '-q');
    write(root, 'README.md', `See [alpha](specs/${A}/spec.md) and [beta](specs/${B}/spec.md).\n`);
    write(root, 'specs/README.md', `| [Alpha](${A}/spec.md) | [Beta](${B}/spec.md) |\n`);
    write(root, `specs/${A}/spec.md`, '# Alpha\n\n## Scope\n');
    write(root, `specs/${A}/tasks.md`, tasks);
    write(root, `specs/${A}/plan.md`, [
      `Depends on [beta](../${B}/spec.md), see [docs](../../docs/x.md#top) and [contract](contracts/c.md).`,
      `Also [here](#scope), [web](https://example.com/specs/${A}), and [self](./spec.md).`,
      '',
      '```text',
      `[not a link](../${B}/spec.md) in specs/${A}/plan.md`,
      '```',
      '',
      `[ref]: ../${B}/plan.md "Beta plan"`,
      '',
    ].join('\n'));
    write(root, `specs/${A}/contracts/c.md`, '# Contract\n');
    write(root, `specs/${B}/spec.md`, '# Beta\n');
    write(root, `specs/${B}/plan.md`, `Uses [the contract](../${A}/contracts/c.md#sec).\r\n\r\n[alpha]: ../${A}/spec.md\r\n`);
    write(root, 'docs/x.md', `Feature [alpha](../specs/${A}/spec.md), folder [alpha](../specs/${A}/).\n`);
    write(root, 'src/Test.cs', `// Copied from specs/${A}/contracts/openapi.yaml; not specs/${A}-other or specs/${A}x.\n`);
    git(root, 'add', '-A');
    git(root, '-c', 'user.name=t', '-c', 'user.email=t@e', 'commit', '-q', '-m', 'seed');
    return fn(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test('archives a spec: moves the folder and rewrites links and mentions everywhere', () => withRepo((root) => {
  const planned = archiveSpec({ root, folder: A, check: true });
  assert.equal(planned.exitCode, 0);
  assert.equal(existsSync(path.join(root, `specs/${A}`)), true, '--check changes nothing');
  assert.deepEqual(renderArchiveReport(A, planned, { check: true })[0], `Would move \`specs/${A}\` to \`specs/archive/${A}\`.`);

  const result = archiveSpec({ root, folder: A });
  assert.equal(result.exitCode, 0);
  assert.equal(existsSync(path.join(root, `specs/${A}`)), false);
  assert.equal(read(root, `specs/archive/${A}/spec.md`), '# Alpha\n\n## Scope\n');
  assert.match(git(root, 'status', '--porcelain'), new RegExp(`R  specs/${A}/spec\\.md -> specs/archive/${A}/spec\\.md`), 'the move is staged as a rename');

  assert.equal(read(root, 'README.md'), `See [alpha](specs/archive/${A}/spec.md) and [beta](specs/${B}/spec.md).\n`);
  assert.equal(read(root, 'specs/README.md'), `| [Alpha](archive/${A}/spec.md) | [Beta](${B}/spec.md) |\n`);
  assert.equal(read(root, `specs/${B}/plan.md`), `Uses [the contract](../archive/${A}/contracts/c.md#sec).\r\n\r\n[alpha]: ../archive/${A}/spec.md\r\n`, 'line endings are kept');
  assert.equal(read(root, 'docs/x.md'), `Feature [alpha](../specs/archive/${A}/spec.md), folder [alpha](../specs/archive/${A}/).\n`);
  assert.equal(read(root, 'src/Test.cs'), `// Copied from specs/archive/${A}/contracts/openapi.yaml; not specs/${A}-other or specs/${A}x.\n`);
  assert.equal(read(root, `specs/archive/${A}/plan.md`), [
    `Depends on [beta](../../${B}/spec.md), see [docs](../../../docs/x.md#top) and [contract](contracts/c.md).`,
    `Also [here](#scope), [web](https://example.com/specs/${A}), and [self](./spec.md).`,
    '',
    '```text',
    `[not a link](../${B}/spec.md) in specs/archive/${A}/plan.md`,
    '```',
    '',
    `[ref]: ../../${B}/plan.md "Beta plan"`,
    '',
  ].join('\n'), 'links leaving the moved folder get one more level; links inside it, anchors, URLs, and code stay');
  assert.deepEqual(result.changes.filter((change) => change.edited && change.file === change.from).map((change) => change.file).sort(),
    ['README.md', 'docs/x.md', `specs/${B}/plan.md`, 'specs/README.md', 'src/Test.cs']);
}));

test('archiving again only rewrites references that appeared since', () => withRepo((root) => {
  write(root, 'docs/mixed.md', 'unrelated\r\nmixed\nline endings\r\n');
  git(root, 'add', '-A');
  archiveSpec({ root, folder: A });
  write(root, 'docs/later.md', `Added later: [alpha](../specs/${A}/tasks.md) in specs/${A}.\n`);
  const again = archiveSpec({ root, folder: A });
  assert.deepEqual([again.exitCode, again.moved], [0, false]);
  assert.equal(read(root, 'docs/later.md'), `Added later: [alpha](../specs/archive/${A}/tasks.md) in specs/archive/${A}.\n`);
  assert.deepEqual(again.changes.map((change) => change.file), ['docs/later.md'], 'files without references stay untouched, mixed line endings included');
  assert.match(renderArchiveReport(A, again)[0], /already archived; only references are rewritten/);
  assert.deepEqual(archiveSpec({ root, folder: A }).changes, [], 'idempotent');
}));

test('refuses specs that are not implemented, missing, ambiguous, or invalid', () => {
  withRepo((root) => {
    assert.match(archiveBlockers({ root, folder: A }).join(), /1 task\(s\) are not checked: T002/);
    assert.equal(archiveSpec({ root, folder: A }).exitCode, 1);
    assert.equal(existsSync(path.join(root, `specs/${A}`)), true);
  }, { tasks: '- [x] T001 Done\n- [ ] T002 Open\n' });
  withRepo((root) => {
    assert.match(archiveBlockers({ root, folder: 'missing' }).join(), /`specs\/missing` does not exist/);
    assert.match(archiveBlockers({ root, folder: B }).join(), /tasks\.md` does not exist, so the spec is not implemented/);
    assert.match(archiveBlockers({ root, folder: 'archive' }).join(), /not a valid spec folder name/);
    assert.match(archiveBlockers({ root, folder: '..' }).join(), /not a valid spec folder name/);
    write(root, `specs/archive/${A}/spec.md`, '# Copy\n');
    assert.match(archiveBlockers({ root, folder: A }).join(), /both `specs\/[^`]+` and `specs\/archive\/[^`]+` exist/);
  });
});

test('the command line archives a folder given by path and reports usage errors', () => withRepo((root) => {
  const lines = [];
  assert.equal(main([`specs/${A}/`, '--check'], { cwd: root, log: (line) => lines.push(line) }), 0);
  assert.match(lines.join('\n'), /Would move[\s\S]*Would rewrite references in 5 file\(s\)/);
  assert.equal(main([A], { cwd: root, log: () => {} }), 0);
  assert.equal(existsSync(path.join(root, `specs/archive/${A}`)), true);
  assert.throws(() => main([], { cwd: root, log: () => {} }), /Usage/);
}));

test('workflow files are never edited, and other archived specs get their outdated references refreshed', () => withRepo((root) => {
  write(root, '.github/workflows/ci.yml', `# builds specs/${A}\n`);
  write(root, `specs/archive/${B}x/spec.md`, '# Gamma\n');
  write(root, 'docs/gamma.md', `[gamma](../specs/${B}x/spec.md)\n`);
  git(root, 'add', '-A');
  assert.equal(main([A], { cwd: root, log: () => {} }), 0);
  assert.equal(read(root, '.github/workflows/ci.yml'), `# builds specs/${A}\n`);
  assert.equal(read(root, 'docs/gamma.md'), `[gamma](../specs/archive/${B}x/spec.md)\n`);
  assert.deepEqual(refreshArchivedReferences({ root, except: A }), [], 'nothing left to refresh');
}));
test('archiveRewrite gives each file exactly what archiveSpec writes, and changes nothing a second time', () => withRepo((root) => {
  write(root, `specs/${B}/tasks.md`, '- [x] T001 Done\n');
  const files = ['README.md', 'specs/README.md', `specs/${A}/plan.md`, `specs/${B}/plan.md`, 'docs/x.md', 'src/Test.cs'];
  const before = Object.fromEntries(files.map((file) => [file, read(root, file)]));
  assert.equal(archiveSpec({ root, folder: A }).exitCode, 0);
  assert.equal(archiveSpec({ root, folder: B }).exitCode, 0);
  for (const file of files) {
    const rewritten = archiveRewrite(before[file], { file, folders: [A, B] });
    assert.equal(rewritten.text, read(root, rewritten.file), file);
    assert.equal(archiveRewrite(rewritten.text, { file: rewritten.file, folders: [B, A] }).text, rewritten.text, `${file} is stable`);
  }
  assert.equal(archiveRewrite('x', { file: `specs/${A}/plan.md`, folders: [A] }).file, `specs/archive/${A}/plan.md`);
  git(root, 'add', '-A');
  assert.deepEqual(archivedFolders(defaultGit(root)), [A, B].sort());
}));