import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const WORKFLOW_JOB_NAMES = new Map([
  ['pre_activation', 'Validate reconciliation prerequisites'],
  ['activation', 'Authorize and initialize reconciliation'],
  ['synchronize', 'Synchronize issue twins and prepare dependency context'],
  ['agent', 'Infer OpenSpec change dependencies'],
  ['detection', 'Validate dependency inference output'],
  ['reconcile_openspec_dependencies', 'Reconcile dependencies and persist the checkpoint'],
  ['safe_outputs', 'Process remaining safe outputs'],
  ['conclusion', 'Report the reconciliation outcome'],
]);

export const AGENT_WORKFLOW_JOB_NAMES = new Map([
  ['activation', 'Prepare the agent session'],
  ['agent', 'Run the OpenSpec agent'],
  ['detection', 'Check the agent changes for threats'],
  ['safe_outputs', 'Push the checkpoint to the pull request'],
  ['wake_controller', 'Wake the OpenSpec orchestrator'],
  ['conclusion', 'Report the agent session outcome'],
]);

// Generated lock files whose compiler-generated jobs get readable display names.
export const NAMED_WORKFLOWS = new Map([
  ['openspec-prepare.lock.yml', WORKFLOW_JOB_NAMES],
  ['openspec-agent.lock.yml', AGENT_WORKFLOW_JOB_NAMES],
]);

const WORKFLOWS_ROOT = path.join(process.cwd(), '.github', 'workflows');

export function applyWorkflowJobNames(source, jobNames = WORKFLOW_JOB_NAMES) {
  const newline = source.includes('\r\n') ? '\r\n' : '\n';
  const lines = source.split(/\r?\n/);
  const jobsIndex = lines.findIndex((line) => line === 'jobs:');
  if (jobsIndex === -1) {
    throw new Error('Generated workflow does not contain a top-level jobs map.');
  }

  const occurrences = new Map([...jobNames.keys()].map((job) => [job, 0]));
  const insertions = [];

  for (let index = jobsIndex + 1; index < lines.length; index += 1) {
    const header = /^  ([A-Za-z0-9_-]+):$/.exec(lines[index]);
    if (!header) {
      if (lines[index] !== '' && !lines[index].startsWith(' ')) break;
      continue;
    }

    const [jobId, expectedName] = [header[1], jobNames.get(header[1])];
    if (expectedName === undefined) continue;
    occurrences.set(jobId, occurrences.get(jobId) + 1);

    let end = index + 1;
    while (end < lines.length && !/^  [A-Za-z0-9_-]+:$/.test(lines[end])) {
      if (lines[end] !== '' && !lines[end].startsWith(' ')) break;
      end += 1;
    }
    const nameLines = [];
    for (let candidate = index + 1; candidate < end; candidate += 1) {
      const name = /^    name: (.+)$/.exec(lines[candidate]);
      if (name) nameLines.push({ index: candidate, value: name[1] });
    }
    if (nameLines.length > 1) {
      throw new Error(`Generated job "${jobId}" contains multiple display names.`);
    }
    if (nameLines.length === 1) {
      if (nameLines[0].value !== expectedName) {
        throw new Error(
          `Generated job "${jobId}" has unexpected display name "${nameLines[0].value}".`,
        );
      }
      continue;
    }
    insertions.push({ index: index + 1, line: `    name: ${expectedName}` });
  }

  for (const [jobId, count] of occurrences) {
    if (count !== 1) {
      throw new Error(`Expected generated job "${jobId}" exactly once, found ${count}.`);
    }
  }
  for (const insertion of insertions.reverse()) {
    lines.splice(insertion.index, 0, insertion.line);
  }
  return lines.join(newline);
}

async function main(argv) {
  const check = argv.includes('--check');
  const positional = argv.filter((argument) => argument !== '--check');
  if (positional.length > 1) {
    throw new Error('Usage: node openspec-change-workflow-names.mjs [--check] [workflow-path]');
  }
  const targets = positional.length === 1
    ? [path.resolve(positional[0])]
    : [...NAMED_WORKFLOWS.keys()].map((file) => path.join(WORKFLOWS_ROOT, file));
  for (const workflowPath of targets) {
    const jobNames = NAMED_WORKFLOWS.get(path.basename(workflowPath));
    if (!jobNames) throw new Error(`No job names are defined for ${workflowPath}`);
    const source = await readFile(workflowPath, 'utf8');
    const normalized = applyWorkflowJobNames(source, jobNames);
    if (check) {
      if (normalized !== source) {
        throw new Error(`Generated workflow job names are not normalized: ${workflowPath}`);
      }
      continue;
    }
    if (normalized !== source) {
      await writeFile(workflowPath, normalized);
    }
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
