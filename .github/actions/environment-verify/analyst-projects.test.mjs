import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const helper = path.join(here, 'analyst-projects.py');
const temporary = mkdtempSync(path.join(tmpdir(), 'analyst-projects-test-'));
after(() => rmSync(temporary, { recursive: true, force: true }));

let counter = 0;
const repository = () => {
  const directory = path.join(temporary, `repo-${counter++}`);
  mkdirSync(directory, { recursive: true });
  return directory;
};

function manifest({ name, requiresPython = '>=3.11', dependencies = [], sources = {}, extra = '', build = true } = {}) {
  const lines = ['[project]'];
  if (name !== undefined) lines.push(`name = "${name}"`);
  if (requiresPython !== null) lines.push(`requires-python = "${requiresPython}"`);
  lines.push(`dependencies = [${dependencies.map((value) => `"${value}"`).join(', ')}]`);
  if (build) lines.push('', '[build-system]', 'requires = ["hatchling"]', 'build-backend = "hatchling.build"');
  if (Object.keys(sources).length) {
    lines.push('', '[tool.uv.sources]');
    for (const [key, value] of Object.entries(sources)) lines.push(`${key} = { path = "${value}" }`);
  }
  if (extra) lines.push('', extra);
  return `${lines.join('\n')}\n`;
}

function project(root, relative, options = {}, { lock = true } = {}) {
  const directory = path.join(root, relative);
  mkdirSync(directory, { recursive: true });
  writeFileSync(path.join(directory, 'pyproject.toml'), options.raw ?? manifest(options));
  if (lock) writeFileSync(path.join(directory, 'uv.lock'), 'version = 1\n');
}

function discover(root, args = []) {
  const result = spawnSync('python', [helper, 'discover', '--root', root, '--python-version', '3.13.5', ...args], { encoding: 'utf8' });
  return {
    status: result.status,
    stderr: result.stderr,
    projects: result.status === 0 ? JSON.parse(result.stdout).projects : undefined,
  };
}

function rejected(root, pattern) {
  const result = discover(root);
  assert.equal(result.status, 1, result.stderr);
  assert.match(result.stderr, pattern);
  assert.match(result.stderr, /Recommended action: /);
  assert.equal(result.stderr.match(/Recommended action: /g).length, result.stderr.trim().split('\n').length);
  return result;
}

describe('project discovery', () => {
  it('finds nothing when no recognized project exists', () => {
    const root = repository();
    mkdirSync(path.join(root, 'src/analysts/manager'), { recursive: true });
    writeFileSync(path.join(root, 'src/analysts/manager/pyproject.toml'), '[project]\nname = "ignored"\n');
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects, []);
  });

  it('recognizes the SDK and immediate capability roots, normalizing identities', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'SocAlytics_Analyst.SDK' });
    project(root, 'src/analysts/capabilities/segment', { name: 'segment' });
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects.map((entry) => [entry.root, entry.name]), [
      ['src/analysts/capabilities/segment', 'segment'],
      ['src/analysts/sdk', 'socalytics-analyst-sdk'],
    ]);
  });

  it('ignores nested, hidden, dependency-environment, and generated manifests', () => {
    const root = repository();
    project(root, 'src/analysts/capabilities/segment/nested', { name: 'nested' });
    project(root, 'src/analysts/capabilities/.venv', { name: 'venv' });
    project(root, 'src/analysts/capabilities/node_modules', { name: 'modules' });
    project(root, 'src/analysts/capabilities/build', { name: 'built' });
    project(root, 'src/analysts/sdk/.venv/lib', { name: 'env' });
    project(root, 'src/analysts/other', { name: 'other' });
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects, []);
  });

  it('does not follow a symbolic-link capability directory', () => {
    const root = repository();
    project(root, 'elsewhere', { name: 'linked' });
    mkdirSync(path.join(root, 'src/analysts/capabilities'), { recursive: true });
    symlinkSync(path.join(root, 'elsewhere'), path.join(root, 'src/analysts/capabilities/linked'));
    assert.deepEqual(discover(root).projects, []);
  });

  it('orders dependencies first and resolves local path dependencies', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['sdk'], sources: { sdk: '../../sdk' } });
    project(root, 'src/analysts/capabilities/b', { name: 'b', dependencies: ['a', 'sdk'], sources: { a: '../a', sdk: '../../sdk' } });
    const result = discover(root);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(result.projects.map((entry) => entry.root), [
      'src/analysts/sdk',
      'src/analysts/capabilities/a',
      'src/analysts/capabilities/b',
    ]);
    assert.deepEqual(result.projects[2].dependencies, ['src/analysts/capabilities/a', 'src/analysts/sdk']);
  });
});

describe('project validation', () => {
  it('rejects malformed manifests', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { raw: '[project\nname = ' });
    rejected(root, /project src\/analysts\/sdk: project convention: pyproject\.toml is malformed/);
  });

  it('rejects a missing project table, name, requires-python, and build system', () => {
    for (const [options, pattern] of [
      [{ raw: '[tool.x]\na = 1\n' }, /\[project\] table is missing/],
      [{ raw: '[project]\nrequires-python = ">=3.11"\n[build-system]\nrequires = ["h"]\nbuild-backend = "h"\n' }, /name must be a static/],
      [{ name: 'sdk', requiresPython: null }, /requires-python is missing/],
      [{ name: 'sdk', build: false }, /\[build-system\] with requires and build-backend is missing/],
    ]) {
      const root = repository();
      project(root, 'src/analysts/sdk', options);
      rejected(root, pattern);
    }
  });

  it('rejects an incompatible or unsupported requires-python', () => {
    let root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk', requiresPython: '>=3.14' });
    rejected(root, /runtime compatibility: requires-python >=3\.14 excludes the installed interpreter 3\.13\.5/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk', requiresPython: '<3.13' });
    rejected(root, /runtime compatibility/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk', requiresPython: 'banana' });
    rejected(root, /not a supported specifier/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk', requiresPython: '>=3.11,<3.14,!=3.12.*' });
    assert.equal(discover(root).status, 0);
  });

  it('requires a committed lock', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' }, { lock: false });
    rejected(root, /locked sync: uv\.lock is missing/);
  });

  it('rejects workspace and layout declarations', () => {
    for (const [extra, pattern] of [
      ['[tool.uv.workspace]\nmembers = ["x"]', /\[tool\.uv\.workspace\] is not supported/],
      ['[tool.uv]\npackage = false', /package = false/],
    ]) {
      const root = repository();
      project(root, 'src/analysts/sdk', { name: 'sdk', extra });
      rejected(root, pattern);
    }
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', {
      name: 'a', dependencies: ['sdk'], extra: '[tool.uv.sources]\nsdk = { workspace = true }',
    });
    rejected(root, /uses workspace/);
  });

  it('rejects non-path sources and sources missing from requirements', () => {
    let root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', {
      name: 'a', dependencies: ['x'], extra: '[tool.uv.sources]\nx = { git = "https://example.invalid/x" }',
    });
    rejected(root, /must be a path to a recognized project/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', sources: { sdk: '../../sdk' } });
    rejected(root, /not in the project requirements/);
  });

  it('rejects a missing local project', () => {
    const root = repository();
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['sdk'], sources: { sdk: '../../sdk' } });
    rejected(root, /local dependency 'sdk' points to src\/analysts\/sdk, which has no project/);
  });

  it('rejects local dependencies outside the repository and the permitted ownership roots', () => {
    let root = repository();
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['x'], sources: { x: '../../../../../x' } });
    rejected(root, /points outside the repository/);
    root = repository();
    project(root, 'src/analysts/manager', { name: 'manager' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['manager'], sources: { manager: '../../manager' } });
    rejected(root, /outside the permitted ownership roots/);
    root = repository();
    project(root, 'src/analysts/capabilities/b/deep', { name: 'deep' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['deep'], sources: { deep: '../b/deep' } });
    rejected(root, /outside the permitted ownership roots/);
  });

  it('rejects a dependency whose name differs from its project and one that is itself invalid', () => {
    let root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['other'], sources: { other: '../../sdk' } });
    rejected(root, /points to project 'sdk'/);
    root = repository();
    project(root, 'src/analysts/sdk', { name: 'sdk' }, { lock: false });
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['sdk'], sources: { sdk: '../../sdk' } });
    const result = rejected(root, /not a valid recognized project/);
    assert.match(result.stderr, /uv\.lock is missing/);
  });

  it('rejects duplicate identities', () => {
    const root = repository();
    project(root, 'src/analysts/sdk', { name: 'Shared_Name' });
    project(root, 'src/analysts/capabilities/a', { name: 'shared-name' });
    rejected(root, /project name 'shared-name' duplicates src\/analysts\/capabilities\/a/);
  });

  it('rejects dependency cycles once', () => {
    const root = repository();
    project(root, 'src/analysts/capabilities/a', { name: 'a', dependencies: ['b'], sources: { b: '../b' } });
    project(root, 'src/analysts/capabilities/b', { name: 'b', dependencies: ['a'], sources: { a: '../a' } });
    const result = rejected(root, /local dependencies form a cycle: .*capabilities\/a -> .*capabilities\/b -> .*capabilities\/a/);
    assert.equal(result.stderr.match(/cycle/g).length, 1);
  });
});
