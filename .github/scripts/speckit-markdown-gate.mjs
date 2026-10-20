import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// The Markdown gate of spec and amendment pull requests: their changed Markdown files are written over the trusted
// default-branch checkout of the record job and checked with check-markdown.mjs, so nothing from the pull request
// but Markdown content (never configuration or code) reaches the job. Spec and amendment pull requests may only change
// these paths; other files are left to the path rules.
const GATED_PREFIXES = ['specs/', 'docs/architecture/'];
const CHECK_SCRIPT = fileURLToPath(new URL('./check-markdown.mjs', import.meta.url));
const MAX_FINDINGS = 20;

const isGated = (file) => typeof file === 'string' && file.endsWith('.md') && GATED_PREFIXES.some((prefix) => file.startsWith(prefix)) &&
  !file.split('/').includes('..');

const runCheck = (root, listFile) => spawnSync(process.execPath, [CHECK_SCRIPT, '--files', listFile], { cwd: root, encoding: 'utf8', windowsHide: true, maxBuffer: 16 * 1024 * 1024 });

// One finding per markdownlint diagnostic or broken link, from check-markdown's output.
export function parseMarkdownFindings(output) {
  const findings = [];
  let linkFile = null;
  for (const raw of String(output ?? '').split(/\r?\n/)) {
    const line = raw.trim();
    const lint = line.match(/^(\S+\.md):(\d+)(?::(\d+))?\s+(?:error\s+)?(MD\d+\/[\w-]+)\s+(.*)$/);
    if (lint) {
      findings.push(`${lint[1]}:${lint[2]}${lint[3] ? `:${lint[3]}` : ''} ${lint[4]} ${lint[5]}`);
      continue;
    }
    const file = line.match(/^FILE:\s+(\S+\.md)$/);
    if (file) {
      linkFile = file[1];
      continue;
    }
    const link = line.match(/^\[✖\]\s+(\S+)\s+→\s+(.*)$/);
    if (link) findings.push(`${linkFile ?? 'a Markdown file'}: broken link ${link[1]} (${link[2]})`);
  }
  return findings;
}

// Reasons (rule findings) why the pull request's Markdown does not pass the repository Markdown check; empty when it
// passes or changes no gated Markdown file.
export async function markdownGate(client, { pull, root = process.cwd(), run = runCheck }) {
  const files = (await client.listPullRequestFiles(pull.number))
    .filter((file) => file.status !== 'removed' && isGated(file.filename))
    .map((file) => file.filename);
  if (files.length === 0) return [];
  const head = pull.head?.sha ?? pull.head?.ref;
  for (const file of files) {
    const content = await client.getFileContent(file, head);
    if (content === null) continue;
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), content);
  }
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-markdown-'));
  try {
    const listFile = path.join(dir, 'files.txt');
    writeFileSync(listFile, `${files.join('\n')}\n`);
    const result = run(root, listFile);
    if (result.status === 0) return [];
    const output = `${result.stdout ?? ''}\n${result.stderr ?? ''}`;
    const findings = parseMarkdownFindings(output);
    if (findings.length === 0) {
      const tail = output.trim().split(/\r?\n/).slice(-5).join(' ').slice(0, 600);
      return [`the Markdown check (node .github/scripts/check-markdown.mjs) fails for the changed Markdown files: ${tail || `exit code ${result.status}`}`];
    }
    const shown = findings.slice(0, MAX_FINDINGS);
    return [
      `the Markdown check fails; fix each problem so \`node .github/scripts/check-markdown.mjs\` passes: ${shown.join('; ')}${findings.length > shown.length ? `; and ${findings.length - shown.length} more` : ''}`,
    ];
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}
