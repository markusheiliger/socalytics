import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
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
  parseVersion,
  pushArguments,
  samplesTarget,
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
  assert.deepEqual(parseVersion('latest'), { digest: null, tag: 'latest' });
  assert.deepEqual(parseVersion('20261028-153012'), { digest: null, tag: '20261028-153012' });
  assert.deepEqual(parseVersion(DIGEST), { digest: DIGEST, tag: null });
  for (const bad of ['ghcr.io/dev/s:latest', 'sha256:abc', '-x', '']) assert.throws(() => parseVersion(bad), /Expected a tag like latest/, bad);
  const repo = (stdout, status = 0) => () => ({ status, stdout, stderr: status ? 'no git repository' : '' });
  assert.deepEqual(samplesTarget(repo('MarkusHeiliger/SocAlytics\n'), {}), { owner: 'markusheiliger', name: 'socalytics-samples', repository: 'markusheiliger/socalytics-samples', image: 'ghcr.io/markusheiliger/socalytics-samples', sourceRepository: 'MarkusHeiliger/SocAlytics' });
  assert.equal(samplesTarget(repo('dev/socalytics'), { SOCALYTICS_SAMPLES_PACKAGE: 'socalytics-samples-selftest' }).image, 'ghcr.io/dev/socalytics-samples-selftest');
  assert.throws(() => samplesTarget(repo('', 1), {}), /inside a clone of the repository/);
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

// A fake gh and oras: records every call and answers like the real tools. `registry` maps layer digests to contents
// for blob downloads; `manifest` is what `oras manifest fetch` returns.
function fakeTools({ scopes = "'repo', 'write:packages', 'delete:packages'", before = null, after = { visibility: 'private' }, pushDigest = DIGEST, manifest = null, refresh = 0, registry: blobs = {}, latest = DIGEST, login = 'Dev' } = {}) {
  const calls = [];
  const state = { deleted: [], refreshed: false, tags: [] };
  const run = (command, args, options = {}) => {
    calls.push({ command, args, cwd: options.cwd, input: options.input });
    const key = `${command} ${args.join(' ')}`;
    if (args[0] === 'version') return { status: 0, stdout: 'v', stderr: '' };
    if (key.startsWith('gh auth status')) return { status: 0, stdout: '', stderr: `  - Token scopes: ${state.refreshed ? "'write:packages', 'delete:packages'" : scopes}` };
    if (key.startsWith('gh auth refresh')) {
      state.refreshed = refresh === 0;
      return { status: refresh, stdout: '', stderr: '' };
    }
    if (key === 'gh api user --jq .login') return { status: 0, stdout: `${login}\n`, stderr: '' };
    if (key.startsWith('gh repo view')) return { status: 0, stdout: 'Dev/socalytics\n', stderr: '' };
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
      writeFileSync(exported, JSON.stringify({ layers }));
      return { status: 0, stdout: `Pushed [registry] ${args[1]}\nDigest: ${pushDigest}\n`, stderr: '' };
    }
    if (args[0] === 'tag') {
      state.tags.push(args.slice(1, 3));
      return { status: 0, stdout: '', stderr: '' };
    }
    if (args[0] === 'resolve') return { status: 0, stdout: `${latest}\n`, stderr: '' };
    if (args[0] === 'manifest') return { status: 0, stdout: JSON.stringify(manifest), stderr: '' };
    if (args[0] === 'blob') {
      const layerDigest = args[2].slice(args[2].indexOf('@') + 1);
      if (!Object.hasOwn(blobs, layerDigest)) return { status: 1, stdout: '', stderr: 'not found' };
      writeFileSync(args[args.indexOf('--output') + 1], blobs[layerDigest]);
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

test('upload pushes every video with its relative path, checks privacy, and only then tags it latest', async () => {
  const root = folder({ 'a.mp4': 'aaa', 'match/half-1.mkv': 'h1', 'notes.txt': 'x' });
  const tools = fakeTools();
  const lines = [];
  const result = await uploadSamples([root], { run: tools.run, fetchImpl: registry(), log: (line) => lines.push(line), now: NOW });
  assert.equal(result.exitCode, 0);
  const push = tools.calls.find((call) => call.command === 'oras' && call.args[0] === 'push');
  assert.equal(push.args[1], 'ghcr.io/dev/socalytics-samples:20261028-150000', 'lowercased owner and a UTC tag');
  assert.equal(push.cwd, discoverVideos(root).root);
  assert.deepEqual(push.args.slice(-2), ['a.mp4:video/mp4', 'match/half-1.mkv:video/x-matroska']);
  assert.deepEqual(tools.state.tags, [[`ghcr.io/dev/socalytics-samples@${DIGEST}`, 'latest']]);
  const order = tools.calls.map((call) => `${call.command} ${call.args[0]} ${call.args[1] ?? ''}`);
  assert.ok(order.lastIndexOf('gh api /user/packages/container/socalytics-samples') < order.findIndex((item) => item.startsWith('oras tag')), 'tagged after the privacy check');
  const login = tools.calls.find((call) => call.command === 'oras' && call.args[0] === 'login');
  assert.equal(login.input, 'gho_secret\n', 'the token goes through stdin');
  assert.ok(tools.calls.every((call) => !call.args.join(' ').includes('gho_secret')), 'never in arguments');
  assert.equal(existsSync(push.args[push.args.indexOf('--registry-config') + 1]), false, 'the temporary registry config is deleted');
  assert.deepEqual(result.files, [{ path: 'a.mp4', size: 3, digest: sha('aaa') }, { path: 'match/half-1.mkv', size: 2, digest: sha('h1') }]);
  assert.ok(lines.includes('Privacy check passed: the package is private, linked to no repository other than Dev/socalytics, and refuses anonymous pulls.'));
  assert.ok(lines.includes('Uploaded 2 file(s) as ghcr.io/dev/socalytics-samples:latest (also :20261028-150000).'));
  assert.ok(lines.includes(`Digest:   ghcr.io/dev/socalytics-samples@${DIGEST}`));
  assert.ok(lines.includes('Download: node tools/download-samples.mjs'));
  assert.ok(lines.some((line) => /notes\.txt \(not a video\)/.test(line)));
  await assert.rejects(uploadSamples([root, '--manifest', 'x.json'], { run: tools.run, fetchImpl: registry(), log: silent, now: NOW }), /Unknown option: --manifest/);
  await assert.rejects(uploadSamples([root, '--package', 'x'], { run: tools.run, fetchImpl: registry(), log: silent, now: NOW }), /Unknown option: --package/, 'the repository defines the package');
});

test('upload only works for the repository owner, and the package can be overridden for a throwaway test', async () => {
  const stranger = fakeTools({ login: 'someone' });
  await assert.rejects(uploadSamples([folder({ 'a.mp4': 'a' })], { run: stranger.run, fetchImpl: registry(), log: silent, now: NOW }), /belongs to dev; you are logged in to gh as someone\. Only dev can upload samples/);
  assert.ok(!stranger.calls.some((call) => call.command === 'oras' && call.args[0] === 'push'));
  const tools = fakeTools();
  await uploadSamples([folder({ 'a.mp4': 'a' })], { run: tools.run, fetchImpl: registry(), log: silent, now: NOW, env: { SOCALYTICS_SAMPLES_PACKAGE: 'socalytics-samples-selftest' } });
  assert.equal(tools.calls.find((call) => call.args[0] === 'push').args[1], 'ghcr.io/dev/socalytics-samples-selftest:20261028-150000');
});

test('upload refreshes missing scopes, and a dry run checks everything but pushes nothing', async () => {
  const tools = fakeTools({ scopes: "'repo'" });
  const lines = [];
  await uploadSamples([folder({ 'a.mp4': 'a' }), '--dry-run'], { run: tools.run, fetchImpl: registry(), log: (line) => lines.push(line), now: NOW });
  assert.deepEqual(tools.calls.find((call) => call.args[1] === 'refresh').args, ['auth', 'refresh', '-h', 'github.com', '-s', 'write:packages,read:packages,delete:packages']);
  assert.ok(!tools.calls.some((call) => call.command === 'oras' && ['push', 'tag'].includes(call.args[0])));
  assert.ok(lines.some((line) => /^Dry run: would push 1 file\(s\) as ghcr\.io\/dev\/socalytics-samples:20261028-150000 and tag it latest/.test(line)));
  await assert.rejects(uploadSamples([folder({ 'a.mp4': 'a' })], { run: fakeTools({ scopes: "'repo'", refresh: 1 }).run, fetchImpl: registry(), log: silent, now: NOW }), /Could not add the scopes write:packages, read:packages, delete:packages/);
});

test('upload accepts a package linked to this repository, which gives its workflows read access', async () => {
  const tools = fakeTools({ before: { visibility: 'private', repository: { full_name: 'dev/SocAlytics' } }, after: { visibility: 'private', repository: { full_name: 'dev/SocAlytics' } } });
  assert.equal((await uploadSamples([folder({ 'a.mp4': 'a' })], { run: tools.run, fetchImpl: registry(), log: silent, now: NOW })).exitCode, 0);
  assert.deepEqual(tools.state.tags, [[`ghcr.io/dev/socalytics-samples@${DIGEST}`, 'latest']]);
});

test('upload never pushes into a public package or one linked to another repository', async () => {
  for (const [before, reason] of [[{ visibility: 'public' }, /visibility is public, not private/], [{ visibility: 'private', repository: { full_name: 'dev/other' } }, /linked to the repository dev\/other, not to Dev\/socalytics/]]) {
    const tools = fakeTools({ before });
    await assert.rejects(uploadSamples([folder({ 'a.mp4': 'a' })], { run: tools.run, fetchImpl: registry(), log: silent, now: NOW }), reason);
    assert.ok(!tools.calls.some((call) => call.command === 'oras' && ['login', 'push', 'tag'].includes(call.args[0])), 'nothing was pushed');
  }
});

test('upload deletes a pushed version that is not private, can be pulled anonymously, or cannot be checked, and never tags it', async () => {
  for (const [options, reason] of [
    [{ after: { visibility: 'public' } }, /visibility is public/],
    [{ after: { visibility: 'private' }, fetch: { anonymous: true } }, /an anonymous pull is allowed/],
    [{ after: { visibility: 'private' }, fetch: { authenticated: false } }, /authenticated pull failed/],
  ]) {
    const tools = fakeTools(options);
    await assert.rejects(uploadSamples([folder({ 'a.mp4': 'a' })], { run: tools.run, fetchImpl: registry(options.fetch), log: silent, now: NOW }), (error) => reason.test(error.message) && /so it was deleted/.test(error.message));
    assert.deepEqual(tools.state.deleted, ['/user/packages/container/socalytics-samples/versions/7']);
    assert.deepEqual(tools.state.tags, [], 'a failed version never becomes latest');
  }
});

const layer = (title, content) => ({ digest: sha(content), size: content.length, annotations: { 'org.opencontainers.image.title': title } });
const sampleManifest = (layers) => ({ artifactType: ARTIFACT_TYPE, layers });

test('download fetches the latest version by default, restores nested folders, and pins the digest it got', async () => {
  const output = path.join(temporary, `out-${counter++}`);
  const tools = fakeTools({ manifest: sampleManifest([layer('a.mp4', 'aaa'), layer('match/half-1.mkv', 'h1')]), registry: { [sha('aaa')]: 'aaa', [sha('h1')]: 'h1' } });
  const lines = [];
  const result = await downloadSamples(['--output', output], { run: tools.run, log: (line) => lines.push(line) });
  assert.deepEqual(tools.calls.find((call) => call.args[0] === 'resolve').args.slice(0, 2), ['resolve', 'ghcr.io/dev/socalytics-samples:latest']);
  assert.ok(lines.includes(`ghcr.io/dev/socalytics-samples:latest is ghcr.io/dev/socalytics-samples@${DIGEST}`));
  assert.deepEqual([result.downloaded, result.kept, result.removed], [['a.mp4', 'match/half-1.mkv'], [], []]);
  assert.equal(readFileSync(path.join(output, 'match', 'half-1.mkv'), 'utf8'), 'h1');
  assert.equal(readFileSync(path.join(output, '.samples-reference'), 'utf8'), `ghcr.io/dev/socalytics-samples@${DIGEST}\n`);
  assert.ok(!tools.calls.some((call) => call.args[1] === 'refresh'), 'read:packages (implied by write:packages) is enough');
  const tagged = fakeTools({ manifest: sampleManifest([layer('a.mp4', 'aaa')]), registry: { [sha('aaa')]: 'aaa' } });
  await downloadSamples(['20261028-153012', '--output', path.join(temporary, `out-${counter++}`)], { run: tagged.run, log: silent });
  assert.equal(tagged.calls.find((call) => call.args[0] === 'resolve').args[1], 'ghcr.io/dev/socalytics-samples:20261028-153012', 'a tag of the repository package');
  assert.ok(tagged.calls.find((call) => call.args[0] === 'blob').args[2].startsWith('ghcr.io/dev/socalytics-samples@sha256:'));
  const pinned = fakeTools({ manifest: sampleManifest([layer('a.mp4', 'aaa')]), registry: { [sha('aaa')]: 'aaa' } });
  const pinnedResult = await downloadSamples([DIGEST, '--output', path.join(temporary, `out-${counter++}`)], { run: pinned.run, log: silent });
  assert.ok(!pinned.calls.some((call) => call.args[0] === 'resolve'), 'a digest needs no resolving');
  assert.equal(pinnedResult.reference, `ghcr.io/dev/socalytics-samples@${DIGEST}`);
  const throwaway = fakeTools({ manifest: sampleManifest([layer('a.mp4', 'aaa')]), registry: { [sha('aaa')]: 'aaa' } });
  await downloadSamples(['--output', path.join(temporary, `out-${counter++}`)], { run: throwaway.run, log: silent, env: { SOCALYTICS_SAMPLES_PACKAGE: 'socalytics-samples-selftest' } });
  assert.equal(throwaway.calls.find((call) => call.args[0] === 'resolve').args[1], 'ghcr.io/dev/socalytics-samples-selftest:latest');
  await downloadSamples(['--output', path.join(temporary, `out-${counter++}`)], { run: fakeTools({ login: 'reader', manifest: sampleManifest([layer('a.mp4', 'aaa')]), registry: { [sha('aaa')]: 'aaa' } }).run, log: silent });
});

test('download brings a folder it filled in line with a newer version: new, changed, unchanged, moved, and removed videos', async () => {
  const output = path.join(temporary, `out-${counter++}`);
  const first = fakeTools({ manifest: sampleManifest([layer('a.mp4', 'aaa'), layer('old/b.mkv', 'bbb'), layer('c.webm', 'c1'), layer('gone.mov', 'g')]), registry: { [sha('aaa')]: 'aaa', [sha('bbb')]: 'bbb', [sha('c1')]: 'c1', [sha('g')]: 'g' } });
  await downloadSamples(['--output', output], { run: first.run, log: silent });
  writeFileSync(path.join(output, 'notes.txt'), 'mine');
  // The next version moved b.mkv, changed c.webm, dropped gone.mov, and added d.mp4.
  const next = fakeTools({ manifest: sampleManifest([layer('a.mp4', 'aaa'), layer('new/b.mkv', 'bbb'), layer('c.webm', 'c2'), layer('d.mp4', 'ddd')]), registry: { [sha('bbb')]: 'bbb', [sha('c2')]: 'c2', [sha('ddd')]: 'ddd' } });
  const result = await downloadSamples(['--output', output], { run: next.run, log: silent });
  assert.deepEqual(result.kept, ['a.mp4']);
  assert.deepEqual(result.copied, ['new/b.mkv'], 'a moved video is copied locally');
  assert.deepEqual(result.downloaded, ['c.webm', 'd.mp4']);
  assert.deepEqual(result.removed.sort(), ['gone.mov', 'old/b.mkv']);
  assert.equal(readFileSync(path.join(output, 'new', 'b.mkv'), 'utf8'), 'bbb');
  assert.ok(!next.calls.some((call) => call.args[0] === 'blob' && call.args[2].endsWith(sha('bbb'))), 'a moved video is not downloaded again');
  assert.equal(readFileSync(path.join(output, 'c.webm'), 'utf8'), 'c2');
  assert.equal(existsSync(path.join(output, 'old')), false, 'emptied folders are removed');
  assert.equal(readFileSync(path.join(output, 'notes.txt'), 'utf8'), 'mine', 'files that are not videos are left alone');
  assert.ok(!next.calls.some((call) => call.args[0] === 'blob' && call.args[2].endsWith(sha('aaa'))), 'an unchanged video is not downloaded again');
});

test('download refuses foreign folders, other artifacts, unsafe paths, and corrupt downloads without leaving partial files', async () => {
  const manifest = sampleManifest([layer('a.mp4', 'aaa')]);
  const foreign = folder({ 'mine.mp4': 'x' });
  await assert.rejects(downloadSamples(['--output', foreign], { run: fakeTools({ manifest }).run, log: silent }), /not empty and was not filled by this script/);
  assert.ok(existsSync(path.join(foreign, 'mine.mp4')));
  await assert.rejects(downloadSamples(['ghcr.io/dev/s:latest'], { run: fakeTools().run, log: silent }), /Expected a tag like latest/, 'only the version, not a reference');
  await assert.rejects(downloadSamples(['--package', 'x'], { run: fakeTools().run, log: silent }), /Unknown option: --package/);
  const other = fakeTools({ manifest: { ...manifest, artifactType: 'application/vnd.oci.image.config.v1+json' } });
  await assert.rejects(downloadSamples(['--output', path.join(temporary, `out-${counter++}`)], { run: other.run, log: silent }), /is not a sample artifact/);
  for (const title of ['../escape.mp4', '/abs.mp4', 'C:/x.mp4', 'a//b.mp4']) {
    const unsafe = fakeTools({ manifest: sampleManifest([layer(title, 'x')]) });
    await assert.rejects(downloadSamples(['--output', path.join(temporary, `out-${counter++}`)], { run: unsafe.run, log: silent }), /unsafe path/, title);
  }
  const output = path.join(temporary, `out-${counter++}`);
  const corrupt = fakeTools({ manifest, registry: { [sha('aaa')]: 'xxx' } });
  await assert.rejects(downloadSamples(['--output', output], { run: corrupt.run, log: silent }), /a\.mp4 does not match its digest/);
  assert.equal(existsSync(path.join(output, 'a.mp4')), false);
  assert.equal(existsSync(path.join(output, 'a.mp4.partial')), false);
});