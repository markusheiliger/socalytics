import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { checkSpecIssue, main } from './speckit-issue.mjs';

const WORKFLOWS = new URL('../workflows/', import.meta.url);

function client(issues) {
  return {
    async getIssue(number) {
      if (!issues[number]) throw new Error('Not Found');
      return issues[number];
    },
  };
}

const ISSUES = {
  5: { number: 5, labels: [{ name: 'speckit:spec' }, { name: 'speckit:stage:tasked' }] },
  6: { number: 6, labels: [{ name: 'bug' }] },
  7: { number: 7, labels: [{ name: 'speckit:spec' }], pull_request: { url: 'x' } },
  8: { number: 8, labels: ['speckit:spec'] },
};

test('checkSpecIssue accepts only issues labelled speckit:spec', async () => {
  const github = client(ISSUES);
  assert.deepEqual(await checkSpecIssue(github, '5'), { ok: true, number: 5 });
  assert.deepEqual(await checkSpecIssue(github, ' 8 '), { ok: true, number: 8 });
  assert.match((await checkSpecIssue(github, '6')).reason, /#6 is not a spec twin issue: it is not labelled `speckit:spec`/);
  assert.match((await checkSpecIssue(github, '7')).reason, /#7 is a pull request/);
  assert.match((await checkSpecIssue(github, '9')).reason, /#9 could not be read \(Not Found\)/);
  for (const value of ['', undefined, '0', '-1', '5a', '5\n::warning::x', '12345678901']) {
    assert.deepEqual(await checkSpecIssue(github, value), { ok: false, reason: 'The input `issue` is not an issue number' }, String(value));
  }
});

test('the check step sets the output spec and fails for any other issue', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-issue-'));
  try {
    const output = path.join(dir, 'output');
    const lines = [];
    const run = (issue) => main(['check'], { env: { SPECKIT_ISSUE: issue, GITHUB_OUTPUT: output }, client: client(ISSUES), log: (line) => lines.push(line) });
    assert.equal(await run('5'), 0);
    assert.equal(await run('6'), 1);
    assert.equal(readFileSync(output, 'utf8'), 'spec=true\nspec=false\n');
    assert.match(lines.at(-1), /^::error::#6 is not a spec twin issue/);
    await assert.rejects(() => main(['other'], { env: {}, client: client(ISSUES) }), /Usage: speckit-issue\.mjs check/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('every Spec Kit workflow takes an issue input, not twin, and checks it first', () => {
  const sources = readdirSync(WORKFLOWS).filter((name) => name.startsWith('speckit-') && (name.endsWith('.yml') && !name.endsWith('.lock.yml') || name.endsWith('.md')));
  const withIssue = [];
  for (const name of sources) {
    const text = readFileSync(new URL(name, WORKFLOWS), 'utf8').replace(/\r\n/g, '\n');
    assert.doesNotMatch(text, /inputs\.twin\b|^\s+twin:\s*$/m, `${name} still takes a twin input`);
    if (!/^\s+issue:\n\s+description: Spec twin issue number/m.test(text)) continue;
    withIssue.push(name);
    // The workflows of the implementation chain check it in the reusable Spec Kit implement prepare they start with.
    if (/uses: \.\/\.github\/workflows\/speckit-implement-prepare\.yml\n[\s\S]*?issue: \$\{\{ inputs\.issue \}\}/.test(text)) continue;
    assert.match(text, /- name: Check the spec issue\n\s+id: issue\n[\s\S]*?SPECKIT_ISSUE: \$\{\{ inputs\.issue \}\}\n\s+run: node "?[^\n]*\.github\/scripts\/speckit-issue\.mjs"? check\n/, `${name} checks its issue`);
  }
  assert.deepEqual(withIssue.sort(), ['speckit-analyze.md', 'speckit-author.yml', 'speckit-diagnose.md', 'speckit-implement-decide.yml', 'speckit-implement-group.yml', 'speckit-implement-merge.yml', 'speckit-implement-prepare.yml', 'speckit-implement-repair.yml', 'speckit-implement-task.yml', 'speckit-implement-verify.yml']);
});
