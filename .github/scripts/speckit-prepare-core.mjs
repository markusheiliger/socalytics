export const TWIN_LABEL = 'speckit:spec';
export const PENDING_LABEL = 'speckit:deps-pending';
export const STAGE_PREFIX = 'speckit:stage:';
export const STAGES = {
  specified: { color: 'c5def5', description: 'Spec twin stage: spec.md exists, no plan yet' },
  planned: { color: '7fb8e8', description: 'Spec twin stage: plan.md exists, no task list yet' },
  tasked: { color: '1d76db', description: 'Spec twin stage: tasks.md exists, no task completed on the default branch' },
  implement: { color: '5319e7', description: 'Spec twin stage: requested by a human; waiting for the speckit-orchestrate workflow to start it' },
  implementing: { color: 'f9d0c4', description: 'Spec twin stage: implementation pull request open, or some tasks completed on the default branch' },
  implemented: { color: '0e8a16', description: 'Spec twin stage: all tasks completed on the default branch' },
  discarded: { color: 'cfd3d7', description: 'Spec twin stage: spec folder was removed; twin closed as not planned' },
};
export const DISCARDED_STAGE = 'discarded';
export const IMPLEMENT_STAGE = 'implement';
// The stage an accepted request moves to once its implementation pull request is open (implement >> implementing >>
// implemented). It is also the computed stage of a folder whose tasks are partly checked on the default branch.
export const IMPLEMENTING_STAGE = 'implementing';
// The only computed stage from which a human may request implementation.
export const IMPLEMENTABLE_STAGE = 'tasked';
export const LABELS = [
  { name: TWIN_LABEL, color: '1d76db', description: 'Generated twin issue of a Spec Kit feature folder' },
  { name: PENDING_LABEL, color: 'fbca04', description: 'Spec twin whose dependencies still need to be inferred' },
  ...Object.entries(STAGES).map(([stage, { color, description }]) => ({ name: stageLabel(stage), color, description })),
];
export const ARTIFACTS = ['spec.md', 'plan.md', 'tasks.md'];
export const MAX_PROMPT_BYTES = 100_000;
export const MAX_REASON_LENGTH = 500;
// Feature folders live in specs/<folder>; implemented ones are archived to specs/archive/<folder> with the same name,
// which stays the twin's identity.
export const SPECS_DIR = 'specs';
export const ARCHIVE_DIR = `${SPECS_DIR}/archive`;

export function specDir(folder, { archived = false } = {}) {
  return `${archived ? ARCHIVE_DIR : SPECS_DIR}/${folder}`;
}

const FOLDER_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;
const TASK_LINE_PATTERN = /^\s*[-*] \[( |x|X)\]\s+T\d{3,}\b/;
const OPEN_CHECKBOX_PATTERN = /^\s*[-*] \[ \]/;
// The twin's identity: its spec folder, whether active (specs/<folder>) or archived (specs/archive/<folder>).
const SPEC_LINE_PATTERN = /^\*\*Spec\*\*:\s*\[`specs\/(?:archive\/)?([^`/\s]+)`\]\([^)\s]+\)\s*$/gm;
const TITLE_PREFIX = /^Feature Specification:\s*/i;
const MAX_SUMMARY_LENGTH = 1000;
const DEPENDENCY_HINT = /depend|relationship|builds on|prerequisite|consumes|requires|sibling/i;

export function isValidFolderName(folder) {
  return typeof folder === 'string' && FOLDER_PATTERN.test(folder) && folder !== '.' && folder !== '..';
}

// A fix spec (`/speckit fix`) names the implementation pull request it unblocks in spec.md: "**Fix for**: #75 …". It
// skips dependency inference (the tooling links it to the spec it unblocks) and starts ahead of the scheduler's limit.
const FIX_FOR_LINE = /^\*\*Fix for\*\*:[^\n]*?#(\d+)/m;
export function parseFixForLine(specMarkdown) {
  const match = String(specMarkdown ?? '').match(FIX_FOR_LINE);
  return match ? Number(match[1]) : null;
}

// Builds the spec entry used by sync and request checks from raw file contents of one spec folder. `archived` marks a
// spec in specs/archive/, which is implemented.
export function buildSpecEntry(folder, { specMarkdown, hasPlan, tasksMarkdown, checklistMarkdowns, archived = false }) {
  const artifacts = ['spec.md', ...(hasPlan ? ['plan.md'] : []), ...(tasksMarkdown === null ? [] : ['tasks.md'])];
  return {
    spec: parseSpec(folder, specMarkdown),
    artifacts,
    tasks: tasksMarkdown,
    openChecklistItems: countOpenChecklistItems(checklistMarkdowns),
    archived,
    fixFor: parseFixForLine(specMarkdown),
  };
}

function truncate(text, max) {
  return text.length <= max ? text : `${text.slice(0, max - 1).trimEnd()}…`;
}

function sectionLines(lines, heading) {
  const start = lines.findIndex((line) => line.trim().toLowerCase() === heading.toLowerCase());
  if (start === -1) return [];
  const result = [];
  for (const line of lines.slice(start + 1)) {
    if (/^#{1,2}\s/.test(line)) break;
    result.push(line);
  }
  return result;
}

export function parseSpec(folder, markdown) {
  const lines = markdown.replaceAll('\r\n', '\n').split('\n');
  const heading = lines.find((line) => /^#\s+\S/.test(line));
  const title = heading ? heading.replace(/^#\s+/, '').replace(TITLE_PREFIX, '').trim() : folder;

  const inputLine = lines.find((line) => /^\*\*Input\*\*:/.test(line));
  let summary = '';
  if (inputLine) {
    summary = inputLine
      .replace(/^\*\*Input\*\*:\s*/, '')
      .replace(/^User description:\s*/i, '')
      .trim()
      .replace(/^"(.*)"$/s, '$1')
      .trim();
  }

  const dependencyNotes = sectionLines(lines, '## Assumptions')
    .filter((line) => /^\s*[-*]\s+/.test(line) && DEPENDENCY_HINT.test(line))
    .map((line) => line.replace(/^\s*[-*]\s+/, '').trim());

  return {
    folder,
    title: title || folder,
    summary: truncate(summary, MAX_SUMMARY_LENGTH),
    dependencyNotes,
  };
}

export function folderUrl({ serverUrl, repository, branch }, folder, { archived = false } = {}) {
  return `${serverUrl}/${repository}/tree/${branch}/${specDir(folder, { archived })}`;
}

export function artifactUrl({ serverUrl, repository, branch }, folder, artifact, { archived = false } = {}) {
  return `${serverUrl}/${repository}/blob/${branch}/${specDir(folder, { archived })}/${artifact}`;
}

export function renderSpecLine(context, folder, { archived = false } = {}) {
  return `**Spec**: [\`${specDir(folder, { archived })}\`](${folderUrl(context, folder, { archived })})`;
}

export function renderTwinTitle(spec) {
  return spec.title;
}

export function renderTwinBody(spec, artifacts, context, { archived = false } = {}) {
  const summary = spec.summary || '_No summary available in the specification._';
  const links = ARTIFACTS
    .filter((artifact) => artifacts.includes(artifact))
    .map((artifact) => `- [${artifact}](${artifactUrl(context, spec.folder, artifact, { archived })})`);
  return [
    renderSpecLine(context, spec.folder, { archived }),
    '',
    '> [!NOTE]',
    '> Generated twin of a Spec Kit feature. The spec folder in the repository is authoritative.',
    '> The `speckit-prepare` workflow regenerates this description, so manual edits are overwritten.',
    '> The "blocked by" dependencies of this issue are maintained on GitHub.',
    '',
    '## Summary',
    '',
    summary,
    '',
    '## Artifacts',
    '',
    ...links,
    '',
  ].join('\n');
}

export function parseTwinFolder(body) {
  if (typeof body !== 'string') return null;
  const matches = [...body.replaceAll('\r\n', '\n').matchAll(SPEC_LINE_PATTERN)];
  if (matches.length !== 1) return null;
  const folder = matches[0][1];
  return isValidFolderName(folder) ? folder : null;
}

function labelNames(issue) {
  return (issue.labels ?? []).map((label) => (typeof label === 'string' ? label : label.name));
}

export function hasLabel(issue, name) {
  return labelNames(issue).includes(name);
}

export function stageLabel(stage) {
  return `${STAGE_PREFIX}${stage}`;
}

// Whether the twin carries an implementation request: waiting to start (`implement`) or with its implementation pull
// request open (`implementing`). Without a computed stage, `implementing` counts as a request; with one, only while the
// default branch has no task checked, because otherwise it is the computed stage of a local implementation.
export function isImplementRequested(issue, computedStage = null) {
  if (hasLabel(issue, stageLabel(IMPLEMENT_STAGE))) return true;
  return hasLabel(issue, stageLabel(IMPLEMENTING_STAGE)) && (computedStage === null || computedStage === IMPLEMENTABLE_STAGE);
}

export function deriveStage(artifacts, tasksMarkdown) {
  if (artifacts.includes('tasks.md')) {
    const tasks = (tasksMarkdown ?? '')
      .split(/\r?\n/)
      .map((line) => line.match(TASK_LINE_PATTERN))
      .filter(Boolean);
    const done = tasks.filter((match) => match[1] !== ' ').length;
    if (done === 0) return 'tasked';
    return done === tasks.length ? 'implemented' : 'implementing';
  }
  return artifacts.includes('plan.md') ? 'planned' : 'specified';
}

function stageLabelsOf(issue) {
  return labelNames(issue).filter((name) => name.startsWith(STAGE_PREFIX));
}

// Labels to add and remove so that the issue carries exactly the target stage label.
export function stageLabelChange(issue, stage) {
  const target = stageLabel(stage);
  const current = stageLabelsOf(issue);
  return {
    add: current.includes(target) ? [] : [target],
    remove: current.filter((name) => name !== target),
  };
}

function hasLabelChange(change) {
  return change.add.length > 0 || change.remove.length > 0;
}

export function countOpenChecklistItems(markdownFiles) {
  return markdownFiles
    .flatMap((markdown) => markdown.split(/\r?\n/))
    .filter((line) => OPEN_CHECKBOX_PATTERN.test(line)).length;
}

// Reasons why a spec cannot carry the implement flag, based on the files on the default branch.
export function implementBlockers({ folder, computedStage, openChecklistItems }) {
  const reasons = [];
  if (computedStage !== IMPLEMENTABLE_STAGE) {
    reasons.push(`the spec is at stage \`${computedStage}\`, but implementation can only be requested at \`${IMPLEMENTABLE_STAGE}\``);
  }
  if (openChecklistItems > 0) {
    reasons.push(`${openChecklistItems} checklist item(s) in \`specs/${folder}/checklists/\` are not checked`);
  }
  return reasons;
}

// Decides the target stage of an open twin; a valid implementation request keeps its stage (`implement` while it
// waits, `implementing` once the scheduler opened its pull request), otherwise the computed stage wins.
// `twin.implementRequester` ({ login, canWrite }) is resolved by the caller from the issue's label history.
export function targetStage(twin, folder, entry) {
  const computed = deriveStage(entry.artifacts, entry.tasks);
  if (!isImplementRequested(twin, computed)) return { stage: computed, revoked: null };
  const requested = hasLabel(twin, stageLabel(IMPLEMENT_STAGE)) ? IMPLEMENT_STAGE : IMPLEMENTING_STAGE;
  const reasons = [
    ...requesterBlockers(twin.implementRequester),
    ...implementBlockers({ folder, computedStage: computed, openChecklistItems: entry.openChecklistItems ?? 0 }),
  ];
  return reasons.length === 0 ? { stage: requested, revoked: null } : { stage: computed, revoked: reasons, requested };
}

export function requesterBlockers(requester) {
  if (requester === undefined) return [];
  if (!requester?.login) return ['the person who set the flag could not be determined'];
  return requester.canWrite ? [] : [`@${requester.login} needs at least write access to request implementation`];
}

// Validates a human request to flag a twin for implementation; `requester` is { login, canWrite } from the label history.
export function planImplementRequest({ issue, entry, requester }) {
  const implementLabel = stageLabel(IMPLEMENT_STAGE);
  const onlyRemoveFlag = { add: [], remove: hasLabel(issue, implementLabel) ? [implementLabel] : [] };
  const folder = hasLabel(issue, TWIN_LABEL) ? parseTwinFolder(issue.body) : null;
  if (!folder) return { action: 'reject', labels: onlyRemoveFlag, reasons: ['this issue is not a spec twin'] };
  if (issue.state !== 'open') return { action: 'reject', labels: onlyRemoveFlag, reasons: ['the spec twin is closed'] };
  if (!entry) {
    return { action: 'reject', labels: onlyRemoveFlag, reasons: [`\`specs/${folder}\` does not exist on the default branch`] };
  }
  if (entry.archived) {
    return { action: 'reject', folder, labels: onlyRemoveFlag, reasons: [`the spec is already implemented and archived in \`${specDir(folder, { archived: true })}\``] };
  }

  const computed = deriveStage(entry.artifacts, entry.tasks);
  const reasons = [
    ...requesterBlockers(requester ?? null),
    ...implementBlockers({ folder, computedStage: computed, openChecklistItems: entry.openChecklistItems ?? 0 }),
  ];
  if (reasons.length === 0) {
    return { action: 'accept', folder, stage: IMPLEMENT_STAGE, labels: stageLabelChange(issue, IMPLEMENT_STAGE), reasons };
  }
  return { action: 'reject', folder, stage: computed, labels: stageLabelChange(issue, computed), reasons };
}

export function resolveTwins(issues) {
  const byFolder = new Map();
  const unreadable = [];
  const duplicates = [];
  for (const issue of [...issues].sort((a, b) => a.number - b.number)) {
    const folder = parseTwinFolder(issue.body);
    if (!folder) {
      unreadable.push(issue);
    } else if (byFolder.has(folder)) {
      duplicates.push({ folder, issue, primary: byFolder.get(folder) });
    } else {
      byFolder.set(folder, issue);
    }
  }
  return { byFolder, unreadable, duplicates };
}

export function planSync({ specs, issues, context }) {
  const { byFolder, unreadable, duplicates } = resolveTwins(issues);
  const plan = {
    create: [],
    update: [],
    reopen: [],
    close: [],
    relabel: [],
    archive: [],
    archivedWithoutTwin: [],
    skippedCreates: [],
    unreadable,
    duplicates,
  };

  for (const [folder, entry] of [...specs.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    const { spec, artifacts, tasks } = entry;
    const title = renderTwinTitle(spec);
    const body = renderTwinBody(spec, artifacts, context, { archived: entry.archived });
    const twin = byFolder.get(folder);
    // An archived spec is implemented: its twin is closed as completed, labelled implemented, and points to the archive
    // (also when it was already closed, which is how its links follow the archive after the merge). It never gets a
    // new twin and is never discarded.
    if (entry.archived) {
      if (!twin) {
        plan.archivedWithoutTwin.push({ folder });
        continue;
      }
      const labels = stageLabelChange(twin, 'implemented');
      const fields = twin.state === 'closed' && twin.state_reason === 'completed' ? {} : { state: 'closed', state_reason: 'completed' };
      if (twin.title !== title || normalizeBody(twin.body) !== normalizeBody(body) || hasLabelChange(labels) || Object.keys(fields).length > 0) {
        plan.archive.push({ folder, issue: twin, title, body, stage: 'implemented', labels, fields });
      }
      continue;
    }
    if (!twin) {
      if (unreadable.length > 0) plan.skippedCreates.push({ folder, title });
      else plan.create.push({ folder, title, body, stage: deriveStage(artifacts, tasks), fix: entry.fixFor !== null && entry.fixFor !== undefined });
      continue;
    }
    if (twin.state === 'closed') {
      if (twin.state_reason === 'not_planned') {
        const stage = deriveStage(artifacts, tasks);
        plan.reopen.push({ folder, issue: twin, title, body, stage, labels: stageLabelChange(twin, stage) });
      }
      continue;
    }
    const { stage, revoked, requested } = targetStage(twin, folder, entry);
    const labels = stageLabelChange(twin, stage);
    if (twin.title !== title || normalizeBody(twin.body) !== normalizeBody(body)) {
      plan.update.push({ folder, issue: twin, title, body, stage, labels, revoked, ...(revoked ? { requested } : {}) });
    } else if (hasLabelChange(labels)) {
      plan.relabel.push({ folder, issue: twin, stage, labels, revoked, ...(revoked ? { requested } : {}) });
    }
  }

  for (const [folder, twin] of byFolder) {
    if (specs.has(folder)) continue;
    const labels = stageLabelChange(twin, DISCARDED_STAGE);
    if (twin.state === 'open') {
      plan.close.push({ folder, issue: twin, labels });
    } else if (twin.state_reason === 'not_planned' && hasLabelChange(labels)) {
      plan.relabel.push({ folder, issue: twin, stage: DISCARDED_STAGE, labels });
    }
  }
  return plan;
}

function normalizeBody(body) {
  return (body ?? '').replaceAll('\r\n', '\n').trimEnd();
}

export function hasPlannedChanges(plan) {
  return ['create', 'update', 'reopen', 'close', 'relabel', 'archive'].some((key) => plan[key].length > 0);
}

function byteLength(text) {
  return Buffer.byteLength(text, 'utf8');
}

function renderContextEntry(entry, { includeNotes }) {
  const lines = [
    `### ${entry.folder}${entry.pending ? ' (NEW)' : ''}`,
    `Title: ${entry.title}`,
    `Summary: ${entry.summary || '(none)'}`,
  ];
  if (includeNotes && entry.dependencyNotes.length > 0) {
    lines.push('Dependency notes from the specification:');
    for (const note of entry.dependencyNotes) lines.push(`- ${truncate(note, 1500)}`);
  }
  return lines.join('\n');
}

export function buildPrompt(entries, maxBytes = MAX_PROMPT_BYTES) {
  const instructions = [
    'You maintain the implementation order of feature specifications for GitHub automation.',
    'Each feature below is an open feature specification. Features marked (NEW) were just added.',
    'Decide which "blocked by" relationships involving at least one NEW feature are needed:',
    'feature A is blocked by feature B when A cannot be implemented before B is implemented,',
    'because A builds on, consumes, extends, or requires something that B introduces.',
    'Rules:',
    '- Only add a relationship when the features\' text gives clear evidence for it.',
    '- Every relationship must involve at least one NEW feature, as blocked feature, blocking feature, or both.',
    '- Relationships may point either way: a NEW feature can block existing features.',
    '- Never create cycles and never relate a feature to itself.',
    '- Prefer direct prerequisites; omit relationships that are only implied transitively.',
    '- An environment feature only extends the build and verification environment (the environment-setup and',
    '  environment-verify actions). A feature that needs a framework, SDK, or tool that an environment feature adds',
    '  is blocked by that environment feature.',
    `- Each reason is one sentence of at most ${MAX_REASON_LENGTH} characters citing the evidence.`,
    'Respond with JSON only, no code fences and no other text, in exactly this shape:',
    '{"links":[{"blocked":"<folder>","blockedBy":"<folder>","reason":"<sentence>"}]}',
    'Use the folder names exactly as written in the headings. Respond with {"links":[]} when no relationship is needed.',
    '',
    '## Features',
    '',
  ].join('\n');

  const sorted = [...entries].sort((a, b) => Number(b.pending) - Number(a.pending) || a.folder.localeCompare(b.folder));
  const parts = [];
  let used = byteLength(instructions);
  let omitted = 0;
  for (const entry of sorted) {
    let text = `${renderContextEntry(entry, { includeNotes: true })}\n\n`;
    if (used + byteLength(text) > maxBytes) text = `${renderContextEntry(entry, { includeNotes: false })}\n\n`;
    if (used + byteLength(text) > maxBytes) {
      if (entry.pending) throw new Error(`Prompt budget exceeded before including new feature ${entry.folder}`);
      omitted += 1;
      continue;
    }
    parts.push(text);
    used += byteLength(text);
  }
  if (omitted > 0) parts.push(`(${omitted} existing features omitted because of the size limit.)\n`);
  return `${instructions}${parts.join('')}`.trimEnd() + '\n';
}

export function parseInferenceOutput(text) {
  if (typeof text !== 'string' || text.trim() === '') throw new Error('Inference output is empty');
  const start = text.indexOf('{');
  const end = text.lastIndexOf('}');
  if (start === -1 || end < start) throw new Error('Inference output contains no JSON object');
  let value;
  try {
    value = JSON.parse(text.slice(start, end + 1));
  } catch (error) {
    throw new Error(`Inference output is not valid JSON: ${error.message}`);
  }
  if (!value || typeof value !== 'object' || !Array.isArray(value.links)) {
    throw new Error('Inference output must be an object with a "links" array');
  }
  return value.links.map((link, index) => {
    if (!link || typeof link !== 'object') throw new Error(`Link ${index} is not an object`);
    const { blocked, blockedBy, reason } = link;
    if (typeof blocked !== 'string' || typeof blockedBy !== 'string') {
      throw new Error(`Link ${index} must have string "blocked" and "blockedBy" folders`);
    }
    if (typeof reason !== 'string' || reason.trim() === '') throw new Error(`Link ${index} has no reason`);
    return { blocked: blocked.trim(), blockedBy: blockedBy.trim(), reason: truncate(reason.trim(), MAX_REASON_LENGTH) };
  });
}

export function findCycle(edges) {
  const graph = new Map();
  for (const [from, to] of edges) {
    if (!graph.has(from)) graph.set(from, []);
    graph.get(from).push(to);
  }
  const state = new Map();
  const stack = [];
  const visit = (node) => {
    state.set(node, 'visiting');
    stack.push(node);
    for (const next of graph.get(node) ?? []) {
      if (state.get(next) === 'visiting') return [...stack.slice(stack.indexOf(next)), next];
      if (!state.has(next)) {
        const cycle = visit(next);
        if (cycle) return cycle;
      }
    }
    stack.pop();
    state.set(node, 'done');
    return null;
  };
  for (const node of graph.keys()) {
    if (!state.has(node)) {
      const cycle = visit(node);
      if (cycle) return cycle;
    }
  }
  return null;
}

export function validateLinks({ links, openFolders, pendingFolders, existingEdges }) {
  const existing = new Set(existingEdges.map(([blocked, blockedBy]) => `${blocked}\u0000${blockedBy}`));
  const accepted = [];
  const skipped = [];
  const seen = new Set();
  for (const link of links) {
    for (const folder of [link.blocked, link.blockedBy]) {
      if (!openFolders.has(folder)) throw new Error(`Link references unknown or closed feature "${folder}"`);
    }
    if (link.blocked === link.blockedBy) throw new Error(`Link relates "${link.blocked}" to itself`);
    if (!pendingFolders.has(link.blocked) && !pendingFolders.has(link.blockedBy)) {
      throw new Error(`Link ${link.blocked} <- ${link.blockedBy} involves no newly added feature`);
    }
    const key = `${link.blocked}\u0000${link.blockedBy}`;
    if (existing.has(key) || seen.has(key)) {
      skipped.push(link);
      continue;
    }
    seen.add(key);
    accepted.push(link);
  }
  const cycle = findCycle([...existingEdges, ...accepted.map((link) => [link.blocked, link.blockedBy])]);
  if (cycle) throw new Error(`Links would create a dependency cycle: ${cycle.join(' -> ')}`);
  return { accepted, skipped };
}
