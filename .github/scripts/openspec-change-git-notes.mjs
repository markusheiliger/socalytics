import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

import {
  parseDependencyCheckpoint,
  serializeDependencyCheckpoint,
  validateDependencyCheckpoint,
} from './openspec-change-core.mjs';

export const DEPENDENCY_NOTES_REF = 'refs/notes/openspec-change-dependencies';

export function createGitRunner({ cwd = process.cwd(), execFileImpl = execFile } = {}) {
  const execute = promisify(execFileImpl);
  return async (...args) => {
    const result = await execute('git', args, {
      cwd,
      encoding: 'utf8',
      maxBuffer: 10 * 1024 * 1024,
    });
    return result.stdout.trim();
  };
}

function isMissingNote(error) {
  const message = `${error?.stderr ?? ''}\n${error?.message ?? ''}`;
  return /no note found|cannot read note data|unknown revision/i.test(message);
}

export async function fetchDependencyNotes({
  git = createGitRunner(),
  remote = 'origin',
  notesRef = DEPENDENCY_NOTES_REF,
  allowMissing = true,
} = {}) {
  try {
    await git('fetch', '--no-tags', remote, `${notesRef}:${notesRef}`);
    return true;
  } catch (error) {
    const message = `${error?.stderr ?? ''}\n${error?.message ?? ''}`;
    if (allowMissing && /couldn't find remote ref|remote ref does not exist/i.test(message)) {
      return false;
    }
    throw error;
  }
}

export async function getDependencyNotesTip({
  git = createGitRunner(),
  notesRef = DEPENDENCY_NOTES_REF,
} = {}) {
  try {
    return await git('rev-parse', notesRef);
  } catch (error) {
    const message = `${error?.stderr ?? ''}\n${error?.message ?? ''}`;
    if (/unknown revision|ambiguous argument|needed a single revision/i.test(message)) return null;
    throw error;
  }
}

export async function readDependencyNote({
  git = createGitRunner(),
  object = 'HEAD',
  notesRef = DEPENDENCY_NOTES_REF,
} = {}) {
  try {
    const note = await git('notes', `--ref=${notesRef}`, 'show', object);
    return parseDependencyCheckpoint(note);
  } catch (error) {
    if (isMissingNote(error)) return null;
    throw error;
  }
}

export async function findNearestDependencyCheckpoint({
  git = createGitRunner(),
  start = 'HEAD',
  notesRef = DEPENDENCY_NOTES_REF,
} = {}) {
  const commits = (await git('rev-list', '--topo-order', start))
    .split(/\r?\n/)
    .filter(Boolean);
  for (const commit of commits) {
    const checkpoint = await readDependencyNote({ git, object: commit, notesRef });
    if (checkpoint) return { commit, checkpoint };
  }
  return null;
}

function refFromChangePath(path) {
  const normalized = path.replaceAll('\\', '/');
  let match = normalized.match(/^openspec\/changes\/([^/]+)\//);
  if (match && match[1] !== 'archive') return match[1];
  match = normalized.match(
    /^openspec\/changes\/archive\/\d{4}-\d{2}-\d{2}-(.+?)\//,
  );
  return match?.[1] ?? null;
}

export async function writeDependencyCheckpoint({
  git = createGitRunner(),
  object = 'HEAD',
  checkpoint,
  notesRef = DEPENDENCY_NOTES_REF,
  replaceInvalid = false,
} = {}) {
  const commit = await git('rev-parse', `${object}^{commit}`);
  const validated = validateDependencyCheckpoint(checkpoint);
  if (validated.commit !== commit) {
    throw new Error(
      `Dependency checkpoint commit ${validated.commit} does not match ${commit}`,
    );
  }
  let replace = false;
  try {
    const note = await git('notes', `--ref=${notesRef}`, 'show', commit);
    parseDependencyCheckpoint(note);
    throw new Error(`Dependency checkpoint already exists for ${commit}`);
  } catch (error) {
    if (error.message === `Dependency checkpoint already exists for ${commit}`) throw error;
    if (!isMissingNote(error)) {
      if (!replaceInvalid) throw error;
      replace = true;
    }
  }
  await git(
    'notes',
    `--ref=${notesRef}`,
    'add',
    ...(replace ? ['-f'] : []),
    '-m',
    serializeDependencyCheckpoint(validated).trimEnd(),
    commit,
  );
  return validated;
}

export async function pushDependencyNotes({
  git = createGitRunner(),
  remote = 'origin',
  notesRef = DEPENDENCY_NOTES_REF,
  expectedRemoteTip = null,
} = {}) {
  const remoteOutput = await git('ls-remote', remote, notesRef);
  const remoteTip = remoteOutput ? remoteOutput.split(/\s+/)[0] : null;
  if (remoteTip !== expectedRemoteTip) {
    throw new Error(
      `Dependency notes changed on ${remote}: expected ${expectedRemoteTip ?? 'missing'}, found ${remoteTip ?? 'missing'}`,
    );
  }
  await git('push', '--porcelain', remote, `${notesRef}:${notesRef}`);
  return getDependencyNotesTip({ git, notesRef });
}
