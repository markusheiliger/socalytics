import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export const CHANGESET_START = '<!-- openspec-changeset:v1:start -->';
export const CHANGESET_END = '<!-- openspec-changeset:v1:end -->';

const KEBAB_CASE = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const ROOT_KEYS = new Set(['version', 'name', 'changes']);
const CHANGE_KEYS = new Set(['ref', 'dependsOn']);

function assertObject(value, path) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${path} must be an object`);
  }
}

function assertKnownKeys(value, allowed, path) {
  const unknown = Object.keys(value).filter((key) => !allowed.has(key));
  if (unknown.length > 0) {
    throw new Error(`${path} contains unknown field(s): ${unknown.join(', ')}`);
  }
}

function occurrences(value, search) {
  return value.split(search).length - 1;
}

export function extractChangesetJson(issueBody) {
  if (typeof issueBody !== 'string') {
    throw new Error('Issue body must be a string');
  }

  if (occurrences(issueBody, CHANGESET_START) !== 1 || occurrences(issueBody, CHANGESET_END) !== 1) {
    throw new Error('Issue body must contain exactly one changeset marker pair');
  }

  const start = issueBody.indexOf(CHANGESET_START) + CHANGESET_START.length;
  const end = issueBody.indexOf(CHANGESET_END, start);
  if (end < start) {
    throw new Error('Changeset end marker must follow the start marker');
  }

  let content = issueBody.slice(start, end).trim();
  const fenced = content.match(/^```json\s*\r?\n([\s\S]*?)\r?\n```$/i);
  if (fenced) {
    content = fenced[1].trim();
  }

  if (!content) {
    throw new Error('Changeset JSON block is empty');
  }

  return content;
}

export function parseChangesetIssue(issueBody) {
  const content = extractChangesetJson(issueBody);
  let parsed;
  try {
    parsed = JSON.parse(content);
  } catch (error) {
    throw new Error(`Changeset block contains invalid JSON: ${error.message}`);
  }

  return validateChangeset(parsed);
}

export function validateChangeset(value) {
  assertObject(value, 'Changeset');
  assertKnownKeys(value, ROOT_KEYS, 'Changeset');

  if (value.version !== 1) {
    throw new Error('Changeset version must be 1');
  }
  if (typeof value.name !== 'string' || !KEBAB_CASE.test(value.name)) {
    throw new Error('Changeset name must be a non-empty kebab-case identifier');
  }
  if (!Array.isArray(value.changes) || value.changes.length === 0) {
    throw new Error('Changeset changes must be a non-empty array');
  }

  const refs = new Set();
  const normalizedChanges = value.changes.map((change, index) => {
    const path = `Changeset changes[${index}]`;
    assertObject(change, path);
    assertKnownKeys(change, CHANGE_KEYS, path);

    if (typeof change.ref !== 'string' || !KEBAB_CASE.test(change.ref)) {
      throw new Error(`${path}.ref must be a non-empty kebab-case identifier`);
    }
    if (refs.has(change.ref)) {
      throw new Error(`Changeset contains duplicate change ref: ${change.ref}`);
    }
    refs.add(change.ref);

    if (!Array.isArray(change.dependsOn)) {
      throw new Error(`${path}.dependsOn must be an array`);
    }
    const dependencies = new Set();
    for (const dependency of change.dependsOn) {
      if (typeof dependency !== 'string' || !KEBAB_CASE.test(dependency)) {
        throw new Error(`${path}.dependsOn entries must be kebab-case identifiers`);
      }
      if (dependency === change.ref) {
        throw new Error(`Change ${change.ref} cannot depend on itself`);
      }
      if (dependencies.has(dependency)) {
        throw new Error(`Change ${change.ref} contains duplicate dependency: ${dependency}`);
      }
      dependencies.add(dependency);
    }

    return { ref: change.ref, dependsOn: [...dependencies].sort() };
  });

  for (const change of normalizedChanges) {
    for (const dependency of change.dependsOn) {
      if (!refs.has(dependency)) {
        throw new Error(`Change ${change.ref} depends on missing change: ${dependency}`);
      }
    }
  }

  const normalized = {
    version: 1,
    name: value.name,
    changes: normalizedChanges.sort((left, right) => left.ref.localeCompare(right.ref)),
  };
  assertAcyclic(normalized);
  return normalized;
}

export function assertAcyclic(changeset) {
  const dependencies = new Map(changeset.changes.map((change) => [change.ref, change.dependsOn]));
  const visited = new Set();
  const visiting = new Set();
  const path = [];

  function visit(ref) {
    if (visiting.has(ref)) {
      const cycleStart = path.indexOf(ref);
      throw new Error(`Changeset contains dependency cycle: ${[...path.slice(cycleStart), ref].join(' -> ')}`);
    }
    if (visited.has(ref)) {
      return;
    }

    visiting.add(ref);
    path.push(ref);
    for (const dependency of dependencies.get(ref)) {
      visit(dependency);
    }
    path.pop();
    visiting.delete(ref);
    visited.add(ref);
  }

  for (const change of changeset.changes) {
    visit(change.ref);
  }
}

export function canonicalizeChangeset(value) {
  return `${JSON.stringify(validateChangeset(value), null, 2)}\n`;
}

export function hashChangeset(value) {
  return createHash('sha256').update(canonicalizeChangeset(value)).digest('hex');
}

export function calculateRunnableFrontier(value, completedRefs = [], activeRefs = []) {
  const changeset = validateChangeset(value);
  const knownRefs = new Set(changeset.changes.map((change) => change.ref));
  const completed = new Set(completedRefs);
  const active = new Set(activeRefs);

  for (const ref of [...completed, ...active]) {
    if (!knownRefs.has(ref)) {
      throw new Error(`State references change outside the changeset: ${ref}`);
    }
  }

  return changeset.changes
    .filter((change) => !completed.has(change.ref) && !active.has(change.ref))
    .filter((change) => change.dependsOn.every((dependency) => completed.has(dependency)))
    .map((change) => change.ref);
}

export function transitiveDependencies(value, selectedRefs) {
  const changeset = validateChangeset(value);
  const dependencies = new Map(changeset.changes.map((change) => [change.ref, change.dependsOn]));
  const result = new Set();

  function collect(ref) {
    if (!dependencies.has(ref)) {
      throw new Error(`Selected change is outside the changeset: ${ref}`);
    }
    for (const dependency of dependencies.get(ref)) {
      if (!result.has(dependency)) {
        result.add(dependency);
        collect(dependency);
      }
    }
  }

  for (const ref of selectedRefs) {
    collect(ref);
  }
  return [...result].sort();
}

function escapeMermaidLabel(value) {
  return value.replaceAll('\\', '\\\\').replaceAll('"', '\\"');
}

export function renderMermaid(value) {
  const changeset = validateChangeset(value);
  const ids = new Map(changeset.changes.map((change, index) => [change.ref, `n${index}`]));
  const lines = ['graph TD'];

  for (const change of changeset.changes) {
    lines.push(`    ${ids.get(change.ref)}["${escapeMermaidLabel(change.ref)}"]`);
  }
  for (const change of changeset.changes) {
    for (const dependency of change.dependsOn) {
      lines.push(`    ${ids.get(dependency)} --> ${ids.get(change.ref)}`);
    }
  }
  return `${lines.join('\n')}\n`;
}

export function renderChangesetIssue(value) {
  const changeset = validateChangeset(value);
  return [
    `# OpenSpec changeset: ${changeset.name}`,
    '',
    'The JSON block is the authoritative dependency graph. The Mermaid diagram is generated for display.',
    '',
    CHANGESET_START,
    '```json',
    canonicalizeChangeset(changeset).trimEnd(),
    '```',
    CHANGESET_END,
    '',
    '```mermaid',
    renderMermaid(changeset).trimEnd(),
    '```',
    '',
  ].join('\n');
}

function readJsonFile(path) {
  if (!path) {
    throw new Error('A JSON file path is required');
  }
  return JSON.parse(readFileSync(resolve(path), 'utf8'));
}

function parseListArgument(value) {
  return value ? value.split(',').map((entry) => entry.trim()).filter(Boolean) : [];
}

function cli(argv) {
  const [command, file, ...argumentsList] = argv;
  const value = readJsonFile(file);
  switch (command) {
    case 'validate':
      process.stdout.write(canonicalizeChangeset(value));
      break;
    case 'render':
      process.stdout.write(renderChangesetIssue(value));
      break;
    case 'mermaid':
      process.stdout.write(renderMermaid(value));
      break;
    case 'frontier': {
      const completed = argumentsList.find((argument) => argument.startsWith('--completed='))?.slice(12);
      const active = argumentsList.find((argument) => argument.startsWith('--active='))?.slice(9);
      process.stdout.write(`${JSON.stringify(calculateRunnableFrontier(
        value,
        parseListArgument(completed),
        parseListArgument(active),
      ))}\n`);
      break;
    }
    default:
      throw new Error(`Unknown command: ${command ?? '<missing>'}`);
  }
}

const isEntrypoint = process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href;
if (isEntrypoint) {
  try {
    cli(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}