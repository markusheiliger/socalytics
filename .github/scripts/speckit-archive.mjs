import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { ARCHIVE_DIR, SPECS_DIR, isValidFolderName, specDir } from './speckit-prepare-core.mjs';
import { listTasks } from './speckit-implement-core.mjs';

// Archives an implemented spec: moves specs/<folder> to specs/archive/<folder> and rewrites every reference to it in
// the repository, so the archive lands in the same commit as its references. Relative Markdown links are resolved and
// re-relativized (links inside the moved folder, and links elsewhere that point into it); plain-text mentions of
// `specs/<folder>` (code comments, READMEs) point to the archive. Running it again for an archived spec only rewrites
// references that appeared since, for example after merging a base branch that links to the spec.

export class ArchiveUsageError extends Error {}

const LINK_PATTERN = /(\]\(\s*<?)([^)\s>]+)(>?(?:\s+"[^"]*")?\s*\))/g;
const DEFINITION_PATTERN = /^( {0,3}\[[^\]]+\]:\s*<?)(\S+?)(>?(?:\s.*)?)$/;
const FENCE_PATTERN = /^ {0,3}(```|~~~)/;

const posix = path.posix;
const isRelativeTarget = (target) => target !== '' && !target.startsWith('#') && !target.startsWith('/') && !/^[a-z][a-z0-9+.-]*:/i.test(target);
const isMarkdown = (file) => file.toLowerCase().endsWith('.md');

// Maps a repository path into the archive when it is the spec folder or inside it.
function archivedPath(file, folder) {
  const from = specDir(folder);
  if (file === from) return specDir(folder, { archived: true });
  return file.startsWith(`${from}/`) ? `${specDir(folder, { archived: true })}${file.slice(from.length)}` : file;
}

// Rewrites one relative link target of a Markdown file that lives at `newFile` and was written for `oldFile`.
function rewriteTarget(target, { oldFile, newFile, folder }) {
  if (!isRelativeTarget(target)) return target;
  const hash = target.indexOf('#');
  const pathPart = hash >= 0 ? target.slice(0, hash) : target;
  const anchor = hash >= 0 ? target.slice(hash) : '';
  if (!pathPart) return target;
  const resolved = posix.normalize(posix.join(posix.dirname(oldFile), pathPart));
  if (resolved.startsWith('../')) return target;
  const goal = archivedPath(resolved, folder);
  if (goal === resolved && oldFile === newFile) return target;
  let relative = posix.relative(posix.dirname(newFile), goal) || '.';
  if (pathPart.endsWith('/') && !relative.endsWith('/')) relative += '/';
  if (pathPart.startsWith('./') && !relative.startsWith('.')) relative = `./${relative}`;
  return relative === pathPart ? target : `${relative}${anchor}`;
}

// Line endings stay as they are: each line is rewritten without its trailing carriage return.
function rewriteLinks(text, context) {
  let fenced = false;
  return text.split('\n').map((raw) => {
    const cr = raw.endsWith('\r') ? '\r' : '';
    const line = cr ? raw.slice(0, -1) : raw;
    if (FENCE_PATTERN.test(line)) {
      fenced = !fenced;
      return raw;
    }
    if (fenced) return raw;
    const definition = line.match(DEFINITION_PATTERN);
    if (definition) return `${definition[1]}${rewriteTarget(definition[2], context)}${definition[3]}${cr}`;
    return `${line.replace(LINK_PATTERN, (match, open, target, close) => `${open}${rewriteTarget(target, context)}${close}`)}${cr}`;
  }).join('\n');
}

// Plain-text mentions of specs/<folder> that are not already archive paths; the folder name must end there (a sentence
// may end right after it). Mentions inside URLs are left alone: they point elsewhere or to a fixed version.
function rewriteMentions(text, folder) {
  const escaped = `${SPECS_DIR}/${folder}`.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return text.replace(new RegExp(`(?<![\\w.-])${escaped}(?![\\w-]|\\.[\\w-])`, 'g'), (match, offset) => {
    const tokenStart = Math.max(...[' ', '\t', '\n', '(', '<', '"', "'", '`'].map((char) => text.lastIndexOf(char, offset - 1)));
    return text.slice(tokenStart + 1, offset).includes('://') ? match : specDir(folder, { archived: true });
  });
}

const isBinary = (buffer) => buffer.subarray(0, 8000).includes(0);
// The workflow token cannot push changes to workflow files, so the archive never edits them.
const isUntouchable = (file) => file.startsWith('.github/workflows/');

export function defaultGit(cwd) {
  return (args) => {
    const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true, maxBuffer: 50 * 1024 * 1024 });
    if (result.error) throw new ArchiveUsageError(`git is required: ${result.error.message}`);
    return { status: result.status, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  };
}

function trackedFiles(git) {
  const listed = git(['ls-files', '-z', '--cached', '--others', '--exclude-standard']);
  if (listed.status !== 0) throw new ArchiveUsageError(`git ls-files failed: ${listed.stderr.trim()}`);
  return [...new Set(listed.stdout.split('\0').filter(Boolean))];
}

// Why a spec cannot be archived, or an empty list. An already archived spec can always be rewritten again.
export function archiveBlockers({ root, folder }) {
  if (!isValidFolderName(folder) || folder === 'archive') return [`"${folder}" is not a valid spec folder name`];
  const active = path.join(root, specDir(folder));
  const archived = path.join(root, specDir(folder, { archived: true }));
  if (existsSync(active) && existsSync(archived)) return [`both \`${specDir(folder)}\` and \`${specDir(folder, { archived: true })}\` exist`];
  if (existsSync(archived)) return [];
  if (!existsSync(active)) return [`\`${specDir(folder)}\` does not exist`];
  const tasksFile = path.join(active, 'tasks.md');
  if (!existsSync(tasksFile)) return [`\`${specDir(folder)}/tasks.md\` does not exist, so the spec is not implemented`];
  const open = listTasks(readFileSync(tasksFile, 'utf8')).filter((task) => !task.done).map((task) => task.id);
  return open.length > 0 ? [`${open.length} task(s) are not checked: ${open.join(', ')}`] : [];
}

// Plans (and unless `check` is set, applies) the archive of one spec. Returns { moved, changes: [{ file, from }] },
// where `from` is the file's path before the move.
export function archiveSpec({ root, folder, git = defaultGit(root), check = false }) {
  const reasons = archiveBlockers({ root, folder });
  if (reasons.length > 0) return { exitCode: 1, reasons, moved: false, changes: [] };
  const move = existsSync(path.join(root, specDir(folder)));
  const files = trackedFiles(git);
  const changes = [];
  for (const oldFile of files) {
    const newFile = move ? archivedPath(oldFile, folder) : oldFile;
    const absolute = path.join(root, oldFile);
    if (!existsSync(absolute)) continue;
    const buffer = readFileSync(absolute);
    if (isBinary(buffer) || isUntouchable(oldFile)) {
      if (newFile !== oldFile) changes.push({ file: newFile, from: oldFile, content: null });
      continue;
    }
    const original = buffer.toString('utf8');
    let text = original;
    if (isMarkdown(oldFile)) text = rewriteLinks(text, { oldFile, newFile, folder });
    text = rewriteMentions(text, folder);
    const content = text;
    if (content !== original || newFile !== oldFile) changes.push({ file: newFile, from: oldFile, content: content === original ? null : content });
  }
  if (!check) {
    if (move) {
      // Moves the whole folder, untracked files included, and stages the move like `git mv` would.
      mkdirSync(path.join(root, ARCHIVE_DIR), { recursive: true });
      renameSync(path.join(root, specDir(folder)), path.join(root, specDir(folder, { archived: true })));
      git(['add', '-A', '--', specDir(folder), specDir(folder, { archived: true })]);
    }
    for (const change of changes) {
      if (change.content !== null) writeFileSync(path.join(root, change.file), change.content);
    }
  }
  return { exitCode: 0, reasons: [], moved: move, changes: changes.map(({ file, from, content }) => ({ file, from, edited: content !== null })) };
}

// Rewrites references to every other archived spec that are still outdated, for example in files that a branch added
// while the spec was still active and that it merges now. Returns the files it changed (or would change with `check`).
export function refreshArchivedReferences({ root, git = defaultGit(root), check = false, except = null }) {
  const archiveRoot = path.join(root, ARCHIVE_DIR);
  if (!existsSync(archiveRoot)) return [];
  const files = new Set();
  for (const entry of readdirSync(archiveRoot, { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name === except || !isValidFolderName(entry.name)) continue;
    const result = archiveSpec({ root, folder: entry.name, git, check });
    for (const change of result.changes) if (change.edited) files.add(change.file);
  }
  return [...files].sort();
}

// Applies the archive of every folder in `folders` to one file that lived at `file` before those archives, exactly as
// archiveSpec would for each of them in turn. Returns the rewritten text and the file's path afterwards. Running it
// again changes nothing, so it can normalize text that is already partly rewritten.
export function archiveRewrite(text, { file, folders }) {
  let current = file;
  let result = String(text);
  for (const folder of folders) {
    const next = archivedPath(current, folder);
    if (isMarkdown(current)) result = rewriteLinks(result, { oldFile: current, newFile: next, folder });
    result = rewriteMentions(result, folder);
    current = next;
  }
  return { text: result, file: current };
}

// The spec folders archived in a commit (`ref`) or, without one, in the index.
export function archivedFolders(git, ref = null) {
  const listed = ref
    ? git(['ls-tree', '-d', '--name-only', '-z', ref, '--', `${ARCHIVE_DIR}/`])
    : git(['ls-files', '-z', '--', `${ARCHIVE_DIR}/`]);
  if (listed.status !== 0) return [];
  const prefix = `${ARCHIVE_DIR}/`;
  return [...new Set(listed.stdout.split('\0').filter((entry) => entry.startsWith(prefix))
    .map((entry) => entry.slice(prefix.length).split('/')[0])
    .filter((name) => isValidFolderName(name)))].sort();
}

const showBlob = (git, spec) => {
  const shown = git(['cat-file', '-p', spec]);
  return shown.status === 0 ? shown.stdout : null;
};
const unmergedFiles = (git) => [...new Set(git(['diff', '--name-only', '--diff-filter=U', '-z']).stdout.split('\0').filter(Boolean))].sort();
const insideSpec = (file, folder) => [specDir(folder), specDir(folder, { archived: true })].some((dir) => file === dir || file.startsWith(`${dir}/`));

// Resolves the merge conflicts under specs/ that the archive's reference rewrites cause, during an uncommitted merge of
// the base branch (MERGE_HEAD) into the implementation branch of `folder`, without an agent:
// - outside the spec's own folder the branch may only rewrite references (referenceOnlySpecChanges), so the base
//   branch's version with every archive applied is the result, and a file the base branch removed stays removed;
// - in the spec's own folder both sides and their merge base are normalized with every archive and merged three-way;
//   a clean merge is the result, anything else stays a conflict for the conflict resolution.
// Afterwards references in the other files point to every archived spec, also when nothing conflicted: a file the base
// branch added may link to a spec this branch archived (or the reverse), and such a link would be dead after the merge.
// Returns the resolved and remaining conflicts and the files whose references were rewritten.
export function resolveSpecConflicts({ git, root, folder }) {
  const conflicts = unmergedFiles(git);
  const specConflicts = conflicts.filter((file) => file.startsWith(`${SPECS_DIR}/`));
  if (specConflicts.length === 0) {
    const refreshed = refreshReferences({ root, git, folders: archivedFolders(git), skip: new Set(conflicts) });
    return { resolved: [], remaining: conflicts, refreshed };
  }
  const folders = [...new Set([...archivedFolders(git, 'HEAD'), ...archivedFolders(git, 'MERGE_HEAD')])].sort();
  const mergeBase = git(['merge-base', 'HEAD', 'MERGE_HEAD']).stdout.trim();
  const resolved = new Set();
  const drop = (file) => {
    git(['rm', '-q', '--cached', '--ignore-unmatch', '--', file]);
    rmSync(path.join(root, file), { force: true });
  };
  const write = (file, text) => {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), text);
    git(['add', '--', file]);
  };
  for (const file of specConflicts) {
    if (resolved.has(file)) continue;
    if (!insideSpec(file, folder)) {
      const theirs = showBlob(git, `MERGE_HEAD:${file}`);
      if (theirs === null) {
        drop(file);
      } else {
        const rewritten = archiveRewrite(theirs, { file, folders });
        if (rewritten.file !== file) drop(file);
        write(rewritten.file, rewritten.text);
        resolved.add(rewritten.file);
      }
      resolved.add(file);
      continue;
    }
    const relative = file.startsWith(`${specDir(folder, { archived: true })}/`)
      ? file.slice(specDir(folder, { archived: true }).length)
      : file.slice(specDir(folder).length);
    const active = `${specDir(folder)}${relative}`;
    const archived = `${specDir(folder, { archived: true })}${relative}`;
    const oursPath = showBlob(git, `HEAD:${archived}`) !== null ? archived : active;
    const ours = showBlob(git, `HEAD:${oursPath}`);
    const theirsPath = showBlob(git, `MERGE_HEAD:${archived}`) !== null ? archived : active;
    const theirs = showBlob(git, `MERGE_HEAD:${theirsPath}`);
    const base = mergeBase ? (showBlob(git, `${mergeBase}:${active}`) ?? showBlob(git, `${mergeBase}:${archived}`) ?? '') : '';
    if (ours === null || theirs === null) continue;
    const normalize = (text, from) => archiveRewrite(text, { file: from, folders }).text;
    const scratch = mkdtempSync(path.join(tmpdir(), 'speckit-spec-merge-'));
    try {
      const sides = { ours: normalize(ours, oursPath), base: normalize(base, active), theirs: normalize(theirs, theirsPath) };
      for (const [name, text] of Object.entries(sides)) writeFileSync(path.join(scratch, name), text);
      const merged = git(['merge-file', '-p', path.join(scratch, 'ours'), path.join(scratch, 'base'), path.join(scratch, 'theirs')]);
      if (merged.status !== 0) continue;
      for (const stale of new Set([file, active, archived])) if (stale !== oursPath && unmergedFiles(git).includes(stale)) drop(stale);
      write(oursPath, merged.stdout);
      for (const done of [file, active, archived]) resolved.add(done);
    } finally {
      rmSync(scratch, { recursive: true, force: true });
    }
  }
  const remaining = unmergedFiles(git);
  const refreshed = refreshReferences({ root, git, folders: archivedFolders(git), skip: new Set(remaining) });
  return { resolved: specConflicts.filter((file) => resolved.has(file) && !remaining.includes(file)), remaining, refreshed };
}

// Points every reference in the tracked text files (except `skip`) to the archive of each folder in `folders` and
// stages the files it changed. Files inside an active folder of an archived spec are left alone.
export function refreshReferences({ root, git, folders, skip = new Set() }) {
  const changed = [];
  if (folders.length === 0) return changed;
  for (const file of [...new Set(git(['ls-files', '-z', '--cached']).stdout.split('\0').filter(Boolean))]) {
    if (skip.has(file) || isUntouchable(file) || folders.some((folder) => file.startsWith(`${specDir(folder)}/`))) continue;
    const absolute = path.join(root, file);
    if (!existsSync(absolute)) continue;
    const buffer = readFileSync(absolute);
    if (isBinary(buffer)) continue;
    const original = buffer.toString('utf8');
    const { text } = archiveRewrite(original, { file, folders });
    if (text === original) continue;
    writeFileSync(absolute, text);
    git(['add', '--', file]);
    changed.push(file);
  }
  return changed;
}

// Changes under specs/ outside the spec's own folder that are not reference rewrites, comparing the staged tree (the
// prospective merge result) with the base branch commit `baseSha`: such a file must equal the base branch's version
// with every archive of the staged tree applied; adding or removing a file there is never a reference change.
export function referenceRewriteCheck({ git, baseSha, folder }) {
  const listed = git(['diff', '--cached', '--name-status', '--no-renames', '-z', baseSha, '--', `${SPECS_DIR}/`]);
  if (listed.status !== 0) return { rewritten: [], violations: [], error: `the changes under ${SPECS_DIR}/ could not be listed: ${listed.stderr.trim()}` };
  const parts = listed.stdout.split('\0').filter(Boolean);
  const folders = archivedFolders(git);
  const rewritten = [];
  const violations = [];
  for (let index = 0; index + 1 < parts.length; index += 2) {
    const [status, file] = [parts[index], parts[index + 1]];
    if (insideSpec(file, folder)) continue;
    if (status !== 'M') {
      violations.push(file);
      continue;
    }
    const base = showBlob(git, `${baseSha}:${file}`);
    const staged = showBlob(git, `:${file}`);
    if (base === null || staged === null || archiveRewrite(base, { file, folders }).text !== staged) violations.push(file);
    else rewritten.push(file);
  }
  return { rewritten, violations, error: null };
}

export function referenceOnlySpecChanges({ git, baseSha, folder }) {
  const { violations, error } = referenceRewriteCheck({ git, baseSha, folder });
  if (error !== null) return [error];
  return violations.length > 0
    ? [`changes under ${SPECS_DIR}/ outside ${specDir(folder)} must be reference rewrites of the archive, but these differ: ${violations.join(', ')}`]
    : [];
}

export function renderArchiveReport(folder, result, { check = false } = {}) {
  if (result.exitCode !== 0) return [`Not archived: \`${specDir(folder)}\`: ${result.reasons.join('; ')}.`];
  const verb = check ? 'Would' : 'Did';
  const edited = result.changes.filter((change) => change.edited && change.file === change.from);
  return [
    result.moved
      ? `${verb} move \`${specDir(folder)}\` to \`${specDir(folder, { archived: true })}\`.`
      : `\`${specDir(folder, { archived: true })}\` is already archived; only references are rewritten.`,
    ...(edited.length > 0 ? [`${verb} rewrite references in ${edited.length} file(s):`, ...edited.map((change) => `- ${change.file}`)] : ['No other file references the spec.']),
  ];
}

export function main(argv, { cwd = process.cwd(), log = console.log, git } = {}) {
  const check = argv.includes('--check');
  const rest = argv.filter((arg) => arg !== '--check');
  if (rest.length !== 1 || rest[0].startsWith('-')) throw new ArchiveUsageError('Usage: speckit-archive.mjs <spec folder> [--check]');
  const folder = path.basename(rest[0].replaceAll('\\', '/').replace(/\/+$/, ''));
  const runner = git ?? defaultGit(cwd);
  const result = archiveSpec({ root: cwd, folder, git: runner, check });
  for (const line of renderArchiveReport(folder, result, { check })) log(line);
  if (result.exitCode !== 0) return result.exitCode;
  // A check run plans this spec only: the other archived specs see the old state of the files it would move.
  if (!check) {
    const refreshed = refreshArchivedReferences({ root: cwd, git: runner, except: folder });
    if (refreshed.length > 0) log(`Also pointed outdated references to other archived specs to the archive in ${refreshed.length} file(s): ${refreshed.join(', ')}`);
  }
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = error instanceof ArchiveUsageError ? 2 : 1;
  }
}
