import { createHash } from 'node:crypto';
import {
  appendFile,
  mkdir,
  readFile,
  readdir,
  stat,
  writeFile,
} from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  extractDependencySafeOutput,
  reconcileDependencies,
} from './openspec-change-dependencies.mjs';
import { GitHubChangeClient } from './openspec-change-github.mjs';
import {
  createGitRunner,
  fetchDependencyNotes,
  findNearestDependencyCheckpoint,
  getDependencyNotesTip,
  pushDependencyNotes,
  readDependencyNote,
  writeDependencyCheckpoint,
} from './openspec-change-git-notes.mjs';
import {
  JSON_CONTRACTS,
  serializeDependencyCheckpoint,
  validateDependencyGraphPatch,
  validateDependencyCheckpoint,
} from './openspec-change-core.mjs';

async function existingFile(filePath) {
  try {
    return (await stat(filePath)).isFile();
  } catch (error) {
    if (error.code === 'ENOENT') return false;
    throw error;
  }
}

async function collectDeltaSpecs(directory, root, files) {
  let entries;
  try {
    entries = await readdir(directory, { withFileTypes: true });
  } catch (error) {
    if (error.code === 'ENOENT') return;
    throw error;
  }
  for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name))) {
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      await collectDeltaSpecs(absolute, root, files);
    } else if (entry.isFile() && entry.name.endsWith('.md')) {
      files.push(path.relative(root, absolute).replaceAll('\\', '/'));
    }
  }
}

export async function inventoryActiveChanges({ root = process.cwd() } = {}) {
  const changesRoot = path.join(root, 'openspec', 'changes');
  const entries = await readdir(changesRoot, { withFileTypes: true });
  const inventory = [];
  for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name))) {
    if (!entry.isDirectory() || entry.name === 'archive') continue;
    const changeRoot = path.join(changesRoot, entry.name);
    const artifacts = [];
    for (const name of ['proposal.md', 'design.md', 'tasks.md']) {
      const absolute = path.join(changeRoot, name);
      if (await existingFile(absolute)) {
        artifacts.push(path.relative(root, absolute).replaceAll('\\', '/'));
      }
    }
    await collectDeltaSpecs(path.join(changeRoot, 'specs'), root, artifacts);
    artifacts.sort();

    const hash = createHash('sha256');
    for (const artifact of artifacts) {
      const content = await readFile(path.join(root, ...artifact.split('/')));
      hash.update(artifact);
      hash.update('\0');
      hash.update(String(content.length));
      hash.update('\0');
      hash.update(content);
      hash.update('\0');
    }
    inventory.push({
      ref: entry.name,
      digest: hash.digest('hex'),
      artifacts,
    });
  }
  return inventory;
}

export function compareDependencyInventory(inventory, checkpoint) {
  const current = new Map(inventory.map((change) => [change.ref, change]));
  const previous = new Map((checkpoint?.changes ?? []).map((change) => [change.ref, change]));
  const added = [...current.keys()].filter((ref) => !previous.has(ref)).sort();
  const modified = [...current.values()]
    .filter((change) => previous.has(change.ref)
      && previous.get(change.ref).digest !== change.digest)
    .map((change) => change.ref)
    .sort();
  const archived = [...previous.keys()].filter((ref) => !current.has(ref)).sort();
  return { added, modified, archived };
}

function incrementalEvaluatedRefs(inventory, checkpoint, changes) {
  const active = new Set(inventory.map(({ ref }) => ref));
  const archived = new Set(changes.archived);
  const evaluated = new Set([...changes.added, ...changes.modified]);
  for (const edge of checkpoint?.managedEdges ?? []) {
    if ((archived.has(edge.changeRef) || archived.has(edge.dependsOn))
      && active.has(edge.changeRef)) {
      evaluated.add(edge.changeRef);
    }
  }
  return [...evaluated].sort();
}

async function appendGitHubOutputs(outputPath, outputs) {
  if (!outputPath) return;
  const lines = Object.entries(outputs).map(([key, value]) => `${key}=${value}`);
  await appendFile(outputPath, `${lines.join('\n')}\n`, 'utf8');
}

export async function prepareDependencyReconciliation({
  root = process.cwd(),
  contextPath = path.join(root, '.openspec-change-dependency-context.json'),
  requestedMode = 'auto',
  remote = 'origin',
  dryRun = false,
  githubOutputPath = process.env.GITHUB_OUTPUT,
  git = createGitRunner({ cwd: root }),
} = {}) {
  if (!['auto', 'full', 'incremental'].includes(requestedMode)) {
    throw new Error('Requested dependency mode must be auto, full, or incremental');
  }
  const targetHead = await git('rev-parse', 'HEAD^{commit}');
  const inventory = await inventoryActiveChanges({ root });
  let notesTip = await getDependencyNotesTip({ git });
  let prior = null;
  let checkpointState = 'missing';

  if (!dryRun) {
    const fetched = await fetchDependencyNotes({ git, remote });
    notesTip = fetched ? await getDependencyNotesTip({ git }) : null;
  }
  try {
    prior = await findNearestDependencyCheckpoint({ git, start: targetHead });
    checkpointState = prior ? 'valid' : 'missing';
  } catch {
    prior = null;
    checkpointState = 'invalid';
  }

  const changes = compareDependencyInventory(inventory, prior?.checkpoint ?? null);
  const mode = requestedMode === 'full' || checkpointState !== 'valid'
    ? 'full'
    : 'incremental';
  const evaluatedRefs = mode === 'full'
    ? inventory.map(({ ref }) => ref).sort()
    : incrementalEvaluatedRefs(inventory, prior.checkpoint, changes);
  const cachedSummaries = new Map(
    (prior?.checkpoint.changes ?? []).map(({ ref, summary }) => [ref, summary]),
  );
  const context = {
    $schema: JSON_CONTRACTS.dependencyReconciliationContext,
    targetHead,
    notesTip,
    checkpointState,
    prior: prior ? { noteCommit: prior.commit, checkpoint: prior.checkpoint } : null,
    mode,
    evaluatedRefs,
    changes,
    inventory: inventory.map((change) => ({
      ...change,
      cachedSummary: cachedSummaries.get(change.ref) ?? null,
    })),
    managedEdges: prior?.checkpoint.managedEdges ?? [],
    outputContract: {
      $schema: JSON_CONTRACTS.dependencyGraphPatch,
      evaluationMode: mode,
      evaluatedRefs,
      summaries: 'Exactly one {ref,summary} object per evaluated ref.',
      upsert: 'Managed dependency edges to create or replace.',
      remove: 'Managed dependency edge identities to remove.',
    },
  };

  const absoluteContextPath = path.resolve(contextPath);
  if (!dryRun) {
    await mkdir(path.dirname(absoluteContextPath), { recursive: true });
    await writeFile(absoluteContextPath, `${JSON.stringify(context, null, 2)}\n`, 'utf8');
    await appendGitHubOutputs(githubOutputPath, {
      agent_context_path: absoluteContextPath,
      dependency_mode: mode,
      evaluated_refs: JSON.stringify(evaluatedRefs),
      target_head: targetHead,
    });
  }
  return { context, contextPath: absoluteContextPath, dryRun };
}

function parseAgentOutput(document) {
  return document?.items ? extractDependencySafeOutput(document) : document;
}

function checkpointChanges(context, summaries) {
  const generated = new Map(summaries.map(({ ref, summary }) => [ref, summary]));
  const previous = new Map(
    (context.prior?.checkpoint.changes ?? []).map((change) => [change.ref, change]),
  );
  return context.inventory.map(({ ref, digest }) => {
    if (generated.has(ref)) return { ref, digest, summary: generated.get(ref) };
    const cached = previous.get(ref);
    if (!cached || cached.digest !== digest) {
      throw new Error(`No updated summary was provided for changed ref ${ref}`);
    }
    return { ref, digest, summary: cached.summary };
  });
}

export async function reconcileDependencyState({
  context,
  output,
  client,
  git = createGitRunner(),
  remote = 'origin',
  minimumConfidence = 0.85,
  reconcile = reconcileDependencies,
  fetchNotes = fetchDependencyNotes,
  writeCheckpoint = writeDependencyCheckpoint,
  pushNotes = pushDependencyNotes,
  verifyPriorNote = readDependencyNote,
} = {}) {
  if (context?.$schema !== JSON_CONTRACTS.dependencyReconciliationContext) {
    throw new Error(
      `Reconciliation context.$schema must be ${JSON_CONTRACTS.dependencyReconciliationContext}`,
    );
  }
  const head = await git('rev-parse', 'HEAD^{commit}');
  if (head !== context.targetHead) {
    throw new Error(`Target HEAD changed: expected ${context.targetHead}, found ${head}`);
  }
  await fetchNotes({ git, remote });
  if (context.prior) {
    const persisted = await verifyPriorNote({
      git,
      object: context.prior.noteCommit,
    });
    if (!persisted
      || serializeDependencyCheckpoint(persisted)
        !== serializeDependencyCheckpoint(context.prior.checkpoint)) {
      throw new Error('The prior dependency checkpoint no longer matches the prepared context');
    }
  }

  const parsedOutput = parseAgentOutput(output);
  const preparedPatch = validateDependencyGraphPatch(
    parsedOutput,
    context.inventory.map(({ ref }) => ref),
    [],
    minimumConfidence,
  );
  if (preparedPatch.evaluationMode !== context.mode
    || JSON.stringify(preparedPatch.evaluatedRefs)
      !== JSON.stringify(context.evaluatedRefs)) {
    throw new Error('Safe output evaluation scope does not match the prepared context');
  }

  const result = await reconcile({
    client,
    output: parsedOutput,
    minimumConfidence,
    checkpoint: context.prior?.checkpoint ?? null,
  });
  const checkpoint = validateDependencyCheckpoint({
    $schema: JSON_CONTRACTS.dependencyCheckpoint,
    commit: context.targetHead,
    changes: checkpointChanges(context, result.summaries),
    managedEdges: result.accepted,
    inference: {
      mode: context.mode,
      evaluatedRefs: context.evaluatedRefs,
      baseCommit: context.prior?.checkpoint.commit ?? null,
      minimumConfidence,
    },
  });

  await writeCheckpoint({
    git,
    object: context.targetHead,
    checkpoint,
    replaceInvalid: context.checkpointState === 'invalid',
  });
  await pushNotes({
    git,
    remote,
    expectedRemoteTip: context.notesTip,
  });
  return { ...result, checkpoint };
}

function parseArguments(args) {
  const [command, ...rest] = args;
  const options = { command, dryRun: false };
  for (let index = 0; index < rest.length; index += 1) {
    const argument = rest[index];
    if (argument === '--dry-run') {
      options.dryRun = true;
      continue;
    }
    if (!argument.startsWith('--') || index + 1 >= rest.length) {
      throw new Error(`Invalid argument: ${argument}`);
    }
    options[argument.slice(2).replaceAll('-', '_')] = rest[index + 1];
    index += 1;
  }
  return options;
}

async function main() {
  const options = parseArguments(process.argv.slice(2));
  if (options.command === 'prepare') {
    const result = await prepareDependencyReconciliation({
      contextPath: options.context,
      requestedMode: options.mode ?? 'auto',
      remote: options.remote ?? 'origin',
      dryRun: options.dryRun,
    });
    process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
    return;
  }
  if (options.command === 'reconcile') {
    if (!options.context || !options.safe_output) {
      throw new Error('reconcile requires --context and --safe-output');
    }
    const repository = process.env.GITHUB_REPOSITORY;
    const token = process.env.GITHUB_TOKEN;
    if (!repository || !token) {
      throw new Error('GITHUB_REPOSITORY and GITHUB_TOKEN are required');
    }
    const [owner, repo] = repository.split('/');
    const context = JSON.parse(await readFile(options.context, 'utf8'));
    const document = JSON.parse(await readFile(options.safe_output, 'utf8'));
    const result = await reconcileDependencyState({
      context,
      output: parseAgentOutput(document),
      client: new GitHubChangeClient({
        owner,
        repo,
        repositoryToken: token,
      }),
      remote: options.remote ?? 'origin',
    });
    process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
    return;
  }
  throw new Error('Command must be prepare or reconcile');
}

const isEntrypoint = process.argv[1]
  && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isEntrypoint) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
