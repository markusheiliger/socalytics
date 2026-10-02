import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const workflow = readFileSync(new URL('../workflows/openspec.yml', import.meta.url), 'utf8').replaceAll('\r\n', '\n');
const lines = workflow.split('\n');

function jobBlocks() {
  const start = lines.indexOf('jobs:');
  assert.ok(start > 0, 'workflow must define jobs');
  const blocks = [];
  for (let index = start + 1; index < lines.length; index += 1) {
    const match = lines[index].match(/^ {2}([a-z][a-z0-9_-]*):$/);
    if (match) blocks.push({ id: match[1], lines: [] });
    else if (blocks.length > 0) blocks.at(-1).lines.push(lines[index]);
  }
  return blocks;
}

function stepBlocks(job) {
  const steps = [];
  let inSteps = false;
  for (const line of job.lines) {
    if (line === '    steps:') {
      inSteps = true;
      continue;
    }
    if (!inSteps) continue;
    if (/^ {4}\S/.test(line)) break;
    if (line.startsWith('      - ')) steps.push([line]);
    else if (steps.length > 0) steps.at(-1).push(line);
  }
  return steps;
}

const EXPECTED_JOBS = new Map([
  ['observe', 'Read current state'],
  ['credit', 'Check agent result'],
  ['plan', 'Decide next steps'],
  ['admit', 'Start change'],
  ['apply', 'Apply next task'],
  ['verify', 'Verify change'],
  ['sync', 'Sync specs'],
  ['archive', 'Archive change'],
  ['gate', 'Ask for human input'],
  ['finalize', 'Finish change'],
  ['publish', 'Update PR and issue status'],
]);

test('names the workflow openspec and titles every run', () => {
  assert.match(workflow, /^name: openspec$/m);
  assert.match(workflow, /^run-name: >-$/m);
  for (const title of ['enqueued #', 'merged', 'new commits on', 'command on #', 'watchdog', 'manual (dry run)']) {
    assert.ok(workflow.includes(title), `run-name must cover "${title}"`);
  }
});

test('gives every job a readable static name without matrix expressions', () => {
  const jobs = jobBlocks();
  assert.deepEqual(jobs.map(({ id }) => id), [...EXPECTED_JOBS.keys()]);
  for (const job of jobs) {
    const name = job.lines.find((line) => line.startsWith('    name: '))?.slice('    name: '.length);
    assert.equal(name, EXPECTED_JOBS.get(job.id), `job ${job.id} must be named`);
    assert.doesNotMatch(name, /matrix\.|\$\{\{/);
  }
});

test('gives every step a readable name', () => {
  for (const job of jobBlocks()) {
    const steps = stepBlocks(job);
    assert.ok(steps.length > 0, `job ${job.id} must have steps`);
    for (const step of steps) {
      const named = step[0].startsWith('      - name: ') || step.some((line) => line.startsWith('        name: '));
      assert.ok(named, `job ${job.id} has an unnamed step: ${step[0].trim()}`);
    }
  }
});

test('keeps trusted execution, least privilege, and the repository-wide lock', () => {
  assert.match(workflow, /^permissions: \{\}$/m);
  assert.match(workflow, /&& 'openspec'\n\s+\|\| format\('openspec-ignored-\{0\}', github\.run_id\)/);
  assert.match(workflow, /cancel-in-progress: false/);
  assert.doesNotMatch(workflow, /^\s+push:\n\s+branches: \[openspec/m);
  assert.match(workflow, /pull_request_target:\n\s+types: \[synchronize, closed\]/);
  for (const job of jobBlocks()) {
    const block = job.lines.join('\n');
    assert.match(block, /ref: main/, `job ${job.id} must check out the trusted controller`);
    assert.match(block, /persist-credentials: false/, `job ${job.id} must not persist credentials`);
    assert.match(block, /\n {4}permissions:\n/, `job ${job.id} must declare permissions`);
    if (block.includes('COPILOT_AGENT_TOKEN')) {
      assert.match(block, /\n {4}environment: openspec\n/, `job ${job.id} must use the openspec environment for the agent token`);
    }
    assert.doesNotMatch(block, /run: .*\$\{\{/, `job ${job.id} must pass expressions through env, not inline in run`);
  }
});
