// Shared code of tools/upload-samples.mjs and tools/download-samples.mjs: sample video discovery, the OCI artifact
// manifest, the gh and oras command runners, and the checks that keep the GHCR package private.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream, existsSync, lstatSync, mkdtempSync, readdirSync, realpathSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

export const DEFAULT_PACKAGE = 'socalytics-samples';
export const ARTIFACT_TYPE = 'application/vnd.socalytics.samples.v1';
export const REGISTRY = 'ghcr.io';
export const UPLOAD_SCOPES = ['write:packages', 'read:packages', 'delete:packages'];
export const DOWNLOAD_SCOPES = ['read:packages'];
export const MEDIA_TYPES = Object.freeze({
  '.mp4': 'video/mp4',
  '.m4v': 'video/x-m4v',
  '.mov': 'video/quicktime',
  '.mkv': 'video/x-matroska',
  '.webm': 'video/webm',
  '.avi': 'video/x-msvideo',
  '.ts': 'video/mp2t',
});
// Windows limits a command line to 32,767 characters; stay well below it.
export const MAX_COMMAND_LENGTH = 30000;

export class SamplesError extends Error {}

export const isVideo = (file) => Object.hasOwn(MEDIA_TYPES, path.extname(file).toLowerCase());
export const mediaTypeOf = (file) => MEDIA_TYPES[path.extname(file).toLowerCase()];
export const toPosix = (file) => file.split(path.sep).join('/');

// Every video below `root`, as POSIX paths relative to it, sorted. Symbolic links and junctions are never followed, so
// nothing outside the folder is packaged. Empty videos and paths that differ only in case (they would collide when
// extracted on Windows or macOS) are refused.
export function discoverVideos(root) {
  if (!existsSync(root) || !statSync(root).isDirectory()) throw new SamplesError(`Not a folder: ${root}`);
  const base = realpathSync(root);
  const videos = [];
  const skipped = [];
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const full = path.join(dir, entry.name);
      const relative = toPosix(path.relative(base, full));
      if (entry.isSymbolicLink() || lstatSync(full).isSymbolicLink()) {
        skipped.push({ path: relative, reason: 'symbolic link' });
      } else if (entry.isDirectory()) {
        walk(full);
      } else if (!entry.isFile()) {
        skipped.push({ path: relative, reason: 'not a regular file' });
      } else if (!isVideo(entry.name)) {
        skipped.push({ path: relative, reason: 'not a video' });
      } else {
        const size = statSync(full).size;
        if (size === 0) throw new SamplesError(`Empty video file: ${relative}`);
        videos.push({ path: relative, size, mediaType: mediaTypeOf(entry.name) });
      }
    }
  };
  walk(base);
  if (videos.length === 0) throw new SamplesError(`No video files (${Object.keys(MEDIA_TYPES).join(' ')}) below ${root}`);
  const seen = new Map();
  for (const video of videos) {
    const key = video.path.toLowerCase();
    if (seen.has(key)) throw new SamplesError(`Paths differ only in case, which collides on Windows and macOS: ${seen.get(key)} and ${video.path}`);
    seen.set(key, video.path);
  }
  videos.sort((a, b) => a.path.localeCompare(b.path));
  return { root: base, videos, skipped };
}

export function sha256File(file) {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256');
    createReadStream(file).on('data', (chunk) => hash.update(chunk)).on('error', reject).on('end', () => resolve(`sha256:${hash.digest('hex')}`));
  });
}

export function utcTag(date = new Date()) {
  const pad = (value) => String(value).padStart(2, '0');
  return `${date.getUTCFullYear()}${pad(date.getUTCMonth() + 1)}${pad(date.getUTCDate())}-${pad(date.getUTCHours())}${pad(date.getUTCMinutes())}${pad(date.getUTCSeconds())}`;
}

// A GHCR package name: lowercase letters and digits, with single separators between them.
export function validatePackageName(name) {
  if (!/^[a-z0-9]+(?:[._-][a-z0-9]+)*$/.test(String(name ?? ''))) throw new SamplesError(`Invalid package name: ${name} (lowercase letters and digits, with . _ - between them)`);
  return name;
}

// `ghcr.io/<owner>/<package>` followed by `@sha256:<64 hex>` (exactly that version) or `:<tag>` (resolved to a digest
// before downloading, so the download is still exactly one version).
export const LATEST_TAG = 'latest';
export function parseReference(reference) {
  const match = /^ghcr\.io\/([a-z0-9-]+)\/([a-z0-9]+(?:[._-][a-z0-9]+)*)(?:@(sha256:[0-9a-f]{64})|:([A-Za-z0-9_][A-Za-z0-9._-]{0,127}))$/.exec(String(reference ?? ''));
  if (!match) throw new SamplesError(`Expected ghcr.io/<owner>/<package>@sha256:<64 hex> or ghcr.io/<owner>/<package>:<tag>, not: ${reference}`);
  return { owner: match[1], name: match[2], digest: match[3] ?? null, tag: match[4] ?? null, repository: `${match[1]}/${match[2]}` };
}

// The digest `oras resolve` reports for a reference.
export function resolveDigest(run, reference, config) {
  const result = run('oras', ['resolve', reference, '--registry-config', config]);
  const digest = /sha256:[0-9a-f]{64}/.exec(result.stdout)?.[0];
  if (result.status !== 0 || !digest) throw new SamplesError(`Could not resolve ${reference}: ${result.stderr.trim() || result.stdout.trim()}. Check that it exists and that you have read access.`);
  return digest;
}

// Runs a command without a shell; `input` goes to stdin (used for the token, which never appears in arguments).
export function defaultRun(command, args, { cwd, input, inherit = false } = {}) {
  const result = spawnSync(command, args, {
    cwd,
    input,
    encoding: 'utf8',
    stdio: inherit ? 'inherit' : ['pipe', 'pipe', 'pipe'],
    maxBuffer: 64 * 1024 * 1024,
    windowsHide: true,
  });
  if (result.error?.code === 'ENOENT') return { status: 127, stdout: '', stderr: `${command} was not found on PATH` };
  return { status: result.status ?? 1, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
}

const INSTALL_HINTS = {
  gh: 'Install the GitHub CLI: https://cli.github.com',
  oras: 'Install ORAS: https://oras.land/docs/installation (for example `winget install oras` or `brew install oras`)',
};

export function requireTool(run, tool) {
  const result = run(tool, ['version']);
  if (result.status !== 0) throw new SamplesError(`${tool} is required. ${INSTALL_HINTS[tool] ?? ''}`.trim());
}

// The scopes of gh's token on github.com, from `gh auth status` (classic OAuth tokens list them). GitHub folds
// read:packages into write:packages and does not list it then, so it is added as implied.
export function tokenScopes(statusText) {
  const line = String(statusText).split(/\r?\n/).find((item) => /Token scopes:/i.test(item));
  if (!line) return null;
  const scopes = [...line.matchAll(/'([^']+)'/g)].map((match) => match[1]);
  if (scopes.includes('write:packages') && !scopes.includes('read:packages')) scopes.push('read:packages');
  return scopes;
}

// Makes sure gh is logged in and its token carries `scopes`; refreshes it interactively when it does not.
export function ensureGhAuth(run, scopes, { log = console.log } = {}) {
  requireTool(run, 'gh');
  const status = run('gh', ['auth', 'status', '-h', 'github.com']);
  if (status.status !== 0) throw new SamplesError('gh is not logged in to github.com. Run `gh auth login` first.');
  const have = tokenScopes(`${status.stdout}\n${status.stderr}`);
  const missing = have === null ? [] : scopes.filter((scope) => !have.includes(scope));
  if (missing.length > 0) {
    log(`gh's token lacks ${missing.join(', ')}; refreshing it (gh asks you to approve in the browser).`);
    const refreshed = run('gh', ['auth', 'refresh', '-h', 'github.com', '-s', scopes.join(',')], { inherit: true });
    if (refreshed.status !== 0) throw new SamplesError(`Could not add the scopes ${missing.join(', ')}. Run \`gh auth refresh -h github.com -s ${scopes.join(',')}\` yourself.`);
  }
}

export function ghLogin(run) {
  const result = run('gh', ['api', 'user', '--jq', '.login']);
  if (result.status !== 0 || !result.stdout.trim()) throw new SamplesError(`Could not read your GitHub login: ${result.stderr.trim()}`);
  return result.stdout.trim();
}

export function ghToken(run) {
  const result = run('gh', ['auth', 'token', '-h', 'github.com']);
  if (result.status !== 0 || !result.stdout.trim()) throw new SamplesError('Could not read gh\'s token. Run `gh auth login` first.');
  return result.stdout.trim();
}

// The container package `name` of the logged-in user, or null when it does not exist yet.
export function getPackage(run, name) {
  const result = run('gh', ['api', `/user/packages/container/${encodeURIComponent(name)}`]);
  if (result.status !== 0) {
    if (/HTTP 404|Not Found/i.test(`${result.stderr}${result.stdout}`)) return null;
    throw new SamplesError(`Could not read the package ${name}: ${result.stderr.trim() || result.stdout.trim()}`);
  }
  return JSON.parse(result.stdout);
}

export function findVersion(run, name, digest) {
  const result = run('gh', ['api', '--paginate', '--jq', '.[] | {id, name}', `/user/packages/container/${encodeURIComponent(name)}/versions`]);
  if (result.status !== 0) throw new SamplesError(`Could not list the versions of ${name}: ${result.stderr.trim()}`);
  const versions = result.stdout.split(/\r?\n/).filter((line) => line.trim()).map((line) => JSON.parse(line));
  return versions.find((version) => version.name === digest) ?? null;
}

export function deleteVersion(run, name, id) {
  const result = run('gh', ['api', '-X', 'DELETE', `/user/packages/container/${encodeURIComponent(name)}/versions/${id}`]);
  if (result.status !== 0) throw new SamplesError(`Could not delete version ${id} of ${name}: ${result.stderr.trim()}`);
}

// Refuses a package that is not private or that is linked to a repository (whose access it may inherit). A package
// that does not exist yet (null) is created private by GHCR.
export function privacyProblems(pkg) {
  if (!pkg) return [];
  const problems = [];
  if (pkg.visibility !== 'private') problems.push(`its visibility is ${pkg.visibility ?? 'unknown'}, not private`);
  if (pkg.repository) problems.push(`it is linked to the repository ${pkg.repository.full_name ?? pkg.repository.name}, so it may inherit that repository's access`);
  return problems;
}

// Whether the registry serves the manifest `digest` of `repository` (owner/name) with `token` (null: anonymously). An
// anonymous request asks GHCR's token service for an anonymous pull token first, as any client does.
export async function canPullManifest(fetchImpl, repository, digest, token) {
  let bearer = token ? Buffer.from(token).toString('base64') : null;
  if (!token) {
    const response = await fetchImpl(`https://${REGISTRY}/token?scope=${encodeURIComponent(`repository:${repository}:pull`)}&service=${REGISTRY}`);
    if (response.status !== 200) return false;
    bearer = (await response.json().catch(() => null))?.token ?? null;
    if (!bearer) return false;
  }
  const response = await fetchImpl(`https://${REGISTRY}/v2/${repository}/manifests/${digest}`, {
    method: 'HEAD',
    headers: { Authorization: `Bearer ${bearer}`, Accept: 'application/vnd.oci.image.manifest.v1+json' },
  });
  return response.status === 200;
}

// A temporary ORAS registry configuration logged in with gh's token (through stdin). `close` deletes it.
export function orasSession(run, { login, token }) {
  requireTool(run, 'oras');
  const dir = mkdtempSync(path.join(tmpdir(), 'socalytics-samples-'));
  const config = path.join(dir, 'config.json');
  const result = run('oras', ['login', REGISTRY, '-u', login, '--password-stdin', '--registry-config', config], { input: `${token}\n` });
  if (result.status !== 0) {
    rmSync(dir, { recursive: true, force: true });
    throw new SamplesError(`oras could not log in to ${REGISTRY}: ${result.stderr.trim()}`);
  }
  return { dir, config, close: () => rmSync(dir, { recursive: true, force: true }) };
}

// The arguments of `oras push`, run with the sample folder as working directory, so every layer is titled with its
// relative path. No org.opencontainers.image.source annotation: it would link the package to a repository. ORAS
// uploads only the files the registry does not have yet, so a grown set sends only its new videos.
export function pushArguments({ reference, config, videos, manifestFile, description }) {
  const args = [
    'push', reference,
    '--registry-config', config,
    '--artifact-type', ARTIFACT_TYPE,
    '--annotation', `org.opencontainers.image.description=${description}`,
    '--export-manifest', manifestFile,
    ...videos.map((video) => `${video.path}:${video.mediaType}`),
  ];
  const length = ['oras', ...args].reduce((sum, item) => sum + item.length + 3, 0);
  if (length > MAX_COMMAND_LENGTH) throw new SamplesError(`Too many files for one command line (${videos.length} files, ${length} characters); split the folder into smaller sets.`);
  return args;
}

// The digest oras printed for the pushed manifest.
export function parsePushedDigest(output) {
  const match = /Digest:\s*(sha256:[0-9a-f]{64})/.exec(String(output));
  if (!match) throw new SamplesError(`oras did not report the pushed digest:\n${output}`);
  return match[1];
}

// Splits `argv` into positional arguments and `--name value` / `--flag` options; unknown options are refused.
export function parseArguments(argv, { values = [], flags = [] }) {
  const positional = [];
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const item = argv[index];
    if (!item.startsWith('--')) {
      positional.push(item);
      continue;
    }
    const name = item.slice(2);
    if (flags.includes(name)) options[name] = true;
    else if (values.includes(name)) {
      if (index + 1 >= argv.length) throw new SamplesError(`--${name} needs a value`);
      options[name] = argv[(index += 1)];
    } else throw new SamplesError(`Unknown option: ${item}`);
  }
  return { positional, options };
}
