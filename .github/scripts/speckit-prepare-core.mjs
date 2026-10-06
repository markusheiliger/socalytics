export const TWIN_LABEL = 'speckit:spec';
export const PENDING_LABEL = 'speckit:deps-pending';
export const STAGE_PREFIX = 'speckit:stage:';
export const STAGES = {
  specified: { color: 'c5def5', description: 'Spec twin stage: spec.md exists, no plan yet' },
  planned: { color: '7fb8e8', description: 'Spec twin stage: plan.md exists, no task list yet' },
  tasked: { color: '1d76db', description: 'Spec twin stage: tasks.md exists, no task completed on the default branch' },
  implementing: { color: 'f9d0c4', description: 'Spec twin stage: some tasks completed on the default branch' },
  implemented: { color: '0e8a16', description: 'Spec twin stage: all tasks completed on the default branch' },
  discarded: { color: 'cfd3d7', description: 'Spec twin stage: spec folder was removed; twin closed as not planned' },
};
export const DISCARDED_STAGE = 'discarded';
export const LABELS = [
  { name: TWIN_LABEL, color: '1d76db', description: 'Generated twin issue of a Spec Kit feature folder' },
  { name: PENDING_LABEL, color: 'fbca04', description: 'Spec twin whose dependencies still need to be inferred' },
  ...Object.entries(STAGES).map(([stage, { color, description }]) => ({ name: stageLabel(stage), color, description })),
];
export const ARTIFACTS = ['spec.md', 'plan.md', 'tasks.md'];
export const MAX_PROMPT_BYTES = 100_000;
export const MAX_REASON_LENGTH = 500;

const FOLDER_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;
const TASK_LINE_PATTERN = /^\s*[-*] \[( |x|X)\]\s+T\d{3,}\b/;
const SPEC_LINE_PATTERN = /^\*\*Spec\*\*:\s*\[`specs\/([^`/\s]+)`\]\([^)\s]+\)\s*$/gm;
const TITLE_PREFIX = /^Feature Specification:\s*/i;
const MAX_SUMMARY_LENGTH = 1000;
const DEPENDENCY_HINT = /depend|relationship|builds on|prerequisite|consumes|requires|sibling/i;

export function isValidFolderName(folder) {
  return typeof folder === 'string' && FOLDER_PATTERN.test(folder) && folder !== '.' && folder !== '..';
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

export function folderUrl({ serverUrl, repository, branch }, folder) {
  return `${serverUrl}/${repository}/tree/${branch}/specs/${folder}`;
}

export function artifactUrl({ serverUrl, repository, branch }, folder, artifact) {
  return `${serverUrl}/${repository}/blob/${branch}/specs/${folder}/${artifact}`;
}

export function renderSpecLine(context, folder) {
  return `**Spec**: [\`specs/${folder}\`](${folderUrl(context, folder)})`;
}

export function renderTwinTitle(spec) {
  return spec.title;
}

export function renderTwinBody(spec, artifacts, context) {
  const summary = spec.summary || '_No summary available in the specification._';
  const links = ARTIFACTS
    .filter((artifact) => artifacts.includes(artifact))
    .map((artifact) => `- [${artifact}](${artifactUrl(context, spec.folder, artifact)})`);
  return [
    renderSpecLine(context, spec.folder),
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
    skippedCreates: [],
    unreadable,
    duplicates,
  };

  for (const [folder, { spec, artifacts, tasks }] of [...specs.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    const title = renderTwinTitle(spec);
    const body = renderTwinBody(spec, artifacts, context);
    const stage = deriveStage(artifacts, tasks);
    const twin = byFolder.get(folder);
    if (!twin) {
      if (unreadable.length > 0) plan.skippedCreates.push({ folder, title });
      else plan.create.push({ folder, title, body, stage });
      continue;
    }
    if (twin.state === 'closed') {
      if (twin.state_reason === 'not_planned') {
        plan.reopen.push({ folder, issue: twin, title, body, stage, labels: stageLabelChange(twin, stage) });
      }
      continue;
    }
    if (twin.title !== title || normalizeBody(twin.body) !== normalizeBody(body)) {
      plan.update.push({ folder, issue: twin, title, body });
    }
    const labels = stageLabelChange(twin, stage);
    if (hasLabelChange(labels)) plan.relabel.push({ folder, issue: twin, stage, labels });
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
  return ['create', 'update', 'reopen', 'close', 'relabel'].some((key) => plan[key].length > 0);
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
