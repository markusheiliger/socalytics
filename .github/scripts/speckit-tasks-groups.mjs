import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import { describeParallelGroups } from './speckit-implement-core.mjs';

// Prints the phases of a spec's tasks.md with the order its unticked tasks will run in, exactly as Spec Kit implement
// phase orders them, so `[P]` groups can be reviewed (speckit.gha.refine). Read-only.

export class GroupsUsageError extends Error {}

export function renderGroups({ phases, warnings }) {
  const lines = [];
  for (const phase of phases) {
    lines.push(`## ${phase.phase ?? 'Tasks'} (${phase.open} open, ${phase.done} done)`);
    for (const entry of phase.order) {
      const under = entry.heading && entry.heading !== phase.phase ? ` (under "${entry.heading}")` : '';
      lines.push(`- ${entry.tasks.join(', ')}${entry.parallel ? ' in parallel' : ''}${under}`);
    }
    if (phase.order.length === 0) lines.push('- Nothing open');
    lines.push('');
  }
  lines.push(warnings.length > 0 ? 'Warnings:' : 'Warnings: none');
  for (const warning of warnings) lines.push(`- ${warning}`);
  return `${lines.join('\n')}\n`;
}

export function main(argv, { cwd = process.cwd(), log = (text) => process.stdout.write(text) } = {}) {
  const json = argv.includes('--json');
  const target = argv.filter((arg) => arg !== '--json');
  if (target.length !== 1) throw new GroupsUsageError('Usage: speckit-tasks-groups.mjs <spec folder | path to tasks.md> [--json]');
  const candidates = [path.resolve(cwd, target[0]), path.resolve(cwd, target[0], 'tasks.md'), path.resolve(cwd, 'specs', target[0], 'tasks.md'), path.resolve(cwd, 'specs', 'archive', target[0], 'tasks.md')];
  const file = candidates.find((candidate) => candidate.endsWith('.md') && existsSync(candidate));
  if (!file) throw new GroupsUsageError(`No tasks.md found for ${target[0]}`);
  const groups = describeParallelGroups(readFileSync(file, 'utf8'));
  log(json ? `${JSON.stringify(groups, null, 2)}\n` : renderGroups(groups));
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = error instanceof GroupsUsageError ? 2 : 1;
  }
}
