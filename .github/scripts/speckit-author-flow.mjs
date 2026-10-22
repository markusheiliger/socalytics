import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, renameSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import { assess, collectFacts, hasArchitectureTask } from './speckit-author.mjs';
import { runMarkdownChecks } from './check-markdown.mjs';
import { describeParallelGroups } from './speckit-implement-core.mjs';
import { isValidFolderName, parseTwinFolder, specDir } from './speckit-prepare-core.mjs';
import { UsageError, defaultGit, resolveFolder } from './speckit-orchestrate.mjs';
import { FlowError, describeRun, option, readFlowState, readJson, readRunInputs, runAsScript, runDir, runFlowCommand, writeFlowState, writeResult } from './speckit-flow.mjs';

export { FlowError, runDir };

// The deterministic half of the Spec Kit workflow `author`, which the gha extension ships
// (.specify/extension-src/gha/workflows/author/workflow.yml): every decision of the workflow comes from one of these
// subcommands, which print one JSON object for the workflow's conditions (shell steps with `output_format: json`).
// Agent steps only run Spec Kit commands and write the files these subcommands validate; an invalid file fails the
// step with a message instead of letting the workflow guess.
//
// Every subcommand takes `--run <run id>`: the workflow inputs come from the run's inputs.json, and the run folder
// (`.specify/workflows/runs/<run id>/`, ignored by git) holds the agent files and `author.json`, the state of the run.
// It follows the common design of the gha workflows (speckit-flow.mjs): the last step writes result.json.

export const STAGES = ['specify', 'clarify', 'plan', 'tasks', 'analyze'];
// The stages a `stage` input can restart; feedback reworks one of the first three.
const RESTART_STAGES = ['clarify', 'plan', 'tasks', 'analyze'];
const FEEDBACK_STAGES = ['clarify', 'plan', 'tasks'];
const ANSWER_KEYS = ['A', 'B', 'C', 'D', 'E'];
const SEVERITIES = ['CRITICAL', 'HIGH', 'MEDIUM', 'LOW'];
export const MAX_QUESTIONS = 5;
export const MAX_CANDIDATES = 3;
// The fixable findings a person is asked about per remediation round; later ones come up in the next round.
export const MAX_ASKED_FINDINGS = 5;
// A folder made by /speckit-specify starts with the UTC time it was made (AGENTS.md); a prefix further away from
// the current UTC time than this means the script ran with local time.
export const MAX_FOLDER_CLOCK_SKEW_MS = 15 * 60 * 1000;
const STAMP = /^\d{8}-\d{6}$/;
const ARCHITECTURE_DIR = 'docs/architecture';

const relative = (rootDir, file) => path.relative(rootDir, file).replaceAll('\\', '/');

export function readInputs(rootDir, run) {
  const { text, mode } = readRunInputs(rootDir, run);
  const stage = text('stage');
  if (stage && !RESTART_STAGES.includes(stage)) throw new FlowError(`Input stage must be one of ${RESTART_STAGES.join(', ')}, got "${stage}".`);
  const stamp = text('stamp');
  if (stamp && !STAMP.test(stamp)) throw new FlowError(`Input stamp must be a UTC time stamp YYYYMMDD-HHMMSS, got "${stamp}".`);
  return { feature: text('feature'), feedback: text('feedback'), mode, publish: text('publish') || 'ask', stage, stamp };
}

export const readState = (rootDir, run) => readFlowState(rootDir, run, 'author');
const writeState = (rootDir, run, state) => writeFlowState(rootDir, run, 'author', state);

// A spec folder named by the input: `<folder>`, `specs/<folder>`, or `specs/archive/<folder>` that exists.
export function existingFolder(rootDir, input) {
  const name = String(input).trim().replaceAll('\\', '/').replace(/\/+$/, '').replace(/^specs\/(archive\/)?/, '');
  if (!isValidFolderName(name) || name.includes(' ')) return null;
  return [specDir(name), specDir(name, { archived: true })].some((dir) => existsSync(path.join(rootDir, dir))) ? name : null;
}

function statusOf(rootDir, folder, git, env) {
  return assess(collectFacts({ rootDir, folder, git, env }));
}

// Which stage the run starts at: the next Spec Kit step of the folder, or the stage that a person's feedback revises
// (the stage before the next step: the spec before plan, the plan before tasks, the tasks before analyze).
export function revisedStage(nextStep) {
  return { clarify: 'clarify', plan: 'clarify', tasks: 'plan', analyze: 'tasks' }[nextStep] ?? null;
}

// Where a run on an existing folder starts: the `stage` input (at most the folder's next step), the stage feedback
// revises, or the next step.
function continueAt(rootDir, folder, inputs, git, env) {
  const status = statusOf(rootDir, folder, git, env);
  if (status.nextStep === 'published') throw new FlowError(`specs/${folder} is already pushed; the GitHub workflows own it now.`);
  if (status.nextStep === 'done') throw new FlowError(`specs/${folder} is implemented or archived.`);
  if (status.nextStep === 'specify') {
    throw new FlowError(`specs/${folder} has no spec.md, so there is nothing to continue. Delete the folder and run the workflow with a feature description instead.`);
  }
  let from = inputs.feedback ? revisedStage(status.nextStep) : status.nextStep;
  if (inputs.stage) {
    if (STAGES.indexOf(inputs.stage) > STAGES.indexOf(status.nextStep)) {
      throw new FlowError(`specs/${folder} cannot restart at ${inputs.stage}: its next step is ${status.nextStep}.`);
    }
    from = inputs.stage;
  }
  if (inputs.feedback && !FEEDBACK_STAGES.includes(from)) throw new FlowError(`Feedback reworks the stage clarify, plan, or tasks, not ${from}.`);
  return from;
}

export function start({ rootDir, run, git, env = {} }) {
  // /speckit-specify names the folder after the current time, which AGENTS.md requires in UTC; every agent and script
  // the run starts inherits this environment.
  if (!['UTC', 'UTC0', 'Etc/UTC'].includes(String(env.TZ ?? ''))) {
    throw new FlowError('Run the workflow with TZ=UTC (AGENTS.md): $env:TZ=\'UTC\'; specify workflow run author … in PowerShell, or TZ=UTC specify workflow run author … in a POSIX shell.');
  }
  const inputs = readInputs(rootDir, run);
  const base = { feedback: inputs.feedback, mode: inputs.mode, stamp: inputs.stamp || null };
  if (!inputs.feature) {
    if (inputs.feedback || inputs.stage) throw new FlowError('Feedback and stage rework an existing spec: pass its folder as the feature input.');
    let active = null;
    try {
      active = resolveFolder({ folder: null, env, rootDir });
      if (['published', 'done', 'specify'].includes(statusOf(rootDir, active, git, env).nextStep)) active = null;
    } catch {
      active = null;
    }
    if (active) {
      writeState(rootDir, run, { ...base, kind: 'active', folder: active });
      return { active: true, folder: active };
    }
    writeState(rootDir, run, { ...base, kind: 'discover', folder: null, from: 'specify', feature: null });
    return { active: false, folder: '' };
  }
  const folder = existingFolder(rootDir, inputs.feature);
  if (!folder && (inputs.feedback || inputs.stage)) throw new FlowError('Feedback and stage rework an existing spec: pass its folder as the feature input.');
  if (folder) {
    writeState(rootDir, run, { ...base, kind: 'continue', folder, from: continueAt(rootDir, folder, inputs, git, env) });
  } else {
    writeState(rootDir, run, { ...base, kind: 'feature', folder: null, from: 'specify', feature: inputs.feature });
  }
  return { active: false, folder: folder ?? '' };
}

// Records what the person chose for the active feature (`continue`, the recommended choice, or `propose`), and before
// a discovery fetches the remote and lists the spec twins, so the proposal sees specs on GitHub. Prints where the run
// starts.
export function begin({ rootDir, run, git, env = {}, gh = defaultGh(rootDir), choice = '' }) {
  let state = readState(rootDir, run);
  if (state.kind === 'active') {
    state = choice === 'propose'
      ? { ...state, kind: 'discover', folder: null, from: 'specify', feature: null }
      : { ...state, kind: 'continue', from: continueAt(rootDir, state.folder, readInputs(rootDir, run), git, env) };
  }
  let twins = null;
  if (state.kind === 'discover') {
    git(['fetch', '--quiet']);
    twins = discoveryTwins({ rootDir, run, gh });
  }
  writeState(rootDir, run, state);
  return {
    kind: state.kind,
    folder: state.folder ?? '',
    from: state.from,
    discover: state.kind === 'discover',
    specify: state.from === 'specify',
    twins,
  };
}

function defaultGh(rootDir) {
  return (args) => {
    const result = spawnSync('gh', args, { cwd: rootDir, encoding: 'utf8', windowsHide: true, maxBuffer: 20 * 1024 * 1024 });
    return { status: result.error ? 1 : result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  };
}

// The spec twin issues for the discovery (twins.json): number, title, state, stage, and folder. Best effort: without
// `gh` or a login the discovery works from the repository alone.
export function discoveryTwins({ rootDir, run, gh }) {
  const result = gh(['issue', 'list', '--label', 'speckit:spec', '--state', 'all', '--limit', '500', '--json', 'number,title,state,labels,body']);
  if (result.status !== 0) return null;
  let list;
  try {
    list = JSON.parse(result.stdout);
  } catch {
    return null;
  }
  const twins = list.map((issue) => ({
    number: issue.number,
    title: issue.title,
    state: issue.state,
    stage: (issue.labels ?? []).map((label) => label.name ?? label).find((name) => String(name).startsWith('speckit:stage:'))?.slice('speckit:stage:'.length) ?? null,
    folder: parseTwinFolder(issue.body),
  }));
  mkdirSync(runDir(rootDir, run), { recursive: true });
  writeFileSync(path.join(runDir(rootDir, run), 'twins.json'), `${JSON.stringify(twins, null, 2)}\n`);
  return twins.length;
}

// Discovery: the agent's ranked candidates for the next logical spec (proposal.json), rendered for the gate.
export function validateProposal(data) {
  const candidates = Array.isArray(data?.candidates) ? data.candidates : null;
  if (!candidates || candidates.length === 0 || candidates.length > MAX_CANDIDATES) {
    throw new FlowError(`proposal.json must list 1 to ${MAX_CANDIDATES} candidates.`);
  }
  candidates.forEach((candidate, index) => {
    for (const field of ['title', 'description', 'why']) {
      if (typeof candidate?.[field] !== 'string' || !candidate[field].trim()) throw new FlowError(`Candidate ${index + 1} has no ${field}.`);
    }
    if (!Array.isArray(candidate.architecture) || candidate.architecture.length === 0) throw new FlowError(`Candidate ${index + 1} names no architecture section.`);
  });
  return candidates;
}

export function proposal({ rootDir, run }) {
  const candidates = validateProposal(readJson(path.join(runDir(rootDir, run), 'proposal.json'), 'The proposal of the next logical spec'));
  const lines = candidates.flatMap((candidate, index) => [
    `${index + 1}. ${candidate.title}${index === 0 ? ' (recommended)' : ''}`,
    `   Why next: ${candidate.why}`,
    `   Architecture: ${candidate.architecture.join(', ')}`,
    `   ${candidate.description}`,
    '',
  ]);
  const file = path.join(runDir(rootDir, run), 'proposal.md');
  writeFileSync(file, `${lines.join('\n')}\n`);
  return { count: candidates.length, file: relative(rootDir, file) };
}

// The feature description for /speckit-specify: the input, or the chosen candidate ("1" to "3"; empty means the
// recommended first one, as in unattended mode).
export function pick({ rootDir, run, choice = '' }) {
  const state = readState(rootDir, run);
  if (state.kind === 'feature') return { feature: state.feature };
  if (state.kind !== 'discover') throw new FlowError('Nothing to pick: the run continues an existing spec.');
  const candidates = validateProposal(readJson(path.join(runDir(rootDir, run), 'proposal.json'), 'The proposal of the next logical spec'));
  const index = choice && choice !== 'recommended' ? Number(choice) - 1 : 0;
  if (!Number.isInteger(index) || !candidates[index]) throw new FlowError(`There is no candidate ${choice}.`);
  const candidate = candidates[index];
  const feature = `${candidate.description}\n\nArchitecture sections: ${candidate.architecture.join(', ')}`;
  writeState(rootDir, run, { ...state, feature });
  return { feature };
}

function filesBelow(dir) {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    return statSync(full).isDirectory() ? filesBelow(full) : [full];
  });
}

// Gives a new spec folder the time stamp of the `stamp` input (the server fixes it when `/speckit author` is
// requested): renames the folder, its mentions inside it, and the active feature in .specify/feature.json.
function restamp(rootDir, folder, stamp) {
  const renamed = `${stamp}-${folder.replace(/^\d{8}-\d{6}-/, '')}`;
  if (renamed === folder) return folder;
  if (existsSync(path.join(rootDir, specDir(renamed)))) throw new FlowError(`specs/${renamed} already exists.`);
  renameSync(path.join(rootDir, specDir(folder)), path.join(rootDir, specDir(renamed)));
  for (const file of filesBelow(path.join(rootDir, specDir(renamed)))) {
    const text = readFileSync(file, 'utf8');
    if (text.includes(folder)) writeFileSync(file, text.replaceAll(folder, renamed));
  }
  const featureFile = path.join(rootDir, '.specify', 'feature.json');
  const feature = existsSync(featureFile) ? JSON.parse(readFileSync(featureFile, 'utf8')) : {};
  writeFileSync(featureFile, `${JSON.stringify({ ...feature, feature_directory: specDir(renamed) }, null, 2)}\n`);
  return renamed;
}

// After /speckit-specify: the new folder (from .specify/feature.json), which must have a spec.md and a UTC prefix:
// the `stamp` input when one is given, the current time otherwise.
export function created({ rootDir, run, git, env = {}, now = Date.now() }) {
  const state = readState(rootDir, run);
  let folder = resolveFolder({ folder: null, env: {}, rootDir });
  if (!existsSync(path.join(rootDir, specDir(folder), 'spec.md'))) throw new FlowError(`/speckit-specify did not write specs/${folder}/spec.md.`);
  if (state.stamp) {
    folder = restamp(rootDir, folder, state.stamp);
  } else {
    const stamp = folder.match(/^(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})-/);
    if (stamp) {
      const [, y, mo, d, h, mi, s] = stamp.map(Number);
      if (Math.abs(Date.UTC(y, mo - 1, d, h, mi, s) - now) > MAX_FOLDER_CLOCK_SKEW_MS) {
        throw new FlowError(`specs/${folder} does not start with the current UTC time; /speckit-specify must run its script with TZ=UTC (AGENTS.md). Delete the folder and run the workflow again.`);
      }
    }
  }
  writeState(rootDir, run, { ...state, folder });
  return { folder, nextStep: statusOf(rootDir, folder, git, env).nextStep };
}

// Whether the run works on a stage, and the feedback that revises it. A run starts at a stage and then runs every
// later one, because each Spec Kit step builds on the artifacts of the steps before it.
export function stage({ rootDir, run, name }) {
  if (!STAGES.includes(name)) throw new UsageError(`Unknown stage "${name}".`);
  const state = readState(rootDir, run);
  if (!state.folder && name !== 'specify') throw new FlowError('The run has no spec folder yet.');
  const runs = STAGES.indexOf(name) >= STAGES.indexOf(state.from ?? 'specify');
  const revise = runs && state.from === name && Boolean(state.feedback);
  return { run: runs, folder: state.folder ?? '', feedback: revise ? state.feedback : '', revise };
}

// Clarification: the questions /speckit-clarify would ask (questions.json), one gate per question.
export function validateQuestions(data) {
  if (!Array.isArray(data)) throw new FlowError('questions.json must be a list of questions.');
  if (data.length > MAX_QUESTIONS) throw new FlowError(`questions.json lists ${data.length} questions; /speckit-clarify asks at most ${MAX_QUESTIONS}.`);
  data.forEach((question, index) => {
    if (typeof question?.question !== 'string' || !question.question.trim()) throw new FlowError(`Question ${index + 1} has no text.`);
    const options = Array.isArray(question.options) ? question.options : [];
    if (options.length < 2 || options.length > ANSWER_KEYS.length) throw new FlowError(`Question ${index + 1} needs 2 to ${ANSWER_KEYS.length} options.`);
    options.forEach((option, at) => {
      if (option?.key !== ANSWER_KEYS[at] || typeof option.text !== 'string' || !option.text.trim()) {
        throw new FlowError(`Question ${index + 1}: option ${at + 1} must have key "${ANSWER_KEYS[at]}" and a text.`);
      }
    });
    if (!options.some((option) => option.key === question.recommended)) throw new FlowError(`Question ${index + 1} recommends no listed option.`);
  });
  return data;
}

export function renderQuestion(question, index, total) {
  const lines = [`Question ${index + 1} of ${total}: ${question.question}`, ''];
  for (const option of question.options) lines.push(`  ${option.key}) ${option.text}${option.key === question.recommended ? '  (recommended)' : ''}`);
  if (question.reason) lines.push('', `Why ${question.recommended}: ${question.reason}`);
  return lines.join('\n');
}

// `final`: the scan after the last round; questions it still finds stop the run, because the spec is not clear yet.
export function questions({ rootDir, run, final = false }) {
  const list = validateQuestions(readJson(path.join(runDir(rootDir, run), 'questions.json'), 'The clarification questions'));
  if (final && list.length > 0) {
    const { folder } = readState(rootDir, run);
    throw new FlowError([
      `specs/${folder} still has ${list.length} open clarification question(s) after three rounds:`,
      ...list.map((question, index) => `- Q${index + 1}: ${question.question}`),
      '',
      `Answer them with: specify workflow run author -i feature=${folder} -i stage=clarify -i feedback="your answers"`,
    ].join('\n'));
  }
  // One flat field per possible question: the workflow's gates are top-level steps (a paused gate must never rerun an
  // agent step on resume), one per question, and their conditions and messages cannot index a list.
  const flat = {};
  for (let index = 0; index < MAX_QUESTIONS; index += 1) {
    flat[`has_q${index + 1}`] = index < list.length;
    flat[`q${index + 1}`] = index < list.length ? renderQuestion(list[index], index, list.length) : '';
  }
  return { has: list.length > 0, count: list.length, items: list.map((question, index) => ({ index, text: renderQuestion(question, index, list.length) })), ...flat };
}

// The answers for /speckit-clarify: the gate choices by question position ("recommended", an option key, or empty for
// a question that was not asked or not answered), which fall back to the recommended option (unattended mode).
export function answers({ rootDir, run, choices = '' }) {
  const list = validateQuestions(readJson(path.join(runDir(rootDir, run), 'questions.json'), 'The clarification questions'));
  const picked = String(choices).split(',').map((choice) => choice.trim());
  const extra = picked.slice(list.length).filter(Boolean);
  if (extra.length > 0) throw new FlowError(`Got answers for ${picked.filter(Boolean).length} questions, but only ${list.length} were asked.`);
  const lines = list.map((question, index) => {
    const choice = picked[index] && picked[index] !== 'recommended' ? picked[index] : question.recommended;
    const option = question.options.find((item) => item.key === choice);
    if (!option) throw new FlowError(`Question ${index + 1} has no option ${choice}.`);
    return `Q${index + 1}: ${question.question} = ${option.key}) ${option.text}`;
  });
  writeFileSync(path.join(runDir(rootDir, run), 'answers.md'), `${lines.join('\n')}\n`);
  return { count: lines.length, text: lines.join('; ') };
}

// After /speckit-plan and /speckit-tasks: what the mandatory gha hooks must have produced, and what to repair. After
// the tasks: `refine` for [P] group warnings (/speckit-gha-refine), `docs_task` when plan.md lists Architecture Updates
// but no task references docs/architecture/ (/speckit-tasks again). `strict` fails instead of asking for a repair.
export function check({ rootDir, run, after, git, env = {}, strict = false }) {
  const { folder } = readState(rootDir, run);
  const facts = collectFacts({ rootDir, folder, git, env });
  if (after === 'plan') {
    if (!facts.hasPlan) throw new FlowError(`/speckit-plan did not write specs/${folder}/plan.md.`);
    if (strict && !facts.architecture.present) throw new FlowError(`specs/${folder}/plan.md still has no \`## Architecture Updates\` section after /speckit-gha-architecture.`);
    return { repair: !facts.architecture.present, architecture: facts.architecture.present };
  }
  if (after === 'tasks') {
    if (!facts.hasTasks) throw new FlowError(`/speckit-tasks did not write specs/${folder}/tasks.md.`);
    const groups = describeParallelGroups(facts.tasksMarkdown);
    const docsTask = Boolean(facts.architecture?.present && !facts.architecture.none && !hasArchitectureTask(facts.tasksMarkdown));
    if (strict && docsTask) throw new FlowError(`specs/${folder}/tasks.md still has no task that references docs/architecture/, although plan.md lists Architecture Updates.`);
    if (strict && groups.warnings.length > 0) throw new FlowError(`The [P] groups of specs/${folder}/tasks.md still have warnings after /speckit-gha-refine:\n- ${groups.warnings.join('\n- ')}`);
    const file = path.join(runDir(rootDir, run), 'tasks.md');
    const lines = groups.phases.flatMap((phase) => [
      `${phase.phase ?? 'Tasks'} (${phase.open} tasks)`,
      ...phase.order.map((entry) => `  - ${entry.tasks.join(', ')}${entry.parallel ? ' in parallel' : ''}`),
    ]);
    writeFileSync(file, `${[...lines, '', groups.warnings.length > 0 ? 'Warnings:' : 'Warnings: none', ...groups.warnings.map((warning) => `  - ${warning}`)].join('\n')}\n`);
    const refine = groups.warnings.length > 0;
    return { refine, docs_task: docsTask, repair: refine || docsTask, warnings: groups.warnings, file: relative(rootDir, file) };
  }
  throw new UsageError('check needs --after plan or --after tasks.');
}

// The environment check after the plan (environment.json): whether the spec needs tooling that the environment
// actions do not provide yet, which an environment spec must add first. An environment spec itself (it changes only
// the environment actions) provides that tooling and is never held by the check.
export function environment({ rootDir, run }) {
  const data = readJson(path.join(runDir(rootDir, run), 'environment.json'), 'The environment check');
  if (typeof data?.needed !== 'boolean') throw new FlowError('environment.json needs a boolean "needed".');
  if (data.environment_spec === true) return { needed: false, what: '', environment_spec: true };
  if (data.needed && (typeof data.what !== 'string' || !data.what.trim())) throw new FlowError('environment.json must say what the environment lacks.');
  // Unattended runs cannot decide to continue without the tooling: they stop, and an environment spec comes first.
  if (data.needed && readInputs(rootDir, run).mode === 'unattended') {
    throw new FlowError(`The plan needs tooling the environment actions do not provide yet: ${data.what.trim()}\nAuthor the environment spec first (it changes only .github/actions/environment-setup and environment-verify), then run this workflow again for this spec.`);
  }
  return { needed: data.needed, what: data.needed ? data.what.trim() : '', environment_spec: false };
}

function readFindings(rootDir, run, name = 'findings.json') {
  const data = readJson(path.join(runDir(rootDir, run), name), 'The analysis findings');
  const list = Array.isArray(data?.findings) ? data.findings : null;
  if (!list) throw new FlowError(`${name} needs a "findings" list.`);
  list.forEach((finding, index) => {
    if (!SEVERITIES.includes(finding?.severity)) throw new FlowError(`Finding ${index + 1} has no severity of ${SEVERITIES.join(', ')}.`);
    if (typeof finding.summary !== 'string' || !finding.summary.trim()) throw new FlowError(`Finding ${index + 1} has no summary.`);
  });
  return list;
}

// The findings with a concrete fix, the most severe first: the order in which a person is asked about them.
const fixableOf = (list) => list
  .filter((finding) => typeof finding.fix === 'string' && finding.fix.trim())
  .sort((a, b) => SEVERITIES.indexOf(a.severity) - SEVERITIES.indexOf(b.severity));

export function renderFinding(finding, index, asked, total) {
  const later = total > asked && index === asked - 1 ? `\n\n${total - asked} more fixable finding(s) come up in the next round.` : '';
  return `Finding ${index + 1} of ${asked}: ${[finding.severity, finding.id, finding.summary].filter(Boolean).join(' ')}\n\nFix: ${finding.fix}${later}`;
}

// The findings of /speckit-analyze (findings.json): whether a remediation round is due, and one flat field per fixable
// finding a person is asked about (at most MAX_ASKED_FINDINGS, the most severe first).
export function findings({ rootDir, run }) {
  const list = readFindings(rootDir, run);
  const fixable = fixableOf(list);
  const asked = Math.min(fixable.length, MAX_ASKED_FINDINGS);
  const file = path.join(runDir(rootDir, run), 'findings.md');
  const lines = list.length === 0
    ? ['No findings.']
    : list.map((finding) => `- ${[finding.severity, finding.id, finding.summary].filter(Boolean).join(' ')}${finding.fix ? `\n  Fix: ${finding.fix}` : ''}`);
  writeFileSync(file, `${lines.join('\n')}\n`);
  const flat = {};
  for (let index = 0; index < MAX_ASKED_FINDINGS; index += 1) {
    flat[`has_f${index + 1}`] = index < asked;
    flat[`f${index + 1}`] = index < asked ? renderFinding(fixable[index], index, asked, fixable.length) : '';
  }
  return {
    count: list.length,
    fixable: fixable.length > 0,
    blocking: list.some((finding) => ['CRITICAL', 'HIGH'].includes(finding.severity)),
    file: relative(rootDir, file),
    ...flat,
  };
}

// The remediation of a round (remediation.json): in unattended mode every fixable finding; otherwise the asked ones
// whose gate chose `apply` (choices by position), so the person decides finding by finding.
export function approve({ rootDir, run, choices = '' }) {
  const fixable = fixableOf(readFindings(rootDir, run));
  const unattended = readInputs(rootDir, run).mode === 'unattended';
  const picked = String(choices).split(',').map((choice) => choice.trim());
  const approved = unattended ? fixable : fixable.slice(0, MAX_ASKED_FINDINGS).filter((_, index) => picked[index] === 'apply');
  writeFileSync(path.join(runDir(rootDir, run), 'remediation.json'), `${JSON.stringify({ findings: approved }, null, 2)}\n`);
  return { any: approved.length > 0, count: approved.length, skipped: fixable.length - approved.length };
}

// The changed files under docs/architecture/ (the after_plan hook applies the spec's design updates there), which are
// published with the spec folder.
export function changedArchitecture(git) {
  const result = git(['status', '--porcelain', '--untracked-files=all', '--', ARCHITECTURE_DIR]);
  if (result.status !== 0) return [];
  return result.stdout.split(/\r?\n/).filter(Boolean).map((line) => line.slice(3).split(' -> ').pop().replace(/^"|"$/g, ''));
}

// The Markdown files a spec publishes: its folder's and the changed ones under docs/architecture/.
function publishedMarkdown(rootDir, folder, git) {
  const listed = git(['ls-files', '--cached', '--others', '--exclude-standard', '--', specDir(folder)]).stdout.split(/\r?\n/);
  return [...listed, ...changedArchitecture(git)].filter((file) => file.endsWith('.md'));
}

// Runs the repository's Markdown check on `files` and collects its output instead of printing it, because stdout
// carries only this command's JSON. A missing tool is an error of the machine, not a finding.
function checkMarkdown({ rootDir, files, markdown }) {
  const output = [];
  const spawn = (command, args, options) => {
    if (options?.stdio !== 'inherit') return spawnSync(command, args, options);
    const result = spawnSync(command, args, { ...options, stdio: 'pipe', encoding: 'utf8' });
    output.push(String(result.stdout ?? ''), String(result.stderr ?? ''));
    return result;
  };
  try {
    markdown({ cwd: rootDir, files, log: () => {}, spawn });
    return { clean: true, output: '' };
  } catch (error) {
    if (/ requires "/.test(error.message)) throw new FlowError(error.message);
    return { clean: false, output: [...output, error.message].map((text) => text.trim()).filter(Boolean).join('\n') };
  }
}

// The Markdown check before the publish checks: its findings (markdown.md) go to a formatting-only agent step.
export function markdownCheck({ rootDir, run, git, markdown = runMarkdownChecks }) {
  const { folder } = readState(rootDir, run);
  const result = checkMarkdown({ rootDir, files: publishedMarkdown(rootDir, folder, git), markdown });
  const file = path.join(runDir(rootDir, run), 'markdown.md');
  writeFileSync(file, `${result.clean ? 'No findings.' : result.output}\n`);
  return { clean: result.clean, folder, file: relative(rootDir, file) };
}

// The publish checks: the spec is ready to implement when speckit-author.mjs reports no publish blockers, no
// CRITICAL or HIGH finding remains, and its Markdown passes the repository check. Records what is published with it
// and says when the checkout is not on the default branch.
export function ready({ rootDir, run, git, env = {}, markdown = runMarkdownChecks }) {
  const { folder } = readState(rootDir, run);
  const status = statusOf(rootDir, folder, git, env);
  const reasons = [...status.publishBlockers];
  if (existsSync(path.join(runDir(rootDir, run), 'findings.json')) && findings({ rootDir, run }).blocking) reasons.push('/speckit-analyze still reports CRITICAL or HIGH findings');
  const lint = checkMarkdown({ rootDir, files: publishedMarkdown(rootDir, folder, git), markdown });
  if (!lint.clean) reasons.push(`the Markdown check still fails:\n${lint.output}`);
  if (reasons.length > 0) throw new FlowError(`specs/${folder} is not ready to implement:\n- ${reasons.join('\n- ')}`);
  const spec = readFileSync(path.join(rootDir, specDir(folder), 'spec.md'), 'utf8');
  const title = spec.match(/^#\s+(?:Feature Specification:\s*)?(.+)$/m)?.[1]?.trim() ?? folder;
  const architecture = changedArchitecture(git);
  const branch = status.git?.currentBranch ?? null;
  const defaultBranch = status.git?.defaultBranch ?? null;
  const parts = [`specs/${folder} is ready to implement`];
  if (architecture.length > 0) parts.push(` and is published with its ${architecture.length} change(s) to docs/architecture/ (${architecture.join(', ')})`);
  parts.push('.');
  if (branch && defaultBranch && branch !== defaultBranch) parts.push(` Note: the checkout is on ${branch}, not ${defaultBranch}; the spec twin is only created once the spec reaches ${defaultBranch}.`);
  writeState(rootDir, run, { ...readState(rootDir, run), title, architecture });
  return { folder, title, files: status.git?.uncommitted ?? [], architecture, branch, defaultBranch, summary: parts.join('') };
}

// The handover: commit the spec folder and its docs/architecture/ changes as `Spec: <title>` and push them to the
// branch's upstream (`push`), commit only (`commit`), or leave them uncommitted (`none`). Never rebases, merges, or
// force-pushes.
export function publish({ rootDir, run, choice, git }) {
  // `ask` is the default of the publish input: interactive runs replace it with the gate's choice, unattended runs
  // leave the spec uncommitted unless the input asks for more.
  const action = !choice || choice === 'ask' ? 'none' : choice;
  if (!['push', 'commit', 'none'].includes(action)) throw new UsageError(`Publish must be push, commit, or none, got "${action}".`);
  const { folder, title, architecture = [] } = readState(rootDir, run);
  if (!title) throw new FlowError('Run the publish checks first.');
  const record = (published) => {
    writeState(rootDir, run, { ...readState(rootDir, run), published });
    return published;
  };
  if (action === 'none') return record({ action, folder, committed: false, pushed: false });
  const must = (args, what) => {
    const result = git(args);
    if (result.status !== 0) throw new FlowError(`${what} failed: ${(result.stderr || result.stdout).trim()}`);
    return result;
  };
  const branch = must(['rev-parse', '--abbrev-ref', 'HEAD'], 'Reading the current branch').stdout.trim();
  if (branch === 'HEAD') throw new FlowError('The checkout is not on a branch (detached HEAD); check out the branch the spec belongs on and run the workflow again for its folder.');
  const upstream = git(['rev-parse', '--abbrev-ref', '@{u}']);
  if (action === 'push' && upstream.status === 0) {
    must(['fetch', '--quiet'], 'git fetch');
    must(['merge', '--ff-only', '--quiet', '@{u}'], 'Fast-forwarding to the upstream branch (the branch has diverged; reconcile it by hand)');
  }
  const paths = [specDir(folder), ...architecture];
  must(['add', '-A', '--', ...paths], 'git add');
  const staged = git(['diff', '--cached', '--quiet', '--', ...paths]);
  if (staged.status !== 0) must(['commit', '--quiet', '-m', `Spec: ${title}`, '-m', 'Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>', '--', ...paths], 'git commit');
  if (action === 'push') {
    if (upstream.status === 0) must(['push', '--quiet'], 'git push');
    else must(['push', '--quiet', '-u', 'origin', branch], 'git push');
  }
  return record({ action, folder, committed: staged.status !== 0, pushed: action === 'push', architecture });
}

// The last step: result.json in the common shape (speckit-flow.mjs) with the spec folder, its title, and what the
// handover did.
export function result({ rootDir, run, git }) {
  const { folder, title = null, published = null, architecture = [] } = readState(rootDir, run);
  if (!folder) throw new FlowError('The run has no spec folder.');
  const changed = git(['status', '--porcelain', '--untracked-files=all', '--', specDir(folder), ...architecture]).stdout.split(/\r?\n/).filter(Boolean).map((line) => line.slice(3));
  return writeResult(rootDir, run, { workflow: 'author', folder, title, publish: published, files: changed });
}

// What a paused run waits for, for the /speckit-gha-author skill, which asks the person in Chat and resumes the run with
// `-i <input>=<choice>`. Every gate of the workflow names its verdict input after its id (`publish-verdict` →
// `publish_verdict`); the gates of a clarification round (`answer-<round>-<n>`) and of a remediation round
// (`fix-<round>-<n>`) are returned together, so they can be asked at once and answered in one resume. The first
// option of a gate is its recommended one.
export function pending({ rootDir, run }) {
  const described = describeRun(rootDir, run);
  const { status, step, state } = described;
  const { folder = null } = readState(rootDir, run);
  if (status === 'completed') {
    const results = state.step_results ?? {};
    const published = results['publish-chosen']?.output?.data ?? results['publish-input']?.output?.data ?? null;
    return { status, step, folder, publish: published };
  }
  if (status !== 'paused') return { status, step, folder, error: described.error };
  const questionRound = String(step).match(/^answer-(\d+)-\d+$/)?.[1];
  if (questionRound) {
    const list = validateQuestions(readJson(path.join(runDir(rootDir, run), 'questions.json'), 'The clarification questions'));
    return {
      status,
      kind: 'questions',
      round: Number(questionRound),
      questions: list.map((question, index) => ({
        input: `answer_${questionRound}_${index + 1}`,
        question: question.question,
        options: question.options,
        recommended: question.recommended,
        reason: question.reason ?? '',
      })),
    };
  }
  const fixRound = String(step).match(/^fix-(\d+)-\d+$/)?.[1];
  if (fixRound) {
    const fixable = fixableOf(readFindings(rootDir, run)).slice(0, MAX_ASKED_FINDINGS);
    return {
      status,
      kind: 'findings',
      round: Number(fixRound),
      options: ['apply', 'skip', 'abort'],
      findings: fixable.map((finding, index) => ({
        input: `fix_${fixRound}_${index + 1}`,
        severity: finding.severity,
        id: finding.id ?? '',
        summary: finding.summary,
        fix: finding.fix,
      })),
    };
  }
  return described;
}

export function main(argv, { rootDir = process.cwd(), env = process.env, git = defaultGit(rootDir), gh = defaultGh(rootDir), log = (text) => process.stdout.write(text), now = Date.now() } = {}) {
  const at = (run) => ({ rootDir, run, git, env });
  const commands = {
    start: (run) => start(at(run)),
    begin: (run) => begin({ ...at(run), gh, choice: option(argv, 'choice') ?? '' }),
    proposal: (run) => proposal(at(run)),
    pick: (run) => pick({ ...at(run), choice: option(argv, 'choice') ?? '' }),
    created: (run) => created({ ...at(run), now }),
    stage: (run) => stage({ ...at(run), name: option(argv, 'name') }),
    questions: (run) => questions({ ...at(run), final: argv.includes('--final') }),
    answers: (run) => answers({ ...at(run), choices: option(argv, 'choices') ?? '' }),
    check: (run) => check({ ...at(run), after: option(argv, 'after'), strict: argv.includes('--strict') }),
    environment: (run) => environment(at(run)),
    findings: (run) => findings(at(run)),
    approve: (run) => approve({ ...at(run), choices: option(argv, 'choices') ?? '' }),
    markdown: (run) => markdownCheck(at(run)),
    ready: (run) => ready(at(run)),
    publish: (run) => publish({ ...at(run), choice: option(argv, 'choice') ?? '' }),
    result: (run) => result(at(run)),
    pending: (run) => pending(at(run)),
  };
  return runFlowCommand(argv, commands, { script: 'speckit-author-flow.mjs', rootDir, helper: import.meta.url, log });
}

runAsScript(import.meta.url, main);
