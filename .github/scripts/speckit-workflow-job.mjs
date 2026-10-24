import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { describeRun, runDir } from './speckit-flow.mjs';

// The GitHub side of a gha Spec Kit workflow run (the speckit-workflow action): after `specify workflow run` ran a
// workflow unattended in the workspace, `collect` turns the run into what the job's package step reads, so the land
// jobs stay as they are:
//
// - agent-exit.txt: 0 when the run completed, 124 when it hit the time limit, 1 otherwise;
// - workflow.json: { workflow, run_id, status, step, error, result }, the outcome with the helper's own message;
// - workflow-run/: the run folder (inputs, state, the helper's state, changes.diff, result.json) for the diagnosis.
//
// It removes the run folder from the workspace, because the package step stages the whole working tree and a branch
// whose .specify/.gitignore predates the run folders would otherwise carry it as a protected change. On GitHub a run
// never outlives its job: a run that paused stopped at a gate, which an unattended run must never reach.

export class WorkflowJobError extends Error {}

const TIMEOUT_EXIT = 124;

export function collect({ workspace, resultDir, runJson, exitCode, workflow }) {
  mkdirSync(resultDir, { recursive: true });
  const output = existsSync(runJson) ? readFileSync(runJson, 'utf8') : '';
  const run = output.match(/"run_id"\s*:\s*"([\w-]+)"/)?.[1] ?? null;
  let outcome;
  if (!run) {
    outcome = { status: 'failed', step: null, error: `the Spec Kit workflow \`${workflow}\` did not start${output.trim() ? `: ${output.trim().slice(-1000)}` : ''}` };
  } else if (!existsSync(path.join(runDir(workspace, run), 'state.json'))) {
    outcome = { status: 'failed', step: null, error: `the Spec Kit workflow \`${workflow}\` left no run state` };
  } else {
    const { state, ...described } = describeRun(workspace, run);
    outcome = described;
    if (described.status === 'paused') {
      outcome = { status: 'paused', step: described.step, error: `a gate (${described.step}) paused the unattended run, which must never ask; the workflow \`${workflow}\` has a defect` };
    }
  }
  if (Number(exitCode) === TIMEOUT_EXIT && outcome.status !== 'completed') {
    outcome = { ...outcome, status: 'timeout', error: `the Spec Kit workflow \`${workflow}\` did not finish within its time limit${outcome.step ? ` (at step ${outcome.step})` : ''}` };
  }
  const agentExit = outcome.status === 'completed' ? 0 : outcome.status === 'timeout' ? TIMEOUT_EXIT : 1;
  writeFileSync(path.join(resultDir, 'agent-exit.txt'), `${agentExit}\n`);
  const record = { workflow, run_id: run, status: outcome.status, step: outcome.step ?? null, error: outcome.error ?? '', result: outcome.result ?? null };
  writeFileSync(path.join(resultDir, 'workflow.json'), `${JSON.stringify(record, null, 2)}\n`);
  if (run && existsSync(runDir(workspace, run))) {
    cpSync(runDir(workspace, run), path.join(resultDir, 'workflow-run'), { recursive: true });
    rmSync(runDir(workspace, run), { recursive: true, force: true });
  }
  return record;
}

// The reason a collected run did not complete, for the package step's report, or null.
export function workflowFailure(resultDir) {
  const file = path.join(resultDir, 'workflow.json');
  if (!existsSync(file)) return null;
  let record;
  try {
    record = JSON.parse(readFileSync(file, 'utf8'));
  } catch {
    return 'the Spec Kit workflow left an unreadable workflow.json';
  }
  if (record.status === 'completed') return null;
  const where = record.status === 'timeout' || !record.step ? '' : ` at step \`${record.step}\``;
  return `the Spec Kit workflow \`${record.workflow}\` ${record.status === 'timeout' ? 'timed out' : record.status}${where}: ${String(record.error || 'no message').trim().slice(0, 1500)}`;
}

function option(argv, name) {
  const index = argv.indexOf(`--${name}`);
  return index >= 0 ? (argv[index + 1] ?? '') : '';
}

export function main(argv, { env = process.env, log = console.log } = {}) {
  const [command] = argv;
  if (command !== 'collect') throw new WorkflowJobError('Usage: speckit-workflow-job.mjs collect --workspace <dir> --result-dir <dir> --run-json <file> --exit-code <n> --workflow <id>');
  const record = collect({
    workspace: path.resolve(option(argv, 'workspace') || '.'),
    resultDir: path.resolve(option(argv, 'result-dir')),
    runJson: path.resolve(option(argv, 'run-json')),
    exitCode: option(argv, 'exit-code'),
    workflow: option(argv, 'workflow'),
  });
  if (env.GITHUB_OUTPUT) writeFileSync(env.GITHUB_OUTPUT, `status=${record.status}\n`, { flag: 'a' });
  log(record.status === 'completed' ? `The Spec Kit workflow ${record.workflow} completed.` : `::warning::${workflowFailure(path.resolve(option(argv, 'result-dir')))}`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = error instanceof WorkflowJobError ? 2 : 1;
  }
}
