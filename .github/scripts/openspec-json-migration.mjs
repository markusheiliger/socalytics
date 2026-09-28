import { pathToFileURL } from 'node:url';

import {
  JSON_CONTRACTS,
  JSON_MARKER_START,
  parseQueueState,
  QUEUE_CHECKPOINT_TRAILER,
} from './openspec-change-core.mjs';
import { GitHubChangeClient } from './openspec-change-github.mjs';
import {
  createGitRunner,
  DEPENDENCY_NOTES_REF,
  fetchDependencyNotes,
  getDependencyNotesTip,
  pushDependencyNotes,
} from './openspec-change-git-notes.mjs';

const LEGACY_MARKERS = Object.freeze({
  changeMarker: '<!-- openspec-change:v1',
  queueState: '<!-- openspec-queue-state:v1',
  dependencySummary: '<!-- openspec-dependencies:v1',
});
const LEGACY_CHECKPOINT_TRAILER = 'OpenSpec-Queue-Checkpoint:';
const ACTIVE_TASK_STATES = new Set(['queued', 'in_progress', 'idle', 'waiting_for_user']);

function legacyObject(body, marker, label) {
  if (typeof body !== 'string') throw new Error(`${label} body must be a string`);
  const count = body.split(marker).length - 1;
  if (count !== 1) throw new Error(`${label} must contain exactly one ${marker} marker`);
  const start = body.indexOf(marker) + marker.length;
  const end = body.indexOf('-->', start);
  if (end < start) throw new Error(`${label} marker is not closed`);
  const value = JSON.parse(body.slice(start, end).trim());
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} legacy payload must be an object`);
  }
  return value;
}

function schemaObject(value, schema) {
  const { version: _version, schema: _schema, ...content } = value;
  return { $schema: schema, ...content };
}

function replaceLegacyMarker(body, marker, schema, label) {
  const value = legacyObject(body, marker, label);
  const start = body.indexOf(marker);
  const end = body.indexOf('-->', start) + '-->'.length;
  const replacement = `${JSON_MARKER_START}\n${JSON.stringify(schemaObject(value, schema))}\n-->`;
  return { body: `${body.slice(0, start)}${replacement}${body.slice(end)}`, value };
}

export function migrateLegacyChangeMarker(body) {
  return replaceLegacyMarker(
    body,
    LEGACY_MARKERS.changeMarker,
    JSON_CONTRACTS.changeMarker,
    'Issue change marker',
  ).body;
}

export function migrateLegacyQueueState(body) {
  return replaceLegacyMarker(
    body,
    LEGACY_MARKERS.queueState,
    JSON_CONTRACTS.queueState,
    'Queue state',
  );
}

export function migrateLegacyDependencySummary(body) {
  return replaceLegacyMarker(
    body,
    LEGACY_MARKERS.dependencySummary,
    JSON_CONTRACTS.dependencySummary,
    'Dependency summary',
  ).body;
}

export function migrateLegacyCheckpointMessage(message) {
  if (typeof message !== 'string') throw new Error('Commit message must be a string');
  const lines = message.split(/\r?\n/);
  const indexes = lines
    .map((line, index) => line.startsWith(LEGACY_CHECKPOINT_TRAILER) ? index : -1)
    .filter((index) => index >= 0);
  if (indexes.length !== 1) {
    throw new Error(`Commit message must contain exactly one ${LEGACY_CHECKPOINT_TRAILER}`);
  }
  const index = indexes[0];
  const legacy = JSON.parse(lines[index].slice(LEGACY_CHECKPOINT_TRAILER.length).trim());
  lines[index] = `${QUEUE_CHECKPOINT_TRAILER} ${JSON.stringify(schemaObject(
    legacy,
    JSON_CONTRACTS.queueCheckpoint,
  ))}`;
  return lines.join('\n');
}

function latestLegacyComment(comments, marker) {
  return comments
    .filter((comment) => comment.user?.login === 'github-actions[bot]')
    .filter((comment) => comment.body?.includes(marker))
    .sort((left, right) => Date.parse(right.updated_at) - Date.parse(left.updated_at))[0] ?? null;
}

function latestQueueRecord(comments) {
  const candidates = comments
    .filter((comment) => comment.user?.login === 'github-actions[bot]')
    .sort((left, right) => Date.parse(right.updated_at) - Date.parse(left.updated_at));
  for (const comment of candidates) {
    if (comment.body?.includes(LEGACY_MARKERS.queueState)) {
      const migrated = migrateLegacyQueueState(comment.body);
      return { comment, body: migrated.body, state: migrated.value };
    }
    try {
      return { comment, body: null, state: parseQueueState(comment.body) };
    } catch {
      // Other comments do not carry current queue state.
    }
  }
  return null;
}

async function migrationPlan(client) {
  const issues = await client.listIssueTwins();
  const plans = [];
  for (const issue of issues) {
    const comments = await client.listIssueComments(issue.number);
    const queue = latestQueueRecord(comments);
    const dependencyComment = latestLegacyComment(comments, LEGACY_MARKERS.dependencySummary);
    if (queue?.state.taskId) {
      const task = await client.getAgentTask(queue.state.taskId);
      if (ACTIVE_TASK_STATES.has(task.state)) {
        throw new Error(
          `Issue #${issue.number} Agent Task ${queue.state.taskId} is still ${task.state}`,
        );
      }
    }
    plans.push({
      issue,
      issueBody: issue.body?.includes(LEGACY_MARKERS.changeMarker)
        ? migrateLegacyChangeMarker(issue.body)
        : null,
      queueComment: queue?.comment ?? null,
      queueBody: queue?.body ?? null,
      dependencyComment,
      dependencyBody: dependencyComment
        ? migrateLegacyDependencySummary(dependencyComment.body)
        : null,
      queueState: queue?.state ?? null,
    });
  }
  return plans;
}

async function appendCheckpointMigration(client, state, dryRun) {
  if (!state?.headRef) return null;
  const branch = await client.getBranch(state.headRef);
  const commit = await client.getCommit(branch.commit.sha);
  const message = commit.commit?.message ?? '';
  if (!message.includes(LEGACY_CHECKPOINT_TRAILER)) return null;
  const migratedMessage = migrateLegacyCheckpointMessage(message);
  if (dryRun) return { branch: state.headRef, previousSha: branch.commit.sha, nextSha: null };
  const created = await client.createGitCommit({
    message: migratedMessage,
    tree: commit.commit.tree.sha,
    parents: [branch.commit.sha],
  });
  await client.updateGitRef(state.headRef, created.sha);
  return { branch: state.headRef, previousSha: branch.commit.sha, nextSha: created.sha };
}

export async function migrateGitHubJsonContracts({ client, dryRun = true }) {
  const plans = await migrationPlan(client);
  const result = {
    dryRun,
    issues: [],
    comments: [],
    checkpoints: [],
  };
  for (const plan of plans) {
    if (plan.issueBody) {
      result.issues.push(plan.issue.number);
      if (!dryRun) await client.updateIssue(plan.issue.number, { body: plan.issueBody });
    }
    for (const [comment, body] of [
      [plan.queueComment, plan.queueBody],
      [plan.dependencyComment, plan.dependencyBody],
    ]) {
      if (!comment || !body) continue;
      result.comments.push(comment.id);
      if (!dryRun) await client.updateIssueComment(comment.id, body);
    }
    const checkpoint = await appendCheckpointMigration(client, plan.queueState, dryRun);
    if (checkpoint) result.checkpoints.push(checkpoint);
  }
  return result;
}

export async function migrateDependencyNote({
  git = createGitRunner(),
  remote = 'origin',
  dryRun = true,
} = {}) {
  const fetched = await fetchDependencyNotes({ git, remote });
  if (!fetched) return null;
  const expectedRemoteTip = await getDependencyNotesTip({ git });
  const commits = (await git('rev-list', '--topo-order', 'HEAD'))
    .split(/\r?\n/)
    .filter(Boolean);
  for (const commit of commits) {
    let note;
    try {
      note = await git('notes', `--ref=${DEPENDENCY_NOTES_REF}`, 'show', commit);
    } catch (error) {
      const detail = `${error?.stderr ?? ''}\n${error?.message ?? ''}`;
      if (/no note found|cannot read note data/i.test(detail)) continue;
      throw error;
    }
    const value = JSON.parse(note);
    if (value.$schema === JSON_CONTRACTS.dependencyCheckpoint) return null;
    const migrated = JSON.stringify(schemaObject(
      value,
      JSON_CONTRACTS.dependencyCheckpoint,
    ));
    if (!dryRun) {
      await git(
        'notes',
        `--ref=${DEPENDENCY_NOTES_REF}`,
        'add',
        '-f',
        '-m',
        migrated,
        commit,
      );
      await pushDependencyNotes({ git, remote, expectedRemoteTip });
    }
    return { commit, dryRun };
  }
  return null;
}

async function main() {
  const repository = process.env.GITHUB_REPOSITORY;
  const repositoryToken = process.env.GITHUB_TOKEN;
  const agentToken = process.env.COPILOT_AGENT_TOKEN;
  if (!repository || !repositoryToken || !agentToken) {
    throw new Error('GITHUB_REPOSITORY, GITHUB_TOKEN, and COPILOT_AGENT_TOKEN are required');
  }
  const [owner, repo] = repository.split('/');
  const dryRun = !process.argv.includes('--apply');
  const github = await migrateGitHubJsonContracts({
    client: new GitHubChangeClient({
      owner,
      repo,
      repositoryToken,
      agentToken,
    }),
    dryRun,
  });
  const dependencyNote = await migrateDependencyNote({ dryRun });
  process.stdout.write(`${JSON.stringify({ github, dependencyNote }, null, 2)}\n`);
}

const isEntrypoint = process.argv[1]
  && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isEntrypoint) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
