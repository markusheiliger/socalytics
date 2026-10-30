import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { markdownGate, parseMarkdownFindings } from './speckit-markdown-gate.mjs';

test('parses markdownlint diagnostics and broken links from the Markdown check output', () => {
  const output = [
    'markdownlint-cli2 v0.23.3 (markdownlint v0.41.1)',
    'specs/f/spec.md:7:425 error MD037/no-space-in-emphasis Spaces inside emphasis markers [Context: "s _"]',
    'docs/architecture/a.md:12 MD013/line-length Line length',
    'FILE: specs/f/plan.md',
    '  [✖] ../g/spec.md → Status: 400',
    'Summary: 2 issues in 2 files',
  ].join('\n');
  assert.deepEqual(parseMarkdownFindings(output), [
    'specs/f/spec.md:7:425 MD037/no-space-in-emphasis Spaces inside emphasis markers [Context: "s _"]',
    'docs/architecture/a.md:12 MD013/line-length Line length',
    'specs/f/plan.md: broken link ../g/spec.md (Status: 400)',
  ]);
});

test('checks only the changed Markdown under specs/ and docs/architecture/, written over the trusted checkout', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-gate-'));
  try {
    const client = {
      async listPullRequestFiles() {
        return [
          { filename: 'specs/f/spec.md', status: 'added' },
          { filename: 'docs/architecture/a.md', status: 'modified' },
          { filename: 'specs/f/old.md', status: 'removed' },
          { filename: '.markdownlint-cli2.cjs', status: 'added' },
          { filename: 'README.md', status: 'modified' },
          { filename: 'specs/../x.md', status: 'added' },
        ];
      },
      async getFileContent(file, ref) {
        assert.equal(ref, 'sha-head');
        return `# ${file}\n`;
      },
      async listRepositoryFiles(ref) {
        assert.equal(ref, 'sha-head');
        return ['specs/f/spec.md', 'docs/architecture/a.md'];
      },
    };
    let listed = null;
    const run = (cwd, listFile) => {
      assert.equal(cwd, root);
      listed = readFileSync(listFile, 'utf8').trim().split('\n');
      return { status: 1, stdout: 'specs/f/spec.md:7:425 error MD037/no-space-in-emphasis Spaces inside emphasis markers\n', stderr: 'Markdown diagnostics failed with exit code 1.' };
    };
    const reasons = await markdownGate(client, { pull: { number: 12, head: { sha: 'sha-head' } }, root, run });
    assert.deepEqual(listed, ['specs/f/spec.md', 'docs/architecture/a.md']);
    assert.equal(readFileSync(path.join(root, 'specs/f/spec.md'), 'utf8'), '# specs/f/spec.md\n');
    assert.equal(existsSync(path.join(root, '.markdownlint-cli2.cjs')), false, 'configuration and code never reach the job');
    assert.deepEqual(reasons, ['the Markdown check fails; fix each problem so `node .github/scripts/check-markdown.mjs` passes: specs/f/spec.md:7:425 MD037/no-space-in-emphasis Spaces inside emphasis markers']);

    assert.deepEqual(await markdownGate(client, { pull: { number: 12, head: { sha: 'sha-head' } }, root, run: () => ({ status: 0, stdout: '', stderr: '' }) }), []);
    const unparsed = await markdownGate(client, { pull: { number: 12, head: { sha: 'sha-head' } }, root, run: () => ({ status: 1, stdout: '', stderr: 'Markdown diagnostics requires "markdownlint-cli2".' }) });
    assert.match(unparsed[0], /fails for the changed Markdown files: Markdown diagnostics requires/);
    const none = { async listPullRequestFiles() { return [{ filename: 'src/x.cs', status: 'added' }]; } };
    assert.deepEqual(await markdownGate(none, { pull: { number: 12, head: { sha: 'sha-head' } }, root, run: () => assert.fail('nothing to check') }), []);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('#420: unchanged archived spec Markdown comes from the pinned amendment head, never its tooling', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-gate-archive-'));
  const folder = 'specs/archive/20261023-085421-cv-inference-environment';
  const changed = `${folder}/tasks.md`;
  const contents = new Map([
    [changed, '# Tasks\n\n[Plan](plan.md), [Data](data-model.md), [Quickstart](quickstart.md)\n'],
    [`${folder}/plan.md`, '# Plan from the amendment head\n'],
    [`${folder}/data-model.md`, '# Data model\n'],
    [`${folder}/quickstart.md`, '# Quickstart\n'],
    [`${folder}/contracts/environment-interface.md`, '# Environment interface\n'],
    [`${folder}/.markdownlint-cli2.cjs`, 'throw new Error("untrusted configuration");'],
    [`${folder}/check.mjs`, 'throw new Error("untrusted code");'],
    ['.markdownlint-cli2.cjs', 'throw new Error("untrusted root configuration");'],
    ['specs/../outside.md', '# Unsafe path\n'],
    ['specs/..\\outside.md', '# Unsafe Windows path\n'],
  ]);
  try {
    writeFileSync(path.join(root, '.markdownlint-cli2.cjs'), 'trusted configuration');
    const fetched = [];
    const client = {
      async listPullRequestFiles() { return [{ filename: changed, status: 'modified' }]; },
      async listRepositoryFiles(ref) {
        assert.equal(ref, 'sha-head');
        return [...contents.keys()];
      },
      async getFileContent(file, ref) {
        assert.equal(ref, 'sha-head');
        fetched.push(file);
        assert.ok(file.endsWith('.md'));
        return contents.get(file);
      },
    };
    const reasons = await markdownGate(client, {
      pull: { number: 420, head: { sha: 'sha-head', ref: 'moving-branch' } },
      root,
      run(cwd, listFile) {
        assert.deepEqual(readFileSync(listFile, 'utf8').trim().split('\n'), [changed]);
        for (const file of ['plan.md', 'data-model.md', 'quickstart.md', 'contracts/environment-interface.md']) {
          assert.equal(readFileSync(path.join(cwd, folder, file), 'utf8'), contents.get(`${folder}/${file}`));
        }
        assert.equal(readFileSync(path.join(cwd, '.markdownlint-cli2.cjs'), 'utf8'), 'trusted configuration');
        assert.equal(existsSync(path.join(cwd, folder, '.markdownlint-cli2.cjs')), false);
        assert.equal(existsSync(path.join(cwd, folder, 'check.mjs')), false);
        assert.equal(existsSync(path.join(cwd, 'outside.md')), false);
        return { status: 0 };
      },
    });
    assert.deepEqual(reasons, []);
    assert.equal(fetched.length, 5);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('a linked document removed on the PR head cannot pass using a stale trusted-checkout copy', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-gate-removed-'));
  try {
    mkdirSync(path.join(root, 'specs/f'), { recursive: true });
    writeFileSync(path.join(root, 'specs/f/plan.md'), '# Stale plan\n');
    const client = {
      async listPullRequestFiles() { return [{ filename: 'specs/f/tasks.md', status: 'modified' }]; },
      async listRepositoryFiles() { return ['specs/f/tasks.md']; },
      async getFileContent() { return '# Tasks\n\n[Plan](plan.md)\n'; },
    };
    const reasons = await markdownGate(client, {
      pull: { number: 12, head: { sha: 'sha-head' } },
      root,
      run(cwd) {
        return existsSync(path.join(cwd, 'specs/f/plan.md'))
          ? { status: 0 }
          : { status: 1, stdout: 'FILE: specs/f/tasks.md\n[✖] plan.md → Status: 400\n' };
      },
    });

    assert.match(reasons[0], /specs\/f\/tasks\.md: broken link plan\.md/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('missing snapshot content and repository listing failures cannot become passing Markdown checks', async () => {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-gate-missing-'));
  try {
    const client = {
      async listPullRequestFiles() { return [{ filename: 'specs/f/tasks.md', status: 'modified' }]; },
      async listRepositoryFiles() { return ['specs/f/tasks.md', 'specs/f/plan.md']; },
      async getFileContent(file) { return file.endsWith('/plan.md') ? null : '# Tasks\n'; },
    };
    const options = { pull: { number: 12, head: { sha: 'sha-head' } }, root, run: () => assert.fail('incomplete context must not be checked') };
    await assert.rejects(() => markdownGate(client, options), /context file specs\/f\/plan\.md could not be read at sha-head/);
    await assert.rejects(() => markdownGate({
      ...client,
      async listRepositoryFiles() { return []; },
    }, options), /Changed Markdown file specs\/f\/tasks\.md is missing from sha-head/);
    await assert.rejects(() => markdownGate({
      ...client,
      async listRepositoryFiles() { throw new Error('listing unavailable'); },
    }, options), /listing unavailable/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
