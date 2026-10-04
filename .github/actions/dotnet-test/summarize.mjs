#!/usr/bin/env node
// Part of the dotnet-test action: condenses a `dotnet test` log into the feedback someone needs to
// fix a failure (build errors, then each failed test with its message, own stack frames, and output).
//
// Usage: node summarize.mjs <dotnet-test.log> <summary.txt>

import { readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export const MAX_SUMMARY = 4000;

// Keeps the end of the log within the limit: when nothing else is recognized, the last lines
// (where dotnet test reports aborts and exit codes) are the most useful.
function tail(text, limit) {
  const value = String(text ?? '').replace(/\x1b\[[0-9;]*m/g, '').trim();
  return value.length > limit ? `…${value.slice(-(limit - 1))}` : value;
}

const CRASH_LINE = /Catastrophic failure|Test Run Aborted|test run was aborted|Test host process crashed|crashed with exit code|\[FATAL ERROR\]/i;
const TEST_RUN_LINE = /^Test run for .*\/([^/]+\.dll)\b/;
const TEST_RESULT_LINE = /^\s*(Failed|Passed|Skipped) \S+ \[[^\]]*\]\s*$/;
const MAX_FRAMES_PER_FAILURE = 6;
const MAX_MESSAGE_LINES = 12;

// Condenses a dotnet test log into what an agent needs to fix a failure: build errors, then
// each failed test with its full error message, the repository's own stack frames, and test
// output. Framework frames and inner stack traces are dropped. Falls back to the log tail.
export function summarizeTestLog(text, limit = MAX_SUMMARY) {
  const lines = String(text ?? '').replace(/\x1b\[[0-9;]*m/g, '').split(/\r?\n/);
  const sections = [];
  const buildErrors = [...new Set(lines.filter((line) => /\berror [A-Za-z]+\d+:/.test(line)).map((line) => line.trim()))];
  if (buildErrors.length > 0) sections.push(['Build errors:', ...buildErrors.slice(0, 10)].join('\n'));
  for (let index = 0; index < lines.length; index += 1) {
    const failed = /^\s*Failed (\S+) \[[^\]]*\]\s*$/.exec(lines[index]);
    if (!failed) continue;
    const block = [`Failed test: ${failed[1]}`];
    let section = null;
    let frames = 0;
    let messageLines = 0;
    let next = index + 1;
    for (; next < lines.length; next += 1) {
      const line = lines[next];
      const trimmed = line.trim();
      if (TEST_RESULT_LINE.test(line) || /^(Passed|Failed)!\s/.test(trimmed)) break;
      if (trimmed === 'Error Message:') { section = 'message'; block.push('Error message:'); continue; }
      if (trimmed === 'Stack Trace:') { section = 'stack'; block.push('Stack (repository frames):'); continue; }
      if (trimmed === 'Standard Output Messages:') { section = 'output'; block.push('Test output:'); continue; }
      if (section === 'stack' && trimmed.startsWith('----- Inner Stack Trace')) { section = 'inner'; continue; }
      if (section === 'message' && trimmed && !/^at /.test(trimmed) && messageLines < MAX_MESSAGE_LINES) {
        block.push(`  ${trimmed}`);
        messageLines += 1;
      }
      if (section === 'output' && trimmed) block.push(`  ${trimmed}`);
      if (section === 'stack' && frames < MAX_FRAMES_PER_FAILURE && /:line \d+$/.test(trimmed) && !trimmed.includes(' in /_/')) {
        const frame = `  ${trimmed.replace(/ in \/home\/runner\/work\/[^/]+\/[^/]+\//, ' in ')}`;
        if (block.at(-1) !== frame) {
          block.push(frame);
          frames += 1;
        }
      }
    }
    sections.push(block.join('\n'));
    index = next - 1;
  }
  // A crashed or aborted test host reports no individual failed test, only these lines.
  let assembly = null;
  const crashes = new Map();
  for (const line of lines) {
    const run = TEST_RUN_LINE.exec(line.trim());
    if (run) assembly = run[1];
    if (CRASH_LINE.test(line)) {
      const key = assembly ?? 'unknown test assembly';
      if (!crashes.has(key)) crashes.set(key, []);
      const entry = line.trim().replace(/^\[xUnit\.net [^\]]*\]\s*/, '');
      if (!crashes.get(key).includes(entry) && crashes.get(key).length < MAX_MESSAGE_LINES) crashes.get(key).push(entry);
    }
  }
  for (const [name, entries] of crashes) {
    sections.push([`Test run crashed or was aborted: ${name}`, ...entries.map((entry) => `  ${entry}`)].join('\n'));
  }
  if (sections.length === 0) return tail(text, limit);
  const results = lines.map((line) => line.trim()).filter((line) => /^(Passed|Failed)!\s/.test(line));
  if (results.length > 0) sections.push(['Test assemblies:', ...results].join('\n'));
  const summary = sections.join('\n\n');
  return summary.length > limit ? `${summary.slice(0, limit - 1)}…` : summary;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [logPath, outPath] = process.argv.slice(2);
  if (!logPath || !outPath) {
    console.error('Usage: node summarize.mjs <dotnet-test.log> <summary.txt>');
    process.exit(2);
  }
  writeFileSync(outPath, `${summarizeTestLog(readFileSync(logPath, 'utf8'))}\n`);
}