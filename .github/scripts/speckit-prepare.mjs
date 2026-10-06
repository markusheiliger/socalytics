import { appendFileSync, existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  ARTIFACTS,
  DISCARDED_STAGE,
  LABELS,
  PENDING_LABEL,
  TWIN_LABEL,
  buildPrompt,
  hasLabel,
  isValidFolderName,
  parseInferenceOutput,
  parseSpec,
  planSync,
  resolveTwins,
  stageLabel,
  validateLinks,
} from './speckit-prepare-core.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';

export function discoverSpecs(rootDir) {
  const specsDir = path.join(rootDir, 'specs');
  const specs = new Map();
  if (!existsSync(specsDir)) return specs;
  for (const name of readdirSync(specsDir).sort()) {
    const folderPath = path.join(specsDir, name);
    if (!statSync(folderPath).isDirectory() || !isValidFolderName(name)) continue;
    const specPath = path.join(folderPath, 'spec.md');
    if (!existsSync(specPath)) continue;
    const artifacts = ARTIFACTS.filter((artifact) => existsSync(path.join(folderPath, artifact)));
    const tasks = artifacts.includes('tasks.md') ? readFileSync(path.join(folderPath, 'tasks.md'), 'utf8') : null;
    specs.set(name, { spec: parseSpec(name, readFileSync(specPath, 'utf8')), artifacts, tasks });
  }
  return specs;
}

function createReporter(env, log) {
  const lines = [];
  return {
    line(text = '') {
      lines.push(text);
      log(text);
    },
    flush() {
      if (env.GITHUB_STEP_SUMMARY) appendFileSync(env.GITHUB_STEP_SUMMARY, `${lines.join('\n')}\n`);
    },
  };
}

function setOutput(env, name, value) {
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `${name}=${value}\n`);
}

function issueRef(issue) {
  return `#${issue.number}`;
}

function contextFromEnv(env) {
  return {
    serverUrl: (env.GITHUB_SERVER_URL || 'https://github.com').replace(/\/$/, ''),
    repository: env.GITHUB_REPOSITORY,
    branch: env.SPECKIT_BRANCH || 'main',
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
  ];
  for (const row of rows.length > 0 ? rows : ['- No twin changes needed']) report.line(row);
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
async function updateIssueWithLabels(client, issue, { add, remove }, fields = {}) {
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
  for (const item of plan.relabel) await updateIssueWithLabels(client, item.issue, item.labels);
}

function openTwinsWithSpecs(issues, specs) {
  const { byFolder } = resolveTwins(issues);
  return [...byFolder.entries()]
    .filter(([folder, issue]) => issue.state === 'open' && specs.has(folder))
    .map(([folder, issue]) => ({ folder, issue, pending: hasLabel(issue, PENDING_LABEL) }));
}

export async function runSync({ client, rootDir, env, dryRun, promptFile, log }) {
  const report = createReporter(env, log);
  const specs = discoverSpecs(rootDir);
  const issues = await client.listTwinIssues(TWIN_LABEL);
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
    else throw new Error(`Unknown argument: ${arg}`);
  }
  return options;
}

export async function main(argv, { env = process.env, rootDir = process.cwd(), log = console.log, client } = {}) {
  const options = parseArgs(argv);
  const githubClient = client ?? new GitHubClient({
    token: env.GITHUB_TOKEN || env.GH_TOKEN,
    repository: env.GITHUB_REPOSITORY,
    apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
    graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
  });
  if (options.command === 'sync') {
    const dryRun = options.dryRun || env.SPECKIT_DRY_RUN === 'true';
    if (!dryRun && !options.promptFile) throw new Error('sync requires --prompt-file unless it is a dry run');
    return (await runSync({ client: githubClient, rootDir, env, dryRun, promptFile: options.promptFile, log })).exitCode;
  }
  if (options.command === 'apply') {
    if (!options.outputFile) throw new Error('apply requires --output-file');
    return (await runApply({ client: githubClient, rootDir, env, outputFile: options.outputFile, log })).exitCode;
  }
  throw new Error('Usage: speckit-prepare.mjs <sync [--dry-run] --prompt-file <file> | apply --output-file <file>>');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
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
