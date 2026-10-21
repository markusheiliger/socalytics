import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { UsageError } from './speckit-orchestrate.mjs';

// The common design of the Spec Kit workflows the gha extension ships (.specify/extension-src/gha/workflows/, see its
// README, "Workflow design"). Each workflow has one deterministic helper (`speckit-<name>-flow.mjs`) whose
// subcommands make every decision and print one JSON object for the workflow's conditions; agent steps only run Spec
// Kit commands or prompts and write the files the helper validates. This module holds what the helpers share:
//
// - the run folder `.specify/workflows/runs/<run id>/` (ignored by git), with the engine's inputs.json and state.json,
//   the helper's own state, and the files agent steps write;
// - `result.json`, the outcome of a finished run in one shape for every workflow ({ workflow, outcome, folder, files,
//   ... }), which the GitHub workflows read instead of guessing from the working tree;
// - `describeRun`, what a run waits for or why it ended, for the skills that resume paused runs;
// - the command line: `--run <run id>` and ASCII-only JSON output;
// - the tooling fingerprint (tooling.json): the engine pins a run's definition (it resumes from its own copy in the run
//   folder), but the helper scripts come from the checkout. A run paused locally and resumed after a merge changed the
//   helpers would mix versions, so every subcommand after `start` refuses a run whose helpers changed since. On
//   GitHub a run never outlives the job that starts it (unattended, never resumed), so this never triggers there.

export class FlowError extends Error {}

export const MODES = ['interactive', 'unattended'];

// The format of result.json; a reader refuses a format it does not know.
export const RESULT_FORMAT = 1;
const SHARED = fileURLToPath(import.meta.url);

export const runDir = (rootDir, run) => path.join(rootDir, '.specify', 'workflows', 'runs', run);

export function readJson(file, what) {
  if (!existsSync(file)) throw new FlowError(`${what} was not written: ${file} does not exist.`);
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch (error) {
    throw new FlowError(`${what} is not valid JSON (${file}): ${error.message}`);
  }
}

// The workflow inputs of a run (inputs.json, written by the engine), trimmed strings by name, plus the validated mode.
export function readRunInputs(rootDir, run) {
  const file = path.join(runDir(rootDir, run), 'inputs.json');
  const data = existsSync(file) ? readJson(file, 'The run inputs') : {};
  const raw = data.inputs ?? {};
  const text = (name) => String(raw[name] ?? '').trim();
  const mode = text('mode') || 'interactive';
  if (!MODES.includes(mode)) throw new FlowError(`Input mode must be "${MODES.join('" or "')}", got "${mode}".`);
  return { text, mode };
}

export function readFlowState(rootDir, run, name) {
  const file = path.join(runDir(rootDir, run), `${name}.json`);
  return existsSync(file) ? readJson(file, 'The run state') : {};
}

export function writeFlowState(rootDir, run, name, state) {
  mkdirSync(runDir(rootDir, run), { recursive: true });
  writeFileSync(path.join(runDir(rootDir, run), `${name}.json`), `${JSON.stringify(state, null, 2)}\n`);
}

// The helpers of a run: a hash of the helper script and of this module, and the version of the run's definition (the
// engine's copy in the run folder).
export function toolingFingerprint(rootDir, run, helper) {
  const hash = createHash('sha256');
  for (const file of [fileURLToPath(helper), SHARED]) hash.update(readFileSync(file));
  const copy = path.join(runDir(rootDir, run), 'workflow.yml');
  const version = existsSync(copy) ? readFileSync(copy, 'utf8').match(/^\s+version:\s*['"]?(\d+\.\d+\.\d+)/m)?.[1] ?? null : null;
  return { helpers: hash.digest('hex').slice(0, 16), workflow_version: version };
}

function workflowOf(rootDir, run) {
  const file = path.join(runDir(rootDir, run), 'state.json');
  try {
    return JSON.parse(readFileSync(file, 'utf8')).workflow_id ?? null;
  } catch {
    return null;
  }
}

// Refuses a run whose helpers changed since its start (tooling.json); a run without a fingerprint started before the
// guard existed and is left alone.
export function assertSameTooling(rootDir, run, helper) {
  const file = path.join(runDir(rootDir, run), 'tooling.json');
  if (!existsSync(file)) return;
  const started = readJson(file, 'The tooling fingerprint');
  if (started.helpers === toolingFingerprint(rootDir, run, helper).helpers) return;
  const workflow = workflowOf(rootDir, run) ?? '<workflow>';
  throw new FlowError([
    `Run ${run} started with other workflow helpers than the checkout has now (they changed since, for example through a merge), so it cannot continue.`,
    `Start the workflow again (specify workflow run ${workflow} with the same inputs); the changes of this run stay in the working tree.`,
  ].join('\n'));
}

// Writes result.json, the outcome of a finished run: { format, workflow, outcome, folder, files, ... } plus the tooling
// fingerprint of the run, so a reader can tell which helpers wrote it.
export function writeResult(rootDir, run, result) {
  const file = path.join(runDir(rootDir, run), 'tooling.json');
  const tooling = existsSync(file) ? readJson(file, 'The tooling fingerprint') : {};
  const value = { format: RESULT_FORMAT, outcome: 'completed', files: [], ...result, tooling };
  mkdirSync(runDir(rootDir, run), { recursive: true });
  writeFileSync(path.join(runDir(rootDir, run), 'result.json'), `${JSON.stringify(value, null, 2)}\n`);
  return value;
}

export function readResult(rootDir, run) {
  const file = path.join(runDir(rootDir, run), 'result.json');
  return existsSync(file) ? readJson(file, 'The run result') : null;
}

// What a run waits for or why it ended, from the engine's state.json: `paused` at a gate (whose verdict input is named
// after its id, `review-verdict` → `review_verdict`; its first option is the recommended one), `completed` with its
// result, or `failed` with the helper's own message (the failed step's stderr; the run's error only says that a
// command failed).
export function describeRun(rootDir, run) {
  const state = readJson(path.join(runDir(rootDir, run), 'state.json'), 'The run state');
  const status = state.status;
  const step = state.current_step_id ?? null;
  if (status === 'paused') {
    const output = state.step_results?.[step]?.output ?? {};
    return {
      status,
      kind: 'gate',
      step,
      input: String(step).replaceAll('-', '_'),
      message: output.message ?? '',
      options: output.options ?? [],
      recommended: output.options?.[0] ?? null,
      file: output.show_file ?? null,
    };
  }
  if (status === 'completed') return { status, step, result: readResult(rootDir, run), state };
  const result = state.step_results?.[step] ?? {};
  const error = String(result.output?.stderr ?? '').replace(/\s*Shell command exited with code \d+\.\s*$/, '').trim() || result.error || state.error || '';
  return { status, step, error, state };
}

export function option(argv, name) {
  const index = argv.indexOf(`--${name}`);
  return index >= 0 ? (argv[index + 1] ?? '') : null;
}

// ASCII only: on Windows the workflow engine decodes a shell step's output with the system code page, so every other
// character (an arrow, an umlaut in a question) is escaped; the JSON value is the same.
export const asciiJson = (value) => JSON.stringify(value).replace(/[\u007f-\uffff]/g, (char) => `\\u${char.charCodeAt(0).toString(16).padStart(4, '0')}`);

// Runs one subcommand of a helper: checks `--run`, records the tooling fingerprint at `start` and checks it before
// every other subcommand but `pending` (which only reads), runs `commands[name]`, and prints its result as ASCII JSON.
export function runFlowCommand(argv, commands, { script, rootDir = process.cwd(), helper, log = (text) => process.stdout.write(text) } = {}) {
  const [command] = argv;
  const run = option(argv, 'run');
  if (!run || !/^[\w-]+$/.test(run)) throw new UsageError(`Usage: ${script} <command> --run <run id> [options]`);
  if (!commands[command]) throw new UsageError(`Unknown command "${command}". Commands: ${Object.keys(commands).join(', ')}.`);
  if (helper && command === 'start') {
    mkdirSync(runDir(rootDir, run), { recursive: true });
    writeFileSync(path.join(runDir(rootDir, run), 'tooling.json'), `${JSON.stringify(toolingFingerprint(rootDir, run, helper), null, 2)}\n`);
  } else if (helper && command !== 'pending') {
    assertSameTooling(rootDir, run, helper);
  }
  log(`${asciiJson(commands[command](run))}\n`);
  return 0;
}

// Runs `main` when the helper is executed directly: a FlowError or UsageError ends it with its message (exit code 1
// or 2), which the workflow engine shows as the failed step's error.
export function runAsScript(metaUrl, main) {
  if (!process.argv[1] || metaUrl !== pathToFileURL(process.argv[1]).href) return;
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = error instanceof UsageError ? 2 : 1;
  }
}
