// Builds the verification plan: the components a change calls for, how far each is tested, what stays uncovered, and
// which tool groups and shards the selected work needs. It is the only selection logic of environment-verify.
// Usage: node verification-plan.mjs --root <workspace> --mode <task|phase|finalize> [--changed-files <list>]
// Prints the plan as JSON; exits 0 when the plan could be built (its errors list then names invalid declarations).
//
// Components are discovered by convention under src/: a folder with a .NET solution (*.slnx or *.sln) is a
// dotnet-solution component, a folder with package.json an npm-package component. Python projects keep the
// conventions and selection of analyst-projects.py (areas analysts and agents). A component may declare more in a
// verification.json next to its manifest; any change to such a declaration tests the component in full and leaves the
// declaration uncovered, so a person reviews it.
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { classify } from './verification-scope.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));

export const DECLARATION = 'verification.json';
export const TOOL_GROUPS = ['dotnet', 'node', 'docker', 'softhsm', 'kiota', 'playwright', 'analyst-media', 'markdown'];
export const RUNNERS = ['linux-x64', 'linux-x64-gpu', 'windows-x64', 'macos-arm64'];
const KIND_TOOLS = { 'dotnet-solution': ['dotnet'], 'npm-package': ['node'] };
const DEFAULT_ORDER = 100;
const SKIPPED = new Set([
  'node_modules', 'bin', 'obj', '.venv', 'venv', 'dist', 'build', '__pycache__', 'site-packages', 'TestResults',
  'test-results', 'playwright-report',
]);
// Paths that need no check: repository metadata, Spec Kit artifacts, Markdown (the Markdown check), and the
// environment actions (their own tests and the merge self-test).
export const STATIC_COVERED = [
  /\.md$/, /^specs\//, /^\.gitignore$/, /^\.gitattributes$/, /^\.editorconfig$/, /^\.github\/actions\/environment-(setup|verify)\//,
];
const FOLDER_PREFIX = /^(src|contracts)\/(?:[^/.][^/]*\/)*$/;
const RELATIVE_FILE = /^(?!\/)(?!.*(?:^|\/)\.\.?(?:\/|$))[^\\:*?"<>|]+$/;

const escapeRegExp = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

function discover(root) {
  const components = [];
  const errors = [];
  const visit = (relative) => {
    let entries;
    try {
      entries = readdirSync(path.join(root, relative), { withFileTypes: true });
    } catch {
      return;
    }
    const files = entries.filter((entry) => entry.isFile()).map((entry) => entry.name).sort();
    const solutions = files.filter((name) => /\.slnx?$/.test(name));
    if (solutions.length > 1) {
      errors.push(`${relative}: several .NET solutions (${solutions.join(', ')}); keep one solution per component folder`);
    } else if (solutions.length === 1) {
      components.push({ kind: 'dotnet-solution', root: relative, manifest: solutions[0] });
    }
    if (files.includes('package.json')) components.push({ kind: 'npm-package', root: relative, manifest: 'package.json' });
    for (const entry of entries) {
      if (entry.isDirectory() && !entry.name.startsWith('.') && !SKIPPED.has(entry.name)) visit(`${relative}/${entry.name}`);
    }
  };
  visit('src');
  const roots = new Map();
  for (const component of components) {
    if (roots.has(component.root)) {
      errors.push(`${component.root}: a ${roots.get(component.root)} and a ${component.kind} share one folder; give each component its own folder`);
    }
    roots.set(component.root, component.kind);
  }
  return { components, errors };
}

const isStringArray = (value) => Array.isArray(value) && value.every((item) => typeof item === 'string');

// Validates a verification.json; returns the settings or null and adds every problem to errors.
export function readDeclaration(root, component, errors) {
  const file = `${component.root}/${DECLARATION}`;
  if (!existsSync(path.join(root, file))) return {};
  let data;
  try {
    data = JSON.parse(readFileSync(path.join(root, file), 'utf8'));
  } catch (error) {
    errors.push(`${file}: not valid JSON (${error.message})`);
    return null;
  }
  const problems = [];
  if (data === null || typeof data !== 'object' || Array.isArray(data)) {
    errors.push(`${file}: must be a JSON object`);
    return null;
  }
  const known = ['$comment', 'version', 'check', 'order', 'triggers', 'covers', 'alwaysRun', 'sdkPinOf', 'browserTests', 'tools', 'shards'];
  for (const key of Object.keys(data)) if (!known.includes(key)) problems.push(`unknown setting "${key}"`);
  if (data.version !== 1) problems.push('version must be 1');
  if (data.check !== undefined && (typeof data.check !== 'string' || !data.check.trim() || data.check.length > 80 || data.check.includes(','))) {
    problems.push('check must be a name of at most 80 characters without commas');
  }
  if (data.order !== undefined && !Number.isInteger(data.order)) problems.push('order must be an integer');
  for (const key of ['triggers', 'covers']) {
    if (data[key] === undefined) continue;
    if (!isStringArray(data[key]) || data[key].some((prefix) => !FOLDER_PREFIX.test(prefix))) {
      problems.push(`${key} must list folders under src/ or contracts/, each ending with "/"`);
    }
  }
  if (data.alwaysRun !== undefined) {
    if (component.kind !== 'dotnet-solution') problems.push('alwaysRun applies only to a .NET solution');
    else if (!isStringArray(data.alwaysRun) || data.alwaysRun.some((file_) => !RELATIVE_FILE.test(file_) || !file_.endsWith('.csproj'))) {
      problems.push('alwaysRun must list test project files relative to the component folder');
    }
  }
  if (data.sdkPinOf !== undefined) {
    if (component.kind !== 'dotnet-solution') problems.push('sdkPinOf applies only to a .NET solution');
    else if (typeof data.sdkPinOf !== 'string' || !RELATIVE_FILE.test(data.sdkPinOf) || !data.sdkPinOf.endsWith('global.json')) {
      problems.push('sdkPinOf must name a global.json by its repository path');
    }
  }
  if (data.browserTests !== undefined) {
    if (component.kind !== 'npm-package') problems.push('browserTests applies only to an npm package');
    else if (typeof data.browserTests !== 'string' || !/^(?:[^/.][^/]*\/)+$/.test(data.browserTests)) {
      problems.push('browserTests must name a folder relative to the component folder, ending with "/"');
    }
  }
  if (data.tools !== undefined && (!isStringArray(data.tools) || data.tools.some((tool) => !TOOL_GROUPS.includes(tool)))) {
    problems.push(`tools must list tool groups out of ${TOOL_GROUPS.join(', ')}`);
  }
  if (data.shards !== undefined) {
    if (!Array.isArray(data.shards)) {
      problems.push('shards must be a list');
    } else {
      const names = new Set();
      for (const shard of data.shards) {
        const label = `shard ${JSON.stringify(shard?.name ?? '?')}`;
        if (shard === null || typeof shard !== 'object' || Array.isArray(shard)) { problems.push('every shard must be an object'); continue; }
        for (const key of Object.keys(shard)) if (!['name', 'tests', 'runner', 'docker', 'budgetMinutes'].includes(key)) problems.push(`${label}: unknown setting "${key}"`);
        if (typeof shard.name !== 'string' || !/^[a-z0-9][a-z0-9-]{0,39}$/.test(shard.name) || shard.name === 'default') {
          problems.push(`${label}: name must be lowercase letters, digits, and hyphens, and not "default"`);
        } else if (names.has(shard.name)) {
          problems.push(`${label}: duplicate name`);
        }
        names.add(shard.name);
        if (!isStringArray(shard.tests) || shard.tests.length === 0 || shard.tests.some((file_) => !RELATIVE_FILE.test(file_))) {
          problems.push(`${label}: tests must list test projects relative to the component folder`);
        }
        if (shard.runner !== undefined && !RUNNERS.includes(shard.runner)) problems.push(`${label}: runner must be one of ${RUNNERS.join(', ')}`);
        if (shard.docker !== undefined && typeof shard.docker !== 'boolean') problems.push(`${label}: docker must be true or false`);
        if (shard.budgetMinutes !== undefined && (!Number.isInteger(shard.budgetMinutes) || shard.budgetMinutes < 1 || shard.budgetMinutes > 360)) {
          problems.push(`${label}: budgetMinutes must be a whole number from 1 to 360`);
        }
      }
    }
  }
  for (const problem of problems) errors.push(`${file}: ${problem}`);
  return problems.length ? null : data;
}

function pythonArea(root, mode, changedFiles, area, documentationOnly) {
  const interpreter = process.env.SOCALYTICS_PYTHON || 'python3';
  const helper = process.env.ANALYST_PROJECTS || path.join(here, 'analyst-projects.py');
  const args = [helper, 'select', ...(area === 'agents' ? ['--area', 'agents'] : []), '--root', root, '--mode', mode, '--changed-files', changedFiles ?? ''];
  const result = spawnSync(interpreter, args, { encoding: 'utf8' });
  const status = result.error ? 127 : result.status ?? 1;
  const entry = { interpreter, status, error: (result.stderr ?? '').trim() || (result.error ? String(result.error.message) : ''), selected: [], covered: [] };
  if (status === 0) {
    try {
      const output = JSON.parse(result.stdout);
      entry.selected = documentationOnly ? [] : output.projects.map((project) => project.root);
      entry.covered = output.covered;
    } catch (error) {
      entry.status = 1;
      entry.error = `the project helper printed no valid JSON (${error.message})`;
    }
  }
  return entry;
}

export function buildPlan({ root, mode, changedFiles }) {
  if (!['task', 'phase', 'finalize'].includes(mode)) throw new Error(`unknown mode ${mode}`);
  let changed = [];
  try {
    if (changedFiles) changed = [...new Set(readFileSync(changedFiles, 'utf8').split(/\r?\n/).map((line) => line.trim()).filter(Boolean))];
  } catch {
    changed = [];
  }
  const scope = classify(root, changedFiles);
  const { components, errors } = discover(root);
  const declarationChanges = changed.filter((file) => file.startsWith('src/') && path.posix.basename(file) === DECLARATION);
  const planned = [];
  for (const component of components) {
    const declaration = readDeclaration(root, component, errors);
    if (declaration === null) continue;
    const prefixes = [`${component.root}/`, ...(declaration.covers ?? [])];
    const triggers = [...prefixes, ...(declaration.triggers ?? [])];
    const declarationChanged = declarationChanges.includes(`${component.root}/${DECLARATION}`);
    const hit = changed.find((file) => triggers.some((prefix) => file.startsWith(prefix)));
    let reason = '';
    if (scope.documentationOnly) reason = '';
    else if (mode === 'finalize') reason = 'finalize';
    else if (declarationChanged) reason = `declaration change: ${component.root}/${DECLARATION}`;
    else if (hit) reason = `changed: ${hit}`;
    const always = declaration.alwaysRun ?? [];
    const declaredShards = (declaration.shards ?? []).map((shard) => ({
      name: shard.name, tests: shard.tests, runner: shard.runner ?? 'linux-x64', docker: shard.docker ?? false, budgetMinutes: shard.budgetMinutes ?? null,
    }));
    planned.push({
      kind: component.kind,
      root: component.root,
      manifest: component.manifest,
      check: declaration.check ?? `${component.root} build and {tests}`,
      order: declaration.order ?? DEFAULT_ORDER,
      declared: Object.keys(declaration).length > 0,
      covers: prefixes,
      triggers,
      selected: reason !== '',
      reason,
      tests: mode === 'task' && !declarationChanged ? 'affected' : 'all',
      alwaysRun: always,
      alwaysRunPattern: always.length ? `^(${always.map(escapeRegExp).join('|')})$` : '',
      sdkPinOf: declaration.sdkPinOf ?? '',
      browserTests: declaration.browserTests ?? '',
      tools: [...new Set([...KIND_TOOLS[component.kind], ...(declaration.tools ?? [])])].sort(),
      shards: [...declaredShards, { name: 'default', tests: [], runner: 'linux-x64', docker: (declaration.tools ?? []).includes('docker'), budgetMinutes: null }],
    });
  }
  planned.sort((a, b) => a.order - b.order || a.root.localeCompare(b.root));

  const python = {
    analysts: pythonArea(root, mode, changedFiles, 'analysts', scope.documentationOnly),
    agents: pythonArea(root, mode, changedFiles, 'agents', scope.documentationOnly),
  };
  const covered = [
    ...STATIC_COVERED,
    ...planned.flatMap((component) => component.covers.map((prefix) => new RegExp(`^${escapeRegExp(prefix)}`))),
    ...Object.values(python).flatMap((area) => area.covered.map((projectRoot) => new RegExp(`^${escapeRegExp(projectRoot)}/`))),
  ];
  const uncovered = changed.filter((file) => declarationChanges.includes(file) || !covered.some((pattern) => pattern.test(file)));

  const tools = new Set(['node', 'markdown']);
  for (const component of planned.filter((item) => item.selected)) for (const tool of component.tools) tools.add(tool);
  if (Object.values(python).some((area) => area.selected.length > 0 || area.status !== 0)) tools.add('analyst-media');

  return {
    version: 1,
    mode,
    documentationOnly: scope.documentationOnly,
    scope: `${scope.documentationOnly ? 'documentation-only' : 'full'}: ${scope.reason}`,
    errors,
    components: planned,
    python,
    uncovered,
    tools: [...tools].sort(),
  };
}

export function main(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 2) {
    if (!['--root', '--mode', '--changed-files'].includes(argv[index]) || argv[index + 1] === undefined) {
      console.error('usage: verification-plan.mjs --root <workspace> --mode <task|phase|finalize> [--changed-files <list>]');
      return 2;
    }
    options[argv[index].slice(2)] = argv[index + 1];
  }
  if (!options.root || !options.mode) {
    console.error('usage: verification-plan.mjs --root <workspace> --mode <task|phase|finalize> [--changed-files <list>]');
    return 2;
  }
  try {
    console.log(JSON.stringify(buildPlan({ root: options.root, mode: options.mode, changedFiles: options['changed-files'] }), null, 2));
    return 0;
  } catch (error) {
    console.error(error.message);
    return 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = main(process.argv.slice(2));
