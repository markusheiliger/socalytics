import assert from 'node:assert/strict';
import test from 'node:test';

import {
  createValidationCommands,
  discoverMarkdownPaths,
  executableName,
  isRepositoryAuthoredMarkdown,
  parseMarkdownPaths,
  resolveValidationCommand,
  runMarkdownChecks,
} from './check-markdown.mjs';

test('parses, normalizes, and sorts null-delimited Markdown paths', () => {
  assert.deepEqual(
    parseMarkdownPaths('docs\\guide.md\0README.md\0'),
    ['README.md', 'docs/guide.md'],
  );
});

test('excludes only Spec Kit-generated files from style checks', () => {
  assert.equal(isRepositoryAuthoredMarkdown('README.md'), true);
  assert.equal(isRepositoryAuthoredMarkdown('.specify/memory/constitution.md'), true);
  assert.equal(isRepositoryAuthoredMarkdown('.specify/extension-src/gha/README.md'), true);
  assert.equal(isRepositoryAuthoredMarkdown('.github/skills/custom/SKILL.md'), true);
  assert.equal(isRepositoryAuthoredMarkdown('.github/skills/speckit-plan/SKILL.md'), false);
  assert.equal(isRepositoryAuthoredMarkdown('.specify\\templates\\plan-template.md'), false);
  assert.equal(isRepositoryAuthoredMarkdown('.specify/extensions/gha/README.md'), false);
  assert.equal(isRepositoryAuthoredMarkdown('.specify/extension-src/gha/commands/speckit.gha.route.md'), false);
});

test('uses platform-specific executable names', () => {
  assert.equal(executableName('markdownlint-cli2', 'linux'), 'markdownlint-cli2');
  assert.equal(executableName('markdownlint-cli2', 'win32'), 'markdownlint-cli2.cmd');
});

test('builds style and offline relative-link validation commands', () => {
  const commands = createValidationCommands([
    '.github/skills/speckit-plan/SKILL.md',
    'README.md',
  ], 'win32');

  assert.deepEqual(commands, [
    {
      label: 'Markdown diagnostics',
      tool: 'markdownlint-cli2',
      command: 'markdownlint-cli2.cmd',
      args: [':README.md'],
    },
    {
      label: 'Markdown relative links',
      tool: 'markdown-link-check',
      command: 'markdown-link-check.cmd',
      args: [
        '--quiet',
        '--config',
        '.markdown-link-check.json',
        '.github/skills/speckit-plan/SKILL.md',
        'README.md',
      ],
    },
  ]);
});

test('resolves Windows npm shims to shell-free Node entry points', () => {
  const validation = createValidationCommands(['README.md'], 'win32')[0];
  const resolved = resolveValidationCommand(validation, {
    cwd: 'C:\\repo',
    platform: 'win32',
    nodeExecutable: 'C:\\node\\node.exe',
    spawn(command, args) {
      assert.equal(command, 'where.exe');
      assert.deepEqual(args, ['markdownlint-cli2.cmd']);
      return {
        status: 0,
        stdout: 'C:\\Users\\developer\\AppData\\Roaming\\npm\\markdownlint-cli2.cmd\r\n',
      };
    },
  });

  assert.equal(resolved.command, 'C:\\node\\node.exe');
  assert.deepEqual(resolved.args, [
    'C:\\Users\\developer\\AppData\\Roaming\\npm\\node_modules\\markdownlint-cli2\\markdownlint-cli2-bin.mjs',
    ':README.md',
  ]);
});

test('discovers tracked and unignored untracked Markdown files', () => {
  const calls = [];
  const paths = discoverMarkdownPaths('C:\\repo', (command, args, options) => {
    calls.push({ command, args, options });
    return { status: 0, stdout: 'README.md\0docs\\guide.md\0' };
  });

  assert.deepEqual(paths, ['README.md', 'docs/guide.md']);
  assert.equal(calls[0].command, 'git');
  assert.deepEqual(calls[0].args, [
    'ls-files',
    '-z',
    '--cached',
    '--others',
    '--exclude-standard',
    '--',
    '*.md',
  ]);
  assert.equal(calls[0].options.cwd, 'C:\\repo');
});

test('runs both validators and propagates validation failures', () => {
  const calls = [];
  assert.throws(
    () => runMarkdownChecks({
      cwd: '/repo',
      platform: 'linux',
      spawn(command, args, options) {
        calls.push({ command, args, options });
        if (command === 'git') {
          return { status: 0, stdout: 'README.md\0' };
        }
        if (command === 'markdownlint-cli2') {
          return { status: 0 };
        }
        return { status: 1 };
      },
    }),
    /Markdown relative links failed with exit code 1/,
  );

  assert.deepEqual(
    calls.map(({ command }) => command),
    ['git', 'markdownlint-cli2', 'markdown-link-check'],
  );
});

test('reports a missing validator with the setup instruction', () => {
  assert.throws(
    () => runMarkdownChecks({
      cwd: '/repo',
      platform: 'linux',
      spawn(command) {
        if (command === 'git') {
          return { status: 0, stdout: 'README.md\0' };
        }
        return { error: Object.assign(new Error('not found'), { code: 'ENOENT' }) };
      },
    }),
    /Install the pinned repository tooling documented in README\.md/,
  );
});
