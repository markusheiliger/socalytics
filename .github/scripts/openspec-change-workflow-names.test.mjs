import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';

import {
  applyWorkflowJobNames,
  WORKFLOW_JOB_NAMES,
} from './openspec-change-workflow-names.mjs';

const workflowsRoot = path.join(process.cwd(), '.github', 'workflows');

function workflowWithJobs(overrides = new Map()) {
  const jobs = [...WORKFLOW_JOB_NAMES].map(([jobId, displayName]) => {
    const name = overrides.has(jobId) ? overrides.get(jobId) : null;
    return [
      `  ${jobId}:`,
      ...(name === null ? [] : [`    name: ${name ?? displayName}`]),
      '    runs-on: ubuntu-latest',
    ].join('\n');
  });
  return `name: Test\njobs:\n${jobs.join('\n')}\n`;
}

test('generated workflow naming is complete and idempotent', () => {
  const normalized = applyWorkflowJobNames(workflowWithJobs());
  for (const [jobId, displayName] of WORKFLOW_JOB_NAMES) {
    assert.match(normalized, new RegExp(`  ${jobId}:\\n    name: ${displayName}\\n`));
  }
  assert.equal(applyWorkflowJobNames(normalized), normalized);
});

test('generated workflow naming rejects changed compiler structure and names', () => {
  assert.throws(
    () => applyWorkflowJobNames(workflowWithJobs().replace('  activation:', '  renamed_activation:')),
    /Expected generated job "activation" exactly once, found 0/,
  );
  assert.throws(
    () => applyWorkflowJobNames(workflowWithJobs(new Map([['agent', 'Unexpected name']]))),
    /unexpected display name "Unexpected name"/,
  );
});

test('generated workflow naming preserves unrelated workflow content', () => {
  const source = workflowWithJobs().replace(
    '    runs-on: ubuntu-latest',
    '    permissions:\n      contents: read\n    runs-on: ubuntu-latest',
  );
  const normalized = applyWorkflowJobNames(source);
  assert.match(normalized, /permissions:\n      contents: read\n    runs-on: ubuntu-latest/);
});

test('repository workflows expose useful names, scoped Git auth, and the safe DAG', async () => {
  const files = await Promise.all([
    readFile(path.join(workflowsRoot, 'copilot-setup-steps.yml'), 'utf8'),
    readFile(path.join(workflowsRoot, 'openspec-orchestrator.yml'), 'utf8'),
    readFile(path.join(workflowsRoot, 'openspec-prepare.md'), 'utf8'),
    readFile(path.join(workflowsRoot, 'openspec-prepare.lock.yml'), 'utf8'),
  ]);
  const [setup, orchestrator, source, lock] = files.map((content) => content.replaceAll('\r\n', '\n'));

  assert.match(setup, /copilot-setup-steps:\n    name: Prepare the Copilot coding agent environment/);
  assert.match(setup, /uses: \.\/\.github\/actions\/setup-openspec/);
  assert.match(setup, /markdownlint-cli2@0\.23\.3/);
  assert.match(setup, /markdown-link-check@3\.15\.0/);
  assert.match(setup, /markdownlint-cli2 --version/);
  assert.match(setup, /markdown-link-check --version/);
  assert.match(orchestrator, /observe:\n    name: Read current state/);
  assert.match(orchestrator, /node \.github\/scripts\/openspec-change-orchestrator\.mjs observe/);
  assert.doesNotMatch(orchestrator, /--watch/);
  assert.equal(applyWorkflowJobNames(lock), lock);
  assert.match(source, /group: openspec-prepare[\s\S]*queue: max/);
  assert.equal((source.match(/GIT_CONFIG_VALUE_0="AUTHORIZATION: basic \$authorization"/g) ?? []).length, 3);
  assert.equal((source.match(/GH_TOKEN: \$\{\{ github\.token \}\}/g) ?? []).length, 3);
  assert.doesNotMatch(source, /\$GITHUB_ENV/);
  assert.doesNotMatch(source, /persist-credentials:\s*true/);

  assert.match(lock, /agent:\n    name: Infer OpenSpec change dependencies\n    needs:\n      - activation\n      - synchronize/);
  assert.match(lock, /reconcile_openspec_dependencies:\n    name: Reconcile dependencies and persist the checkpoint\n    needs:\n      - agent\n      - detection/);
  assert.match(lock, /safe_outputs:\n    name: Process remaining safe outputs\n    needs:\n      - activation\n      - agent\n      - detection/);
  assert.match(lock, /conclusion:\n    name: Report the reconciliation outcome\n    needs:[\s\S]*- reconcile_openspec_dependencies\n      - safe_outputs/);
  assert.equal((lock.match(/GIT_CONFIG_VALUE_0=/g) ?? []).length, 3);
  assert.equal((lock.match(/AUTHORIZATION: basic \$authorization/g) ?? []).length, 3);
  assert.doesNotMatch(lock, /persist-credentials:\s*true/);
});
