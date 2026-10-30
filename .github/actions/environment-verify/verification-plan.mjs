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

import { SAMPLE_TOOLING_FILE, TOOLING_FILE, classify } from './verification-scope.mjs';

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
  const known = ['$comment', 'version', 'check', 'order', 'triggers', 'covers', 'alwaysRun', 'sdkPinOf', 'browserTests', 'budgetMinutes', 'tools', 'shards'];
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
  if (data.budgetMinutes !== undefined && (!Number.isInteger(data.budgetMinutes) || data.budgetMinutes < 1 || data.budgetMinutes > 360)) {
    problems.push('budgetMinutes must be a whole number from 1 to 360');
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

// The test projects (*Tests.csproj) of a .NET component, relative to its folder, outside bin/ and obj/ like the action's
// own search.
function testProjects(root, componentRoot) {
  const found = [];
  const visit = (relative) => {
    let entries;
    try {
      entries = readdirSync(path.join(root, componentRoot, relative), { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      const child = relative ? `${relative}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        if (entry.name !== 'bin' && entry.name !== 'obj') visit(child);
      } else if (entry.isFile() && entry.name.endsWith('Tests.csproj')) {
        found.push(child);
      }
    }
  };
  visit('');
  return found.sort();
}

export const DEFAULT_UNIT_MINUTES = 10;
// The Python projects, the environment action tests, and the Markdown check always run in the first shard.
export const MAIN_MINUTES = 5;

// Packs the work of the selected components into count shards by estimated minutes: a declared shard of a .NET
// component in all-tests scope is one unit, the rest of each component another. Units go, largest first, to the shard
// with the least work so far (the first on a tie), so the result is deterministic.
export function packShards(components, count) {
  const units = [];
  for (const component of components.filter((item) => item.selected)) {
    if (component.kind === 'dotnet-solution' && component.tests === 'all') {
      const assigned = new Set(component.excludedTests);
      for (const shard of component.shards.filter((item) => item.name !== 'default' && item.runner === 'linux-x64')) {
        const tests = shard.tests.filter((test) => component.testProjects.includes(test) && !assigned.has(test));
        tests.forEach((test) => assigned.add(test));
        if (tests.length) units.push({ root: component.root, name: shard.name, tests, minutes: shard.budgetMinutes ?? DEFAULT_UNIT_MINUTES });
      }
      units.push({
        root: component.root, name: 'default', tests: component.testProjects.filter((test) => !assigned.has(test)),
        minutes: component.budgetMinutes ?? DEFAULT_UNIT_MINUTES,
      });
    } else {
      units.push({ root: component.root, name: 'default', tests: null, minutes: component.budgetMinutes ?? DEFAULT_UNIT_MINUTES });
    }
  }
  const bins = Array.from({ length: count }, (_, index) => ({ index: index + 1, main: index === 0, minutes: index === 0 ? MAIN_MINUTES : 0, units: [] }));
  const ordered = [...units].sort((a, b) => b.minutes - a.minutes || a.root.localeCompare(b.root) || a.name.localeCompare(b.name));
  for (const unit of ordered) {
    const target = bins.reduce((best, bin) => (bin.minutes < best.minutes ? bin : best), bins[0]);
    target.units.push(unit);
    target.minutes += unit.minutes;
  }
  return bins;
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

export function buildPlan({ root, mode, changedFiles, shard = '' }) {
  if (!['task', 'phase', 'finalize'].includes(mode)) throw new Error(`unknown mode ${mode}`);
  let shardIndex = 0;
  let shardCount = 0;
  if (shard) {
    const match = /^([1-9]\d*)\/([1-9]\d*)$/.exec(shard);
    if (!match || Number(match[1]) > Number(match[2]) || Number(match[2]) > 20) throw new Error(`invalid shard ${shard}; expected <index>/<count> with 1 <= index <= count <= 20`);
    shardIndex = Number(match[1]);
    shardCount = Number(match[2]);
  }
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
    const projects = component.kind === 'dotnet-solution' ? testProjects(root, component.root) : [];
    // Test projects of a shard that needs another runner never run on this one.
    const unrun = declaredShards.filter((shard) => shard.runner !== 'linux-x64');
    const excludedTests = [...new Set(unrun.flatMap((shard) => shard.tests))].filter((test) => projects.includes(test)).sort();
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
      budgetMinutes: declaration.budgetMinutes ?? null,
      testProjects: projects,
      excludedTests,
      unrun: unrun.map((shard) => ({ name: shard.name, runner: shard.runner, tests: shard.tests })),
      shards: [...declaredShards, { name: 'default', tests: [], runner: 'linux-x64', docker: (declaration.tools ?? []).includes('docker'), budgetMinutes: declaration.budgetMinutes ?? null }],
    });
  }
  planned.sort((a, b) => a.order - b.order || a.root.localeCompare(b.root));

  // What this execution runs: every selected component with all its tests, or, for a shard, only the units packed into
  // it; the Python projects, environment action tests, and the Markdown check run in the first shard only.
  const shards = shardCount ? packShards(planned, shardCount) : [];
  const own = shardCount ? shards[shardIndex - 1] : null;
  for (const component of planned) {
    const units = own ? own.units.filter((unit) => unit.root === component.root) : [];
    component.run = component.selected && (!own || units.length > 0);
    if (!component.run) {
      component.allowedTests = '';
    } else if (own && units.every((unit) => unit.tests !== null)) {
      component.allowedTests = units.flatMap((unit) => unit.tests).join(';');
    } else if (component.excludedTests.length) {
      component.allowedTests = component.testProjects.filter((test) => !component.excludedTests.includes(test)).join(';');
    } else {
      component.allowedTests = '*';
    }
  }

  const python = {
    analysts: pythonArea(root, mode, changedFiles, 'analysts', scope.documentationOnly),
    agents: pythonArea(root, mode, changedFiles, 'agents', scope.documentationOnly),
  };
  const covered = [
    ...STATIC_COVERED,
    TOOLING_FILE,
    SAMPLE_TOOLING_FILE,
    ...planned.flatMap((component) => component.covers.map((prefix) => new RegExp(`^${escapeRegExp(prefix)}`))),
    ...Object.values(python).flatMap((area) => area.covered.map((projectRoot) => new RegExp(`^${escapeRegExp(projectRoot)}/`))),
  ];
  const uncovered = changed.filter((file) => declarationChanges.includes(file) || !covered.some((pattern) => pattern.test(file)));
  // A selected component with tests for another runner holds the change for review: those tests did not run.
  for (const component of planned.filter((item) => item.selected && item.unrun.length > 0)) {
    const file = `${component.root}/${DECLARATION}`;
    if (!uncovered.includes(file)) uncovered.push(file);
  }

  const tools = new Set(['node', 'markdown']);
  for (const component of planned.filter((item) => item.selected)) for (const tool of component.tools) tools.add(tool);
  if (Object.values(python).some((area) => area.selected.length > 0 || area.status !== 0)) tools.add('analyst-media');

  return {
    version: 1,
    mode,
    documentationOnly: scope.documentationOnly,
    scope: `${scope.documentationOnly ? 'documentation-only' : 'full'}: ${scope.reason}`,
    errors,
    shard: shardCount ? `${shardIndex}/${shardCount}` : '',
    main: !own || own.main,
    shards,
    components: planned,
    python,
    uncovered,
    tools: [...tools].sort(),
  };
}

// The matrix for a workflow that runs the shards side by side: one entry per shard with work (the first shard always,
// for the shared checks), each with its shard input and a name that says what it verifies.
export function shardMatrix(plan) {
  const label = (unit) => `${path.posix.basename(unit.root)}${unit.name === 'default' ? '' : ` ${unit.name}`}`;
  const include = plan.shards
    .filter((bin) => bin.main || bin.units.length > 0)
    .map((bin) => ({
      name: [...bin.units.map(label), ...(bin.main ? ['shared checks'] : [])].join(', '),
      shard: `${bin.index}/${plan.shards.length}`,
    }));
  return { include };
}

export function main(argv) {
  const usage = 'usage: verification-plan.mjs --root <workspace> --mode <task|phase|finalize> [--changed-files <list>] [--shard <index>/<count> | --matrix <count>]';
  const options = {};
  for (let index = 0; index < argv.length; index += 2) {
    if (!['--root', '--mode', '--changed-files', '--shard', '--matrix'].includes(argv[index]) || argv[index + 1] === undefined) {
      console.error(usage);
      return 2;
    }
    options[argv[index].slice(2)] = argv[index + 1];
  }
  if (!options.root || !options.mode || (options.shard && options.matrix)) {
    console.error(usage);
    return 2;
  }
  try {
    if (options.matrix) {
      const plan = buildPlan({ root: options.root, mode: options.mode, changedFiles: options['changed-files'], shard: `1/${options.matrix}` });
      console.log(JSON.stringify(shardMatrix(plan)));
      return 0;
    }
    console.log(JSON.stringify(buildPlan({ root: options.root, mode: options.mode, changedFiles: options['changed-files'], shard: options.shard ?? '' }), null, 2));
    return 0;
  } catch (error) {
    console.error(error.message);
    return 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = main(process.argv.slice(2));
