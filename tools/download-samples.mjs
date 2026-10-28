#!/usr/bin/env node
// Downloads a sample video artifact uploaded with upload-samples.mjs, pinned by digest, restores its nested folders,
// and verifies every file. See download-samples.md next to this script.
import { existsSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  ARTIFACT_TYPE,
  DOWNLOAD_SCOPES,
  SamplesError,
  defaultRun,
  ensureGhAuth,
  ghLogin,
  ghToken,
  orasSession,
  parseArguments,
  parseDigestReference,
  sha256File,
} from './samples-lib.mjs';

const USAGE = 'Usage: node tools/download-samples.mjs <ghcr.io/<owner>/<package>@sha256:<digest>> [--output <folder>]';
export const DEFAULT_OUTPUT = '.samples';

export async function downloadSamples(argv, { run = defaultRun, log = console.log } = {}) {
  const { positional, options } = parseArguments(argv, { values: ['output'], flags: ['help'] });
  if (options.help) {
    log(USAGE);
    return { exitCode: 0 };
  }
  if (positional.length !== 1) throw new SamplesError(USAGE);
  const reference = positional[0];
  parseDigestReference(reference);
  const output = path.resolve(options.output ?? DEFAULT_OUTPUT);
  if (existsSync(output) && (!statSync(output).isDirectory() || readdirSync(output).length > 0)) {
    throw new SamplesError(`The output folder ${output} is not empty; choose another one with --output or empty it first.`);
  }

  ensureGhAuth(run, DOWNLOAD_SCOPES, { log });
  const session = orasSession(run, { login: ghLogin(run), token: ghToken(run) });
  let manifest;
  try {
    const fetched = run('oras', ['manifest', 'fetch', reference, '--registry-config', session.config]);
    if (fetched.status !== 0) throw new SamplesError(`Could not read ${reference}: ${fetched.stderr.trim()}. Check that you have read access to the package.`);
    manifest = JSON.parse(fetched.stdout);
    if (manifest.artifactType !== ARTIFACT_TYPE) throw new SamplesError(`${reference} is not a sample artifact (artifact type ${manifest.artifactType ?? 'none'}).`);
    log(`Downloading ${manifest.layers.length} file(s) from ${reference} into ${output} ...`);
    const pulled = run('oras', ['pull', reference, '--registry-config', session.config, '-o', output]);
    if (pulled.status !== 0) throw new SamplesError(`oras pull failed:\n${pulled.stderr.trim() || pulled.stdout.trim()}`);
  } finally {
    session.close();
  }

  // ORAS verifies the digests while it downloads; check every file again, so a partial download is never used.
  const files = [];
  for (const layer of manifest.layers) {
    const title = layer.annotations?.['org.opencontainers.image.title'];
    if (!title || title.startsWith('/') || title.split('/').includes('..')) throw new SamplesError(`The artifact has a file with an unsafe path: ${title}`);
    const file = path.join(output, ...title.split('/'));
    if (!existsSync(file)) throw new SamplesError(`Missing after the download: ${title}`);
    const digest = await sha256File(file);
    if (digest !== layer.digest) throw new SamplesError(`${title} does not match its digest (${digest}, expected ${layer.digest}).`);
    files.push({ path: title, size: layer.size, digest });
    log(`  ${title}`);
  }
  log(`Downloaded and verified ${files.length} file(s) into ${output}.`);
  return { exitCode: 0, output, files };
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
