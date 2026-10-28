#!/usr/bin/env node
// Packages every video file below a folder (also in nested folders) as one private OCI artifact in the GitHub Container
// Registry, authenticated through the GitHub CLI. See upload-samples.md next to this script.
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  ARTIFACT_TYPE,
  DEFAULT_PACKAGE,
  REGISTRY,
  SamplesError,
  UPLOAD_SCOPES,
  canPullManifest,
  defaultRun,
  deleteVersion,
  discoverVideos,
  ensureGhAuth,
  findVersion,
  getPackage,
  ghLogin,
  ghToken,
  orasSession,
  parseArguments,
  parsePushedDigest,
  privacyProblems,
  pushArguments,
  requireTool,
  sha256File,
  utcTag,
  validatePackageName,
} from './samples-lib.mjs';

const USAGE = 'Usage: node tools/upload-samples.mjs <folder> [--package <name>] [--manifest <file>] [--dry-run]';

export async function uploadSamples(argv, { run = defaultRun, fetchImpl = fetch, log = console.log, now = () => new Date() } = {}) {
  const { positional, options } = parseArguments(argv, { values: ['package', 'manifest'], flags: ['dry-run', 'help'] });
  if (options.help) {
    log(USAGE);
    return { exitCode: 0 };
  }
  if (positional.length !== 1) throw new SamplesError(USAGE);
  const name = validatePackageName(options.package ?? DEFAULT_PACKAGE);

  // 1. The videos to package.
  const { root, videos, skipped } = discoverVideos(positional[0]);
  for (const video of videos) video.digest = await sha256File(path.join(root, ...video.path.split('/')));
  const total = videos.reduce((sum, video) => sum + video.size, 0);
  log(`Found ${videos.length} video file(s), ${formatBytes(total)}, below ${root}:`);
  for (const video of videos) log(`  ${video.path} (${formatBytes(video.size)})`);
  if (skipped.length > 0) {
    log(`Skipped ${skipped.length} other file(s):`);
    for (const item of skipped) log(`  ${item.path} (${item.reason})`);
  }

  // 2. The tools and the account.
  ensureGhAuth(run, UPLOAD_SCOPES, { log });
  requireTool(run, 'oras');
  const login = ghLogin(run);
  const owner = login.toLowerCase();
  const tag = utcTag(now());
  const repository = `${owner}/${name}`;
  const reference = `${REGISTRY}/${repository}:${tag}`;

  // 3. Never push into a package that is not private.
  const before = getPackage(run, name);
  const problems = privacyProblems(before);
  if (problems.length > 0) {
    throw new SamplesError(`Refusing to push to ${REGISTRY}/${repository}: ${problems.join('; ')}. A public package cannot be made private again; use another name with --package.`);
  }
  log(before ? `The package ${REGISTRY}/${repository} exists and is private.` : `The package ${REGISTRY}/${repository} does not exist yet; GHCR creates it private.`);
  if (options['dry-run']) {
    log(`Dry run: would push ${videos.length} file(s) as ${reference}. Nothing was pushed.`);
    return { exitCode: 0, reference, videos };
  }

  // 4. Push.
  const token = ghToken(run);
  const session = orasSession(run, { login, token });
  let digest;
  let manifest;
  try {
    const manifestFile = path.join(session.dir, 'manifest.json');
    log(`Pushing ${reference} ...`);
    const pushed = run('oras', pushArguments({ reference, config: session.config, videos, manifestFile, description: `SocAlytics sample videos (${videos.length} files)` }), { cwd: root });
    if (pushed.status !== 0) throw new SamplesError(`oras push failed:\n${pushed.stderr.trim() || pushed.stdout.trim()}`);
    digest = parsePushedDigest(`${pushed.stdout}\n${pushed.stderr}`);
    manifest = JSON.parse(readFileSync(manifestFile, 'utf8'));
  } finally {
    session.close();
  }

  // 5. Prove the pushed version is private: GitHub reports it private, an anonymous pull is refused, and an authenticated
  //    one works (so the refusal is not an error of the check). Otherwise delete the version again.
  const after = getPackage(run, name);
  const failures = [];
  if (!after) failures.push('GitHub does not list the package');
  else failures.push(...privacyProblems(after));
  if (await canPullManifest(fetchImpl, repository, digest, null)) failures.push('an anonymous pull is allowed');
  if (!(await canPullManifest(fetchImpl, repository, digest, token))) failures.push('an authenticated pull failed, so the anonymous check cannot be trusted');
  if (failures.length > 0) {
    const version = findVersion(run, name, digest);
    if (version) deleteVersion(run, name, version.id);
    throw new SamplesError(`The pushed version ${digest} failed the privacy check (${failures.join('; ')}), ${version ? 'so it was deleted' : 'and it could not be found to delete'}. Check the package settings of ${REGISTRY}/${repository} on GitHub before trying again.`);
  }
  log('Privacy check passed: the package is private, not linked to a repository, and refuses anonymous pulls.');

  // 6. The record to commit.
  const layers = new Map((manifest.layers ?? []).map((layer) => [layer.annotations?.['org.opencontainers.image.title'], layer.digest]));
  const record = {
    reference: `${REGISTRY}/${repository}@${digest}`,
    tag,
    artifactType: ARTIFACT_TYPE,
    created: now().toISOString(),
    files: videos.map((video) => {
      if (layers.get(video.path) !== video.digest) throw new SamplesError(`The pushed layer of ${video.path} does not match its sha256.`);
      return { path: video.path, size: video.size, mediaType: video.mediaType, digest: video.digest };
    }),
  };
  const json = `${JSON.stringify(record, null, 2)}\n`;
  if (options.manifest) {
    writeFileSync(options.manifest, json);
    log(`Wrote the sample manifest to ${options.manifest}; commit it, never the videos.`);
  }
  log('');
  log(`Uploaded: ${record.reference}`);
  log(`Download: node tools/download-samples.mjs ${record.reference}`);
  log(`CI access: grant the repository read access under Package settings > Manage Actions access of ${REGISTRY}/${repository}.`);
  if (!options.manifest) log(json.trimEnd());
  return { exitCode: 0, record };
}

function formatBytes(bytes) {
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${unit === 0 ? value : value.toFixed(1)} ${units[unit]}`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  uploadSamples(process.argv.slice(2)).then(
    (result) => { process.exitCode = result.exitCode; },
    (error) => {
      console.error(error instanceof SamplesError ? `upload-samples: ${error.message}` : error.stack);
      process.exitCode = error instanceof SamplesError ? 2 : 1;
    },
  );
}
