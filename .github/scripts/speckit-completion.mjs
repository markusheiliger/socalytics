import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// Evidence-backed completion of a task without a file change. Upstream Spec Kit has tasks that only verify ("Run
// quickstart.md validation") and tasks whose work depends on a condition ("only if T004 shows a platform defect, fix
// it"); both can be done correctly without changing a file. The implement-task workflow lets its agent claim such an
// outcome instead of a change (completion.json), has a separate read-only agent assess the claim against the task
// (assessment.json), and then:
//
// - `verified`: the trusted runner of the work job (`verify` below) executes exactly the requirements the assessment
//   derived from the task (the tests, filters, and number of consecutive passing runs), never the claiming agent;
// - `not-applicable`: the assessment names the task's condition and cites the repository lines that show it does not
//   hold; each citation is bound to the evaluated commit and its excerpt must appear in the cited lines.
//
// The land job (landCompletion) checks everything again without running agent-written code and ticks the task with a
// note that says what happened. An implementation obligation never completes this way: an assessment that lists one
// cannot be accepted, and ambiguity fails closed. Every function here is deterministic; the judgment is the
// assessment's.

export const COMPLETION_FORMAT = 1;
export const COMPLETION_FILE = 'completion.json';
export const ASSESSMENT_FILE = 'assessment.json';
export const RUNS_FILE = 'completion-runs.json';
export const OUTCOMES = Object.freeze(['verified', 'not-applicable']);
export const DISPOSITIONS = Object.freeze(['verify', 'condition-false', 'change']);
export const RUNNERS = Object.freeze(['dotnet-test', 'pytest', 'npm-script', 'environment-verify']);
export const VERIFY_MODES = Object.freeze(['task', 'phase', 'finalize']);
export const DEFAULT_BUDGET_MINUTES = 25;

const MAX_TEXT = 2000;
const MAX_REFS = 20;
const MAX_OBLIGATIONS = 20;
const MAX_REQUIREMENTS = 10;
const MAX_RUNS = 50;
const MAX_ARGS = 20;
const MAX_ARG = 300;
const MAX_FILTER = 1000;
const MAX_FILES = 500;
const MAX_EXCERPT = 1000;
const MAX_SPAN = 400;
const MAX_NOTE = 160;

const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const isText = (value, max = MAX_TEXT) => typeof value === 'string' && value.trim().length > 0 && value.length <= max && !value.includes('\0');
// One command-line argument: no control characters (no newlines), bounded.
const isArg = (value, max = MAX_ARG) => typeof value === 'string' && value.length > 0 && value.length <= max && !/[\x00-\x1f\x7f]/.test(value);
const unknownKeys = (value, allowed) => Object.keys(value).filter((key) => !allowed.includes(key));

export const sha256 = (text) => createHash('sha256').update(String(text)).digest('hex');
export const taskDigest = (text) => sha256(String(text ?? '').trim());

// A repository-relative path that cannot leave the working tree, reach into .git, or point into a workflow run folder.
export function isSafeEvidencePath(file) {
  return typeof file === 'string' && file.length > 0 && file.length <= 500 && !/[\\\0]/.test(file) && !file.startsWith('/') &&
    !/^[A-Za-z]:/.test(file) && file.split('/').every((part) => part !== '' && part !== '.' && part !== '..' && part.toLowerCase() !== '.git') &&
    !file.startsWith('.specify/workflows/runs/');
}

const underSrc = (file) => isSafeEvidencePath(file) && file.startsWith('src/');
const normalize = (text) => String(text).replace(/\s+/g, ' ').trim();

// Reads a tracked file at a revision: { content, blob } or null for anything that is not a tracked file there.
export function gitReader(git, rev = 'HEAD') {
  return (file) => {
    const blob = git(['rev-parse', '--verify', '--quiet', `${rev}:${file}`]);
    if (blob.status !== 0) return null;
    const id = blob.stdout.trim();
    const type = git(['cat-file', '-t', id]);
    if (type.status !== 0 || type.stdout.trim() !== 'blob') return null;
    const shown = git(['cat-file', 'blob', id]);
    return shown.status === 0 ? { content: shown.stdout, blob: id } : null;
  };
}

// Checks citations ({ path, lines: [start, end], excerpt }) against the files `read` returns and binds each to the
// content it was read from. Returns { reasons, refs } with `blob` added to every valid citation.
export function bindEvidence(list, { read, what = 'evidence' }) {
  const reasons = [];
  const refs = [];
  if (!Array.isArray(list)) return { reasons: [`${what} must be a list`], refs };
  if (list.length > MAX_REFS) reasons.push(`${what} cites more than ${MAX_REFS} sources`);
  list.slice(0, MAX_REFS).forEach((ref, index) => {
    const label = `${what} ${index + 1}`;
    if (!isObject(ref)) return reasons.push(`${label} is not an object`);
    const extra = unknownKeys(ref, ['path', 'lines', 'excerpt', 'blob']);
    if (extra.length > 0) return reasons.push(`${label} has unknown fields: ${extra.join(', ')}`);
    if (!isSafeEvidencePath(ref.path)) return reasons.push(`${label} has an unsafe or missing path`);
    const [start, end] = Array.isArray(ref.lines) ? ref.lines : [];
    if (!Array.isArray(ref.lines) || ref.lines.length !== 2 || !Number.isInteger(start) || !Number.isInteger(end) || start < 1 || end < start || end - start >= MAX_SPAN) {
      return reasons.push(`${label} (\`${ref.path}\`) needs lines [start, end] spanning at most ${MAX_SPAN} lines`);
    }
    if (!isText(ref.excerpt, MAX_EXCERPT)) return reasons.push(`${label} (\`${ref.path}\`) needs an excerpt of at most ${MAX_EXCERPT} characters`);
    const file = read(ref.path);
    if (!file) return reasons.push(`${label} cites \`${ref.path}\`, which is not a tracked file at the evaluated commit`);
    if (ref.blob !== undefined && ref.blob !== file.blob) return reasons.push(`${label} cites \`${ref.path}\`, which changed since the evidence was recorded`);
    const lines = file.content.split(/\r?\n/);
    if (end > lines.length) return reasons.push(`${label} cites lines ${start}-${end} of \`${ref.path}\`, which has ${lines.length} lines`);
    if (!normalize(lines.slice(start - 1, end).join('\n')).includes(normalize(ref.excerpt))) {
      return reasons.push(`${label}: the excerpt does not appear in lines ${start}-${end} of \`${ref.path}\``);
    }
    refs.push({ path: ref.path, lines: [start, end], excerpt: ref.excerpt, blob: file.blob });
  });
  return { reasons, refs };
}

// The implementing agent's claim (completion.json). Evidence is optional for `verified` (the trusted runner provides
// it) and recommended for `not-applicable` (the assessment must cite it either way).
export function validateClaim(claim, { task }) {
  if (!isObject(claim)) return ['completion.json is not a JSON object'];
  const reasons = [];
  const extra = unknownKeys(claim, ['format', 'task', 'outcome', 'rationale', 'evidence']);
  if (extra.length > 0) reasons.push(`completion.json has unknown fields: ${extra.join(', ')}`);
  if (claim.format !== COMPLETION_FORMAT) reasons.push(`completion.json must have format ${COMPLETION_FORMAT}`);
  if (claim.task !== task) reasons.push(`completion.json names task ${JSON.stringify(claim.task)}, not ${task}`);
  if (!OUTCOMES.includes(claim.outcome)) reasons.push(`completion.json outcome must be one of ${OUTCOMES.join(', ')}`);
  if (!isText(claim.rationale)) reasons.push(`completion.json needs a rationale of at most ${MAX_TEXT} characters`);
  if (claim.evidence !== undefined && !Array.isArray(claim.evidence)) reasons.push('completion.json evidence must be a list');
  return reasons;
}

// One verification requirement in its canonical form (only known fields, defaults filled in), or reasons. `exists`
// tells whether a repository path exists; `readText` returns a repository file's text or null.
export function canonicalRequirement(value, { exists, readText }) {
  if (!isObject(value)) return { reasons: ['a requirement is not an object'] };
  const runner = value.runner;
  if (!RUNNERS.includes(runner)) return { reasons: [`requirement runner ${JSON.stringify(runner)} is not supported (supported: ${RUNNERS.join(', ')})`] };
  const allowed = {
    'dotnet-test': ['runner', 'project', 'filter', 'runs'],
    pytest: ['runner', 'project', 'args', 'runs'],
    'npm-script': ['runner', 'project', 'script', 'args', 'runs'],
    'environment-verify': ['runner', 'mode', 'files', 'runs'],
  }[runner];
  const extra = unknownKeys(value, allowed);
  if (extra.length > 0) return { reasons: [`${runner} requirement has fields the trusted runner cannot honor: ${extra.join(', ')}`] };
  const runs = value.runs ?? 1;
  if (!Number.isInteger(runs) || runs < 1 || runs > MAX_RUNS) return { reasons: [`${runner} requirement runs must be an integer from 1 to ${MAX_RUNS}`] };
  const args = value.args ?? [];
  if (['pytest', 'npm-script'].includes(runner) && (!Array.isArray(args) || args.length > MAX_ARGS || !args.every((arg) => isArg(arg)))) {
    return { reasons: [`${runner} requirement args must be at most ${MAX_ARGS} single-line strings of at most ${MAX_ARG} characters`] };
  }
  if (runner === 'dotnet-test') {
    if (!underSrc(value.project) || !/\.(csproj|slnx|sln)$/.test(value.project) || !exists(value.project)) {
      return { reasons: ['dotnet-test requirement project must be an existing .csproj, .slnx, or .sln file under src/'] };
    }
    if (value.filter !== undefined && !isArg(value.filter, MAX_FILTER)) return { reasons: ['dotnet-test requirement filter must be a single line'] };
    return { requirement: { runner, project: value.project, ...(value.filter !== undefined ? { filter: value.filter } : {}), runs } };
  }
  if (runner === 'pytest') {
    if (!underSrc(value.project) || !exists(`${value.project}/pyproject.toml`)) return { reasons: ['pytest requirement project must be a folder under src/ with a pyproject.toml'] };
    return { requirement: { runner, project: value.project, args, runs } };
  }
  if (runner === 'npm-script') {
    if (!underSrc(value.project)) return { reasons: ['npm-script requirement project must be a folder under src/ with a package.json'] };
    let scripts = null;
    try {
      scripts = JSON.parse(readText(`${value.project}/package.json`) ?? 'null')?.scripts ?? null;
    } catch {
      scripts = null;
    }
    if (!isObject(scripts)) return { reasons: ['npm-script requirement project must be a folder under src/ with a package.json'] };
    if (!isArg(value.script, 100) || !Object.hasOwn(scripts, value.script)) return { reasons: [`npm-script requirement script ${JSON.stringify(value.script)} is not a script of ${value.project}/package.json`] };
    return { requirement: { runner, project: value.project, script: value.script, args, runs } };
  }
  if (!VERIFY_MODES.includes(value.mode)) return { reasons: [`environment-verify requirement mode must be one of ${VERIFY_MODES.join(', ')}`] };
  if (runs !== 1) return { reasons: ['environment-verify requirement cannot repeat runs (it reruns failed tests); use dotnet-test, pytest, or npm-script for repeated runs'] };
  const files = value.files;
  if (!Array.isArray(files) || files.length === 0 || files.length > MAX_FILES || !files.every((file) => isSafeEvidencePath(file) && exists(file))) {
    return { reasons: [`environment-verify requirement files must list 1 to ${MAX_FILES} existing repository paths`] };
  }
  return { requirement: { runner, mode: value.mode, files: [...files], runs: 1 } };
}

export function requirementLabel(requirement) {
  const times = requirement.runs > 1 ? `, ${requirement.runs} consecutive passing runs` : '';
  switch (requirement.runner) {
    case 'dotnet-test':
      return `\`dotnet test ${requirement.project}${requirement.filter ? ` --filter ${requirement.filter}` : ''}\`${times}`;
    case 'pytest':
      return `\`pytest ${requirement.args.join(' ')}\` in \`${requirement.project}\`${times}`.replace('`pytest `', '`pytest`');
    case 'npm-script':
      return `\`npm run ${requirement.script}${requirement.args.length > 0 ? ` -- ${requirement.args.join(' ')}` : ''}\` in \`${requirement.project}\`${times}`;
    default:
      return `environment verification (mode ${requirement.mode}) of ${requirement.files.length} file${requirement.files.length === 1 ? '' : 's'}`;
  }
}

// The independent assessment (assessment.json). Returns { reasons, rejected, assessment }: `rejected` holds the
// assessor's own reasons when it rejected the claim, `reasons` what is wrong with the file or what keeps an accepting
// assessment from completing the task, and `assessment` the canonical accepted assessment with bound evidence.
export function validateAssessment(value, { task, outcome, exists, readText, read }) {
  if (!isObject(value)) return { reasons: ['assessment.json is not a JSON object'], rejected: [] };
  const reasons = [];
  const extra = unknownKeys(value, ['format', 'task', 'verdict', 'outcome', 'rationale', 'reasons', 'obligations', 'condition', 'requirements', 'unsupported']);
  if (extra.length > 0) reasons.push(`assessment.json has unknown fields: ${extra.join(', ')}`);
  if (value.format !== COMPLETION_FORMAT) reasons.push(`assessment.json must have format ${COMPLETION_FORMAT}`);
  if (value.task !== task) reasons.push(`assessment.json names task ${JSON.stringify(value.task)}, not ${task}`);
  if (!['accept', 'reject'].includes(value.verdict)) reasons.push('assessment.json verdict must be accept or reject');
  if (reasons.length > 0) return { reasons, rejected: [] };
  if (value.verdict === 'reject') {
    const rejected = Array.isArray(value.reasons) ? value.reasons.filter((reason) => isText(reason)).map((reason) => reason.trim()) : [];
    return { reasons: [], rejected: rejected.length > 0 ? rejected : ['the assessment rejected the claim without a reason'] };
  }
  if (value.outcome !== outcome) reasons.push(`the assessment accepts outcome ${JSON.stringify(value.outcome)}, but the claim is ${JSON.stringify(outcome)}`);
  if (!isText(value.rationale)) reasons.push(`assessment.json needs a rationale of at most ${MAX_TEXT} characters`);
  const obligations = Array.isArray(value.obligations) ? value.obligations : [];
  if (obligations.length === 0 || obligations.length > MAX_OBLIGATIONS) reasons.push(`assessment.json must list 1 to ${MAX_OBLIGATIONS} obligations of the task`);
  const canonicalObligations = [];
  for (const [index, obligation] of obligations.slice(0, MAX_OBLIGATIONS).entries()) {
    if (!isObject(obligation) || !isText(obligation.text) || !DISPOSITIONS.includes(obligation.disposition) || unknownKeys(obligation, ['text', 'disposition']).length > 0) {
      reasons.push(`obligation ${index + 1} needs text and a disposition of ${DISPOSITIONS.join(', ')}`);
      continue;
    }
    canonicalObligations.push({ text: obligation.text.trim(), disposition: obligation.disposition });
  }
  const changes = canonicalObligations.filter((obligation) => obligation.disposition === 'change');
  if (changes.length > 0) reasons.push(`the task still requires a change: ${changes.map((obligation) => obligation.text).join('; ')}`);
  const unsupported = Array.isArray(value.unsupported) ? value.unsupported : value.unsupported === undefined ? [] : null;
  if (unsupported === null || !unsupported.every((item) => isText(item))) reasons.push('assessment.json unsupported must be a list of descriptions');
  else if (unsupported.length > 0) reasons.push(`the task requires verification the trusted runner cannot perform: ${unsupported.map((item) => item.trim()).join('; ')}`);
  const assessment = { format: COMPLETION_FORMAT, task, verdict: 'accept', outcome: value.outcome, rationale: String(value.rationale ?? '').trim(), obligations: canonicalObligations };
  if (value.outcome === 'verified') {
    if (!canonicalObligations.some((obligation) => obligation.disposition === 'verify')) reasons.push('a verified task needs at least one verification obligation');
    if (value.condition !== undefined) reasons.push('a verified assessment has no condition');
    const requirements = Array.isArray(value.requirements) ? value.requirements : [];
    if (requirements.length === 0 || requirements.length > MAX_REQUIREMENTS) reasons.push(`a verified assessment must list 1 to ${MAX_REQUIREMENTS} requirements`);
    assessment.requirements = [];
    for (const [index, requirement] of requirements.slice(0, MAX_REQUIREMENTS).entries()) {
      const canonical = canonicalRequirement(requirement, { exists, readText });
      if (canonical.reasons) reasons.push(...canonical.reasons.map((reason) => `requirement ${index + 1}: ${reason}`));
      else assessment.requirements.push(canonical.requirement);
    }
    if (assessment.requirements.filter((requirement) => requirement.runner === 'environment-verify').length > 1) reasons.push('a verified assessment may list at most one environment-verify requirement');
  } else if (value.outcome === 'not-applicable') {
    const wrong = canonicalObligations.filter((obligation) => obligation.disposition !== 'condition-false');
    if (wrong.length > 0 || canonicalObligations.length === 0) reasons.push('a not-applicable task may only have obligations whose condition does not hold');
    if (value.requirements !== undefined && (!Array.isArray(value.requirements) || value.requirements.length > 0)) reasons.push('a not-applicable assessment lists no requirements');
    const condition = value.condition;
    if (!isObject(condition) || !isText(condition.text) || !Array.isArray(condition.evidence) || condition.evidence.length === 0 || unknownKeys(condition, ['text', 'evidence']).length > 0) {
      reasons.push('a not-applicable assessment needs the condition text and at least one citation that shows it does not hold');
    } else {
      const bound = bindEvidence(condition.evidence, { read, what: 'condition evidence' });
      reasons.push(...bound.reasons);
      assessment.condition = { text: condition.text.trim(), evidence: bound.refs };
    }
  }
  return { reasons, rejected: [], assessment };
}

// Runs one command; injectable for tests. Returns { status, output, timedOut }.
export function defaultExec(command, args, { cwd, env, timeoutMs }) {
  const result = spawnSync(command, args, { cwd, env, encoding: 'utf8', windowsHide: true, timeout: Math.max(1000, timeoutMs), maxBuffer: 256 * 1024 * 1024 });
  const timedOut = result.error?.code === 'ETIMEDOUT';
  const output = `${result.stdout ?? ''}${result.stderr ?? ''}${result.error && !timedOut ? `\n${result.error.message}\n` : ''}`;
  return { status: timedOut ? null : result.status, output, timedOut };
}

function commandsOf(requirement, run) {
  if (requirement.runner === 'dotnet-test') {
    const args = ['test', requirement.project, ...(requirement.filter ? ['--filter', requirement.filter] : []), ...(run > 0 ? ['--no-build'] : [])];
    return { cwd: '.', command: 'dotnet', args };
  }
  if (requirement.runner === 'pytest') return { cwd: requirement.project, command: 'uv', args: ['run', '--no-sync', 'pytest', ...requirement.args] };
  return { cwd: requirement.project, command: 'npm', args: ['run', requirement.script, ...(requirement.args.length > 0 ? ['--', ...requirement.args] : [])] };
}

// The documented preparation of a project, once per project and runner kind.
function preparationOf(requirement) {
  if (requirement.runner === 'pytest') {
    return [
      { command: 'uv', args: ['sync', '--locked', '--all-groups', '--no-install-local', '--no-build'] },
      { command: 'uv', args: ['sync', '--locked', '--all-groups', '--no-build-isolation'] },
    ];
  }
  if (requirement.runner === 'npm-script') return [{ command: 'npm', args: ['ci', '--no-audit', '--no-fund'] }];
  return [];
}

// Executes the assessed requirements (all but environment-verify, which the work job runs as its verify step) on the
// unchanged workspace: every requirement must pass its number of runs in a row, without reruns; the first failure, a
// time-out, or an exhausted budget stops everything. Writes RUNS_FILE and one log per requirement into resultDir.
export function runVerification({ workspace, resultDir, requirements, head, budgetMs = DEFAULT_BUDGET_MINUTES * 60_000, exec = defaultExec, now = Date.now, env = process.env }) {
  mkdirSync(resultDir, { recursive: true });
  const started = now();
  const remaining = () => budgetMs - (now() - started);
  const runEnv = { ...env, UV_PYTHON_DOWNLOADS: 'never', CI: 'true' };
  const prepared = new Set();
  const results = [];
  let status = 'passed';
  for (const [index, requirement] of requirements.entries()) {
    if (requirement.runner === 'environment-verify') continue;
    const log = `completion-${index + 1}.log`;
    const lines = [];
    const entry = { requirement, passed: 0, failed: 0, status: 'passed', log };
    results.push(entry);
    if (status !== 'passed') {
      entry.status = 'not-run';
      writeFileSync(path.join(resultDir, log), 'Not run: an earlier requirement did not pass.\n');
      continue;
    }
    const cwd = path.join(workspace, requirement.runner === 'dotnet-test' ? '.' : requirement.project);
    const step = (command, args) => {
      const left = remaining();
      if (left <= 0) return { status: null, timedOut: true, output: '' };
      lines.push(`$ ${[command, ...args].join(' ')}`);
      const outcome = exec(command, args, { cwd, env: runEnv, timeoutMs: left });
      lines.push(outcome.output.trimEnd(), `(exit ${outcome.timedOut ? 'timeout' : outcome.status})`);
      return outcome;
    };
    const key = `${requirement.runner}:${requirement.project}`;
    if (!prepared.has(key)) {
      for (const { command, args } of preparationOf(requirement)) {
        const outcome = step(command, args);
        if (outcome.status !== 0) {
          entry.status = outcome.timedOut ? 'timeout' : 'error';
          break;
        }
      }
      prepared.add(key);
    }
    for (let run = 0; entry.status === 'passed' && run < requirement.runs; run += 1) {
      const { command, args } = commandsOf(requirement, run);
      lines.push(`Run ${run + 1} of ${requirement.runs}`);
      const outcome = step(command, args);
      if (outcome.status === 0) entry.passed += 1;
      else {
        entry.failed += 1;
        entry.status = outcome.timedOut ? 'timeout' : 'failed';
      }
    }
    writeFileSync(path.join(resultDir, log), `${lines.join('\n')}\n`);
    if (entry.status !== 'passed') status = entry.status === 'timeout' ? 'timeout' : 'failed';
  }
  const record = { format: COMPLETION_FORMAT, head, status, budgetMinutes: Math.round(budgetMs / 60_000), results };
  writeFileSync(path.join(resultDir, RUNS_FILE), `${JSON.stringify(record, null, 2)}\n`);
  return record;
}

const sameJson = (a, b) => JSON.stringify(a) === JSON.stringify(b);

// One line for the tasks.md note, without markers or line breaks.
function noteText(text) {
  const line = String(text ?? '').replaceAll('<!--', '').replace(/\s+/g, ' ').trim();
  return line.length > MAX_NOTE ? `${line.slice(0, MAX_NOTE - 1).trimEnd()}…` : line;
}

// The note the land job appends to the task line when it ticks a task completed without a change.
export function completionNote(completion) {
  const at = String(completion.head ?? '').slice(0, 8);
  if (completion.outcome === 'verified') return `verified without changes by the assessed verification at ${at} (no change by this task)`;
  return `not applicable: ${noteText(completion.assessment?.condition?.text)} (assessed at ${at}; no change by this task)`;
}

// The land job's check of a completion without a change, from the work job's result.json. `headNow` is the pull
// request head, `workspaceHead` the land checkout's HEAD, `taskText` the task's text there (unchecked), `read` reads a
// tracked file at that head, and `exists`/`readText` look at the land checkout. Returns { reasons, note }.
export function landCompletion({ result, task, taskText, headNow, workspaceHead, read, exists, readText }) {
  const fail = (...reasons) => ({ reasons, note: '' });
  const completion = result?.completion;
  if (!isObject(completion)) return fail('the completion without a change was not recorded');
  if (completion.format !== COMPLETION_FORMAT || completion.task !== task || !OUTCOMES.includes(completion.outcome)) return fail('the recorded completion is malformed or names another task');
  if (!headNow || completion.head !== headNow || workspaceHead !== headNow) return fail('the completion was assessed on a different head than the implementation branch has now');
  if (taskText === null) return fail(`${task} is not an unchecked task at the current head`);
  if (completion.taskDigest !== taskDigest(taskText)) return fail(`the text of ${task} changed since its completion was assessed`);
  const claimReasons = validateClaim(completion.claim, { task });
  if (claimReasons.length > 0 || completion.claim.outcome !== completion.outcome) return fail(...claimReasons, ...(claimReasons.length === 0 ? ['the claim and the recorded outcome differ'] : []));
  const checked = validateAssessment(completion.assessment, { task, outcome: completion.outcome, exists, read, readText });
  if (checked.rejected.length > 0) return fail(`the assessment rejected the claim: ${checked.rejected.join('; ')}`);
  if (checked.reasons.length > 0) return fail(...checked.reasons);
  if (!sameJson(checked.assessment, completion.assessment)) return fail('the recorded assessment is not in its validated form');
  if (completion.claim.evidence !== undefined) {
    const bound = bindEvidence(completion.claim.evidence, { read, what: 'claim evidence' });
    if (bound.reasons.length > 0) return fail(...bound.reasons);
  }
  if (completion.outcome === 'verified') {
    const reasons = [];
    const runs = completion.execution;
    const executed = checked.assessment.requirements.filter((requirement) => requirement.runner !== 'environment-verify');
    if (executed.length > 0) {
      if (!isObject(runs) || runs.head !== headNow) reasons.push('the trusted verification did not run on the current head');
      else {
        for (const requirement of executed) {
          const entry = (runs.results ?? []).find((item) => sameJson(item.requirement, requirement));
          if (!entry) reasons.push(`the trusted verification did not run ${requirementLabel(requirement)}`);
          else if (entry.status !== 'passed' || entry.failed !== 0 || entry.passed !== requirement.runs) {
            reasons.push(`${requirementLabel(requirement)} ${entry.status === 'timeout' ? 'did not finish within the verification budget' : `passed ${entry.passed} of ${requirement.runs} required runs`}`);
          }
        }
      }
    }
    const environment = checked.assessment.requirements.find((requirement) => requirement.runner === 'environment-verify');
    if (environment) {
      const record = completion.environmentVerify;
      if (!isObject(record) || record.status !== 'passed') reasons.push(`the environment verification (mode ${environment.mode}) did not pass (${record?.status ?? 'not run'})`);
      else if (record.mode !== environment.mode || !sameJson(record.files, environment.files)) reasons.push('the environment verification ran another scope than the assessment requires');
      else if (!Array.isArray(record.checks) || record.checks.length === 0) reasons.push('no check of the environment verification applied to the required files');
      else if ((record.uncovered ?? []).length > 0) reasons.push(`the environment verification left required files uncovered: ${record.uncovered.join(', ')}`);
    }
    if ((result.flaky ?? []).length > 0) reasons.push('a test passed only on its rerun, which is not a passing verification run');
    if (reasons.length > 0) return fail(...reasons);
  }
  return { reasons: [], note: completionNote(completion) };
}

function option(argv, name) {
  const index = argv.indexOf(`--${name}`);
  return index >= 0 ? (argv[index + 1] ?? '') : '';
}

// `verify`: the work job's step "Run the task's own verification". Reads the packaged result.json of the result
// folder and runs its assessed requirements in the workspace. Exits 1 when they did not all pass (the step continues
// on error; the land job decides from completion-runs.json).
export function main(argv, { env = process.env, log = console.log, exec = defaultExec } = {}) {
  const [command] = argv;
  if (command !== 'verify') throw new Error('Usage: speckit-completion.mjs verify [--workspace <dir>] [--result-dir <dir>]');
  const workspace = path.resolve(option(argv, 'workspace') || env.SPECKIT_WORKSPACE || '.');
  const resultDir = path.resolve(option(argv, 'result-dir') || env.SPECKIT_RESULT_DIR || 'speckit-result');
  const resultFile = path.join(resultDir, 'result.json');
  const result = existsSync(resultFile) ? JSON.parse(readFileSync(resultFile, 'utf8')) : null;
  const completion = result?.completion;
  if (!completion || completion.outcome !== 'verified' || !Array.isArray(completion.assessment?.requirements)) {
    log('No verified completion to run.');
    return 0;
  }
  const minutes = Number(env.SPECKIT_COMPLETION_BUDGET_MINUTES);
  const budgetMs = (Number.isFinite(minutes) && minutes > 0 ? minutes : DEFAULT_BUDGET_MINUTES) * 60_000;
  const record = runVerification({ workspace, resultDir, requirements: completion.assessment.requirements, head: completion.head, budgetMs, exec, env });
  for (const entry of record.results) log(`${requirementLabel(entry.requirement)}: ${entry.status} (${entry.passed} of ${entry.requirement.runs} runs passed)`);
  return record.status === 'passed' ? 0 : 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 2;
  }
}
