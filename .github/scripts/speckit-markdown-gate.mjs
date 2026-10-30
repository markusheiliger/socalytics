import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// The Markdown gate of spec and amendment pull requests: their changed Markdown files are written over the trusted
// default-branch checkout of the record job and checked with check-markdown.mjs. Their owning spec folders (or the
// architecture folder) supply Markdown link context from the same head, including unchanged archived artifacts.
// Nothing but Markdown content (never configuration or code) reaches the job.
const GATED_PREFIXES = ['specs/', 'docs/architecture/'];
const CHECK_SCRIPT = fileURLToPath(new URL('./check-markdown.mjs', import.meta.url));
const MAX_FINDINGS = 20;

const isGated = (file) => typeof file === 'string' && file.endsWith('.md') && GATED_PREFIXES.some((prefix) => file.startsWith(prefix)) &&
  file.split('/').every((part) => part && part !== '.' && part !== '..' && !/[\\:\u0000-\u001f]/.test(part));

function contextFolder(file) {
  if (file.startsWith('docs/architecture/')) return 'docs/architecture';
  const parts = file.split('/');
  const depth = parts[1] === 'archive' ? 3 : 2;
  return parts.length > depth ? parts.slice(0, depth).join('/') : null;
}

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
  const folders = [...new Set(files.map(contextFolder).filter(Boolean))];
  const inContext = (file) => isGated(file) && folders.some((folder) => file.startsWith(`${folder}/`));
  const snapshot = new Set((await client.listRepositoryFiles(head)).filter(isGated));
  const context = new Set([...snapshot].filter(inContext));
  for (const file of files) {
    if (!snapshot.has(file)) throw new Error(`Changed Markdown file ${file} is missing from ${head}.`);
    context.add(file);
  }
  for (const folder of folders) {
    const directory = path.join(root, folder);
    if (!existsSync(directory)) continue;
    for (const entry of readdirSync(directory, { recursive: true })) {
      const file = `${folder}/${entry.replaceAll('\\', '/')}`;
      if (inContext(file) && !snapshot.has(file)) rmSync(path.join(root, file));
    }
  }
  for (const file of context) {
    const content = await client.getFileContent(file, head);
    if (content === null) throw new Error(`Markdown context file ${file} could not be read at ${head}.`);
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
