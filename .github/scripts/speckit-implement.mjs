import { spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  IMPLEMENT_STAGE,
  TWIN_LABEL,
  buildSpecEntry,
  deriveStage,
  hasLabel,
  implementBlockers,
  isValidFolderName,
  requesterBlockers,
  resolveTwins,
  stageLabel,
  stageLabelChange,
} from './speckit-prepare-core.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { discoverSpecs, resolveImplementRequester, updateIssueWithLabels } from './speckit-prepare.mjs';

export class UsageError extends Error {}

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

function openBlockers(blockers) {
  return blockers.filter((blocker) => blocker.state === 'open');
}

function blockerList(blockers) {
  return blockers.map((blocker) => `#${blocker.number}`).join(', ');
}

// Server side: lists flagged twins that are consistent with the default branch and have no open blockers.
export async function runSelect({ client, rootDir, env, log }) {
  const report = createReporter(env, log);
  const specs = discoverSpecs(rootDir);
  const { byFolder } = resolveTwins(await client.listTwinIssues(TWIN_LABEL));
  const ready = [];
  const blocked = [];
  const inconsistent = [];
  for (const [folder, issue] of [...byFolder.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    if (issue.state !== 'open' || !hasLabel(issue, stageLabel(IMPLEMENT_STAGE))) continue;
    const entry = specs.get(folder);
    const reasons = entry
      ? [
        ...requesterBlockers(await resolveImplementRequester(client, issue.number)),
        ...implementBlockers({ folder, computedStage: deriveStage(entry.artifacts, entry.tasks), openChecklistItems: entry.openChecklistItems }),
      ]
      : [`\`specs/${folder}\` does not exist on the default branch`];
    if (reasons.length > 0) {
      inconsistent.push({ number: issue.number, folder, reasons });
      continue;
    }
    const open = openBlockers(await client.listBlockedBy(issue.number));
    if (open.length > 0) blocked.push({ number: issue.number, folder, blockers: open });
    else ready.push({ number: issue.number, folder });
  }

  report.line('## Spec implementation selection');
  report.line();
  for (const twin of ready) report.line(`- Ready: #${twin.number} \`${twin.folder}\``);
  for (const twin of blocked) report.line(`- Blocked: #${twin.number} \`${twin.folder}\` by ${blockerList(twin.blockers)}`);
  for (const twin of inconsistent) {
    report.line(`- Inconsistent (the next sync revokes the flag): #${twin.number} \`${twin.folder}\`: ${twin.reasons.join('; ')}`);
  }
  if (ready.length + blocked.length + inconsistent.length === 0) report.line('- No spec twin is flagged for implementation.');
  report.flush();
  setOutput(env, 'count', String(ready.length));
  setOutput(env, 'matrix', JSON.stringify({ include: ready }));
  return { exitCode: 0, ready, blocked, inconsistent };
}

export function defaultGit(cwd) {
  return (args) => {
    const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true, maxBuffer: 20 * 1024 * 1024 });
    if (result.error) throw new UsageError(`git is required: ${result.error.message}`);
    return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  };
}

export function resolveFolder({ folder, env, rootDir }) {
  let candidate = folder || env.SPECIFY_FEATURE_DIRECTORY;
  if (!candidate) {
    const featureFile = path.join(rootDir, '.specify', 'feature.json');
    if (existsSync(featureFile)) {
      try {
        candidate = JSON.parse(readFileSync(featureFile, 'utf8')).feature_directory;
      } catch {
        throw new UsageError('.specify/feature.json is not valid JSON.');
      }
    }
  }
  if (!candidate) throw new UsageError('No feature folder given. Pass --folder <folder> or select a feature with /speckit-specify.');
  const name = path.basename(String(candidate).replaceAll('\\', '/').replace(/\/+$/, ''));
  if (!isValidFolderName(name)) throw new UsageError(`"${candidate}" is not a valid spec folder name.`);
  return name;
}

// Reads one spec folder from the remote default branch, or returns null when it is not merged there.
export function readSpecFromRef(git, ref, folder) {
  const show = (file) => {
    const result = git(['show', `${ref}:specs/${folder}/${file}`]);
    return result.status === 0 ? result.stdout : null;
  };
  const specMarkdown = show('spec.md');
  if (specMarkdown === null) return null;
  const listing = git(['ls-tree', '-z', '--full-tree', '--name-only', ref, '--', `specs/${folder}/checklists/`]);
  const checklistFiles = listing.status === 0
    ? listing.stdout.split('\0').filter((file) => file.endsWith('.md'))
    : [];
  return buildSpecEntry(folder, {
    specMarkdown,
    hasPlan: show('plan.md') !== null,
    tasksMarkdown: show('tasks.md'),
    checklistMarkdowns: checklistFiles.map((file) => {
      const result = git(['show', `${ref}:${file}`]);
      return result.status === 0 ? result.stdout : '';
    }),
  });
}

// Client side: checks a spec against the remote default branch and flags its twin for implementation.
export async function runRequest({ client, git, rootDir, env, folder, log }) {
  const name = resolveFolder({ folder, env, rootDir });
  const branch = env.SPECKIT_BRANCH || 'main';
  const fetch = git(['fetch', '--quiet', 'origin', branch]);
  if (fetch.status !== 0) throw new UsageError(`git fetch origin ${branch} failed: ${fetch.stderr.trim()}`);
  const ref = `origin/${branch}`;
  log(`Spec folder: specs/${name} (checked against ${ref})`);

  const entry = readSpecFromRef(git, ref, name);
  if (!entry) {
    log(`Not requested: specs/${name} is not merged to ${branch} yet. Merge the spec, plan, and tasks first.`);
    return { exitCode: 1 };
  }
  const computedStage = deriveStage(entry.artifacts, entry.tasks);
  const twin = resolveTwins(await client.listTwinIssues(TWIN_LABEL)).byFolder.get(name);
  if (!twin || twin.state !== 'open') {
    log(`Not requested: no open spec twin exists for specs/${name} yet. The Spec Kit prepare workflow creates it after the merge.`);
    return { exitCode: 1 };
  }
  const url = twin.html_url ?? `#${twin.number}`;
  const implementLabel = stageLabel(IMPLEMENT_STAGE);
  const reportBlockers = async () => {
    const open = openBlockers(await client.listBlockedBy(twin.number));
    log(open.length > 0
      ? `Open blockers: ${blockerList(open)}. The speckit-implement workflow starts after they are closed.`
      : 'No open blockers: the speckit-implement workflow can pick it up.');
  };

  if (hasLabel(twin, implementLabel)) {
    log(`Already requested: ${url} carries ${implementLabel}.`);
    await reportBlockers();
    return { exitCode: 0 };
  }
  const reasons = implementBlockers({ folder: name, computedStage, openChecklistItems: entry.openChecklistItems });
  if (reasons.length > 0) {
    log(`Not requested for ${url}:`);
    for (const reason of reasons) log(`- ${reason}`);
    return { exitCode: 1 };
  }
  await updateIssueWithLabels(client, twin, stageLabelChange(twin, IMPLEMENT_STAGE));
  log(`Requested: ${url} is labelled ${implementLabel}. The Spec Kit prepare workflow validates the request on GitHub.`);
  await reportBlockers();
  return { exitCode: 0 };
}

function parseArgs(argv) {
  const [command, ...rest] = argv;
  const options = { command };
  for (let index = 0; index < rest.length; index += 1) {
    const arg = rest[index];
    if (arg === '--folder') options.folder = rest[++index];
    else throw new UsageError(`Unknown argument: ${arg}`);
  }
  return options;
}

function repositoryFromGit(git) {
  const result = git(['remote', 'get-url', 'origin']);
  const match = result.stdout.trim().match(/github\.com[:/]([^/]+\/[^/]+?)(?:\.git)?$/);
  if (!match) throw new UsageError('Could not determine the GitHub repository from the origin remote; set GITHUB_REPOSITORY.');
  return match[1];
}

function tokenFromGh() {
  const result = spawnSync('gh', ['auth', 'token'], { encoding: 'utf8', windowsHide: true });
  if (result.error || result.status !== 0 || !result.stdout.trim()) {
    throw new UsageError('No GitHub token available. Run `gh auth login` or set GH_TOKEN.');
  }
  return result.stdout.trim();
}

export async function main(argv, { env = process.env, rootDir = process.cwd(), log = console.log, client, git } = {}) {
  const options = parseArgs(argv);
  if (options.command === 'select') {
    const githubClient = client ?? new GitHubClient({
      token: env.GITHUB_TOKEN || env.GH_TOKEN,
      repository: env.GITHUB_REPOSITORY,
      apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
      graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
    });
    return (await runSelect({ client: githubClient, rootDir, env, log })).exitCode;
  }
  if (options.command === 'request') {
    const gitRunner = git ?? defaultGit(rootDir);
    const githubClient = client ?? new GitHubClient({
      token: env.GH_TOKEN || env.GITHUB_TOKEN || tokenFromGh(),
      repository: env.GITHUB_REPOSITORY || repositoryFromGit(gitRunner),
      apiUrl: env.GITHUB_API_URL || 'https://api.github.com',
      graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
    });
    return (await runRequest({ client: githubClient, git: gitRunner, rootDir, env, folder: options.folder, log })).exitCode;
  }
  throw new UsageError('Usage: speckit-implement.mjs <select | request [--folder <folder>]>');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = error instanceof UsageError ? 2 : 1;
    },
  );
}
