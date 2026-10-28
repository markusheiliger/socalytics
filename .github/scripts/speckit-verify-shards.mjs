import { appendFileSync, cpSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// Combines the verification of every shard (one downloaded artifact folder per shard, holding the shard input it ran in
// shard.txt) into one verification: each shard folder under shards/<number>/, one verification.log with every shard's
// log under a heading, the change list, the deduplicated flaky tests and uncovered paths of all shards, the checks in
// shard order, and the checks that failed. Spec Kit implement verify combines a phase verification this way; the merge
// and repair verifications pass the matrix they planned (`expect`), so a shard that is missing, reported twice, not
// planned, or did not finish fails the combined outcome with a named problem instead of passing unnoticed.

const read = (file) => (existsSync(file) ? readFileSync(file, 'utf8') : '');
const list = (text, separator = ',') => text.split(separator).map((item) => item.trim()).filter(Boolean);
const addAll = (target, items) => {
  for (const item of items) if (!target.includes(item)) target.push(item);
};

export function combineShards(sourceDir, targetDir, { expect = null } = {}) {
  const folders = existsSync(sourceDir)
    ? readdirSync(sourceDir, { withFileTypes: true }).filter((entry) => entry.isDirectory()).map((entry) => entry.name)
    : [];
  const shards = folders
    .map((name) => {
      const declared = /^(\d+)\/\d+$/.exec(read(path.join(sourceDir, name, 'shard.txt')).trim());
      return { name, number: Number(declared?.[1] ?? /(\d+)$/.exec(name)?.[1] ?? Number.NaN), label: declared?.[0] ?? '' };
    })
    .filter((shard) => Number.isInteger(shard.number))
    .sort((a, b) => a.number - b.number || a.name.localeCompare(b.name));
  mkdirSync(targetDir, { recursive: true });
  const logs = [];
  const flaky = [];
  const uncovered = [];
  const checks = [];
  const failed = [];
  const problems = [];
  let changed = '';
  for (const shard of shards) {
    const folder = path.join(sourceDir, shard.name);
    cpSync(folder, path.join(targetDir, 'shards', String(shard.number)), { recursive: true });
    const outcome = read(path.join(folder, 'outcome.txt')).trim() || 'unknown';
    const names = list(read(path.join(folder, 'checks.txt')));
    shard.outcome = outcome;
    logs.push(`=== Shard ${shard.label || shard.number} (${outcome}): ${names.join(', ') || 'no check'} ===\n${read(path.join(folder, 'verification.log')).trimEnd()}\n`);
    addAll(flaky, list(read(path.join(folder, 'flaky.txt'))));
    addAll(uncovered, list(read(path.join(folder, 'uncovered.txt'))));
    addAll(checks, names);
    // The action reports its checks in order and stops at the first that fails, so a failed shard's last check failed.
    if (outcome === 'failure' && names.length > 0) addAll(failed, [names.at(-1)]);
    changed ||= read(path.join(folder, 'changed-files.txt'));
  }
  if (expect) {
    const planned = (expect.include ?? []).map((entry) => ({ shard: String(entry.shard ?? ''), name: String(entry.name ?? '') }));
    if (planned.length === 0) problems.push('the verification planned no shard');
    for (const entry of planned) {
      const reported = shards.filter((shard) => shard.label === entry.shard);
      const title = `verification shard ${entry.shard}${entry.name ? ` (${entry.name})` : ''}`;
      if (reported.length === 0) problems.push(`${title} did not report`);
      else if (reported.length > 1) problems.push(`${title} reported ${reported.length} times`);
      else if (reported[0].outcome !== 'success' && !(reported[0].outcome === 'failure' && list(read(path.join(sourceDir, reported[0].name, 'checks.txt'))).length > 0)) {
        problems.push(`${title} did not finish (${reported[0].outcome})`);
      }
    }
    for (const shard of shards) {
      if (!planned.some((entry) => entry.shard === shard.label)) problems.push(`verification shard ${shard.label || shard.name} was not planned`);
    }
  }
  writeFileSync(path.join(targetDir, 'verification.log'), logs.join('\n'));
  if (changed) writeFileSync(path.join(targetDir, 'changed-files.txt'), changed);
  const passed = failed.length === 0 && problems.length === 0 && shards.length > 0 && shards.every((shard) => shard.outcome === 'success');
  return {
    shards: shards.length,
    outcome: passed ? 'success' : 'failure',
    checks: checks.join(','),
    failed: failed.join(','),
    uncovered: uncovered.join(','),
    flaky: flaky.join(','),
    problems,
  };
}

const DELIMITER = 'SPECKIT_SHARD_PROBLEMS_END';

export function main(argv, { env = process.env, log = console.log } = {}) {
  const [command, sourceDir, targetDir, ...rest] = argv;
  const usage = 'Usage: speckit-verify-shards.mjs combine <shard artifacts folder> <combined folder> [--expect <matrix json>]';
  if (command !== 'combine' || !sourceDir || !targetDir || (rest.length > 0 && (rest.length !== 2 || rest[0] !== '--expect'))) {
    log(usage);
    return 2;
  }
  let expect = null;
  if (rest.length === 2) {
    try {
      expect = JSON.parse(rest[1]);
    } catch {
      expect = { include: [] };
    }
  }
  const result = combineShards(sourceDir, targetDir, { expect });
  if (env.GITHUB_OUTPUT) {
    appendFileSync(env.GITHUB_OUTPUT, [
      `shards=${result.shards}`,
      `flaky=${result.flaky}`,
      // Only the planned verifications read the combined outcome; a phase verification reads the two lines above.
      ...(expect ? [
        `outcome=${result.outcome}`,
        `checks=${result.checks}`,
        `failed=${result.failed}`,
        `uncovered=${result.uncovered}`,
        `problems<<${DELIMITER}`,
        ...result.problems,
        DELIMITER,
      ] : []),
      '',
    ].join('\n'));
  }
  if (!expect) log(`Combined ${result.shards} shard verification(s)${result.flaky ? `; flaky: ${result.flaky}` : ''}.`);
  else log(`Combined ${result.shards} shard verification(s): ${result.outcome}${result.failed ? `; failed: ${result.failed}` : ''}${result.flaky ? `; flaky: ${result.flaky}` : ''}.`);
  for (const problem of result.problems) log(`- ${problem}`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) process.exitCode = main(process.argv.slice(2));