import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

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

export function executableName(name, platform = process.platform) {
  return platform === 'win32' ? `${name}.cmd` : name;
}

export function createValidationCommands(markdownPaths, platform = process.platform) {
  return [
    {
      label: 'Markdown diagnostics',
      tool: 'markdownlint-cli2',
      command: executableName('markdownlint-cli2', platform),
      args: markdownPaths.map((filePath) => `:${filePath}`),
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
      path.join(path.dirname(shimPath), 'node_modules', packageName, entryPoint),
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

export function runMarkdownChecks({
  cwd = process.cwd(),
  platform = process.platform,
  spawn = spawnSync,
} = {}) {
  const markdownPaths = discoverMarkdownPaths(cwd, spawn);
  if (markdownPaths.length === 0) {
    throw new Error('Markdown validation found no Markdown files.');
  }

  for (const validation of createValidationCommands(markdownPaths, platform)) {
    if (validation.args.length === 0) continue;
    const resolved = resolveValidationCommand(validation, {
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

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    runMarkdownChecks();
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
