import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';

import { AGENT_WORKFLOW_JOB_NAMES, applyWorkflowJobNames } from './openspec-change-workflow-names.mjs';
import { AGENT_WORKFLOW, sessionStepLabel } from './openspec-change-orchestrator.mjs';

const workflow = readFileSync(new URL('../workflows/openspec-orchestrator.yml', import.meta.url), 'utf8').replaceAll('\r\n', '\n');
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
  ['verify-checkpoint', 'Verify checkpoint (${{ matrix.change }}, ${{ matrix.sha }})'],
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

test('names the workflow OpenSpec orchestrator and titles every run', () => {
  assert.match(workflow, /^name: OpenSpec orchestrator$/m);
  assert.match(workflow, /^run-name: >-$/m);
  for (const title of ['enqueued #', 'merged', 'new commits on', 'command on #', 'agent run finished on #', 'watchdog', 'manual (dry run)']) {
    assert.ok(workflow.includes(title), `run-name must cover "${title}"`);
  }
});

test('gives every job a readable static name without matrix expressions', () => {
  const jobs = jobBlocks();
  assert.deepEqual(jobs.map(({ id }) => id), [...EXPECTED_JOBS.keys()]);
  for (const job of jobs) {
    const name = job.lines.find((line) => line.startsWith('    name: '))?.slice('    name: '.length);
    assert.equal(name, EXPECTED_JOBS.get(job.id), `job ${job.id} must be named`);
    // The verification caller's name is the contract the credit step matches on.
    if (job.id !== 'verify-checkpoint') assert.doesNotMatch(name, /matrix\.|\$\{\{/);
  }
});

test('gives every step a readable name', () => {
  for (const job of jobBlocks()) {
    if (job.lines.some((line) => line.startsWith('    uses: '))) continue;
    const steps = stepBlocks(job);
    assert.ok(steps.length > 0, `job ${job.id} must have steps`);
    for (const step of steps) {
      const named = step[0].startsWith('      - name: ') || step.some((line) => line.startsWith('        name: '));
      assert.ok(named, `job ${job.id} has an unnamed step: ${step[0].trim()}`);
    }
  }
});

test('keeps downstream jobs running when an upstream matrix job was skipped', () => {
  // Without a status function GitHub adds an implicit success(), which is false after any skipped ancestor.
  for (const job of jobBlocks()) {
    if (job.id === 'observe') continue;
    const condition = job.lines.find((line) => line.startsWith('    if: '));
    assert.match(condition ?? '', /^ {4}if: \$\{\{ !cancelled\(\) && needs\.\w+\.result == 'success'/, `job ${job.id} must guard with !cancelled()`);
  }
});

test('checks out the pull request branch only after a successful prompt step', () => {
  const step = agentSource.slice(agentSource.indexOf('  - name: Check out the pull request branch at the dispatched commit'), agentSource.indexOf('safe-outputs:'));
  assert.match(step, /if: \$\{\{ steps\.prepare\.outcome == 'success' \}\}/);
  assert.doesNotMatch(step, /fromJSON/);
});

test('wakes on every finished agent run, not only when the agent calls wake_controller', () => {
  // workflow_run does not fire for runs dispatched with GITHUB_TOKEN; repository_dispatch does.
  assert.match(workflow, /\n  repository_dispatch:\n    types: \[openspec-agent-finished\]\n/);
  assert.doesNotMatch(workflow, /\n  workflow_run:/);
  assert.match(agentSource, /\njobs:\n  conclusion:\n[\s\S]*pre-steps:\n      - name: Wake the OpenSpec orchestrator after the run\n[\s\S]*event_type: 'openspec-agent-finished'/);
  const conclusion = agentLock.slice(agentLock.indexOf('\n  conclusion:\n'));
  assert.match(conclusion.slice(0, 1200), /if: >\n\s+always\(\)/);
  assert.match(conclusion, /name: Wake the OpenSpec orchestrator after the run\n\s+uses: actions\/github-script@/);
  assert.doesNotMatch(conclusion.slice(1, conclusion.indexOf('Wake the OpenSpec orchestrator after the run')), /\n  [a-z_]+:\n    name: /, 'the wake must be a step of the conclusion job');
});

test('keeps trusted execution, least privilege, and the repository-wide lock', () => {
  assert.match(workflow, /^permissions: \{\}$/m);
  assert.match(workflow, /&& 'openspec'\n\s+\|\| format\('openspec-ignored-\{0\}', github\.run_id\)/);
  assert.match(workflow, /cancel-in-progress: false/);
  assert.doesNotMatch(workflow, /^\s+push:\n\s+branches: \[openspec/m);
  assert.match(workflow, /pull_request_target:\n\s+types: \[synchronize, closed\]/);
  for (const job of jobBlocks()) {
    const block = job.lines.join('\n');
    if (job.id === 'verify-checkpoint') {
      assert.match(block, /\n {4}permissions:\n {6}contents: read\n {4}strategy:/, 'the verification job must only read repository contents');
      assert.match(block, /\n {4}uses: \.\/\.github\/workflows\/verification\.yml\n/, 'the verification job must call the repository verification workflow');
      assert.doesNotMatch(block, /secrets|environment:|GITHUB_TOKEN/, 'the verification job must not receive secrets');
      continue;
    }
    assert.match(block, /ref: main/, `job ${job.id} must check out the trusted controller`);
    assert.match(block, /persist-credentials: false/, `job ${job.id} must not persist credentials`);
    assert.match(block, /\n {4}permissions:\n/, `job ${job.id} must declare permissions`);
    if (block.includes('COPILOT_AGENT_TOKEN')) {
      assert.match(block, /\n {4}environment: openspec\n/, `job ${job.id} must use the openspec environment for the agent token`);
    }
    assert.doesNotMatch(block, /run: .*\$\{\{/, `job ${job.id} must pass expressions through env, not inline in run`);
  }
});

const agentSource = readFileSync(new URL('../workflows/openspec-agent.md', import.meta.url), 'utf8').replaceAll('\r\n', '\n');
const agentLock = readFileSync(new URL('../workflows/openspec-agent.lock.yml', import.meta.url), 'utf8').replaceAll('\r\n', '\n');

test('dispatches the compiled agentic workflow the controller names', () => {
  assert.equal(AGENT_WORKFLOW, 'openspec-agent.lock.yml');
  assert.match(agentSource, /^run-name: "OpenSpec agent · #\$\{\{ inputs\.pr \}\} \$\{\{ inputs\.branch \}\} · \$\{\{ inputs\.step \|\| 'session' \}\} · \$\{\{ inputs\.dispatch_id \}\}"$/m);
  assert.match(agentLock, /^run-name: "OpenSpec agent · #\$\{\{ inputs\.pr \}\} \$\{\{ inputs\.branch \}\} · \$\{\{ inputs\.step \|\| 'session' \}\} · \$\{\{ inputs\.dispatch_id \}\}"$/m);
  assert.match(agentSource, /check-branch-protection: false/);
  assert.match(agentSource, /workflow_dispatch:\n    inputs:\n      pr:[\s\S]*dispatch_id:[\s\S]*step:/);
});

test('labels agent runs with the operation, task, and retry attempt', () => {
  assert.equal(sessionStepLabel({ operation: 'apply', task: { id: '2.3' }, attempt: 1 }), 'apply 2.3');
  assert.equal(sessionStepLabel({ operation: 'apply', task: { id: '2.3' }, attempt: 2 }), 'apply 2.3 (attempt 2)');
  assert.equal(sessionStepLabel({ operation: 'verify', task: null, attempt: 1 }), 'verify');
});

test('runs the agent without a personal access token and without push rights', () => {
  assert.match(agentSource, /^  copilot-requests: write$/m);
  assert.doesNotMatch(agentSource, /COPILOT_AGENT_TOKEN|secrets\./);
  assert.doesNotMatch(agentSource, /^  contents: write$/m);
  assert.match(agentSource, /push-to-pull-request-branch:\n    target: \$\{\{ inputs\.pr \}\}\n    required-title-prefix: "OpenSpec: "/);
  assert.match(agentSource, /policy: blocked/);
  assert.doesNotMatch(agentSource, /exclude:[\s\S]*- \.github\//);
  assert.match(agentSource, /strict: true/);
});

test('wakes the controller after safe outputs finish', () => {
  assert.match(agentSource, /wake-controller:[\s\S]*needs: safe_outputs[\s\S]*gh workflow run openspec-orchestrator\.yml[^\n]*-f reason="agent finished on #\$PR"/);
  assert.match(workflow, /inputs\.reason && format\('OpenSpec orchestrator · \{0\}', inputs\.reason\)/);
  assert.match(agentLock, /gh workflow run openspec-orchestrator\.yml --repo "\$GITHUB_REPOSITORY" --ref main -f reason="agent finished on #\$PR"\n/);
  assert.match(agentLock, /wake_controller:\n    name: Wake the OpenSpec orchestrator\n    needs:\n      - agent\n      - detection\n      - safe_outputs/);
});

test('keeps the agent workflow compiled with the pinned gh-aw version and readable job names', () => {
  assert.match(agentLock, /"compiler_version":"v0\.89\.21"/);
  assert.equal(applyWorkflowJobNames(agentLock, AGENT_WORKFLOW_JOB_NAMES), agentLock);
  const pre = agentSource.slice(agentSource.indexOf('pre-agent-steps:'), agentSource.indexOf('safe-outputs:'));
  for (const step of pre.split('\n  - ').slice(1)) {
    assert.match(step, /^name: /, `pre-agent step must be named: ${step.split('\n')[0]}`);
  }
});

test('builds the agent prompt in a trusted step from the dispatched state', () => {
  assert.match(agentSource, /openspec-change-orchestrator\.mjs agent-prompt \\\n\s+--pr "\$PR" --dispatch-id "\$DISPATCH_ID"/);
  assert.match(agentSource, /run_verification` tool\nis available/);
});

const workflowsDirectory = new URL('../workflows/', import.meta.url);
const scriptsDirectory = new URL('./', import.meta.url);
const readText = (url) => readFileSync(url, 'utf8').replaceAll('\r\n', '\n');
const toolchainSource = readText(new URL('shared/repository-toolchain.md', workflowsDirectory));
const verificationSource = readText(new URL('verification.yml', workflowsDirectory));
const setupOpenSpec = readText(new URL('../actions/setup-openspec/action.yml', workflowsDirectory));
const setupToolchain = readText(new URL('../actions/setup-toolchain/action.yml', workflowsDirectory));
const dotnetTest = readText(new URL('../actions/dotnet-test/action.yml', workflowsDirectory));

test('keeps the openspec-* workflows and scripts free of repository specifics', () => {
  const generic = [
    ...readdirSync(workflowsDirectory).filter((name) => /^openspec-.*\.(yml|md)$/.test(name) && !name.endsWith('.lock.yml'))
      .map((name) => new URL(name, workflowsDirectory)),
    ...readdirSync(scriptsDirectory).filter((name) => /^openspec-.*\.mjs$/.test(name) && !name.endsWith('.test.mjs'))
      .map((name) => new URL(name, scriptsDirectory)),
  ];
  assert.ok(generic.length > 5);
  for (const url of generic) {
    assert.doesNotMatch(readText(url), /dotnet|src\/platform|SocAlytics|Testcontainers|setup-dotnet/i, `${url.pathname.split('/').at(-1)} must stay generic`);
  }
});

test('installs tooling only through the shared composite actions', () => {
  assert.match(setupOpenSpec, /node-version: 24/);
  assert.match(setupOpenSpec, /npm install --global\n\s+@fission-ai\/openspec@\d+\.\d+\.\d+\n\s+markdownlint-cli2@\d+\.\d+\.\d+\n\s+markdown-link-check@\d+\.\d+\.\d+\n/);
  assert.match(setupToolchain, /uses: actions\/setup-dotnet@/);
  const consumers = [
    ...readdirSync(workflowsDirectory).filter((name) => /^(openspec-.*\.(yml|md)|verification\.yml|copilot-setup-steps\.yml)$/.test(name) && !name.endsWith('.lock.yml')),
    'shared/repository-toolchain.md',
  ];
  for (const name of consumers) {
    const source = readText(new URL(name, workflowsDirectory));
    assert.doesNotMatch(source, /actions\/setup-node@|actions\/setup-dotnet@|@fission-ai\/openspec@|markdownlint-cli2@|markdown-link-check@/, `${name} must use the composite actions`);
  }
  assert.match(workflow, /uses: \.\/\.github\/actions\/setup-openspec/);
  assert.match(agentSource, /uses: \.\/\.github\/actions\/setup-openspec/);
  assert.match(toolchainSource, /uses: \.\/\.github\/actions\/setup-toolchain/);
  assert.match(verificationSource, /uses: \.\/\.github\/actions\/setup-toolchain/);
  const copilotSetup = readText(new URL('copilot-setup-steps.yml', workflowsDirectory));
  assert.match(copilotSetup, /uses: \.\/\.github\/actions\/setup-openspec/);
  assert.match(copilotSetup, /uses: \.\/\.github\/actions\/setup-toolchain/);
});

test('imports the repository toolchain into the agent with a gated, token-free verification tool', () => {
  assert.match(agentSource, /^imports:\n(?:  #.*\n)?  - shared\/repository-toolchain\.md$/m);
  assert.doesNotMatch(toolchainSource, /^on:/m);
  assert.match(toolchainSource, /mcp-scripts:\n  run_verification:/);
  assert.match(toolchainSource, /HOST_TESTS: \$\{\{ vars\.OPENSPEC_AGENT_HOST_TESTS \}\}[\s\S]*!= "true"[\s\S]*env -i /);
  assert.match(agentLock, /"name": "run_verification"/);
});

test('keeps the repository verification workflow on the orchestrator contract', () => {
  assert.match(verificationSource, /^on:\n  workflow_call:\n    inputs:\n      change:[\s\S]*      sha:[\s\S]*      baseline:/m);
  assert.match(verificationSource, /^permissions:\n  contents: read$/m);
  assert.doesNotMatch(verificationSource, /\$\{\{\s*secrets\.|GITHUB_TOKEN|github\.token/);
  assert.match(verificationSource, /ref: \$\{\{ inputs\.sha \}\}\n {10}path: checkpoint/);
  assert.equal((verificationSource.match(/persist-credentials: false/g) ?? []).length, 2);
  assert.match(verificationSource, /name: openspec-verification-\$\{\{ inputs\.change \}\}/);
  assert.match(verificationSource, /openspec-verification\/verification\.txt/);
});

test('keeps verification.yml the visible test runner with dedicated runner actions', () => {
  const steps = verificationSource.slice(verificationSource.indexOf('    steps:\n'));
  const order = [
    'name: Check out the checkpoint commit',
    'name: Check whether the checkpoint changes the platform',
    'uses: ./.github/actions/setup-toolchain',
    'uses: ./.github/actions/dotnet-test',
    'name: Upload the verification result',
  ].map((marker) => steps.indexOf(marker));
  assert.ok(order.every((index) => index > 0), 'verification.yml must show every verification step');
  assert.deepEqual([...order].sort((left, right) => left - right), order, 'verification steps must keep their order');
  assert.match(steps, /git diff --name-only "\$BASELINE" "\$SHA" -- src\/platform\//);
  assert.match(steps, /uses: \.\/\.github\/actions\/dotnet-test\n {8}with:\n {10}working-directory: checkpoint\n {10}project: src\/platform\/SocAlytics\.Platform\.slnx\n/);
  assert.doesNotMatch(verificationSource, /run-verification|verify\.sh/);
  // Runner actions run one technology's tests and know nothing about this repository.
  assert.doesNotMatch(dotnetTest, /src\/platform|SocAlytics|openspec/i);
  assert.match(dotnetTest, /dotnet "\$\{args\[@\]\}"/);
  assert.deepEqual(readdirSync(new URL('../actions/', workflowsDirectory)).sort(), ['dotnet-test', 'setup-openspec', 'setup-toolchain']);
  assert.match(toolchainSource, /run_verification:[\s\S]*args=\(test src\/platform\/SocAlytics\.Platform\.slnx[\s\S]*env -i /);
});
