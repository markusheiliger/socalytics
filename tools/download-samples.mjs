#!/usr/bin/env node
// Brings a local folder in line with one version of the sample video artifact that upload-samples.mjs pushed (by
// default the latest): downloads new and changed videos, keeps the ones that already match, removes videos the version
// no longer has, and verifies every file. See download-samples.md next to this script.
import { copyFileSync, existsSync, mkdirSync, readdirSync, renameSync, rmSync, rmdirSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  ARTIFACT_TYPE,
  DEFAULT_PACKAGE,
  DOWNLOAD_SCOPES,
  LATEST_TAG,
  REGISTRY,
  SamplesError,
  defaultRun,
  ensureGhAuth,
  ghLogin,
  ghToken,
  isVideo,
  orasSession,
  parseArguments,
  parseReference,
  resolveDigest,
  sha256File,
  toPosix,
  validatePackageName,
} from './samples-lib.mjs';

const USAGE = 'Usage: node tools/download-samples.mjs [<ghcr.io/<owner>/<package>@sha256:<digest> | :<tag>>] [--package <name>] [--output <folder>]';
export const DEFAULT_OUTPUT = '.samples';
// Written into the output folder; a folder that has it was filled by this script and may be brought up to date.
export const MARKER_FILE = '.samples-reference';

// Every file below `dir`, as POSIX paths relative to it.
function listFiles(dir, base = dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return listFiles(full, base);
    return entry.isFile() ? [toPosix(path.relative(base, full))] : [];
  });
}

// Removes the empty folders below `dir`, deepest first; `dir` itself stays.
function removeEmptyFolders(dir, root = dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) removeEmptyFolders(path.join(dir, entry.name), root);
  }
  if (dir !== root && readdirSync(dir).length === 0) rmdirSync(dir);
}

export async function downloadSamples(argv, { run = defaultRun, log = console.log } = {}) {
  const { positional, options } = parseArguments(argv, { values: ['output', 'package'], flags: ['help'] });
  if (options.help) {
    log(USAGE);
    return { exitCode: 0 };
  }
  if (positional.length > 1) throw new SamplesError(USAGE);
  if (positional.length === 1 && options.package) throw new SamplesError('Pass either a reference or --package, not both.');
  const output = path.resolve(options.output ?? DEFAULT_OUTPUT);
  if (existsSync(output)) {
    if (!statSync(output).isDirectory()) throw new SamplesError(`The output ${output} is not a folder.`);
    if (readdirSync(output).length > 0 && !existsSync(path.join(output, MARKER_FILE))) {
      throw new SamplesError(`The output folder ${output} is not empty and was not filled by this script, so it is not changed. Choose another folder with --output.`);
    }
  }

  ensureGhAuth(run, DOWNLOAD_SCOPES, { log });
  const login = ghLogin(run);
  const reference = positional[0] ?? `${REGISTRY}/${login.toLowerCase()}/${validatePackageName(options.package ?? DEFAULT_PACKAGE)}:${LATEST_TAG}`;
  const target = parseReference(reference);
  const session = orasSession(run, { login, token: ghToken(run) });
  const result = { kept: [], copied: [], downloaded: [], removed: [] };
  let pinned;
  try {
    const digest = target.digest ?? resolveDigest(run, reference, session.config);
    pinned = `${REGISTRY}/${target.repository}@${digest}`;
    if (target.tag) log(`${reference} is ${pinned}`);
    const fetched = run('oras', ['manifest', 'fetch', pinned, '--registry-config', session.config]);
    if (fetched.status !== 0) throw new SamplesError(`Could not read ${pinned}: ${fetched.stderr.trim()}. Check that you have read access to the package.`);
    const manifest = JSON.parse(fetched.stdout);
    if (manifest.artifactType !== ARTIFACT_TYPE) throw new SamplesError(`${pinned} is not a sample artifact (artifact type ${manifest.artifactType ?? 'none'}).`);
    const layers = (manifest.layers ?? []).map((layer) => {
      const title = layer.annotations?.['org.opencontainers.image.title'];
      if (!title || title.startsWith('/') || /^[A-Za-z]:/.test(title) || title.split('/').some((part) => part === '..' || part === '')) {
        throw new SamplesError(`The artifact has a file with an unsafe path: ${title}`);
      }
      return { title, digest: layer.digest, size: layer.size };
    });

    mkdirSync(output, { recursive: true });
    log(`Bringing ${output} in line with ${layers.length} file(s) ...`);
    // The videos already in the folder, by content, so a video that moved is copied locally instead of downloaded.
    const local = new Map();
    for (const file of listFiles(output).filter(isVideo)) {
      const digest = await sha256File(path.join(output, ...file.split('/')));
      if (!local.has(digest)) local.set(digest, file);
    }
    for (const layer of layers) {
      const file = path.join(output, ...layer.title.split('/'));
      if (local.get(layer.digest) === layer.title || (existsSync(file) && statSync(file).isFile() && (await sha256File(file)) === layer.digest)) {
        result.kept.push(layer.title);
        continue;
      }
      // Copy or download next to the target, and replace it only once the content is verified.
      mkdirSync(path.dirname(file), { recursive: true });
      const partial = `${file}.partial`;
      rmSync(partial, { force: true });
      const copyFrom = local.get(layer.digest);
      if (copyFrom) {
        copyFileSync(path.join(output, ...copyFrom.split('/')), partial);
      } else {
        const blob = run('oras', ['blob', 'fetch', `${REGISTRY}/${target.repository}@${layer.digest}`, '--output', partial, '--registry-config', session.config]);
        if (blob.status !== 0) {
          rmSync(partial, { force: true });
          throw new SamplesError(`Could not download ${layer.title}: ${blob.stderr.trim()}`);
        }
      }
      const digestOfFile = await sha256File(partial);
      if (digestOfFile !== layer.digest) {
        rmSync(partial, { force: true });
        throw new SamplesError(`${layer.title} does not match its digest (${digestOfFile}, expected ${layer.digest}).`);
      }
      rmSync(file, { recursive: true, force: true });
      renameSync(partial, file);
      (copyFrom ? result.copied : result.downloaded).push(layer.title);
      log(`  ${copyFrom ? '>' : '+'} ${layer.title}${copyFrom ? ` (from ${copyFrom})` : ''}`);
    }
  } finally {
    session.close();
  }

  // Videos that the version no longer has were removed or moved; other files are left alone.
  const wanted = new Set([...result.kept, ...result.copied, ...result.downloaded]);
  for (const file of listFiles(output)) {
    if (!wanted.has(file) && isVideo(file)) {
      rmSync(path.join(output, ...file.split('/')));
      result.removed.push(file);
      log(`  - ${file}`);
    }
  }
  removeEmptyFolders(output);
  writeFileSync(path.join(output, MARKER_FILE), `${pinned}\n`);
  log(`${output} matches ${pinned}: ${result.downloaded.length} downloaded, ${result.copied.length} moved locally, ${result.kept.length} unchanged, ${result.removed.length} removed.`);
  return { exitCode: 0, output, reference: pinned, ...result };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  downloadSamples(process.argv.slice(2)).then(
    (result) => { process.exitCode = result.exitCode; },
    (error) => {
      console.error(error instanceof SamplesError ? `download-samples: ${error.message}` : error.stack);
      process.exitCode = error instanceof SamplesError ? 2 : 1;
    },
  );
}
