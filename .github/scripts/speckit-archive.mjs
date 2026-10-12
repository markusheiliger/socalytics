import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
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
