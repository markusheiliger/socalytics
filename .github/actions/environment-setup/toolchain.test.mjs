import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const installer = path.join(here, 'install-analyst-media.sh');
const realDeclaration = JSON.parse(readFileSync(path.join(here, 'analyst-media-toolchain.json'), 'utf8'));
const temporary = mkdtempSync(path.join(tmpdir(), 'toolchain-test-'));
after(() => rmSync(temporary, { recursive: true, force: true }));

let counter = 0;
const scratch = () => {
  const directory = path.join(temporary, `case-${counter++}`);
  mkdirSync(directory, { recursive: true });
  return directory;
};

const sha256 = (file) => createHash('sha256').update(readFileSync(file)).digest('hex');

function script(directory, relative, output) {
  const file = path.join(directory, relative);
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, `#!/usr/bin/env bash\necho "${output}"\n`);
  chmodSync(file, 0o755);
}

// Builds a fixture archive whose single tool prints the given version.
function fixtureArchive(directory, { root = 'pkg', version = 'fixture 1.0', extra } = {}) {
  const source = path.join(directory, 'source');
  rmSync(source, { recursive: true, force: true });
  script(source, `${root}/bin/fixture`, version);
  writeFileSync(path.join(source, root, 'LICENSE.txt'), 'fixture license\n');
  if (extra) extra(source);
  const archive = path.join(directory, 'fixture.tar.gz');
  execFileSync('tar', ['-czf', archive, '-C', source, '.']);
  return archive;
}

function declaration(archive, overrides = {}) {
  return {
    schemaVersion: 1,
    supportedHost: { os: 'Linux', architecture: 'x86_64' },
    installRoot: 'fixture-tools',
    tools: [{
      id: 'fixture',
      name: 'Fixture',
      version: '1.0',
      archive: {
        url: `file://${archive}`,
        fileName: 'fixture.tar.gz',
        format: 'tar.gz',
        sha256: sha256(archive),
      },
      extractedRoot: 'pkg',
      executables: { python: 'pkg/bin/fixture' },
      versionCheck: { executable: 'python', arguments: [], expectedOutput: 'fixture 1.0' },
      environmentVariable: 'SOCALYTICS_FIXTURE',
      pathDirectories: ['pkg/bin'],
      licenseLocations: ['pkg/LICENSE.txt'],
      provenanceLocation: 'https://example.invalid/provenance',
      ...overrides,
    }],
  };
}

function install(directory, toolchain, env = {}) {
  const file = path.join(directory, 'toolchain.json');
  writeFileSync(file, JSON.stringify(toolchain));
  const githubPath = path.join(directory, 'github-path');
  const githubEnv = path.join(directory, 'github-env');
  writeFileSync(githubPath, '');
  writeFileSync(githubEnv, '');
  const result = spawnSync('bash', [installer], {
    encoding: 'utf8',
    env: {
      ...process.env,
      ANALYST_MEDIA_TOOLCHAIN: file,
      ANALYST_MEDIA_INSTALL_ROOT: path.join(directory, 'install'),
      GITHUB_PATH: githubPath,
      GITHUB_ENV: githubEnv,
      ...env,
    },
  });
  return { ...result, githubPath: readFileSync(githubPath, 'utf8'), githubEnv: readFileSync(githubEnv, 'utf8') };
}

function assertFailure(result, tool, stage) {
  assert.notEqual(result.status, 0, result.stdout);
  assert.match(result.stderr, new RegExp(`tool=${tool} stage=${stage}:`));
  assert.match(result.stderr, /Recommended action: /);
  assert.equal((result.stderr.match(/Recommended action:/g) ?? []).length, 1);
}

describe('committed toolchain declaration', () => {
  it('pins exact archives with digests and no floating selectors', () => {
    assert.deepEqual(realDeclaration.supportedHost, { os: 'Linux', architecture: 'x86_64' });
    for (const tool of realDeclaration.tools) {
      assert.match(tool.archive.sha256, /^[0-9a-f]{64}$/);
      assert.doesNotMatch(tool.archive.url, /latest|nightly/i);
    }
  });
});

describe('install-analyst-media.sh', () => {
  it('installs a verified archive, retains notices, and publishes paths', () => {
    const directory = scratch();
    const result = install(directory, declaration(fixtureArchive(directory)));
    assert.equal(result.status, 0, result.stderr);
    const root = path.join(directory, 'install');
    assert.ok(existsSync(path.join(root, 'pkg/bin/fixture')));
    assert.equal(readFileSync(path.join(root, 'notices/fixture/LICENSE.txt'), 'utf8'), 'fixture license\n');
    assert.match(readFileSync(path.join(root, 'notices/fixture/PROVENANCE.txt'), 'utf8'), /example\.invalid/);
    assert.ok(!existsSync(path.join(root, 'downloads')));
    assert.equal(result.githubPath.trim(), path.join(root, 'pkg/bin'));
    assert.equal(result.githubEnv.trim(), `SOCALYTICS_FIXTURE=${path.join(root, 'pkg/bin/fixture')}`);
  });

  it('rejects an unsupported host before downloading', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory));
    toolchain.tools[0].archive.url = `file://${directory}/missing.tar.gz`;
    const result = install(directory, toolchain, { ANALYST_MEDIA_HOST_OS: 'Darwin' });
    assertFailure(result, 'host', 'platform');
    assert.match(result.stderr, /Darwin\/x86_64 is unsupported/);
    const arch = install(directory, toolchain, { ANALYST_MEDIA_HOST_ARCH: 'aarch64' });
    assertFailure(arch, 'host', 'platform');
  });

  it('rejects an invalid declaration', () => {
    const directory = scratch();
    const result = install(directory, { schemaVersion: 2, tools: [] });
    assertFailure(result, 'toolchain', 'declaration');
  });

  it('names the tool and stage when the download fails', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory));
    toolchain.tools[0].archive.url = `file://${directory}/missing.tar.gz`;
    assertFailure(install(directory, toolchain), 'fixture', 'download');
  });

  it('rejects a digest mismatch before extracting', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory));
    toolchain.tools[0].archive.sha256 = '0'.repeat(64);
    const result = install(directory, toolchain);
    assertFailure(result, 'fixture', 'integrity');
    assert.match(result.stderr, /never bypass/);
    assert.ok(!existsSync(path.join(directory, 'install/pkg')));
  });

  it('rejects a malformed digest', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory));
    toolchain.tools[0].archive.sha256 = 'abc';
    assertFailure(install(directory, toolchain), 'fixture', 'declaration');
  });

  it('rejects path traversal entries', () => {
    const directory = scratch();
    const source = path.join(directory, 'traversal');
    script(source, 'pkg/bin/fixture', 'fixture 1.0');
    writeFileSync(path.join(source, 'evil'), 'x');
    const archive = path.join(directory, 'fixture.tar.gz');
    execFileSync('tar', ['-czf', archive, '-C', source, '--transform', 's,^evil$,../evil,', 'pkg', 'evil']);
    assertFailure(install(directory, declaration(archive)), 'fixture', 'extract');
    assert.ok(!existsSync(path.join(directory, 'install/evil')));
  });

  it('rejects absolute entries', () => {
    const directory = scratch();
    const source = path.join(directory, 'absolute');
    script(source, 'pkg/bin/fixture', 'fixture 1.0');
    const archive = path.join(directory, 'fixture.tar.gz');
    execFileSync('tar', ['-czPf', archive, path.join(source, 'pkg')]);
    assertFailure(install(directory, declaration(archive)), 'fixture', 'extract');
  });

  it('rejects links that leave the archive root', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, {
      extra: (source) => symlinkSync('../../outside', path.join(source, 'pkg/escape')),
    });
    assertFailure(install(directory, declaration(archive)), 'fixture', 'extract');
    const absolute = fixtureArchive(directory, {
      extra: (source) => symlinkSync('/etc/passwd', path.join(source, 'pkg/escape')),
    });
    assertFailure(install(directory, declaration(absolute)), 'fixture', 'extract');
  });

  it('rejects an unexpected layout', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, { root: 'other' });
    assertFailure(install(directory, declaration(archive)), 'fixture', 'layout');
  });

  it('rejects a missing declared executable', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory), { executables: { python: 'pkg/bin/missing' } });
    assertFailure(install(directory, toolchain), 'fixture', 'layout');
  });

  it('rejects unsafe declared paths', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory), { executables: { python: '../pkg/bin/fixture' } });
    assertFailure(install(directory, toolchain), 'fixture', 'declaration');
  });

  it('rejects a missing license file', () => {
    const directory = scratch();
    const toolchain = declaration(fixtureArchive(directory), { licenseLocations: ['pkg/NOTICE'] });
    assertFailure(install(directory, toolchain), 'fixture', 'notices');
  });

  it('rejects a wrong executable version', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, { version: 'fixture 2.0' });
    const result = install(directory, declaration(archive));
    assertFailure(result, 'fixture', 'version');
    assert.match(result.stderr, /fixture 2\.0/);
  });

  it('rejects an executable that fails to run', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, {
      extra: (source) => {
        writeFileSync(path.join(source, 'pkg/bin/fixture'), '#!/usr/bin/env bash\nexit 3\n');
      },
    });
    assertFailure(install(directory, declaration(archive)), 'fixture', 'version');
  });

  it('verifies additional executables of one tool', () => {
    const directory = scratch();
    const archive = fixtureArchive(directory, {
      extra: (source) => script(source, 'pkg/bin/probe', 'probe 9.9'),
    });
    const toolchain = declaration(archive, {
      executables: { python: 'pkg/bin/fixture', probe: 'pkg/bin/probe' },
      versionCheck: {
        executable: 'python',
        arguments: [],
        expectedOutput: 'fixture 1.0',
        additionalChecks: [{ executable: 'probe', arguments: [], expectedOutput: 'probe 1.0' }],
      },
    });
    assertFailure(install(directory, toolchain), 'fixture', 'version');
  });
});
