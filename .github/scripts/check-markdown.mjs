import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// Spec Kit-generated files and Spec Kit command sources, whose format (no top-level heading) Spec Kit defines.
const SPEC_KIT_FORMAT_PATTERNS = [
  /^\.github\/skills\/speckit-/,
  /^\.specify\/templates\//,
  /^\.specify\/extensions\//,
  /^\.specify\/extension-src\/[^/]+\/commands\//,
];
const GLOBAL_TOOL_ENTRIES = new Map([
  ['markdownlint-cli2', ['markdownlint-cli2', 'markdownlint-cli2-bin.mjs']],
  ['markdown-link-check', ['markdown-link-check', 'markdown-link-check']],
]);

function normalizePath(filePath) {
  return filePath.replaceAll('\\', '/');
}

export function parseMarkdownPaths(output) {
  return output
    .split('\0')
    .filter(Boolean)
    .map(normalizePath)
    .sort();
}

export function isRepositoryAuthoredMarkdown(filePath) {
  const normalizedPath = normalizePath(filePath);
  return !SPEC_KIT_FORMAT_PATTERNS.some((pattern) => pattern.test(normalizedPath));
}

export function executableName(name, platform = process.platform) {
  return platform === 'win32' ? `${name}.cmd` : name;
}

export function createValidationCommands(markdownPaths, platform = process.platform) {
  return [
    {
      label: 'Markdown diagnostics',
      tool: 'markdownlint-cli2',
      command: executableName('markdownlint-cli2', platform),
      args: markdownPaths
        .filter(isRepositoryAuthoredMarkdown)
        .map((filePath) => `:${filePath}`),
    },
    {
      label: 'Markdown relative links',
      tool: 'markdown-link-check',
      command: executableName('markdown-link-check', platform),
      args: [
        '--quiet',
        '--config',
        '.markdown-link-check.json',
        ...markdownPaths,
      ],
    },
  ];
}

export function resolveValidationCommand(
  validation,
  {
    cwd,
    platform = process.platform,
    spawn = spawnSync,
    nodeExecutable = process.execPath,
  },
) {
  if (platform !== 'win32') return validation;

  const lookup = spawn('where.exe', [validation.command], {
    cwd,
    encoding: 'utf8',
    windowsHide: true,
  });
  requireSuccessfulProcess(lookup, validation.label, validation.command);
  const shimPath = lookup.stdout.split(/\r?\n/, 1)[0];
  const [packageName, entryPoint] = GLOBAL_TOOL_ENTRIES.get(validation.tool);
  return {
    ...validation,
    command: nodeExecutable,
    args: [
      // Windows paths, also when the resolution itself is tested on another platform.
      path.win32.join(path.win32.dirname(shimPath), 'node_modules', packageName, entryPoint),
      ...validation.args,
    ],
  };
}

function requireSuccessfulProcess(result, label, command) {
  if (result.error) {
    if (result.error.code === 'ENOENT') {
      throw new Error(
        `${label} requires "${command}". Install the pinned repository tooling documented in README.md.`,
      );
    }
    throw result.error;
  }
  if (result.status !== 0) {
    throw new Error(`${label} failed with exit code ${result.status ?? 'unknown'}.`);
  }
}

export function discoverMarkdownPaths(cwd, spawn = spawnSync) {
  const result = spawn(
    'git',
    [
      'ls-files',
      '-z',
      '--cached',
      '--others',
      '--exclude-standard',
      '--',
      '*.md',
    ],
    {
      cwd,
      encoding: 'utf8',
      windowsHide: true,
    },
  );
  requireSuccessfulProcess(result, 'Markdown file discovery', 'git');
  return parseMarkdownPaths(result.stdout);
}

// Command lines stay below Windows' 32,767-character limit; the path arguments are split into several runs.
const MAX_ARGUMENT_CHARS = 24_000;
const FIXED_ARGUMENTS = new Map([['markdown-link-check', 3]]);

export function chunkValidation(validation, maxChars = MAX_ARGUMENT_CHARS) {
  const fixed = validation.args.slice(0, FIXED_ARGUMENTS.get(validation.tool) ?? 0);
  const paths = validation.args.slice(fixed.length);
  const chunks = [];
  let current = [];
  let length = 0;
  for (const arg of paths) {
    if (current.length > 0 && length + arg.length + 1 > maxChars) {
      chunks.push(current);
      current = [];
      length = 0;
    }
    current.push(arg);
    length += arg.length + 1;
  }
  if (current.length > 0) chunks.push(current);
  return chunks.map((chunk) => ({ ...validation, args: [...fixed, ...chunk] }));
}

// The paths of a newline-separated list (for example the changed files of a task) that are discovered Markdown files.
export function selectMarkdownPaths(markdownPaths, listedPaths) {
  const listed = new Set(listedPaths.map((file) => normalizePath(file.trim())).filter(Boolean));
  return markdownPaths.filter((file) => listed.has(file));
}

export function runMarkdownChecks({
  cwd = process.cwd(),
  platform = process.platform,
  spawn = spawnSync,
  files = null,
  log = console.log,
} = {}) {
  const discovered = discoverMarkdownPaths(cwd, spawn);
  if (discovered.length === 0) {
    throw new Error('Markdown validation found no Markdown files.');
  }
  const markdownPaths = files === null ? discovered : selectMarkdownPaths(discovered, files);
  if (files !== null) {
    log(markdownPaths.length > 0
      ? `Checking ${markdownPaths.length} changed Markdown file(s): ${markdownPaths.join(', ')}`
      : 'No changed Markdown files to check.');
    if (markdownPaths.length === 0) return;
  }

  for (const validation of createValidationCommands(markdownPaths, platform)) {
    if (validation.args.length === 0) continue;
    for (const chunk of chunkValidation(validation)) {
      if (chunk.args.length === (FIXED_ARGUMENTS.get(chunk.tool) ?? 0)) continue;
      const resolved = resolveValidationCommand(chunk, {
        cwd,
        platform,
        spawn,
      });
      const result = spawn(resolved.command, resolved.args, {
        cwd,
        stdio: 'inherit',
        windowsHide: true,
      });
      requireSuccessfulProcess(result, validation.label, validation.command);
    }
  }
}

// `--files <list>` checks only the Markdown files named in the newline-separated list file.
export function parseArguments(argv) {
  const index = argv.indexOf('--files');
  if (index < 0) return { filesFrom: null };
  const filesFrom = argv[index + 1];
  if (!filesFrom) throw new Error('Usage: check-markdown.mjs [--files <newline-separated list of paths>]');
  return { filesFrom };
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const { filesFrom } = parseArguments(process.argv.slice(2));
    const files = filesFrom === null ? null : (existsSync(filesFrom) ? readFileSync(filesFrom, 'utf8').split(/\r?\n/) : []);
    runMarkdownChecks({ files });
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
