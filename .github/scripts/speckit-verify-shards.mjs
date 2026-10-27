import { appendFileSync, cpSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// Spec Kit implement verify, job "Record the phase verification": combines the verification of every shard (one
// downloaded artifact folder per shard, named after its shard number) into the one phase verification the repair flow
// reads: each shard folder under shards/<number>/, one verification.log with every shard's log under a heading, the
// change list, and the deduplicated flaky tests of all shards.

const read = (file) => (existsSync(file) ? readFileSync(file, 'utf8') : '');

export function combineShards(sourceDir, targetDir) {
  const folders = existsSync(sourceDir)
    ? readdirSync(sourceDir, { withFileTypes: true }).filter((entry) => entry.isDirectory()).map((entry) => entry.name)
    : [];
  const shards = folders
    .map((name) => ({ name, number: Number(/(\d+)$/.exec(name)?.[1] ?? Number.NaN) }))
    .filter((shard) => Number.isInteger(shard.number))
    .sort((a, b) => a.number - b.number);
  mkdirSync(targetDir, { recursive: true });
  const logs = [];
  const flaky = [];
  let changed = '';
  for (const shard of shards) {
    const folder = path.join(sourceDir, shard.name);
    cpSync(folder, path.join(targetDir, 'shards', String(shard.number)), { recursive: true });
    const outcome = read(path.join(folder, 'outcome.txt')).trim() || 'unknown';
    const checks = read(path.join(folder, 'checks.txt')).trim() || 'no check';
    logs.push(`=== Shard ${shard.number} (${outcome}): ${checks} ===\n${read(path.join(folder, 'verification.log')).trimEnd()}\n`);
    for (const name of read(path.join(folder, 'flaky.txt')).split(',').map((item) => item.trim()).filter(Boolean)) {
      if (!flaky.includes(name)) flaky.push(name);
    }
    changed ||= read(path.join(folder, 'changed-files.txt'));
  }
  writeFileSync(path.join(targetDir, 'verification.log'), logs.join('\n'));
  if (changed) writeFileSync(path.join(targetDir, 'changed-files.txt'), changed);
  return { shards: shards.length, flaky: flaky.join(',') };
}

export function main(argv, { env = process.env, log = console.log } = {}) {
  const [command, sourceDir, targetDir] = argv;
  if (command !== 'combine' || !sourceDir || !targetDir) {
    log('Usage: speckit-verify-shards.mjs combine <shard artifacts folder> <combined folder>');
    return 2;
  }
  const result = combineShards(sourceDir, targetDir);
  if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `shards=${result.shards}\nflaky=${result.flaky}\n`);
  log(`Combined ${result.shards} shard verification(s)${result.flaky ? `; flaky: ${result.flaky}` : ''}.`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = main(process.argv.slice(2));
