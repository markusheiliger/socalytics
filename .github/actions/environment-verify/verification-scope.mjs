// Decides whether a change is documentation-only: every changed path is Markdown that no build, package, or test reads.
// Usage: node verification-scope.mjs --root <workspace> --changed-files <list>
// Prints one line with the decision and its reason; exits 0 for documentation-only, 1 otherwise, 2 for a usage error.
// Anything it cannot establish (no or empty change list, a non-Markdown path, a Markdown file a consumer may read)
// keeps the full verification.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Folders whose content is generated, installed, or not part of the source (never searched).
const SKIPPED = new Set([
  '.git', 'node_modules', 'bin', 'obj', '.venv', 'venv', 'dist', 'build', '__pycache__', 'site-packages',
  'TestResults', 'test-results', 'playwright-report', '.vs', '.pytest_cache',
]);
// Only product sources, contracts, and root-level files can read a Markdown file during verification. Specs and
// tooling under .github/ and .specify/ never build or test the product.
const CONSUMER_ROOTS = ['src', 'contracts'];
const MAX_BYTES = 2 * 1024 * 1024;
const PROJECT_FILE = /\.(csproj|fsproj|vbproj|props|targets)$/;
const ITEM_PATTERN = /\b(?:Include|Update)\s*=\s*"([^"]*)"/g;
const QUOTED = /"([^"\n]*)"|'([^'\n]*)'/g;

const toPosix = (value) => value.replace(/\\/g, '/');

// Whether text names a path: an occurrence not preceded by another path or name character (a "/" is allowed when it
// ends a "../" segment, or, when nested is true, for a file name inside a longer path) and not followed by a name
// character.
export function mentions(text, needle, { nested = false } = {}) {
  const before = nested ? /[\w.-]/ : /[\w./-]/;
  for (let index = text.indexOf(needle); index !== -1; index = text.indexOf(needle, index + 1)) {
    const previous = index === 0 ? '' : text[index - 1];
    const next = text[index + needle.length] ?? '';
    const relative = previous === '/' && text[index - 2] === '.';
    if (!(previous && before.test(previous) && !relative) && !/[\w-]/.test(next)) return true;
  }
  return false;
}

function walk(root, relative, files) {
  let entries;
  try {
    entries = readdirSync(path.join(root, relative), { withFileTypes: true });
  } catch {
    return files;
  }
  for (const entry of entries) {
    const child = relative ? `${relative}/${entry.name}` : entry.name;
    if (entry.isDirectory()) {
      if (!SKIPPED.has(entry.name)) walk(root, child, files);
    } else if (entry.isFile()) {
      files.push(child);
    }
  }
  return files;
}

// MSBuild and hatch glob patterns, relative and with forward slashes: ** spans folders, * and ? stay inside one.
export function globToRegExp(pattern) {
  let expression = '';
  for (let index = 0; index < pattern.length; index += 1) {
    const char = pattern[index];
    if (char === '*' && pattern[index + 1] === '*') {
      if (pattern[index + 2] === '/') {
        expression += '(?:.*/)?';
        index += 2;
      } else {
        expression += '.*';
        index += 1;
      }
    } else if (char === '*') {
      expression += '[^/]*';
    } else if (char === '?') {
      expression += '[^/]';
    } else {
      expression += char.replace(/[.+^${}()|[\]\\]/g, '\\$&');
    }
  }
  return new RegExp(`^${expression}$`);
}

// Whether a pattern written relative to a folder of the repository names the Markdown file, as a glob, the file
// itself, or a folder that contains it.
export function patternMatches(base, pattern, markdown) {
  const raw = toPosix(pattern.trim());
  if (!raw || /^[a-z][a-z0-9+.-]*:/i.test(raw)) return false;
  const resolved = path.posix.normalize(base ? `${base}/${raw}` : raw).replace(/\/+$/, '');
  if (resolved.startsWith('..')) return false;
  if (/[*?]/.test(resolved)) return globToRegExp(resolved).test(markdown);
  return markdown === resolved || markdown.startsWith(`${resolved}/`);
}

function readText(root, file) {
  try {
    if (statSync(path.join(root, file)).size > MAX_BYTES) return null;
    const text = readFileSync(path.join(root, file), 'utf8');
    return text.includes('\0') ? null : text;
  } catch {
    return null;
  }
}

// The consumers that may read a Markdown file: project files whose items include it, Python manifests whose paths
// include it, and source, test, or configuration files that name it (by its repository path when that path has a
// folder; by its file name when that name is unique among the repository's Markdown files; or by its file name from
// the same folder).
export function consumersOf(root, markdownFiles) {
  const all = walk(root, '', []);
  const basenameCounts = new Map();
  for (const file of all) {
    if (file.endsWith('.md')) basenameCounts.set(path.posix.basename(file), (basenameCounts.get(path.posix.basename(file)) ?? 0) + 1);
  }
  for (const file of markdownFiles) {
    const name = path.posix.basename(file);
    if (!basenameCounts.has(name)) basenameCounts.set(name, 1);
  }
  const candidates = all.filter((file) => !file.endsWith('.md') && (!file.includes('/') || CONSUMER_ROOTS.some((root_) => file.startsWith(`${root_}/`))));
  const found = new Map(markdownFiles.map((file) => [file, []]));
  for (const consumer of candidates) {
    const text = readText(root, consumer);
    if (text === null) continue;
    const folder = path.posix.dirname(consumer) === '.' ? '' : path.posix.dirname(consumer);
    const patterns = [];
    if (PROJECT_FILE.test(consumer)) {
      for (const match of text.matchAll(ITEM_PATTERN)) patterns.push(...match[1].split(';'));
    } else if (path.posix.basename(consumer) === 'pyproject.toml') {
      for (const match of text.matchAll(QUOTED)) patterns.push(match[1] ?? match[2]);
    }
    for (const markdown of markdownFiles) {
      const name = path.posix.basename(markdown);
      const named = (markdown.includes('/') && mentions(text, markdown))
        || (basenameCounts.get(name) === 1 && mentions(text, name, { nested: true }))
        || (path.posix.dirname(markdown) === (folder || '.') && mentions(text, name));
      if (named || patterns.some((pattern) => patternMatches(folder, pattern, markdown))) found.get(markdown).push(consumer);
    }
  }
  return found;
}

export function classify(root, changedFilesPath) {
  if (!changedFilesPath) return { documentationOnly: false, reason: 'no change list was given' };
  let content;
  try {
    content = readFileSync(changedFilesPath, 'utf8');
  } catch {
    return { documentationOnly: false, reason: `the change list ${changedFilesPath} cannot be read` };
  }
  const changed = [...new Set(content.split(/\r?\n/).map((line) => toPosix(line.trim())).filter(Boolean))];
  if (changed.length === 0) return { documentationOnly: false, reason: 'the change list is empty' };
  const other = changed.find((file) => !file.endsWith('.md'));
  if (other) return { documentationOnly: false, reason: `${other} is not Markdown` };
  for (const [markdown, consumers] of consumersOf(root, changed)) {
    if (consumers.length > 0) {
      return { documentationOnly: false, reason: `${markdown} may be read by ${consumers.slice(0, 3).join(', ')}${consumers.length > 3 ? ', …' : ''}` };
    }
  }
  return {
    documentationOnly: true,
    reason: `${changed.length} Markdown file(s) that no build, package, or test reads; product builds and tests are skipped`,
  };
}

export function main(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 2) {
    if (!['--root', '--changed-files'].includes(argv[index]) || argv[index + 1] === undefined) {
      console.log('usage: verification-scope.mjs --root <workspace> --changed-files <list>');
      return 2;
    }
    options[argv[index].slice(2)] = argv[index + 1];
  }
  if (!options.root) {
    console.log('usage: verification-scope.mjs --root <workspace> --changed-files <list>');
    return 2;
  }
  const result = classify(options.root, options['changed-files']);
  console.log(`${result.documentationOnly ? 'documentation-only' : 'full'}: ${result.reason}`);
  return result.documentationOnly ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = main(process.argv.slice(2));
