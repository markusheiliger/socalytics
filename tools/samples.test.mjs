import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, test } from 'node:test';

import { downloadSamples } from './download-samples.mjs';
import {
  ARTIFACT_TYPE,
  SamplesError,
  canPullManifest,
  discoverVideos,
  parseArguments,
  parseDigestReference,
  pushArguments,
  tokenScopes,
  utcTag,
  validatePackageName,
} from './samples-lib.mjs';
import { uploadSamples } from './upload-samples.mjs';

const temporary = mkdtempSync(path.join(tmpdir(), 'samples-test-'));
after(() => rmSync(temporary, { recursive: true, force: true }));
let counter = 0;
const silent = () => {};
const sha = (content) => `sha256:${createHash('sha256').update(content).digest('hex')}`;
const DIGEST = `sha256:${'a'.repeat(64)}`;

function folder(files) {
  const root = path.join(temporary, `samples-${counter++}`);
  for (const [file, content] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    writeFileSync(path.join(root, file), content);
  }
  return root;
}

test('finds every video below the folder, also nested, with relative POSIX paths, and lists the other files', () => {
  const root = folder({ 'b.MP4': 'b', 'match/2026/half-1.mkv': 'h1', 'match/notes.txt': 'n', 'a.webm': 'a', 'clip.TS': 't' });
  const { videos, skipped } = discoverVideos(root);
  assert.deepEqual(videos.map((video) => [video.path, video.mediaType, video.size]), [
    ['a.webm', 'video/webm', 1],
    ['b.MP4', 'video/mp4', 1],
    ['clip.TS', 'video/mp2t', 1],
    ['match/2026/half-1.mkv', 'video/x-matroska', 2],
  ]);
  assert.deepEqual(skipped, [{ path: 'match/notes.txt', reason: 'not a video' }]);
});

test('refuses a missing folder, no videos, empty videos, and paths that differ only in case; never follows links', (t) => {
  assert.throws(() => discoverVideos(path.join(temporary, 'missing')), /Not a folder/);
  assert.throws(() => discoverVideos(folder({ 'readme.md': 'x' })), /No video files/);
  assert.throws(() => discoverVideos(folder({ 'a.mp4': '' })), /Empty video file: a\.mp4/);
  const clash = folder({ 'x/a.mp4': '1', 'X/A.mp4': '2' });
  if (discoverVideos(folder({ 'probe/a.mp4': '1', 'probe/A.mp4': '2' })).videos.length === 2) {
    assert.throws(() => discoverVideos(clash), /differ only in case/);
  }
  const outside = folder({ 'secret.mp4': 's' });
  const root = folder({ 'a.mp4': 'a' });
  try {
    symlinkSync(outside, path.join(root, 'linked'), 'junction');
  } catch {
    t.skip('this platform cannot create the link');
    return;
  }
  const { videos, skipped } = discoverVideos(root);
  assert.deepEqual(videos.map((video) => video.path), ['a.mp4']);
  assert.deepEqual(skipped, [{ path: 'linked', reason: 'symbolic link' }]);
});

test('helpers: tag, package names, digest references, scopes, arguments, and the push command', () => {
  assert.equal(utcTag(new Date('2026-10-28T09:05:03Z')), '20261028-090503');
  assert.equal(validatePackageName('socalytics-samples'), 'socalytics-samples');
  for (const bad of ['Samples', 'a--b', '-a', 'a/b', '']) assert.throws(() => validatePackageName(bad), SamplesError, bad);
  assert.deepEqual(parseDigestReference(`ghcr.io/dev/socalytics-samples@${DIGEST}`), { owner: 'dev', name: 'socalytics-samples', digest: DIGEST, repository: 'dev/socalytics-samples' });
  assert.throws(() => parseDigestReference('ghcr.io/dev/socalytics-samples:20261028-090503'), /pinned by digest/);
  assert.deepEqual(tokenScopes("  - Token scopes: 'gist', 'read:packages', 'repo'"), ['gist', 'read:packages', 'repo']);
  assert.deepEqual(tokenScopes("  - Token scopes: 'delete:packages', 'repo', 'write:packages'"), ['delete:packages', 'repo', 'write:packages', 'read:packages'], 'write:packages implies read:packages');
  assert.equal(tokenScopes('no scopes listed'), null);
  assert.deepEqual(parseArguments(['dir', '--package', 'x', '--dry-run'], { values: ['package'], flags: ['dry-run'] }), { positional: ['dir'], options: { package: 'x', 'dry-run': true } });
  assert.throws(() => parseArguments(['--force'], { values: [], flags: [] }), /Unknown option: --force/);
  assert.throws(() => parseArguments(['--package'], { values: ['package'], flags: [] }), /needs a value/);
  const args = pushArguments({ reference: 'ghcr.io/dev/s:t', config: 'c.json', manifestFile: 'm.json', description: 'd', videos: [{ path: 'match/a.mp4', mediaType: 'video/mp4' }] });
  assert.deepEqual(args.slice(-1), ['match/a.mp4:video/mp4'], 'the layer title is the relative path');
  assert.ok(args.includes(ARTIFACT_TYPE));
  assert.ok(!args.join(' ').includes('org.opencontainers.image.source'), 'never linked to a repository');
  assert.throws(() => pushArguments({ reference: 'r', config: 'c', manifestFile: 'm', description: 'd', videos: Array.from({ length: 2000 }, (_, i) => ({ path: `folder-${i}/clip-${i}.mp4`, mediaType: 'video/mp4' })) }), /Too many files/);
});

test('the anonymous pull check asks the token service first; a refused token or manifest means no access', async () => {
  const calls = [];
  const fetchImpl = (status) => async (url, init) => {
    calls.push([url, init?.headers?.Authorization ?? null]);
    if (url.includes('/token?')) return { status: status.token, json: async () => ({ token: 'anon' }) };
    return { status: status.manifest };
  };
  assert.equal(await canPullManifest(fetchImpl({ token: 403, manifest: 200 }), 'dev/s', DIGEST, null), false);
  assert.equal(await canPullManifest(fetchImpl({ token: 200, manifest: 401 }), 'dev/s', DIGEST, null), false);
  assert.equal(await canPullManifest(fetchImpl({ token: 200, manifest: 200 }), 'dev/s', DIGEST, null), true);
  assert.match(calls[0][0], /^https:\/\/ghcr\.io\/token\?scope=repository%3Adev%2Fs%3Apull/);
  calls.length = 0;
  assert.equal(await canPullManifest(fetchImpl({ token: 500, manifest: 200 }), 'dev/s', DIGEST, 'gho_x'), true);
  assert.deepEqual(calls, [[`https://ghcr.io/v2/dev/s/manifests/${DIGEST}`, `Bearer ${Buffer.from('gho_x').toString('base64')}`]], 'authenticated: straight to the manifest');
});

// A fake gh and oras: records every call and answers like the real tools.
function fakeTools({ scopes = "'repo', 'write:packages', 'read:packages', 'delete:packages'", before = null, after = { visibility: 'private' }, pushDigest = DIGEST, manifest = null, refresh = 0, files = {} } = {}) {
  const calls = [];
  const state = { deleted: [], refreshed: false };
  const run = (command, args, options = {}) => {
    calls.push({ command, args, cwd: options.cwd, input: options.input });
    const key = `${command} ${args.join(' ')}`;
    if (args[0] === 'version') return { status: 0, stdout: 'v', stderr: '' };
    if (key.startsWith('gh auth status')) return { status: 0, stdout: '', stderr: `  - Token scopes: ${state.refreshed ? "'write:packages', 'read:packages', 'delete:packages'" : scopes}` };
    if (key.startsWith('gh auth refresh')) {
      state.refreshed = refresh === 0;
      return { status: refresh, stdout: '', stderr: '' };
    }
    if (key === 'gh api user --jq .login') return { status: 0, stdout: 'Dev\n', stderr: '' };
    if (key.startsWith('gh auth token')) return { status: 0, stdout: 'gho_secret\n', stderr: '' };
    if (key.startsWith('gh api /user/packages/container/') && !key.includes('/versions')) {
      const pkg = calls.some((call) => call.command === 'oras' && call.args[0] === 'push') ? after : before;
      return pkg ? { status: 0, stdout: JSON.stringify(pkg), stderr: '' } : { status: 1, stdout: '', stderr: 'gh: Not Found (HTTP 404)' };
    }
    if (key.includes('/versions') && !key.includes('DELETE')) return { status: 0, stdout: `${JSON.stringify({ id: 7, name: pushDigest })}\n`, stderr: '' };
    if (key.startsWith('gh api -X DELETE')) {
      state.deleted.push(args.at(-1));
      return { status: 0, stdout: '', stderr: '' };
    }
    if (args[0] === 'login') return { status: 0, stdout: 'Login Succeeded', stderr: '' };
    if (args[0] === 'push') {
      const exported = args[args.indexOf('--export-manifest') + 1];
      const layers = args.slice(args.indexOf('--export-manifest') + 2).map((item) => {
        const file = item.slice(0, item.lastIndexOf(':'));
        return { digest: sha(readFileSync(path.join(options.cwd, file))), annotations: { 'org.opencontainers.image.title': file } };
      });
      writeFileSync(exported, JSON.stringify(manifest ?? { layers }));
      return { status: 0, stdout: `Pushed [registry] ${args[1]}\nDigest: ${pushDigest}\n`, stderr: '' };
    }
    if (args[0] === 'manifest') return { status: 0, stdout: JSON.stringify(manifest), stderr: '' };
    if (args[0] === 'pull') {
      const output = args[args.indexOf('-o') + 1];
      for (const [file, content] of Object.entries(files)) {
        mkdirSync(path.dirname(path.join(output, file)), { recursive: true });
        writeFileSync(path.join(output, file), content);
      }
      return { status: 0, stdout: '', stderr: '' };
    }
    return { status: 1, stdout: '', stderr: `unexpected: ${key}` };
  };
  return { run, calls, state };
}

const registry = ({ anonymous = false, authenticated = true } = {}) => async (url, init) => {
  if (url.includes('/token?')) return anonymous ? { status: 200, json: async () => ({ token: 'anon' }) } : { status: 403, json: async () => ({}) };
  return { status: (init?.headers?.Authorization === 'Bearer anon' ? anonymous : authenticated) ? 200 : 401 };
};
const NOW = () => new Date('2026-10-28T15:00:00Z');

test('upload pushes every video with its relative path, checks privacy, and writes the manifest to commit', async () => {
  const root = folder({ 'a.mp4': 'aaa', 'match/half-1.mkv': 'h1', 'notes.txt': 'x' });
  const manifestFile = path.join(temporary, `samples-${counter++}.json`);
  const tools = fakeTools();
  const lines = [];
  const result = await uploadSamples([root, '--manifest', manifestFile], { run: tools.run, fetchImpl: registry(), log: (line) => lines.push(line), now: NOW });
  assert.equal(result.exitCode, 0);
  const push = tools.calls.find((call) => call.command === 'oras' && call.args[0] === 'push');
  assert.equal(push.args[1], 'ghcr.io/dev/socalytics-samples:20261028-150000', 'lowercased owner and a UTC tag');
  assert.equal(push.cwd, discoverVideos(root).root);
  assert.deepEqual(push.args.slice(-2), ['a.mp4:video/mp4', 'match/half-1.mkv:video/x-matroska']);
  const login = tools.calls.find((call) => call.command === 'oras' && call.args[0] === 'login');
  assert.equal(login.input, 'gho_secret\n', 'the token goes through stdin');
  assert.ok(tools.calls.every((call) => !call.args.join(' ').includes('gho_secret')), 'never in arguments');
  assert.equal(existsSync(push.args[push.args.indexOf('--registry-config') + 1]), false, 'the temporary registry config is deleted');
  const record = JSON.parse(readFileSync(manifestFile, 'utf8'));
  assert.deepEqual(record, {
    reference: `ghcr.io/dev/socalytics-samples@${DIGEST}`,
    tag: '20261028-150000',
    artifactType: ARTIFACT_TYPE,
    created: '2026-10-28T15:00:00.000Z',
    files: [
      { path: 'a.mp4', size: 3, mediaType: 'video/mp4', digest: sha('aaa') },
      { path: 'match/half-1.mkv', size: 2, mediaType: 'video/x-matroska', digest: sha('h1') },
    ],
  });
  assert.ok(lines.includes('Privacy check passed: the package is private, not linked to a repository, and refuses anonymous pulls.'));
  assert.ok(lines.includes(`Download: node tools/download-samples.mjs ghcr.io/dev/socalytics-samples@${DIGEST}`));
  assert.ok(lines.some((line) => /notes\.txt \(not a video\)/.test(line)));
});

test('upload refreshes missing scopes, and a dry run checks everything but pushes nothing', async () => {
  const tools = fakeTools({ scopes: "'repo'" });
  const lines = [];
  await uploadSamples([folder({ 'a.mp4': 'a' }), '--dry-run'], { run: tools.run, fetchImpl: registry(), log: (line) => lines.push(line), now: NOW });
  assert.deepEqual(tools.calls.find((call) => call.args[1] === 'refresh').args, ['auth', 'refresh', '-h', 'github.com', '-s', 'write:packages,read:packages,delete:packages']);
  assert.ok(!tools.calls.some((call) => call.command === 'oras' && call.args[0] === 'push'));
  assert.ok(lines.some((line) => /^Dry run: would push 1 file\(s\) as ghcr\.io\/dev\/socalytics-samples:20261028-150000/.test(line)));
  await assert.rejects(uploadSamples([folder({ 'a.mp4': 'a' })], { run: fakeTools({ scopes: "'repo'", refresh: 1 }).run, fetchImpl: registry(), log: silent, now: NOW }), /Could not add the scopes write:packages, read:packages, delete:packages/);
});

test('upload never pushes into a public or linked package', async () => {
  for (const [before, reason] of [[{ visibility: 'public' }, /visibility is public, not private/], [{ visibility: 'private', repository: { full_name: 'dev/socalytics' } }, /linked to the repository dev\/socalytics/]]) {
    const tools = fakeTools({ before });
    await assert.rejects(uploadSamples([folder({ 'a.mp4': 'a' })], { run: tools.run, fetchImpl: registry(), log: silent, now: NOW }), reason);
    assert.ok(!tools.calls.some((call) => call.command === 'oras' && ['login', 'push'].includes(call.args[0])), 'nothing was pushed');
  }
});

test('upload deletes a pushed version that is not private, can be pulled anonymously, or cannot be checked', async () => {
  for (const [options, reason] of [
    [{ after: { visibility: 'public' } }, /visibility is public/],
    [{ after: { visibility: 'private' }, fetch: { anonymous: true } }, /an anonymous pull is allowed/],
    [{ after: { visibility: 'private' }, fetch: { authenticated: false } }, /authenticated pull failed/],
  ]) {
    const tools = fakeTools(options);
    await assert.rejects(uploadSamples([folder({ 'a.mp4': 'a' })], { run: tools.run, fetchImpl: registry(options.fetch), log: silent, now: NOW }), (error) => reason.test(error.message) && /so it was deleted/.test(error.message));
    assert.deepEqual(tools.state.deleted, ['/user/packages/container/socalytics-samples/versions/7']);
  }
});

test('download pins a digest, restores nested folders, and verifies every file', async () => {
  const manifest = {
    artifactType: ARTIFACT_TYPE,
    layers: [
      { digest: sha('aaa'), size: 3, annotations: { 'org.opencontainers.image.title': 'a.mp4' } },
      { digest: sha('h1'), size: 2, annotations: { 'org.opencontainers.image.title': 'match/half-1.mkv' } },
    ],
  };
  const reference = `ghcr.io/dev/socalytics-samples@${DIGEST}`;
  const output = path.join(temporary, `out-${counter++}`);
  const tools = fakeTools({ manifest, files: { 'a.mp4': 'aaa', 'match/half-1.mkv': 'h1' } });
  const result = await downloadSamples([reference, '--output', output], { run: tools.run, log: silent });
  assert.deepEqual(result.files.map((file) => file.path), ['a.mp4', 'match/half-1.mkv']);
  assert.equal(readFileSync(path.join(output, 'match', 'half-1.mkv'), 'utf8'), 'h1');
  assert.ok(tools.calls.some((call) => call.command === 'gh' && call.args[1] === 'status'));
  assert.ok(!tools.calls.some((call) => call.args[1] === 'refresh'), 'read:packages is enough');

  await assert.rejects(downloadSamples(['ghcr.io/dev/socalytics-samples:20261028-150000'], { run: tools.run, log: silent }), /pinned by digest/);
  await assert.rejects(downloadSamples([reference, '--output', output], { run: tools.run, log: silent }), /is not empty/);
  const corrupt = fakeTools({ manifest, files: { 'a.mp4': 'aaa', 'match/half-1.mkv': 'xx' } });
  await assert.rejects(downloadSamples([reference, '--output', path.join(temporary, `out-${counter++}`)], { run: corrupt.run, log: silent }), /match\/half-1\.mkv does not match its digest/);
  const other = fakeTools({ manifest: { ...manifest, artifactType: 'application/vnd.oci.image.config.v1+json' } });
  await assert.rejects(downloadSamples([reference, '--output', path.join(temporary, `out-${counter++}`)], { run: other.run, log: silent }), /is not a sample artifact/);
  const unsafe = fakeTools({ manifest: { ...manifest, layers: [{ digest: sha('x'), annotations: { 'org.opencontainers.image.title': '../escape.mp4' } }] } });
  await assert.rejects(downloadSamples([reference, '--output', path.join(temporary, `out-${counter++}`)], { run: unsafe.run, log: silent }), /unsafe path/);
});
