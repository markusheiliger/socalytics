import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { runDir } from './speckit-flow.mjs';
import { collect, main, workflowFailure } from './speckit-workflow-job.mjs';

const repo = (file) => new URL(`../../${file}`, import.meta.url);
const read = (file) => readFileSync(repo(file), 'utf8').replaceAll('\r\n', '\n');

function withDirs(body) {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-workflow-job-'));
  const workspace = path.join(root, 'workspace');
  const resultDir = path.join(root, 'result');
  mkdirSync(workspace);
  try {
    return body({ root, workspace, resultDir, runJson: path.join(root, 'run.json') });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

// A run folder as the engine leaves it: state.json, and result.json when the helper's last step ran.
function engineRun(workspace, run, state, result = null) {
  mkdirSync(runDir(workspace, run), { recursive: true });
  writeFileSync(path.join(runDir(workspace, run), 'state.json'), JSON.stringify({ run_id: run, workflow_id: 'implement-task', ...state }));
  if (result) writeFileSync(path.join(runDir(workspace, run), 'result.json'), JSON.stringify(result));
}

test('collect: a completed run exits 0, keeps its result, and leaves the workspace without its run folder', () => withDirs(({ workspace, resultDir, runJson }) => {
  engineRun(workspace, 'abc123', { status: 'completed', current_step_id: 'result' }, { workflow: 'implement-task', files: ['src/a.cs'] });
  writeFileSync(runJson, '{\n  "run_id": "abc123",\n  "status": "completed"\n}\n');
  const record = collect({ workspace, resultDir, runJson, exitCode: '0', workflow: 'implement-task' });
  assert.deepEqual(record, { workflow: 'implement-task', run_id: 'abc123', status: 'completed', step: 'result', error: '', result: { workflow: 'implement-task', files: ['src/a.cs'] } });
  assert.equal(readFileSync(path.join(resultDir, 'agent-exit.txt'), 'utf8'), '0\n');
  assert.ok(existsSync(path.join(resultDir, 'workflow-run', 'result.json')), 'the run folder is kept with the result');
  assert.ok(!existsSync(runDir(workspace, 'abc123')), 'the package step never stages the run folder');
  assert.equal(workflowFailure(resultDir), null);
}));

test('collect: a failed run reports the helper\'s message; a paused run is a workflow defect; a time limit is 124', () => withDirs(({ workspace, resultDir, runJson }) => {
  engineRun(workspace, 'f1', { status: 'failed', current_step_id: 'check', step_results: { check: { output: { stderr: 'The change cannot be accepted:\n- changed protected paths: .github/x.yml\nShell command exited with code 1.' } } } });
  writeFileSync(runJson, '{"run_id": "f1"}');
  assert.equal(collect({ workspace, resultDir, runJson, exitCode: '0', workflow: 'implement-task' }).status, 'failed');
  assert.equal(readFileSync(path.join(resultDir, 'agent-exit.txt'), 'utf8'), '1\n');
  assert.equal(workflowFailure(resultDir), 'the Spec Kit workflow `implement-task` failed at step `check`: The change cannot be accepted:\n- changed protected paths: .github/x.yml');

  engineRun(workspace, 'p1', { status: 'paused', current_step_id: 'review-verdict', step_results: { 'review-verdict': { output: { options: ['accept'] } } } });
  writeFileSync(runJson, '{"run_id": "p1"}');
  collect({ workspace, resultDir, runJson, exitCode: '0', workflow: 'converge' });
  assert.equal(workflowFailure(resultDir), 'the Spec Kit workflow `converge` paused at step `review-verdict`: a gate (review-verdict) paused the unattended run, which must never ask; the workflow `converge` has a defect');

  engineRun(workspace, 't1', { status: 'running', current_step_id: 'implement' });
  writeFileSync(runJson, '{"run_id": "t1"}');
  collect({ workspace, resultDir, runJson, exitCode: '124', workflow: 'implement-task' });
  assert.equal(readFileSync(path.join(resultDir, 'agent-exit.txt'), 'utf8'), '124\n');
  assert.equal(workflowFailure(resultDir), 'the Spec Kit workflow `implement-task` timed out: the Spec Kit workflow `implement-task` did not finish within its time limit (at step implement)');

  writeFileSync(runJson, 'Error: Workflow not found');
  collect({ workspace, resultDir, runJson, exitCode: '1', workflow: 'repair' });
  assert.match(workflowFailure(resultDir), /^the Spec Kit workflow `repair` failed: the Spec Kit workflow `repair` did not start: Error: Workflow not found$/);
}));

test('main collects and writes the status output', () => withDirs(({ root, workspace, resultDir, runJson }) => {
  engineRun(workspace, 'm1', { status: 'completed', current_step_id: 'result' });
  writeFileSync(runJson, '{"run_id": "m1"}');
  const output = path.join(root, 'output.txt');
  const lines = [];
  assert.equal(main(['collect', '--workspace', workspace, '--result-dir', resultDir, '--run-json', runJson, '--exit-code', '0', '--workflow', 'implement-task'], { env: { GITHUB_OUTPUT: output }, log: (line) => lines.push(line) }), 0);
  assert.equal(readFileSync(output, 'utf8'), 'status=completed\n');
  assert.deepEqual(lines, ['The Spec Kit workflow implement-task completed.']);
  assert.throws(() => main(['run']), /Usage: speckit-workflow-job\.mjs collect/);
}));

// The GitHub jobs that run a gha Spec Kit workflow: each names workflows the tooling has, and every input it passes is
// one of them declares (the action skips lines without a value, so a kind only gets the inputs it has values for).
test('every speckit-workflow step runs existing gha workflows with inputs they declare', () => {
  const declared = (id) => {
    const text = read(`.specify/workflows/${id}/workflow.yml`);
    const block = text.match(/\ninputs:\n([\s\S]*?)\nsteps:/)?.[1] ?? '';
    return [...block.matchAll(/^ {2}([a-z_]+):/gm)].map((match) => match[1]);
  };
  const seen = new Set();
  for (const file of readdirSync(repo('.github/workflows/')).filter((name) => /^speckit-.*\.yml$/.test(name) && !name.endsWith('.lock.yml'))) {
    const text = read(`.github/workflows/${file}`);
    for (const match of text.matchAll(/uses: \.\/tooling\/\.github\/actions\/speckit-workflow\n((?:[ \t]+(?!- ).*\n)*)/g)) {
      const value = match[1].match(/^\s+workflow: (.+)$/m)?.[1] ?? '';
      // A literal id, or an expression that picks one per kind (inputs.kind == 'implement' && 'implement-task' || inputs.kind).
      const ids = value.startsWith('${{')
        ? [...value.matchAll(/'([\w-]+)'/g)].map((item) => item[1]).filter((id) => existsSync(repo(`.specify/workflows/${id}/workflow.yml`)))
          .concat(/\|\| inputs\.kind/.test(value) ? ['converge', 'repair'] : [])
        : [value.trim()];
      assert.ok(ids.length > 0, `${file} names a Spec Kit workflow`);
      for (const id of ids) assert.ok(existsSync(repo(`.specify/workflows/${id}/workflow.yml`)), `${file} runs the unknown Spec Kit workflow ${id}`);
      const names = [...(match[1].match(/^\s+inputs: \|\n((?:\s{12}.*\n)*)/m)?.[1] ?? '').matchAll(/^\s+([a-z_]+)=/gm)].map((line) => line[1]);
      for (const name of names) assert.ok(ids.some((id) => declared(id).includes(name)), `${file} passes ${name}, which none of ${ids.join(', ')} declares`);
      assert.ok(names.includes('folder'), `${file} passes the spec folder`);
      for (const id of ids) seen.add(id);
    }
  }
  assert.deepEqual([...seen].sort(), ['converge', 'implement-task', 'repair', 'resolve']);
  for (const id of seen) assert.ok(declared(id).includes('tooling') && declared(id).includes('mode') && declared(id).includes('folder'), `${id} takes the inputs the action sets and the folder`);
});
