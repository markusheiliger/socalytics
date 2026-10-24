import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  ARCHIVE_DIR,
  DISCARDED_STAGE,
  IMPLEMENT_STAGE,
  IMPLEMENTABLE_STAGE,
  IMPLEMENTING_STAGE,
  LABELS,
  PENDING_LABEL,
  SPECS_DIR,
  STAGE_PREFIX,
  TWIN_LABEL,
  buildPrompt,
  buildSpecEntry,
  deriveStage,
  hasLabel,
  implementBlockers,
  isImplementRequested,
  isValidFolderName,
  parseInferenceOutput,
  parseTwinFolder,
  planSync,
  requesterBlockers,
  resolveTwins,
  specDir,
  stageLabel,
  stageLabelChange,
  validateLinks,
} from './speckit-prepare-core.mjs';
import { createGitHubClient, createReporter, setOutput } from './speckit-script-runtime.mjs';

// The workflow token's user, which records implementation requests and sets the stage labels.
const BOT_LOGIN = 'github-actions[bot]';
function readOptional(filePath) {
  return existsSync(filePath) ? readFileSync(filePath, 'utf8') : null;
}

export function readSpecFolder(rootDir, name, { archived = false } = {}) {
  const folderPath = path.join(rootDir, ...specDir(name, { archived }).split('/'));
  if (!isValidFolderName(name) || !existsSync(folderPath) || !statSync(folderPath).isDirectory()) return null;
  const specMarkdown = readOptional(path.join(folderPath, 'spec.md'));
  if (specMarkdown === null) return null;
  const checklistDir = path.join(folderPath, 'checklists');
  const checklistMarkdowns = existsSync(checklistDir)
    ? readdirSync(checklistDir).filter((file) => file.endsWith('.md')).sort().map((file) => readFileSync(path.join(checklistDir, file), 'utf8'))
    : [];
  return buildSpecEntry(name, {
    specMarkdown,
    hasPlan: existsSync(path.join(folderPath, 'plan.md')),
    tasksMarkdown: readOptional(path.join(folderPath, 'tasks.md')),
    checklistMarkdowns,
    archived,
  });
}

// Every spec folder: active ones in specs/, implemented ones in specs/archive/. An active folder wins over an archived
// one of the same name.
export function discoverSpecs(rootDir) {
  const specs = new Map();
  for (const archived of [true, false]) {
    const dir = path.join(rootDir, ...(archived ? ARCHIVE_DIR : SPECS_DIR).split('/'));
    if (!existsSync(dir)) continue;
    for (const name of readdirSync(dir).sort()) {
      if (!archived && `${SPECS_DIR}/${name}` === ARCHIVE_DIR) continue;
      const entry = readSpecFolder(rootDir, name, { archived });
      if (entry) specs.set(name, entry);
    }
  }
  return specs;
}

function issueRef(issue) {
  return `#${issue.number}`;
}

// Twins mirror the specs of the repository's default branch, so their links point there.
function contextFromEnv(env) {
  const branch = String(env.SPECKIT_DEFAULT_BRANCH ?? '').trim();
  if (!branch) throw new Error('Expected SPECKIT_DEFAULT_BRANCH (the repository default branch).');
  return {
    serverUrl: (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, ''),
    repository: env.GITHUB_REPOSITORY,
    branch,
  };
}

function reportPlan(report, plan, dryRun) {
  const verb = dryRun ? 'Would' : 'Did';
  report.line(`## Spec twin synchronization${dryRun ? ' (dry run)' : ''}`);
  report.line();
  const rows = [
    ...plan.create.map((item) => `- ${verb} create twin for \`${item.folder}\` (stage \`${item.stage}\`)`),
    ...plan.update.map((item) => `- ${verb} update ${issueRef(item.issue)} for \`${item.folder}\`${item.labels.add.length > 0 ? ` (stage \`${item.stage}\`)` : ''}`),
    ...plan.reopen.map((item) => `- ${verb} reopen ${issueRef(item.issue)} for \`${item.folder}\` (stage \`${item.stage}\`)`),
    ...plan.close.map((item) => `- ${verb} close ${issueRef(item.issue)} as not planned and mark it \`discarded\` (\`${item.folder}\` no longer exists)`),
    ...plan.relabel.map((item) => `- ${verb} set stage \`${item.stage}\` on ${issueRef(item.issue)} for \`${item.folder}\``),
    ...plan.archive.map((item) => `- ${verb} point ${issueRef(item.issue)} to the archived \`${item.folder}\` (closed as completed, \`implemented\`)`),
    ...plan.archivedWithoutTwin.map((item) => `- Note: the archived \`${item.folder}\` has no twin; archived specs get none`),
  ];
  for (const row of rows.length > 0 ? rows : ['- No twin changes needed']) report.line(row);
  for (const item of [...plan.update, ...plan.relabel].filter((entry) => entry.revoked)) {
    report.line(`- ${verb} revoke the implement flag on ${issueRef(item.issue)}: ${item.revoked.join('; ')}`);
  }
  for (const duplicate of plan.duplicates) {
    report.line(`- Warning: ${issueRef(duplicate.issue)} duplicates ${issueRef(duplicate.primary)} for \`${duplicate.folder}\` and is ignored`);
  }
  if (plan.unreadable.length > 0) {
    report.line();
    report.line('### Unreadable twins');
    report.line();
    report.line(`These \`${TWIN_LABEL}\` issues have no readable \`**Spec**\` line. Restore the line from the issue's edit history or remove the label. No new twins are created until this is fixed.`);
    report.line();
    for (const issue of plan.unreadable) report.line(`- ${issueRef(issue)} ${issue.html_url ?? ''}`.trimEnd());
    for (const skipped of plan.skippedCreates) report.line(`- Skipped creating a twin for \`${skipped.folder}\``);
  }
}

function labelName(label) {
  return typeof label === 'string' ? label : label.name;
}

// Re-reads the issue's labels right before writing so concurrent label edits are kept, then writes
// the complete label set together with any other issue fields in a single request.
export async function updateIssueWithLabels(client, issue, { add, remove }, fields = {}) {
  if (add.length === 0 && remove.length === 0) {
    await client.updateIssue(issue.number, fields);
    Object.assign(issue, fields);
    return;
  }
  const current = (await client.getIssueLabels(issue.number)).map(labelName);
  const labels = [...current.filter((name) => !remove.includes(name)), ...add.filter((name) => !current.includes(name))];
  const changed = labels.length !== current.length || labels.some((name) => !current.includes(name));
  if (!changed && Object.keys(fields).length === 0) return;
  await client.updateIssue(issue.number, changed ? { ...fields, labels } : fields);
  Object.assign(issue, fields, { labels: labels.map((name) => ({ name })) });
}

function revocationComment(item) {
  return [
    `The \`${stageLabel(item.requested ?? IMPLEMENT_STAGE)}\` ${item.requested === IMPLEMENTING_STAGE ? 'stage' : 'flag'} was removed because:`,
    '',
    ...item.revoked.map((reason) => `- ${reason}`),
    '',
    `The twin is back at its computed stage \`${item.stage}\`. Comment \`/speckit implement\` on this issue again once the conditions hold.`,
  ].join('\n');
}

async function applyPlan(client, plan, issues) {
  for (const label of LABELS) await client.ensureLabel(label);
  for (const item of plan.create) {
    item.issue = await client.createIssue({
      title: item.title,
      body: item.body,
      labels: [TWIN_LABEL, PENDING_LABEL, stageLabel(item.stage)],
    });
    issues.push(item.issue);
  }
  for (const item of plan.reopen) {
    await updateIssueWithLabels(client, item.issue, item.labels, {
      state: 'open', state_reason: 'reopened', title: item.title, body: item.body,
    });
  }
  for (const item of plan.update) {
    await updateIssueWithLabels(client, item.issue, item.labels, { title: item.title, body: item.body });
    if (item.revoked) await client.createComment(item.issue.number, revocationComment(item));
  }
  for (const item of plan.close) {
    await client.createComment(
      item.issue.number,
      `The spec folder \`specs/${item.folder}\` no longer exists on the default branch, so this twin is closed as not planned and marked \`${stageLabel(DISCARDED_STAGE)}\`. It is reopened automatically if the folder returns.`,
    );
    await updateIssueWithLabels(
      client,
      item.issue,
      { add: item.labels.add, remove: [...item.labels.remove, PENDING_LABEL] },
      { state: 'closed', state_reason: 'not_planned' },
    );
  }
  for (const item of plan.relabel) {
    await updateIssueWithLabels(client, item.issue, item.labels);
    if (item.revoked) await client.createComment(item.issue.number, revocationComment(item));
  }
  for (const item of plan.archive) {
    await updateIssueWithLabels(client, item.issue, { add: item.labels.add, remove: [...item.labels.remove, PENDING_LABEL] }, { ...item.fields, title: item.title, body: item.body });
  }
}

function openTwinsWithSpecs(issues, specs) {
  const { byFolder } = resolveTwins(issues);
  return [...byFolder.entries()]
    .filter(([folder, issue]) => issue.state === 'open' && specs.has(folder) && !specs.get(folder).archived)
    .map(([folder, issue]) => ({ folder, issue, pending: hasLabel(issue, PENDING_LABEL) }));
}

export async function runSync({ client, rootDir, env, dryRun, promptFile, log }) {
  const report = createReporter(env, log);
  const specs = discoverSpecs(rootDir);
  const issues = await client.listTwinIssues(TWIN_LABEL);
  for (const issue of issues) {
    if (issue.state === 'open' && isImplementRequested(issue)) {
      issue.implementRequester = await resolveImplementRequester(client, issue.number);
    }
  }
  const plan = planSync({ specs, issues, context: contextFromEnv(env) });
  if (!dryRun) await applyPlan(client, plan, issues);
  reportPlan(report, plan, dryRun);

  // Derive the post-sync state from this run's own results instead of listing issues again,
  // because freshly written issues are not reliably visible to an immediate re-read.
  const twins = openTwinsWithSpecs(issues, specs);
  let pending = twins.filter((twin) => twin.pending).map((twin) => twin.folder);
  if (dryRun) {
    pending = [...pending, ...plan.create.map((item) => item.folder)];
  } else if (pending.length > 0) {
    const entries = twins.map((twin) => ({ ...specs.get(twin.folder).spec, pending: twin.pending }));
    writeFileSync(promptFile, buildPrompt(entries), 'utf8');
  }

  report.line();
  report.line(pending.length > 0
    ? `Dependency inference ${dryRun ? 'would run' : 'runs'} for: ${pending.map((folder) => `\`${folder}\``).join(', ')}`
    : 'No twins need dependency inference.');
  report.flush();
  setOutput(env, 'pending', String(!dryRun && pending.length > 0));
  return { plan, pending, exitCode: plan.unreadable.length > 0 ? 1 : 0 };
}

function linkLine(direction, other, reason) {
  return `- ${direction} #${other.issue.number} (\`${other.folder}\`): ${reason}`;
}

const WRITE_ROLES = new Set(['admin', 'maintain', 'write']);

export function canWrite(permission) {
  return WRITE_ROLES.has(permission?.role_name) || WRITE_ROLES.has(permission?.permission);
}

// Who requested the implementation of a twin. `/speckit implement` records the requester in a workflow comment on the
// twin (renderImplementRequested), the only way to request it; the latest such comment counts. Twins requested before
// that fall back to who most recently added the implement label (the issue's event history rather than the workflow
// trigger, so a flag counts only if its actual author has write access), and a flag the workflow set for a prerequisite
// to the person it names in its own comment on the twin (renderFlaggedFor). `labeledAt` is when the request was made.
export async function resolveImplementRequester(client, issueNumber, fallbackLogin = null) {
  const comments = await client.listIssueComments(issueNumber);
  const recorded = comments
    .filter((comment) => comment.user?.login === BOT_LOGIN)
    .map((comment) => ({ comment, data: parseImplementRequested(comment.body) }))
    .filter((item) => item.data)
    .at(-1);
  if (recorded) {
    const login = recorded.data.by;
    return { login, canWrite: canWrite(await client.getPermission(login)), labeledAt: recorded.comment.created_at ?? null, source: 'record' };
  }
  const implementLabel = stageLabel(IMPLEMENT_STAGE);
  const events = await client.listIssueEvents(issueNumber);
  const last = events.filter((event) => event.event === 'labeled' && event.label?.name === implementLabel).at(-1);
  let login = last?.actor?.login ?? fallbackLogin;
  if (login === BOT_LOGIN) {
    login = comments
      .filter((comment) => comment.user?.login === BOT_LOGIN)
      .map((comment) => String(comment.body ?? '').match(FLAGGED_FOR_PATTERN)?.[1])
      .filter(Boolean)
      .at(-1) ?? login;
  }
  if (!login) return null;
  return { login, canWrite: canWrite(await client.getPermission(login)), labeledAt: last?.created_at ?? null, source: 'label' };
}

// Only at the start of the workflow's own comment, so text it quotes from elsewhere cannot name another requester.
const FLAGGED_FOR_PATTERN = /^<!-- speckit:flagged-for ([A-Za-z0-9-]+) -->/;
// Written by earlier tooling only; read for twins it requested.
export const renderFlaggedFor = (login) => `<!-- speckit:flagged-for ${login} -->`;
// The record of a `/speckit implement` request: who requested it and the comment that did.
const IMPLEMENT_REQUESTED_PATTERN = /^<!-- speckit:implement-requested (\{[^\n]*?\}) -->/;
export const renderImplementRequested = ({ by, comment = null }) => `<!-- speckit:implement-requested ${JSON.stringify({ by, comment })} -->`;
export function parseImplementRequested(body) {
  const match = String(body ?? '').match(IMPLEMENT_REQUESTED_PATTERN);
  if (!match) return null;
  try {
    const data = JSON.parse(match[1]);
    return typeof data?.by === 'string' && /^[A-Za-z0-9-]+(\[bot\])?$/.test(data.by) ? { by: data.by, comment: Number.isInteger(data.comment) ? data.comment : null } : null;
  } catch {
    return null;
  }
}

const GUARD_MARKER = '<!-- speckit:stage-restored -->';

// The stage labels of a twin are generated: from the spec folder on the default branch, and `implement` or
// `implementing` from the implementation request (`/speckit implement`) and the implementation pull request. When a
// person adds or removes one (`action` labeled or unlabeled, `label` its name), the computed set is restored and one
// comment says why and how to change the state instead; for an added `implement` label it recommends
// `/speckit implement`. Changes by the workflow are kept.
export async function runGuardStage({ client, rootDir, env, issueNumber, actor, action, label, log }) {
  const report = createReporter(env, log);
  report.line(`## Stage label change on #${issueNumber}`);
  report.line();
  if (!String(label ?? '').startsWith(STAGE_PREFIX) || actor === BOT_LOGIN || String(actor ?? '').endsWith('[bot]')) {
    report.line(`Kept: \`${label}\` was changed by @${actor}.`);
    report.flush();
    return { exitCode: 0, outcome: 'kept' };
  }
  const issue = await client.getIssue(issueNumber);
  const folder = hasLabel(issue, TWIN_LABEL) ? parseTwinFolder(issue.body) : null;
  if (!folder) {
    report.line('Not a spec twin; nothing to restore.');
    report.flush();
    return { exitCode: 0, outcome: 'ignored' };
  }
  const implementLabel = stageLabel(IMPLEMENT_STAGE);
  const entry = readSpecFolder(rootDir, folder);
  const archived = entry ? null : readSpecFolder(rootDir, folder, { archived: true });
  let stage;
  if (archived || (issue.state === 'closed' && issue.state_reason === 'completed')) stage = 'implemented';
  else if (!entry || (issue.state === 'closed' && issue.state_reason === 'not_planned')) stage = DISCARDED_STAGE;
  else if (issue.state !== 'open') stage = deriveStage(entry.artifacts, entry.tasks);
  else {
    const computed = deriveStage(entry.artifacts, entry.tasks);
    // Whether the twin was requested before the person's change: the change itself is undone.
    const wasRequested = label === implementLabel ? action === 'unlabeled' : hasLabel(issue, implementLabel);
    const openPull = (await client.listPullRequestsForHead(`speckit/${folder}`)).some((pull) => pull.state === 'open');
    const requester = wasRequested || openPull ? await resolveImplementRequester(client, issueNumber) : undefined;
    const valid = requesterBlockers(requester).length === 0
      && implementBlockers({ folder, computedStage: computed, openChecklistItems: entry.openChecklistItems ?? 0 }).length === 0;
    stage = openPull && computed === IMPLEMENTABLE_STAGE && valid ? IMPLEMENTING_STAGE : wasRequested && valid ? IMPLEMENT_STAGE : computed;
  }
  const change = stageLabelChange(issue, stage);
  await updateIssueWithLabels(client, issue, change);
  const addedImplement = action === 'labeled' && label === implementLabel && stage !== IMPLEMENT_STAGE;
  const body = addedImplement
    ? [
      GUARD_MARKER,
      `@${actor}, the \`${implementLabel}\` label was removed: stage labels are generated, and implementation is requested with a comment, so it is checked and records who requested it.`,
      '',
      '**Recommended next step**: Comment this on this issue (people with write access):',
      '',
      '```text',
      '/speckit implement',
      '```',
      '',
      `- **Why**: The command checks that the spec is \`${IMPLEMENTABLE_STAGE}\` with all checklists checked, records you as the requester, and sets \`${implementLabel}\` itself.`,
      '- **What it does**: The scheduler opens the implementation pull request, assigned to you, once its prerequisites are implemented. `/speckit-implement <folder>` locally does the same.',
    ]
    : [
      GUARD_MARKER,
      `@${actor}, the change of \`${label}\` was undone: stage labels are generated from the spec folder on the default branch and the implementation request, so this twin is back at \`${stageLabel(stage)}\`.`,
      '',
      'To change the state, change the spec on the default branch, request implementation with `/speckit implement` on this issue, or close its implementation pull request to stop an implementation.',
    ];
  const last = (await client.listIssueComments(issueNumber)).at(-1);
  const repeated = last?.user?.login === BOT_LOGIN && last.body === body.join('\n');
  if (!repeated && (change.add.length > 0 || change.remove.length > 0 || addedImplement)) await client.createComment(issueNumber, body.join('\n'));
  report.line(`Restored \`${stageLabel(stage)}\` after @${actor} ${action} \`${label}\`.`);
  report.flush();
  return { exitCode: 0, outcome: 'restored', stage };
}

export async function runApply({ client, rootDir, env, outputFile, log }) {
  const report = createReporter(env, log);
  report.line('## Spec twin dependency inference');
  report.line();
  const specs = discoverSpecs(rootDir);
  const twins = openTwinsWithSpecs(await client.listTwinIssues(TWIN_LABEL), specs);
  const byFolder = new Map(twins.map((twin) => [twin.folder, twin]));
  const byId = new Map(twins.map((twin) => [twin.issue.id, twin]));
  const pending = twins.filter((twin) => twin.pending);
  if (pending.length === 0) {
    report.line('No twins are waiting for dependency inference.');
    report.flush();
    return { exitCode: 0 };
  }

  let result;
  try {
    const links = parseInferenceOutput(readFileSync(outputFile, 'utf8'));
    const existingEdges = [];
    for (const twin of twins) {
      for (const blocker of await client.listBlockedBy(twin.issue.number)) {
        // Blockers can live in other repositories, so match on the global issue id, not the number.
        const blockerTwin = byId.get(blocker.id);
        if (blockerTwin) existingEdges.push([twin.folder, blockerTwin.folder]);
      }
    }
    result = validateLinks({
      links,
      openFolders: new Set(byFolder.keys()),
      pendingFolders: new Set(pending.map((twin) => twin.folder)),
      existingEdges,
    });
  } catch (error) {
    report.line(`Inference output rejected: ${error.message}`);
    report.line();
    report.line(`The \`${PENDING_LABEL}\` label stays on the new twins, so the next run retries the inference.`);
    report.flush();
    return { exitCode: 1 };
  }

  const comments = new Map();
  const addComment = (twin, line) => {
    if (!comments.has(twin.folder)) comments.set(twin.folder, []);
    comments.get(twin.folder).push(line);
  };
  for (const link of result.accepted) {
    const blocked = byFolder.get(link.blocked);
    const blocker = byFolder.get(link.blockedBy);
    await client.addBlockedBy(blocked.issue.number, blocker.issue.id);
    addComment(blocked, linkLine('Blocked by', blocker, link.reason));
    addComment(blocker, linkLine('Blocks', blocked, link.reason));
    report.line(`- #${blocked.issue.number} \`${link.blocked}\` is blocked by #${blocker.issue.number} \`${link.blockedBy}\`: ${link.reason}`);
  }
  for (const link of result.skipped) report.line(`- Already present: \`${link.blocked}\` blocked by \`${link.blockedBy}\``);
  if (result.accepted.length === 0) report.line('- No new dependencies inferred.');

  for (const twin of twins) {
    const lines = comments.get(twin.folder);
    if (lines) {
      await client.createComment(twin.issue.number, [
        'Dependency inference by `speckit-prepare` added these relationships:',
        '',
        ...lines,
        '',
        'From now on these dependencies are maintained on GitHub; adjust them on this issue if they are wrong.',
      ].join('\n'));
    } else if (twin.pending) {
      await client.createComment(twin.issue.number, 'Dependency inference by `speckit-prepare` found no new blocking relationships for this feature.');
    }
  }
  for (const twin of pending) await client.removeLabel(twin.issue.number, PENDING_LABEL);
  report.flush();
  return { exitCode: 0 };
}

function parseArgs(argv) {
  const [command, ...rest] = argv;
  const options = { command, dryRun: false };
  for (let index = 0; index < rest.length; index += 1) {
    const arg = rest[index];
    if (arg === '--dry-run') options.dryRun = true;
    else if (arg === '--prompt-file') options.promptFile = rest[++index];
    else if (arg === '--output-file') options.outputFile = rest[++index];
    else if (arg === '--issue') options.issue = Number(rest[++index]);
    else if (arg === '--actor') options.actor = rest[++index];
    else if (arg === '--action') options.action = rest[++index];
    else if (arg === '--label') options.label = rest[++index];
    else throw new Error(`Unknown argument: ${arg}`);
  }
  return options;
}

export async function main(argv, { env = process.env, rootDir = process.cwd(), log = console.log, client } = {}) {
  const options = parseArgs(argv);
  const githubClient = client ?? createGitHubClient(env);
  if (options.command === 'sync') {
    const dryRun = options.dryRun || env.SPECKIT_DRY_RUN === 'true';
    if (!dryRun && !options.promptFile) throw new Error('sync requires --prompt-file unless it is a dry run');
    return (await runSync({ client: githubClient, rootDir, env, dryRun, promptFile: options.promptFile, log })).exitCode;
  }
  if (options.command === 'apply') {
    if (!options.outputFile) throw new Error('apply requires --output-file');
    return (await runApply({ client: githubClient, rootDir, env, outputFile: options.outputFile, log })).exitCode;
  }
  if (options.command === 'guard-stage') {
    if (!Number.isInteger(options.issue) || options.issue <= 0 || !options.actor || !['labeled', 'unlabeled'].includes(options.action) || !options.label) {
      throw new Error('guard-stage requires --issue <number>, --actor <login>, --action <labeled | unlabeled>, and --label <name>');
    }
    return (await runGuardStage({ client: githubClient, rootDir, env, issueNumber: options.issue, actor: options.actor, action: options.action, label: options.label, log })).exitCode;
  }
  throw new Error('Usage: speckit-prepare.mjs <sync [--dry-run] --prompt-file <file> | apply --output-file <file> | guard-stage --issue <number> --actor <login> --action <labeled | unlabeled> --label <name>>');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = 1;
    },
  );
}
