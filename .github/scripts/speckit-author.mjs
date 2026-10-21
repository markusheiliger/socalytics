import { existsSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { countOpenChecklistItems, deriveStage, implementBlockers, specDir } from './speckit-prepare-core.mjs';
import { UsageError, defaultGit, resolveFolder } from './speckit-orchestrate.mjs';

// Reports how far a spec has come in the Spec Kit authoring flow (specify → clarify → plan → tasks → analyze) and what
// still blocks publishing it, so /speckit-gha-author can resume at the next Spec Kit step. Read-only.

const NEEDS_CLARIFICATION = /\[NEEDS CLARIFICATION/g;
const CLARIFICATION_SESSION = /^###\s+Session\b/;
const ARCHITECTURE_HEADING = /^##\s+Architecture Updates\s*$/;
const ARCHITECTURE_TASK = /^\s*-\s+\[[ xX]\]\s+T\d+\b.*docs\/architecture\//;

// The `## Architecture Updates` section of a plan.md (constitution Principle I): whether it exists, and whether it
// declares that the feature has no architectural effect (its first non-empty line starts with "None").
export function architectureUpdates(planMarkdown) {
  const lines = String(planMarkdown ?? '').replaceAll('\r\n', '\n').split('\n');
  const start = lines.findIndex((line) => ARCHITECTURE_HEADING.test(line.trim()));
  if (start === -1) return { present: false, none: false };
  const body = [];
  for (const line of lines.slice(start + 1)) {
    if (/^##\s/.test(line)) break;
    body.push(line);
  }
  const first = body.map((line) => line.trim()).find(Boolean) ?? '';
  return { present: true, none: /^[*_`]*None\b/i.test(first) };
}

export function hasArchitectureTask(tasksMarkdown) {
  return String(tasksMarkdown ?? '').split(/\r?\n/).some((line) => ARCHITECTURE_TASK.test(line));
}

export function countClarificationSessions(specMarkdown) {
  const lines = specMarkdown.replaceAll('\r\n', '\n').split('\n');
  const start = lines.findIndex((line) => /^##\s+Clarifications\s*$/.test(line.trim()));
  if (start === -1) return 0;
  let sessions = 0;
  for (const line of lines.slice(start + 1)) {
    if (/^##\s/.test(line)) break;
    if (CLARIFICATION_SESSION.test(line)) sessions += 1;
  }
  return sessions;
}

// The next Spec Kit step for a spec in the order of the Spec Kit flow. `analyze` leaves no trace in the artifacts, so
// a spec with tasks that is not yet published is always sent to analyze again before the publish checks.
export function nextAuthoringStep(facts) {
  if (facts.archived) return 'done';
  if (!facts.hasSpec) return 'specify';
  const stage = deriveStage(facts.artifacts, facts.tasksMarkdown);
  if (stage === 'implementing' || stage === 'implemented') return 'done';
  if (!facts.hasPlan) return facts.clarificationSessions === 0 || facts.openClarificationMarkers > 0 ? 'clarify' : 'plan';
  if (!facts.hasTasks) return 'tasks';
  return facts.git?.published ? 'published' : 'analyze';
}

// Everything that keeps a tasked spec from being published as ready for implementation.
export function publishBlockers(facts) {
  if (!facts.hasSpec) return ['spec.md does not exist yet'];
  const stage = deriveStage(facts.artifacts, facts.tasksMarkdown);
  const reasons = implementBlockers({ folder: facts.folder, computedStage: stage, openChecklistItems: facts.openChecklistItems });
  if (facts.openClarificationMarkers > 0) reasons.push(`spec.md still contains ${facts.openClarificationMarkers} [NEEDS CLARIFICATION] marker(s)`);
  // `architecture` is present once plan.md exists.
  if (facts.architecture && !facts.architecture.present) {
    reasons.push('plan.md has no `## Architecture Updates` section (constitution Principle I); run /speckit-gha-architecture');
  } else if (facts.architecture && !facts.architecture.none && facts.tasksMarkdown && !hasArchitectureTask(facts.tasksMarkdown)) {
    reasons.push('tasks.md has no task that references docs/architecture/ although plan.md § Architecture Updates lists changes');
  }
  return reasons;
}

function gitFacts(git, folder, env) {
  const paths = [specDir(folder)];
  const run = (args) => {
    try {
      return git(args);
    } catch {
      return { status: 1, stdout: '', stderr: '' };
    }
  };
  const branch = run(['rev-parse', '--abbrev-ref', 'HEAD']);
  if (branch.status !== 0) return null;
  const currentBranch = branch.stdout.trim();
  const status = run(['status', '--porcelain', '--', ...paths]);
  const uncommitted = status.stdout.split(/\r?\n/).filter(Boolean).map((line) => line.slice(3));
  const upstreamResult = run(['rev-parse', '--abbrev-ref', '@{u}']);
  const upstream = upstreamResult.status === 0 ? upstreamResult.stdout.trim() : null;
  const unpushed = upstream
    ? run(['log', '--format=%h', `${upstream}..HEAD`, '--', ...paths]).stdout.split(/\r?\n/).filter(Boolean).length
    : null;
  const head = run(['symbolic-ref', '--quiet', '--short', 'refs/remotes/origin/HEAD']);
  const defaultBranch = env.SPECKIT_DEFAULT_BRANCH
    || (head.status === 0 ? head.stdout.trim().replace(/^origin\//, '') : null);
  return {
    currentBranch,
    defaultBranch,
    upstream,
    uncommitted,
    unpushedCommits: unpushed,
    published: upstream !== null && uncommitted.length === 0 && unpushed === 0,
  };
}

export function collectFacts({ rootDir, folder, git, env = {} }) {
  const archived = !existsSync(path.join(rootDir, specDir(folder))) && existsSync(path.join(rootDir, specDir(folder, { archived: true })));
  const dir = path.join(rootDir, specDir(folder, { archived }));
  const read = (file) => {
    const full = path.join(dir, file);
    return existsSync(full) ? readFileSync(full, 'utf8') : null;
  };
  const specMarkdown = read('spec.md');
  const tasksMarkdown = read('tasks.md');
  const checklistDir = path.join(dir, 'checklists');
  const checklistFiles = existsSync(checklistDir) ? readdirSync(checklistDir).filter((name) => name.endsWith('.md')).sort() : [];
  const checklists = checklistFiles.map((name) => ({ name, open: countOpenChecklistItems([readFileSync(path.join(checklistDir, name), 'utf8')]) }));
  const planMarkdown = read('plan.md');
  const hasPlan = planMarkdown !== null;
  return {
    folder,
    archived,
    hasSpec: specMarkdown !== null,
    hasPlan,
    hasTasks: tasksMarkdown !== null,
    artifacts: [...(specMarkdown === null ? [] : ['spec.md']), ...(hasPlan ? ['plan.md'] : []), ...(tasksMarkdown === null ? [] : ['tasks.md'])],
    tasksMarkdown,
    architecture: hasPlan ? architectureUpdates(planMarkdown) : null,
    clarificationSessions: specMarkdown ? countClarificationSessions(specMarkdown) : 0,
    openClarificationMarkers: specMarkdown ? (specMarkdown.match(NEEDS_CLARIFICATION) ?? []).length : 0,
    checklists,
    openChecklistItems: checklists.reduce((sum, checklist) => sum + checklist.open, 0),
    git: git ? gitFacts(git, folder, env) : null,
  };
}

export function assess(facts) {
  return {
    folder: facts.folder,
    location: facts.hasSpec ? specDir(facts.folder, { archived: facts.archived }) : null,
    stage: facts.hasSpec ? deriveStage(facts.artifacts, facts.tasksMarkdown) : null,
    nextStep: nextAuthoringStep(facts),
    clarificationSessions: facts.clarificationSessions,
    openClarificationMarkers: facts.openClarificationMarkers,
    checklists: facts.checklists,
    publishBlockers: facts.archived ? [] : publishBlockers(facts),
    git: facts.git,
  };
}

export function renderStatus(status) {
  const lines = [`Spec folder: ${status.location ?? `specs/${status.folder} (not created yet)`}`];
  if (status.stage) lines.push(`Stage: ${status.stage}`);
  lines.push(`Next Spec Kit step: ${status.nextStep}`);
  lines.push(`Clarification sessions: ${status.clarificationSessions}; open [NEEDS CLARIFICATION] markers: ${status.openClarificationMarkers}`);
  for (const checklist of status.checklists) lines.push(`Checklist ${checklist.name}: ${checklist.open} open item(s)`);
  lines.push(status.publishBlockers.length > 0 ? 'Publish blockers:' : 'Publish blockers: none');
  for (const reason of status.publishBlockers) lines.push(`- ${reason}`);
  if (status.git) {
    const { currentBranch, defaultBranch, upstream, uncommitted, unpushedCommits } = status.git;
    lines.push(`Branch: ${currentBranch}${defaultBranch ? ` (default branch: ${defaultBranch})` : ''}${upstream ? `, upstream ${upstream}` : ', no upstream'}`);
    lines.push(`Uncommitted spec changes: ${uncommitted.length > 0 ? uncommitted.join(', ') : 'none'}`);
    if (unpushedCommits !== null) lines.push(`Unpushed commits touching the spec: ${unpushedCommits}`);
  }
  return `${lines.join('\n')}\n`;
}

export function main(argv, { rootDir = process.cwd(), env = process.env, git = defaultGit(rootDir), log = (text) => process.stdout.write(text) } = {}) {
  const json = argv.includes('--json');
  const rest = argv.filter((arg) => arg !== '--json');
  if (rest[0] !== 'status' || rest.length > 2) throw new UsageError('Usage: speckit-author.mjs status [<spec folder>] [--json]');
  const folder = resolveFolder({ folder: rest[1], env, rootDir });
  const status = assess(collectFacts({ rootDir, folder, git, env }));
  log(json ? `${JSON.stringify(status, null, 2)}\n` : renderStatus(status));
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = error instanceof UsageError ? 2 : 1;
  }
}
