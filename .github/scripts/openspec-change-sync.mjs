import { execFileSync } from 'node:child_process';
import {
  existsSync,
  readdirSync,
  statSync,
} from 'node:fs';
import { basename, join, relative, sep } from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  parseChangeMarker,
  renderChangeMarker,
} from './openspec-change-core.mjs';
import { GitHubChangeClient } from './openspec-change-github.mjs';

export const TWIN_SECTION_START = '<!-- openspec-twin:v1:start -->';
export const TWIN_SECTION_END = '<!-- openspec-twin:v1:end -->';

function toRepositoryPath(root, path) {
  return relative(root, path).split(sep).join('/');
}

function existingArtifacts(root, changePath) {
  const candidates = [
    ['Proposal', `${changePath}/proposal.md`],
    ['Design', `${changePath}/design.md`],
    ['Tasks', `${changePath}/tasks.md`],
    ['Specifications', `${changePath}/specs`],
  ];
  return candidates.filter(([, path]) => existsSync(join(root, path)));
}

export function inventoryRepositoryChanges({
  root,
  activeChanges,
}) {
  const inventory = [];
  const refs = new Set();

  for (const change of activeChanges) {
    if (refs.has(change.name)) throw new Error(`Duplicate active change ref: ${change.name}`);
    refs.add(change.name);
    const path = `openspec/changes/${change.name}`;
    inventory.push({
      ref: change.name,
      lifecycle: 'active',
      path,
      artifacts: existingArtifacts(root, path),
    });
  }

  const archiveRoot = join(root, 'openspec', 'changes', 'archive');
  if (existsSync(archiveRoot)) {
    for (const entry of readdirSync(archiveRoot).sort()) {
      const fullPath = join(archiveRoot, entry);
      if (!statSync(fullPath).isDirectory()) continue;
      const match = entry.match(/^(\d{4}-\d{2}-\d{2})-(.+)$/);
      if (!match) continue;
      const ref = match[2];
      if (refs.has(ref)) {
        throw new Error(`Change ref exists in both active and archived locations: ${ref}`);
      }
      refs.add(ref);
      const path = toRepositoryPath(root, fullPath);
      inventory.push({
        ref,
        lifecycle: 'archived',
        path,
        artifacts: existingArtifacts(root, path),
      });
    }
  }

  return inventory.sort((left, right) => left.ref.localeCompare(right.ref));
}

function githubTreeUrl(repository, gitRef, path) {
  return `https://github.com/${repository}/tree/${encodeURIComponent(gitRef)}/${path}`;
}

function githubBlobUrl(repository, gitRef, path) {
  return `https://github.com/${repository}/blob/${encodeURIComponent(gitRef)}/${path}`;
}

export function renderTwinSection(change, repository, gitRef) {
  const directoryUrl = githubTreeUrl(repository, gitRef, change.path);
  const artifactLines = change.artifacts.map(([label, artifactPath]) => {
    const url = artifactPath.endsWith('/specs')
      ? githubTreeUrl(repository, gitRef, artifactPath)
      : githubBlobUrl(repository, gitRef, artifactPath);
    return `- [${label}](${url})`;
  });
  const marker = renderChangeMarker({
    repository,
    ref: change.ref,
    lifecycle: change.lifecycle,
    gitRef,
    path: change.path,
  });

  return [
    TWIN_SECTION_START,
    `## OpenSpec change: \`${change.ref}\``,
    '',
    `**Lifecycle:** ${change.lifecycle}`,
    '',
    `**Authoritative directory:** [${change.path}](${directoryUrl})`,
    '',
    ...(artifactLines.length > 0 ? ['### Artifacts', '', ...artifactLines, ''] : []),
    marker,
    TWIN_SECTION_END,
  ].join('\n');
}

export function updateManagedTwinBody(existingBody, managedSection) {
  const body = existingBody ?? '';
  const starts = body.split(TWIN_SECTION_START).length - 1;
  const ends = body.split(TWIN_SECTION_END).length - 1;
  if (starts !== ends || starts > 1) {
    throw new Error('Issue body contains malformed or duplicate managed twin sections');
  }
  if (starts === 0) {
    return body.trim() ? `${body.trimEnd()}\n\n${managedSection}\n` : `${managedSection}\n`;
  }

  const start = body.indexOf(TWIN_SECTION_START);
  const endMarker = body.indexOf(TWIN_SECTION_END, start);
  if (endMarker <= start) {
    throw new Error('Issue body contains a reversed managed twin section');
  }
  const end = endMarker + TWIN_SECTION_END.length;
  return `${body.slice(0, start)}${managedSection}${body.slice(end)}`;
}

export async function synchronizeTwins({
  client,
  inventory,
  repository,
  gitRef = 'main',
  dryRun = false,
}) {
  if (!dryRun) {
    await client.ensureLabel({
      name: 'openspec:change',
      color: '1d76db',
      description: 'Non-authoritative GitHub projection of an OpenSpec change',
    });
    await client.ensureLabel({
      name: 'openspec:enqueued',
      color: '0e8a16',
      description: 'Authorizes processing when native dependencies are complete',
    });
  }
  const issues = await client.listIssueTwins();
  const byRef = new Map();

  for (const issue of issues) {
    const marker = parseChangeMarker(issue.body ?? '');
    if (marker.repository !== repository) {
      throw new Error(`Issue #${issue.number} marker targets another repository`);
    }
    if (byRef.has(marker.ref)) {
      throw new Error(`Duplicate issue twins for change ref: ${marker.ref}`);
    }
    byRef.set(marker.ref, issue);
  }

  const actions = [];
  for (const change of inventory) {
    const title = `OpenSpec change: ${change.ref}`;
    const section = renderTwinSection(change, repository, gitRef);
    const existing = byRef.get(change.ref);
    if (!existing) {
      if (change.lifecycle === 'archived') {
        actions.push({ action: 'ignored-archive', ref: change.ref });
        continue;
      }
      actions.push({ action: 'create', ref: change.ref });
      if (!dryRun) {
        await client.createIssue({
          title,
          body: `${section}\n`,
          labels: ['openspec:change'],
        });
      }
      continue;
    }

    const body = updateManagedTwinBody(existing.body, section);
    const patch = { title, body };
    if (change.lifecycle === 'archived' && existing.state !== 'closed') {
      patch.state = 'closed';
      patch.state_reason = 'completed';
    }
    if (existing.title !== title
      || existing.body !== body
      || patch.state !== undefined) {
      actions.push({ action: 'update', ref: change.ref, issueNumber: existing.number });
      if (!dryRun) {
        await client.updateIssue(existing.number, patch);
      }
    } else {
      actions.push({ action: 'unchanged', ref: change.ref, issueNumber: existing.number });
    }
  }
  return actions;
}

function readActiveChanges() {
  const output = process.platform === 'win32'
    ? execFileSync(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', 'openspec list --json'], {
      encoding: 'utf8',
    })
    : execFileSync('openspec', ['list', '--json'], { encoding: 'utf8' });
  const parsed = JSON.parse(output);
  if (!Array.isArray(parsed.changes) || !parsed.root?.path) {
    throw new Error('openspec list --json returned an unexpected shape');
  }
  return parsed;
}

async function main() {
  const repository = process.env.GITHUB_REPOSITORY;
  const token = process.env.GITHUB_TOKEN;
  if (!repository || !token) {
    throw new Error('GITHUB_REPOSITORY and GITHUB_TOKEN are required');
  }
  const [owner, repo] = repository.split('/');
  const listed = readActiveChanges();
  const inventory = inventoryRepositoryChanges({
    root: listed.root.path,
    activeChanges: listed.changes,
  });
  const client = new GitHubChangeClient({
    owner,
    repo,
    repositoryToken: token,
  });
  const actions = await synchronizeTwins({
    client,
    inventory,
    repository,
    gitRef: 'main',
    dryRun: process.argv.includes('--dry-run'),
  });
  process.stdout.write(`${JSON.stringify({ inventory, actions }, null, 2)}\n`);
}

const isEntrypoint = process.argv[1]
  && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isEntrypoint) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
