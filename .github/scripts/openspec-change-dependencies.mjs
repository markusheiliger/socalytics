import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import {
  DEPENDENCY_SUMMARY_START,
  calculateManagedEdgeChanges,
  parseChangeMarker,
  parseDependencySummary,
  renderDependencySummary,
  validateDependencyOutput,
} from './openspec-change-core.mjs';
import { GitHubChangeClient } from './openspec-change-github.mjs';

function latestDependencySummary(comments, ref) {
  const candidates = comments
    .filter((comment) => comment.user?.login === 'github-actions[bot]')
    .filter((comment) => comment.body?.includes(DEPENDENCY_SUMMARY_START))
    .sort((left, right) => Date.parse(right.updated_at) - Date.parse(left.updated_at));
  if (candidates.length === 0) {
    return {
      comment: null,
      summary: { version: 1, managedEdges: [] },
    };
  }

  const summary = parseDependencySummary(candidates[0].body);
  if (summary.managedEdges.some((edge) => edge.changeRef !== ref)) {
    throw new Error(`Dependency summary for ${ref} contains another change ref`);
  }
  return { comment: candidates[0], summary };
}

export function extractDependencySafeOutput(agentOutput) {
  if (!agentOutput || typeof agentOutput !== 'object' || Array.isArray(agentOutput)) {
    throw new Error('Agentic safe output must be an object');
  }
  if (!Array.isArray(agentOutput.items)) {
    throw new Error('Agentic safe output items must be an array');
  }
  const items = agentOutput.items.filter(
    (item) => item?.type === 'reconcile_openspec_dependencies',
  );
  if (items.length !== 1) {
    throw new Error('Exactly one reconcile-openspec-dependencies output is required');
  }
  const item = items[0];
  const keys = Object.keys(item).sort();
  if (keys.join(',') !== 'payload,type') {
    throw new Error('Dependency safe-output item has unknown fields');
  }
  if (typeof item.payload !== 'string' || item.payload.trim() === '') {
    throw new Error('Dependency safe-output payload must be a non-empty JSON string');
  }
  try {
    return JSON.parse(item.payload);
  } catch {
    throw new Error('Dependency safe-output payload must contain valid JSON');
  }
}

export async function reconcileDependencies({
  client,
  output,
  minimumConfidence = 0.85,
}) {
  const issues = await client.listIssueTwins();
  const activeByRef = new Map();
  const refs = new Set();
  for (const issue of issues) {
    const marker = parseChangeMarker(issue.body ?? '');
    if (marker.repository !== `${client.owner}/${client.repo}`) {
      throw new Error(`Issue #${issue.number} marker targets another repository`);
    }
    if (refs.has(marker.ref)) {
      throw new Error(`Duplicate issue twins for ${marker.ref}`);
    }
    refs.add(marker.ref);
    if (marker.lifecycle === 'active') activeByRef.set(marker.ref, { issue, marker });
  }

  const activeRefs = [...activeByRef.keys()].sort();
  const commentsByRef = new Map();
  const previousManaged = [];
  const nativeEdges = [];
  const nativeByRef = new Map();
  for (const ref of activeRefs) {
    const { issue } = activeByRef.get(ref);
    const [comments, blockers] = await Promise.all([
      client.listIssueComments(issue.number),
      client.listBlockedBy(issue.number),
    ]);
    const summary = latestDependencySummary(comments, ref);
    commentsByRef.set(ref, summary);
    previousManaged.push(...summary.summary.managedEdges);

    const edges = [];
    for (const blocker of blockers) {
      let blockerRef = null;
      try {
        blockerRef = parseChangeMarker(blocker.body ?? '').ref;
      } catch {
        // Manual non-OpenSpec dependencies are preserved but excluded from AI graph validation.
      }
      if (blockerRef) {
        const edge = { changeRef: ref, dependsOn: blockerRef };
        nativeEdges.push(edge);
        edges.push({ ...edge, issueId: blocker.id });
      }
    }
    nativeByRef.set(ref, edges);
  }

  const validated = validateDependencyOutput(
    output,
    activeRefs,
    nativeEdges,
    minimumConfidence,
  );
  const changes = calculateManagedEdgeChanges(
    previousManaged,
    validated.accepted,
    nativeEdges,
  );

  for (const edge of changes.remove) {
    const managed = nativeByRef.get(edge.changeRef)
      ?.find((native) => native.dependsOn === edge.dependsOn);
    if (!managed) continue;
    await client.removeBlockedBy(
      activeByRef.get(edge.changeRef).issue.number,
      managed.issueId,
    );
  }
  for (const edge of changes.add) {
    await client.addBlockedBy(
      activeByRef.get(edge.changeRef).issue.number,
      activeByRef.get(edge.dependsOn).issue.id,
    );
  }

  for (const ref of activeRefs) {
    const managedEdges = validated.accepted.filter((edge) => edge.changeRef === ref);
    const body = renderDependencySummary({ version: 1, managedEdges });
    const current = commentsByRef.get(ref);
    if (current.comment) {
      if (current.comment.body !== body) {
        await client.updateIssueComment(current.comment.id, body);
      }
    } else if (managedEdges.length > 0) {
      await client.createIssueComment(activeByRef.get(ref).issue.number, body);
    }
  }

  return {
    accepted: validated.accepted,
    review: validated.review,
    changes,
  };
}

async function main() {
  const inputPath = process.argv[2];
  if (!inputPath) throw new Error('Safe-output JSON path is required');
  const repository = process.env.GITHUB_REPOSITORY;
  const token = process.env.GITHUB_TOKEN;
  if (!repository || !token) {
    throw new Error('GITHUB_REPOSITORY and GITHUB_TOKEN are required');
  }
  const [owner, repo] = repository.split('/');
  const client = new GitHubChangeClient({
    owner,
    repo,
    repositoryToken: token,
  });
  const output = extractDependencySafeOutput(
    JSON.parse(readFileSync(inputPath, 'utf8')),
  );
  const result = await reconcileDependencies({ client, output });
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

const isEntrypoint = process.argv[1]
  && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isEntrypoint) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
