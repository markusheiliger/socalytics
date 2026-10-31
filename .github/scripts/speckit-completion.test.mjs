import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  RUNS_FILE,
  bindEvidence,
  canonicalRequirement,
  completionNote,
  landCompletion,
  main,
  requirementLabel,
  runVerification,
  taskDigest,
  validateAssessment,
  validateClaim,
} from './speckit-completion.mjs';

const EVIDENCE_FILE = 'src/Tests/Evidence.md';
const EVIDENCE = '# Evidence\n\n## T004\n\nThe cause is the default-on background worker in the test scenario.\nNo platform defect.\n';
const FILES = {
  [EVIDENCE_FILE]: EVIDENCE,
  'src/Tests/Tests.csproj': '<Project />\n',
  'src/web/package.json': JSON.stringify({ scripts: { test: 'vitest', build: 'vite build' } }),
  'src/sdk/pyproject.toml': '[project]\n',
  'docs/a.md': '# A\n',
};
const readers = {
  read: (file) => (Object.hasOwn(FILES, file) ? { content: FILES[file], blob: `blob-${file}` } : null),
  exists: (file) => Object.hasOwn(FILES, file),
  readText: (file) => FILES[file] ?? null,
};
const CONDITION_REF = { path: EVIDENCE_FILE, lines: [5, 6], excerpt: 'default-on background worker in the test scenario.\nNo platform defect.' };
const TASK_TEXT = '[US1] Only if T004 shows a platform defect, fix it under `src/platform/` and add a test; mark this task done with a note when T004 shows no platform defect';
const HEAD = 'a'.repeat(40);

const claim = (outcome = 'not-applicable', extra = {}) => ({ format: 1, task: 'T006', outcome, rationale: 'T004 found a test-side cause.', ...extra });
const notApplicable = (extra = {}) => ({
  format: 1,
  task: 'T006',
  verdict: 'accept',
  outcome: 'not-applicable',
  rationale: 'The task applies only to a platform defect, and T004 records a test-side cause.',
  obligations: [{ text: 'Fix a platform defect if T004 shows one', disposition: 'condition-false' }],
  condition: { text: 'T004 shows a platform defect', evidence: [CONDITION_REF] },
  ...extra,
});
const verified = (requirements, extra = {}) => ({
  format: 1,
  task: 'T006',
  verdict: 'accept',
  outcome: 'verified',
  rationale: 'The task only reruns the evidence tests.',
  obligations: [{ text: 'Run the evidence tests 20 times', disposition: 'verify' }],
  requirements,
  unsupported: [],
  ...extra,
});
const accepted = (value, outcome) => validateAssessment(value, { task: 'T006', outcome, ...readers });

test('validateClaim requires the task, a known outcome, a rationale, and no unknown fields', () => {
  assert.deepEqual(validateClaim(claim(), { task: 'T006' }), []);
  assert.deepEqual(validateClaim(claim('verified', { evidence: [CONDITION_REF] }), { task: 'T006' }), []);
  const reasons = validateClaim({ format: 2, task: 'T007', outcome: 'skipped', rationale: '', extra: 1 }, { task: 'T006' });
  assert.equal(reasons.length, 5);
  assert.match(reasons.join('\n'), /unknown fields: extra[\s\S]*format 1[\s\S]*"T007", not T006[\s\S]*outcome must be one of verified, not-applicable[\s\S]*rationale/);
  assert.deepEqual(validateClaim('nope', { task: 'T006' }), ['completion.json is not a JSON object']);
});

test('bindEvidence binds citations to tracked content and rejects unsafe, missing, stale, or mismatched ones', () => {
  const bound = bindEvidence([CONDITION_REF], { read: readers.read });
  assert.deepEqual(bound, { reasons: [], refs: [{ ...CONDITION_REF, blob: `blob-${EVIDENCE_FILE}` }] });
  const cases = [
    [{ ...CONDITION_REF, path: '../secret' }, /unsafe or missing path/],
    [{ ...CONDITION_REF, path: '.specify/workflows/runs/r/completion.json' }, /unsafe or missing path/],
    [{ ...CONDITION_REF, path: 'src/missing.md' }, /not a tracked file at the evaluated commit/],
    [{ ...CONDITION_REF, lines: [6, 5] }, /needs lines \[start, end\]/],
    [{ ...CONDITION_REF, lines: [5, 99] }, /cites lines 5-99 .* which has/],
    [{ ...CONDITION_REF, excerpt: 'A platform defect exists.' }, /excerpt does not appear in lines 5-6/],
    [{ ...CONDITION_REF, blob: 'blob-old' }, /changed since the evidence was recorded/],
    [{ ...CONDITION_REF, note: 'x' }, /unknown fields: note/],
  ];
  for (const [ref, reason] of cases) assert.match(bindEvidence([ref], { read: readers.read }).reasons.join('\n'), reason);
  assert.match(bindEvidence('x', { read: readers.read }).reasons[0], /must be a list/);
});

test('canonicalRequirement accepts only runnable requirements in a canonical form', () => {
  assert.deepEqual(canonicalRequirement({ runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', filter: 'FullyQualifiedName~Evidence', runs: 20 }, readers).requirement,
    { runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', filter: 'FullyQualifiedName~Evidence', runs: 20 });
  assert.deepEqual(canonicalRequirement({ runner: 'npm-script', project: 'src/web', script: 'test' }, readers).requirement,
    { runner: 'npm-script', project: 'src/web', script: 'test', args: [], runs: 1 });
  assert.deepEqual(canonicalRequirement({ runner: 'pytest', project: 'src/sdk', args: ['-k', 'probe'], runs: 2 }, readers).requirement,
    { runner: 'pytest', project: 'src/sdk', args: ['-k', 'probe'], runs: 2 });
  assert.deepEqual(canonicalRequirement({ runner: 'environment-verify', mode: 'phase', files: ['docs/a.md'] }, readers).requirement,
    { runner: 'environment-verify', mode: 'phase', files: ['docs/a.md'], runs: 1 });
  const refused = [
    [{ runner: 'shell', command: 'rm -rf /' }, /runner "shell" is not supported/],
    [{ runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', probe: 'x' }, /cannot honor: probe/],
    [{ runner: 'dotnet-test', project: 'src/Missing.csproj' }, /existing \.csproj/],
    [{ runner: 'dotnet-test', project: 'tools/x.csproj' }, /under src\//],
    [{ runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', filter: 'a\nb' }, /filter must be a single line/],
    [{ runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', runs: 0 }, /runs must be an integer from 1 to 50/],
    [{ runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', runs: 51 }, /runs must be an integer from 1 to 50/],
    [{ runner: 'npm-script', project: 'src/web', script: 'deploy' }, /not a script of src\/web\/package\.json/],
    [{ runner: 'npm-script', project: 'src/web', script: 'test', args: ['ok', 'bad\n'] }, /single-line strings/],
    [{ runner: 'pytest', project: 'src/web' }, /pyproject\.toml/],
    [{ runner: 'environment-verify', mode: 'phase', files: ['docs/a.md'], runs: 3 }, /cannot repeat runs/],
    [{ runner: 'environment-verify', mode: 'everything', files: ['docs/a.md'] }, /mode must be one of/],
    [{ runner: 'environment-verify', mode: 'task', files: [] }, /1 to 500 existing repository paths/],
  ];
  for (const [value, reason] of refused) assert.match(canonicalRequirement(value, readers).reasons.join('\n'), reason);
});

test('whole-project dotnet assessments omit absent filters and execute without --filter', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-completion-'));
  try {
    for (const optional of [{}, { filter: undefined }, { filter: null }, { filter: '' }, { filter: '   ' }]) {
      const input = { runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', runs: 2, ...optional };
      const requirement = { runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', runs: 2 };
      assert.deepEqual(canonicalRequirement(input, readers), { requirement });
      const checked = accepted(verified([input]), 'verified');
      assert.deepEqual(checked.reasons, []);
      assert.deepEqual(checked.assessment.requirements, [requirement]);
      const calls = [];
      const record = runVerification({
        workspace: dir, resultDir: dir, requirements: checked.assessment.requirements, head: HEAD, env: {},
        exec: (command, args) => {
          calls.push([command, ...args]);
          return { status: 0, output: '', timedOut: false };
        },
      });
      assert.equal(record.status, 'passed');
      assert.deepEqual(calls, [
        ['dotnet', 'test', 'src/Tests/Tests.csproj'],
        ['dotnet', 'test', 'src/Tests/Tests.csproj', '--no-build'],
      ]);
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('dotnet filter normalization preserves valid filters and rejects invalid raw values', () => {
  const requirement = { runner: 'dotnet-test', project: 'src/Tests/Tests.csproj' };
  for (const filter of [' FullyQualifiedName~Evidence ', 'x'.repeat(1000)]) {
    assert.deepEqual(canonicalRequirement({ ...requirement, filter }, readers),
      { requirement: { ...requirement, filter, runs: 1 } });
  }
  for (const filter of [false, 0, {}, [], '\n', '\r\n', '\t', ' \t ', '\0', 'a\x1fb', '\x7f', 'x'.repeat(1001), ' '.repeat(1001)]) {
    assert.deepEqual(canonicalRequirement({ ...requirement, filter }, readers),
      { reasons: ['dotnet-test requirement filter must be a single line'] });
  }
});

test('validateAssessment accepts a source-bound not-applicable decision and binds its evidence', () => {
  const checked = accepted(notApplicable(), 'not-applicable');
  assert.deepEqual([checked.reasons, checked.rejected], [[], []]);
  assert.deepEqual(checked.assessment.condition.evidence, [{ ...CONDITION_REF, blob: `blob-${EVIDENCE_FILE}` }]);
  assert.equal('unsupported' in checked.assessment, false);
});

test('validateAssessment never completes a task that still requires a change, or a claim without its evidence', () => {
  const change = accepted(notApplicable({ obligations: [
    { text: 'Fix a platform defect if T004 shows one', disposition: 'condition-false' },
    { text: 'Record the decision as section T006 of the evidence file', disposition: 'change' },
  ] }), 'not-applicable');
  assert.match(change.reasons.join('\n'), /the task still requires a change: Record the decision/);
  assert.match(accepted(notApplicable({ condition: { text: 'T004 shows a defect', evidence: [] } }), 'not-applicable').reasons.join('\n'), /at least one citation/);
  assert.match(accepted(notApplicable({ condition: { text: 'T004 shows a defect', evidence: [{ ...CONDITION_REF, excerpt: 'defect found' }] } }), 'not-applicable').reasons.join('\n'), /excerpt does not appear/);
  assert.match(accepted(notApplicable({ obligations: [{ text: 'Run tests', disposition: 'verify' }] }), 'not-applicable').reasons.join('\n'), /only have obligations whose condition does not hold/);
  assert.match(accepted(notApplicable(), 'verified').reasons.join('\n'), /accepts outcome "not-applicable", but the claim is "verified"/);
  assert.match(accepted(notApplicable({ obligations: [] }), 'not-applicable').reasons.join('\n'), /1 to 20 obligations/);
});

test('validateAssessment for verification requires runnable requirements and refuses unsupported ones', () => {
  const dotnet = { runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', filter: 'FullyQualifiedName~Evidence', runs: 20 };
  const ok = accepted(verified([dotnet]), 'verified');
  assert.deepEqual(ok.reasons, []);
  assert.deepEqual(ok.assessment.requirements, [dotnet]);
  assert.match(accepted(verified([]), 'verified').reasons.join('\n'), /1 to 10 requirements/);
  assert.match(accepted(verified([dotnet], { unsupported: ['5 runs under the CPU load of quickstart step 3'] }), 'verified').reasons.join('\n'),
    /cannot perform: 5 runs under the CPU load of quickstart step 3/);
  assert.match(accepted(verified([{ ...dotnet, probe: 'x' }]), 'verified').reasons.join('\n'), /requirement 1: .*cannot honor: probe/);
  const twoEnv = { runner: 'environment-verify', mode: 'phase', files: ['docs/a.md'] };
  assert.match(accepted(verified([twoEnv, twoEnv]), 'verified').reasons.join('\n'), /at most one environment-verify requirement/);
  assert.match(accepted(verified([dotnet], { obligations: [{ text: 'x', disposition: 'condition-false' }] }), 'verified').reasons.join('\n'), /at least one verification obligation/);
});

test('validateAssessment reports a rejection with the assessor reasons and refuses malformed files', () => {
  assert.deepEqual(accepted({ format: 1, task: 'T006', verdict: 'reject', reasons: ['The task requires a code change.'] }, 'verified'),
    { reasons: [], rejected: ['The task requires a code change.'] });
  assert.deepEqual(accepted({ format: 1, task: 'T006', verdict: 'reject' }, 'verified').rejected, ['the assessment rejected the claim without a reason']);
  assert.match(accepted({ format: 1, task: 'T005', verdict: 'accept' }, 'verified').reasons.join('\n'), /names task "T005"/);
  assert.match(accepted({ format: 1, task: 'T006', verdict: 'maybe' }, 'verified').reasons.join('\n'), /verdict must be accept or reject/);
  assert.match(accepted({ ...notApplicable(), instructions: 'tick it' }, 'not-applicable').reasons.join('\n'), /unknown fields: instructions/);
});

test('runVerification requires consecutive passing runs without reruns, prepares projects once, and stops at the first failure', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-completion-'));
  try {
    const calls = [];
    let failOn = Infinity;
    const exec = (command, args, options) => {
      calls.push({ command, args, cwd: options.cwd });
      return { status: calls.length === failOn ? 1 : 0, output: `ran ${command}`, timedOut: false };
    };
    const dotnet = { runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', filter: 'FullyQualifiedName~Evidence', runs: 3 };
    const npm = { runner: 'npm-script', project: 'src/web', script: 'test', args: ['--run'], runs: 2 };
    const env = { runner: 'environment-verify', mode: 'phase', files: ['docs/a.md'], runs: 1 };
    const record = runVerification({ workspace: dir, resultDir: dir, requirements: [dotnet, env, npm], head: HEAD, exec, env: {} });
    assert.equal(record.status, 'passed');
    assert.deepEqual(record.results.map((entry) => [entry.requirement.runner, entry.passed, entry.failed, entry.status]), [['dotnet-test', 3, 0, 'passed'], ['npm-script', 2, 0, 'passed']]);
    assert.deepEqual(calls.map((call) => [call.command, ...call.args].join(' ')), [
      'dotnet test src/Tests/Tests.csproj --filter FullyQualifiedName~Evidence',
      'dotnet test src/Tests/Tests.csproj --filter FullyQualifiedName~Evidence --no-build',
      'dotnet test src/Tests/Tests.csproj --filter FullyQualifiedName~Evidence --no-build',
      'npm ci --no-audit --no-fund',
      'npm run test -- --run',
      'npm run test -- --run',
    ]);
    assert.equal(path.relative(dir, calls.at(-1).cwd), path.join('src', 'web'));
    assert.deepEqual(JSON.parse(readFileSync(path.join(dir, RUNS_FILE), 'utf8')).results.length, 2);
    assert.match(readFileSync(path.join(dir, 'completion-1.log'), 'utf8'), /Run 3 of 3/);

    calls.length = 0;
    failOn = 2;
    const failed = runVerification({ workspace: dir, resultDir: dir, requirements: [dotnet, npm], head: HEAD, exec, env: {} });
    assert.equal(failed.status, 'failed');
    assert.deepEqual(failed.results.map((entry) => [entry.passed, entry.failed, entry.status]), [[1, 1, 'failed'], [0, 0, 'not-run']]);
    assert.equal(calls.length, 2, 'a failed run is not retried and nothing runs after it');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('runVerification stops when the budget is exhausted or a run times out', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-completion-'));
  try {
    let clock = 0;
    const exec = () => {
      clock += 60_000;
      return { status: 0, output: '', timedOut: false };
    };
    const dotnet = { runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', runs: 5 };
    const record = runVerification({ workspace: dir, resultDir: dir, requirements: [dotnet], head: HEAD, exec, now: () => clock, budgetMs: 120_000, env: {} });
    assert.deepEqual([record.status, record.results[0].passed, record.results[0].status], ['timeout', 2, 'timeout']);
    const timedOut = runVerification({ workspace: dir, resultDir: dir, requirements: [dotnet], head: HEAD, exec: () => ({ status: null, output: '', timedOut: true }), env: {} });
    assert.deepEqual([timedOut.status, timedOut.results[0].failed], ['timeout', 1]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('the verify command runs only a verified completion of the packaged result', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-completion-'));
  try {
    const messages = [];
    const log = (text) => messages.push(text);
    assert.equal(main(['verify', '--workspace', dir, '--result-dir', dir], { env: {}, log }), 0);
    assert.match(messages.at(-1), /No verified completion to run/);
    const requirement = { runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', runs: 2 };
    writeFileSync(path.join(dir, 'result.json'), JSON.stringify({ completion: { outcome: 'verified', head: HEAD, assessment: { requirements: [requirement] } } }));
    let runs = 0;
    const exec = () => ({ status: (runs += 1) === 2 ? 1 : 0, output: '', timedOut: false });
    assert.equal(main(['verify', '--workspace', dir, '--result-dir', dir], { env: {}, log, exec }), 1);
    assert.match(messages.at(-1), /failed \(1 of 2 runs passed\)/);
    assert.throws(() => main(['run'], { env: {}, log }), /Usage: speckit-completion\.mjs verify/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// A packaged result as the work job records it for a completion without a change.
function packaged(outcome, assessment, extra = {}) {
  const value = accepted(assessment, outcome).assessment;
  return {
    reasons: [],
    flaky: [],
    completion: { format: 1, task: 'T006', outcome, taskDigest: taskDigest(TASK_TEXT), head: HEAD, claim: claim(outcome), assessment: value, ...extra },
  };
}
const landArgs = (result, extra = {}) => ({ result, task: 'T006', taskText: TASK_TEXT, headNow: HEAD, workspaceHead: HEAD, ...readers, ...extra });

test('landCompletion ticks a not-applicable task only on the assessed head and task text', () => {
  const result = packaged('not-applicable', notApplicable());
  const ok = landCompletion(landArgs(result));
  assert.deepEqual(ok.reasons, []);
  assert.equal(ok.note, `not applicable: T004 shows a platform defect (assessed at ${HEAD.slice(0, 8)}; no change by this task)`);
  assert.match(landCompletion(landArgs(result, { headNow: 'b'.repeat(40) })).reasons[0], /assessed on a different head/);
  assert.match(landCompletion(landArgs(result, { workspaceHead: 'b'.repeat(40) })).reasons[0], /assessed on a different head/);
  assert.match(landCompletion(landArgs(result, { taskText: `${TASK_TEXT} and also refactor` })).reasons[0], /text of T006 changed/);
  assert.match(landCompletion(landArgs(result, { taskText: null })).reasons[0], /not an unchecked task/);
  assert.match(landCompletion(landArgs({ reasons: [] })).reasons[0], /was not recorded/);
  const tampered = structuredClone(result);
  tampered.completion.assessment.obligations.push({ text: 'Write the decision down', disposition: 'change' });
  assert.match(landCompletion(landArgs(tampered)).reasons.join('\n'), /still requires a change/);
  const moved = structuredClone(result);
  moved.completion.assessment.condition.evidence[0].blob = 'blob-older';
  assert.match(landCompletion(landArgs(moved)).reasons.join('\n'), /changed since the evidence was recorded/);
  const rejected = structuredClone(result);
  rejected.completion.assessment = { format: 1, task: 'T006', verdict: 'reject', reasons: ['needs a fix'] };
  assert.match(landCompletion(landArgs(rejected)).reasons[0], /rejected the claim: needs a fix/);
});

test('landCompletion ticks a verified task only with every required passing run on the current head', () => {
  const dotnet = { runner: 'dotnet-test', project: 'src/Tests/Tests.csproj', filter: 'FullyQualifiedName~Evidence', runs: 20 };
  const envVerify = { runner: 'environment-verify', mode: 'phase', files: ['docs/a.md'] };
  const execution = (passed, status = 'passed', head = HEAD) => ({ format: 1, head, status, results: [{ requirement: dotnet, passed, failed: passed === 20 ? 0 : 1, status }] });
  const environmentVerify = { status: 'passed', mode: 'phase', files: ['docs/a.md'], checks: ['platform build and tests'], uncovered: [] };
  const result = packaged('verified', verified([dotnet, envVerify]), { execution: execution(20), environmentVerify });
  const ok = landCompletion(landArgs(result));
  assert.deepEqual(ok.reasons, []);
  assert.equal(ok.note, `verified without changes by the assessed verification at ${HEAD.slice(0, 8)} (no change by this task)`);
  const variant = (change) => {
    const copy = structuredClone(result);
    change(copy);
    return landCompletion(landArgs(copy)).reasons.join('\n');
  };
  assert.match(variant((copy) => { copy.completion.execution = execution(19, 'failed'); }), /passed 19 of 20 required runs/);
  assert.match(variant((copy) => { copy.completion.execution = execution(3, 'timeout'); }), /did not finish within the verification budget/);
  assert.match(variant((copy) => { copy.completion.execution = execution(20, 'passed', 'b'.repeat(40)); }), /did not run on the current head/);
  assert.match(variant((copy) => { delete copy.completion.execution; }), /did not run on the current head/);
  assert.match(variant((copy) => { copy.completion.execution.results[0].requirement = { ...dotnet, filter: 'FullyQualifiedName~Other' }; }), /did not run `dotnet test/);
  assert.match(variant((copy) => { copy.completion.environmentVerify.status = 'failed'; }), /environment verification \(mode phase\) did not pass \(failed\)/);
  assert.match(variant((copy) => { copy.completion.environmentVerify.mode = 'task'; }), /ran another scope/);
  assert.match(variant((copy) => { copy.completion.environmentVerify.uncovered = ['docs/a.md']; }), /left required files uncovered/);
  assert.match(variant((copy) => { copy.flaky = ['Evidence.Test']; }), /passed only on its rerun/);
  assert.match(variant((copy) => { copy.completion.assessment.requirements[0].runs = 1; }), /did not run `dotnet test/, 'a weakened requirement no longer matches what ran');
});

test('requirementLabel and completionNote describe the work in one line', () => {
  assert.equal(requirementLabel({ runner: 'dotnet-test', project: 'src/T.csproj', filter: 'X', runs: 20 }), '`dotnet test src/T.csproj --filter X`, 20 consecutive passing runs');
  assert.equal(requirementLabel({ runner: 'pytest', project: 'src/sdk', args: [], runs: 1 }), '`pytest` in `src/sdk`');
  assert.equal(requirementLabel({ runner: 'npm-script', project: 'src/web', script: 'test', args: ['--run'], runs: 1 }), '`npm run test -- --run` in `src/web`');
  assert.equal(requirementLabel({ runner: 'environment-verify', mode: 'phase', files: ['a', 'b'], runs: 1 }), 'environment verification (mode phase) of 2 files');
  const long = completionNote({ outcome: 'not-applicable', head: HEAD, assessment: { condition: { text: `<!-- x -->\n${'word '.repeat(60)}` } } });
  assert.doesNotMatch(long, /<!--|\n/);
  assert.ok(long.length <= 16 + 160 + 47, 'the condition is capped');
});
