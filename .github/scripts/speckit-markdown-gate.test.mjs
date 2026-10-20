import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
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
