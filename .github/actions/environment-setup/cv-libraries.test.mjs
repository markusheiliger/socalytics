import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const installer = path.join(here, 'install-analyst-cv.sh');
const temporary = mkdtempSync(path.join(tmpdir(), 'cv-libraries-test-'));
after(() => rmSync(temporary, { recursive: true, force: true }));

const realPython = spawnSync('bash', ['-c', 'command -v python3'], { encoding: 'utf8' }).stdout.trim();

let counter = 0;
const scratch = () => {
  const directory = path.join(temporary, `case-${counter++}`);
  mkdirSync(directory, { recursive: true });
  return directory;
};

const sha256 = (file) => createHash('sha256').update(readFileSync(file)).digest('hex');

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buffer) {
  let c = 0xffffffff;
  for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

// Minimal stored (uncompressed) zip writer; enough for wheels read by Python's zipfile.
function zip(entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const [name, content] of Object.entries(entries)) {
    const nameBytes = Buffer.from(name);
    const data = Buffer.from(content);
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBytes.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBytes.length, 28);
    central.writeUInt32LE(offset, 42);
    locals.push(local, nameBytes, data);
    centrals.push(central, nameBytes);
    offset += local.length + nameBytes.length + data.length;
  }
  const centralBuffer = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(Object.keys(entries).length, 8);
  end.writeUInt16LE(Object.keys(entries).length, 10);
  end.writeUInt32LE(centralBuffer.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, centralBuffer, end]);
}

const normalized = (name) => name.replace(/[-_.]+/g, '_');

// Fixture wheel specification; `libs` lists bundled shared libraries placed in `<name>.libs/`.
const defaultWheels = [
  { name: 'opencv-python-headless', version: '1.0.0', role: 'library', license: 'Apache-2.0', libs: ['libavcodec-fixture.so.1'] },
  { name: 'numpy', version: '2.0.0', role: 'library', license: 'BSD-3-Clause', libs: [] },
  { name: 'onnxruntime', version: '3.0.0', role: 'library', license: 'MIT', libs: [] },
  { name: 'flatbuffers', version: '4.0.0', role: 'dependency', license: 'Apache-2.0', libs: [] },
];

// Writes the fixture wheels into `directory`; returns their declaration entries with computed digests.
function buildWheels(directory, specs = defaultWheels, { tag = 'cp312-cp312-manylinux_2_28_x86_64' } = {}) {
  mkdirSync(directory, { recursive: true });
  return specs.map((spec) => {
    const base = normalized(spec.name);
    const distInfo = `${base}-${spec.version}.dist-info`;
    const fileName = `${base}-${spec.version}-${spec.tag ?? tag}.whl`;
    const entries = {
      [`${base}/__init__.py`]: '',
      [`${distInfo}/METADATA`]: `Metadata-Version: 2.4\nName: ${spec.name}\nVersion: ${spec.version}\nLicense-Expression: ${spec.license}\n`,
      [`${distInfo}/licenses/LICENSE.txt`]: `${spec.name} fixture license\n`,
      ...Object.fromEntries((spec.libs ?? []).map((lib) => [`${base}.libs/${lib}`, `binary ${lib}`])),
    };
    const file = path.join(directory, fileName);
    writeFileSync(file, zip(entries));
    return {
      name: spec.name,
      version: spec.version,
      fileName,
      url: `https://fixture.invalid/${fileName}`,
      sha256: sha256(file),
      publisher: 'pypi.org',
      projectUrl: `https://fixture.invalid/${spec.name}`,
      licenseExpression: spec.license,
      licenseText: `${distInfo}/licenses/LICENSE.txt`,
      role: spec.role,
    };
  });
}

// Computes a declaration for the built wheels in the shape of analyst-cv-libraries.json.
function cvDeclaration(wheels, overrides = {}) {
  return {
    schemaVersion: 1,
    supportedHost: { os: 'Linux', architecture: 'x86_64' },
    interpreter: { python: '3.12', wheelTag: 'cp312' },
    wheels,
    deniedPackages: ['opencv-python', 'opencv-contrib-python', 'opencv-contrib-python-headless', 'onnxruntime-gpu', 'onnxruntime-*', 'nvidia-*', 'tensorrt*', 'cuda-*', 'triton'],
    licenseAllowList: ['MIT', 'BSD-2-Clause', 'BSD-3-Clause', 'Apache-2.0', 'ISC', 'Zlib', 'PSF-2.0', 'MPL-2.0', '0BSD', 'CC0-1.0'],
    lgplLicenses: ['LGPL-2.1-only', 'LGPL-2.1-or-later', 'LGPL-3.0-only', 'LGPL-3.0-or-later'],
    runtimeExceptionLicenses: ['GPL-3.0-or-later WITH GCC-exception-3.1'],
    deniedLicenseFamilies: ['GPL', 'AGPL', 'SSPL'],
    deniedLibraries: ['libx264', 'libx265', 'libxvid', 'libvidstab', 'libfdk'],
    licenseTable: [],
    ...overrides,
  };
}

function executable(file, content) {
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, content);
  chmodSync(file, 0o755);
}

const SMOKE_LINE = 'Verified synthetic CV smoke check: decoded 60 frames, NumPy mean 12.5, ONNX Runtime Add graph executed';

// Writes stub curl, uv, python3, and ffmpeg commands into `bin`. Every call is appended to `calls.log` in `state`.
// Behavior is steered per run through STUB_* environment variables:
//   STUB_CURL_WHEELS (directory served by basename), STUB_CURL_FAIL, STUB_UV_FAIL, STUB_SMOKE_FAIL,
//   STUB_SMOKE_TIMEOUT, STUB_SMOKE_OUTPUT (replaces the smoke line), STUB_TIMEOUT (timeout exits 124).
function writeStubs(bin, state) {
  const log = path.join(state, 'calls.log');
  executable(path.join(bin, 'curl'), `#!/usr/bin/env bash
echo "curl $*" >> "${log}"
output=""
url=""
while [ $# -gt 0 ]; do
  case "$1" in
    -o|--output) output="$2"; shift 2 ;;
    -*) shift ;;
    *) url="$1"; shift ;;
  esac
done
[ -z "\${STUB_CURL_FAIL:-}" ] || exit 22
source="\${STUB_CURL_WHEELS:-}/$(basename "$url")"
[ -f "$source" ] || exit 22
if [ -n "$output" ]; then cp "$source" "$output"; else cat "$source"; fi
`);
  const smoke = `
echo "smoke $*" >> "${log}"
[ -z "\${STUB_SMOKE_FAIL:-}" ] || { echo "stub smoke failure: \${STUB_SMOKE_FAIL}" >&2; exit 1; }
[ -z "\${STUB_SMOKE_TIMEOUT:-}" ] || sleep 30
echo "\${STUB_SMOKE_OUTPUT:-${SMOKE_LINE}}"
`;
  executable(path.join(bin, 'uv'), `#!/usr/bin/env bash
echo "uv $*" >> "${log}"
[ -z "\${STUB_UV_FAIL:-}" ] || { echo "stub uv failure" >&2; exit 1; }
if [ "$1" = "venv" ]; then
  target="\${@: -1}"
  mkdir -p "$target/bin"
  printf '#!/usr/bin/env bash\\n%s\\n' '${smoke.replace(/'/g, "'\\''").replace(/\n/g, ' ; ').replace(/^ ; /, '')}' > "$target/bin/python"
  chmod +x "$target/bin/python"
fi
exit 0
`);
  executable(path.join(bin, 'python3'), `#!/usr/bin/env bash
echo "python3 $*" >> "${log}"
case "$*" in
  *analyst-cv-smoke.py*) ${smoke.trim().replace(/\n/g, '\n  ')} ;;
  *) exec "${realPython}" "$@" ;;
esac
`);
  // STUB_TIMEOUT simulates an expired bound without waiting for it.
  executable(path.join(bin, 'timeout'), `#!/usr/bin/env bash
echo "timeout $*" >> "${log}"
[ -z "\${STUB_TIMEOUT:-}" ] || exit 124
while [[ "$1" == -* ]]; do shift; done
shift
exec "$@"
`);
  executable(path.join(bin, 'ffmpeg'), `#!/usr/bin/env bash
echo "ffmpeg $*" >> "${log}"
echo "ffmpeg version 8.1-fixture"
`);
  return log;
}

// Runs install-analyst-cv.sh against a fixture declaration and wheels with the stubs first on PATH.
function runInstaller(directory, { declaration, wheelsDirectory, env = {}, declarationText } = {}) {
  const bin = path.join(directory, 'bin');
  const state = path.join(directory, 'state');
  mkdirSync(state, { recursive: true });
  const log = writeStubs(bin, state);
  const declarationFile = path.join(directory, 'analyst-cv-libraries.json');
  if (declaration !== undefined) writeFileSync(declarationFile, declarationText ?? JSON.stringify(declaration));
  const githubPath = path.join(directory, 'github-path');
  const githubEnv = path.join(directory, 'github-env');
  writeFileSync(githubPath, '');
  writeFileSync(githubEnv, '');
  const installRoot = path.join(directory, 'install');
  const result = spawnSync('bash', [installer], {
    encoding: 'utf8',
    env: {
      ...process.env,
      PATH: `${bin}:${process.env.PATH}`,
      ANALYST_CV_LIBRARIES: declarationFile,
      ANALYST_MEDIA_INSTALL_ROOT: installRoot,
      ANALYST_MEDIA_HOST_OS: 'Linux',
      ANALYST_MEDIA_HOST_ARCH: 'x86_64',
      STUB_CURL_WHEELS: wheelsDirectory ?? '',
      GITHUB_PATH: githubPath,
      GITHUB_ENV: githubEnv,
      ...env,
    },
  });
  return {
    ...result,
    installRoot,
    calls: existsSync(log) ? readFileSync(log, 'utf8').split('\n').filter(Boolean) : [],
    githubPath: readFileSync(githubPath, 'utf8'),
    githubEnv: readFileSync(githubEnv, 'utf8'),
  };
}

// Builds fixture wheels and a matching declaration in one step.
function fixture(overrides = {}, specs = defaultWheels, options = {}) {
  const directory = scratch();
  const wheelsDirectory = path.join(directory, 'served');
  const wheels = buildWheels(wheelsDirectory, specs, options);
  return { directory, wheelsDirectory, wheels, declaration: cvDeclaration(wheels, overrides) };
}

function assertCvFailure(result, library, stage) {
  assert.notEqual(result.status, 0, result.stdout);
  assert.match(result.stderr, new RegExp(`^ERROR: Analyst CV libraries: library=${library} stage=${stage}: `, 'm'));
  assert.equal((result.stderr.match(/Recommended action:/g) ?? []).length, 1);
}

describe('cv-libraries test helpers', () => {
  it('builds wheels whose declaration carries their SHA-256 values', () => {
    const { wheelsDirectory, wheels } = fixture();
    assert.equal(wheels.length, defaultWheels.length);
    for (const wheel of wheels) {
      assert.match(wheel.sha256, /^[0-9a-f]{64}$/);
      assert.equal(sha256(path.join(wheelsDirectory, wheel.fileName)), wheel.sha256);
      assert.ok(wheel.url.endsWith(wheel.fileName));
    }
  });

  it('writes wheels that Python reads with METADATA, licenses, and bundled libraries', () => {
    const { wheelsDirectory, wheels } = fixture();
    const file = path.join(wheelsDirectory, wheels[0].fileName);
    const listing = spawnSync(realPython, ['-c', 'import sys, zipfile; z = zipfile.ZipFile(sys.argv[1]); assert z.testzip() is None; print("\\n".join(z.namelist()))', file], { encoding: 'utf8' });
    assert.equal(listing.status, 0, listing.stderr);
    assert.match(listing.stdout, /\.dist-info\/METADATA/);
    assert.match(listing.stdout, /\.dist-info\/licenses\/LICENSE\.txt/);
    assert.match(listing.stdout, /\.libs\/libavcodec-fixture\.so\.1/);
  });

  it('serves wheels through the stub curl and records the calls', () => {
    const { directory, wheelsDirectory, wheels } = fixture();
    const bin = path.join(directory, 'bin');
    const state = path.join(directory, 'state');
    mkdirSync(state, { recursive: true });
    const log = writeStubs(bin, state);
    const target = path.join(directory, 'out.whl');
    const env = { ...process.env, PATH: `${bin}:${process.env.PATH}`, STUB_CURL_WHEELS: wheelsDirectory };
    const ok = spawnSync('curl', ['--fail', '-o', target, wheels[0].url], { env });
    assert.equal(ok.status, 0);
    assert.equal(sha256(target), wheels[0].sha256);
    const failed = spawnSync('curl', ['-o', target, 'https://fixture.invalid/missing.whl'], { env });
    assert.equal(failed.status, 22);
    assert.match(readFileSync(log, 'utf8'), /^curl --fail -o /m);
  });

  it('stubs uv, python3, and ffmpeg without reaching the network or a product project', () => {
    const { directory } = fixture();
    const bin = path.join(directory, 'bin');
    const state = path.join(directory, 'state');
    mkdirSync(state, { recursive: true });
    const log = writeStubs(bin, state);
    const env = { ...process.env, PATH: `${bin}:${process.env.PATH}` };
    const venv = path.join(directory, 'venv');
    assert.equal(spawnSync('uv', ['venv', venv], { env }).status, 0);
    const smoke = spawnSync(path.join(venv, 'bin/python'), ['analyst-cv-smoke.py'], { env, encoding: 'utf8' });
    assert.equal(smoke.stdout.trim(), SMOKE_LINE);
    const failing = spawnSync('python3', ['analyst-cv-smoke.py'], { env: { ...env, STUB_SMOKE_FAIL: '1' } });
    assert.equal(failing.status, 1);
    assert.equal(spawnSync('python3', ['-c', 'print(1)'], { env, encoding: 'utf8' }).stdout.trim(), '1');
    assert.match(spawnSync('ffmpeg', ['-version'], { env, encoding: 'utf8' }).stdout, /ffmpeg version/);
    assert.match(readFileSync(log, 'utf8'), /^uv venv /m);
  });
});

const declaredUrls = (wheels) => new Set(wheels.map((wheel) => wheel.url));

// A failing installer must not fall back to an unpinned, unhashed, or source-built install.
function assertNoFallback(result, wheels) {
  for (const call of result.calls.filter((line) => line.startsWith('curl '))) {
    const urls = call.split(/\s+/).filter((word) => /^https?:/.test(word));
    assert.ok(urls.length > 0 && urls.every((url) => declaredUrls(wheels).has(url)), `undeclared download: ${call}`);
  }
  const installs = result.calls.filter((line) => /^uv pip install/.test(line));
  assert.deepEqual(installs, [], 'no install after a failed check');
  assert.deepEqual(result.calls.filter((line) => /pip download|setup\.py|--no-binary|--index-url|--extra-index-url|git clone/.test(line)), []);
}

function assertRecommendedActionLast(result) {
  const lines = result.stderr.split('\n').filter(Boolean);
  const failure = lines.findIndex((line) => line.startsWith('ERROR: Analyst CV libraries:'));
  assert.ok(failure >= 0, result.stderr);
  assert.match(lines[failure + 1] ?? '', /^Recommended action: \S/);
  assert.equal(result.status, 1);
}

describe('install-analyst-cv.sh verification stages', () => {
  it('verifies the pinned wheels and prints the success line prefix', () => {
    const { directory, wheelsDirectory, declaration } = fixture();
    const result = runInstaller(directory, { declaration, wheelsDirectory });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /^Verified Analyst CV libraries:/m);
    assert.equal(result.stderr, '');
  });

  it('downloads every declared wheel from its declared URL before installing', () => {
    const { directory, wheelsDirectory, wheels, declaration } = fixture();
    const result = runInstaller(directory, { declaration, wheelsDirectory });
    assert.equal(result.status, 0, result.stderr);
    const downloads = result.calls.filter((line) => line.startsWith('curl '));
    for (const wheel of wheels) {
      assert.equal(downloads.filter((line) => line.includes(wheel.url)).length, 1, wheel.url);
    }
    const lastDownload = result.calls.map((line, index) => (line.startsWith('curl ') ? index : -1)).reduce((a, b) => Math.max(a, b));
    const firstInstall = result.calls.findIndex((line) => /^uv pip install/.test(line));
    assert.ok(firstInstall > lastDownload, 'installation follows download and verification of all wheels');
    const smoke = result.calls.findIndex((line) => line.startsWith('smoke '));
    assert.ok(smoke > firstInstall, 'the smoke check follows installation');
  });

  it('adds nothing to GITHUB_PATH or GITHUB_ENV', () => {
    const { directory, wheelsDirectory, declaration } = fixture();
    const result = runInstaller(directory, { declaration, wheelsDirectory });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.githubPath, '');
    assert.equal(result.githubEnv, '');
  });

  it('fails in the declaration stage when the declaration is missing', () => {
    const { directory, wheelsDirectory } = fixture();
    const result = runInstaller(directory, { wheelsDirectory });
    assertCvFailure(result, '\\S+', 'declaration');
    assertRecommendedActionLast(result);
    assert.deepEqual(result.calls.filter((line) => /^(curl|uv pip) /.test(line)), []);
  });

  it('fails in the host platform stage on an unsupported host', () => {
    for (const env of [{ ANALYST_MEDIA_HOST_OS: 'Darwin' }, { ANALYST_MEDIA_HOST_ARCH: 'aarch64' }]) {
      const { directory, wheelsDirectory, declaration } = fixture();
      const result = runInstaller(directory, { declaration, wheelsDirectory, env });
      assertCvFailure(result, '\\S+', 'host platform');
      assertRecommendedActionLast(result);
      assert.deepEqual(result.calls.filter((line) => /^(curl|uv pip) /.test(line)), []);
    }
  });

  it('fails in the cv download stage when a download fails', () => {
    const { directory, wheelsDirectory, wheels, declaration } = fixture();
    const result = runInstaller(directory, { declaration, wheelsDirectory, env: { STUB_CURL_FAIL: '1' } });
    assertCvFailure(result, wheels[0].name, 'cv download');
    assertRecommendedActionLast(result);
    assertNoFallback(result, wheels);
  });

  it('fails in the cv integrity stage when a digest differs from the declaration', () => {
    const { directory, wheelsDirectory, wheels } = fixture();
    const tampered = wheels.map((wheel, index) => (index === 1 ? { ...wheel, sha256: '0'.repeat(64) } : wheel));
    const result = runInstaller(directory, { declaration: cvDeclaration(tampered), wheelsDirectory });
    assertCvFailure(result, wheels[1].name, 'cv integrity');
    assertRecommendedActionLast(result);
    assertNoFallback(result, wheels);
  });

  it('fails in the cv compatibility stage for a wheel tag incompatible with cp312', () => {
    const specs = defaultWheels.map((spec) => (spec.name === 'numpy' ? { ...spec, tag: 'cp311-cp311-manylinux_2_28_x86_64' } : spec));
    const { directory, wheelsDirectory, wheels, declaration } = fixture({}, specs);
    const result = runInstaller(directory, { declaration, wheelsDirectory });
    assertCvFailure(result, 'numpy', 'cv compatibility');
    assertRecommendedActionLast(result);
    assertNoFallback(result, wheels);
  });

  it('fails in the cv compatibility stage for a wheel tag incompatible with the host', () => {
    for (const tag of ['cp312-cp312-manylinux_2_28_aarch64', 'cp312-cp312-win_amd64', 'cp312-cp312-musllinux_1_2_x86_64']) {
      const specs = defaultWheels.map((spec) => (spec.name === 'onnxruntime' ? { ...spec, tag } : spec));
      const { directory, wheelsDirectory, wheels, declaration } = fixture({}, specs);
      const result = runInstaller(directory, { declaration, wheelsDirectory });
      assertCvFailure(result, 'onnxruntime', 'cv compatibility');
      assertRecommendedActionLast(result);
      assertNoFallback(result, wheels);
    }
  });

  it('fails in the cv compatibility stage for a source distribution', () => {
    const { directory, wheelsDirectory, wheels } = fixture();
    const fileName = 'flatbuffers-4.0.0.tar.gz';
    const file = path.join(wheelsDirectory, fileName);
    writeFileSync(file, 'source distribution fixture');
    const sdist = wheels.map((wheel) => (wheel.name === 'flatbuffers'
      ? { ...wheel, fileName, url: `https://fixture.invalid/${fileName}`, sha256: sha256(file) }
      : wheel));
    const result = runInstaller(directory, { declaration: cvDeclaration(sdist), wheelsDirectory });
    assertCvFailure(result, 'flatbuffers', 'cv compatibility');
    assertRecommendedActionLast(result);
    assertNoFallback(result, sdist);
  });

  it('fails in the cv compatibility stage when the wheel version differs from the declaration', () => {
    const { directory, wheelsDirectory, wheels } = fixture();
    const drifted = wheels.map((wheel) => (wheel.name === 'opencv-python-headless' ? { ...wheel, version: '1.0.1' } : wheel));
    const result = runInstaller(directory, { declaration: cvDeclaration(drifted), wheelsDirectory });
    assertCvFailure(result, 'opencv-python-headless', 'cv compatibility');
    assertRecommendedActionLast(result);
    assertNoFallback(result, drifted);
  });
});

const installCall = (result) => result.calls.find((line) => /^uv pip install/.test(line));

describe('install-analyst-cv.sh offline installation and smoke check', () => {
  it('installs only from the verified wheelhouse with a hash-pinned requirements file', () => {
    const { directory, wheelsDirectory, wheels, declaration } = fixture();
    const result = runInstaller(directory, { declaration, wheelsDirectory });
    assert.equal(result.status, 0, result.stderr);
    const installs = result.calls.filter((line) => /^uv pip install/.test(line));
    assert.equal(installs.length, 1);
    const words = installCall(result).split(/\s+/);
    for (const flag of ['--offline', '--no-index', '--require-hashes', '--no-deps']) {
      assert.ok(words.includes(flag), `missing ${flag}: ${installs[0]}`);
    }
    assert.ok(!/--index-url|--extra-index-url|--no-binary|--find-links|https?:/.test(installs[0]), installs[0]);
    const requirements = words[words.indexOf('-r') + 1] ?? words[words.indexOf('--requirement') + 1];
    assert.ok(requirements && existsSync(requirements), `requirements file not found: ${installs[0]}`);
    const text = readFileSync(requirements, 'utf8');
    for (const wheel of wheels) {
      assert.match(text, new RegExp(`${wheel.name}==${wheel.version.replace(/\./g, '\\.')}.*--hash=sha256:${wheel.sha256}`));
      assert.ok(existsSync(path.join(result.installRoot, 'analyst-cv-wheels', wheel.fileName)), wheel.fileName);
    }
    assert.ok(existsSync(path.join(result.installRoot, 'analyst-cv-venv')));
  });

  it('runs the smoke check in the isolated environment and prints its evidence line', () => {
    const { directory, wheelsDirectory, declaration } = fixture();
    const result = runInstaller(directory, { declaration, wheelsDirectory });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /^Verified synthetic CV smoke check:/m);
    assert.ok(result.calls.some((line) => /^smoke .*analyst-cv-smoke\.py/.test(line)), result.calls.join('\n'));
    assert.ok(result.calls.some((line) => /^timeout /.test(line)), 'the smoke check runs under timeout');
  });

  it('removes the downloads after the check and keeps unrelated files', () => {
    const { directory, wheelsDirectory, declaration } = fixture();
    const sentinel = path.join(directory, 'install', 'unrelated', 'keep.txt');
    mkdirSync(path.dirname(sentinel), { recursive: true });
    writeFileSync(sentinel, 'keep');
    const result = runInstaller(directory, { declaration, wheelsDirectory });
    assert.equal(result.status, 0, result.stderr);
    assert.ok(!existsSync(path.join(result.installRoot, 'downloads')));
    assert.equal(readFileSync(sentinel, 'utf8'), 'keep');
  });

  for (const step of ['decode', 'NumPy', 'ONNX Runtime']) {
    it(`fails in the cv smoke stage when the ${step} step fails`, () => {
      const { directory, wheelsDirectory, declaration } = fixture();
      const result = runInstaller(directory, { declaration, wheelsDirectory, env: { STUB_SMOKE_FAIL: `${step} step failed` } });
      assertCvFailure(result, '\\S+', 'cv smoke');
      assertRecommendedActionLast(result);
      assert.match(result.stderr, new RegExp(`${step} step failed`));
      assert.ok(!/^Verified synthetic CV smoke check:/m.test(result.stdout));
      assert.ok(!existsSync(path.join(result.installRoot, 'downloads')), 'downloads are removed after a failed check');
    });
  }

  it('fails in the cv smoke stage when the smoke check times out', () => {
    const { directory, wheelsDirectory, declaration } = fixture();
    const result = runInstaller(directory, { declaration, wheelsDirectory, env: { STUB_TIMEOUT: '1' } });
    assertCvFailure(result, '\\S+', 'cv smoke');
    assertRecommendedActionLast(result);
    assert.match(result.stderr, /time/i);
  });

  it('truncates the output a failing smoke check reports', () => {
    const { directory, wheelsDirectory, declaration } = fixture();
    const result = runInstaller(directory, { declaration, wheelsDirectory, env: { STUB_SMOKE_FAIL: 'x'.repeat(5000) } });
    assertCvFailure(result, '\\S+', 'cv smoke');
    assert.ok(result.stderr.length < 1500, `stderr has ${result.stderr.length} characters`);
    assert.ok(!result.stderr.includes('x'.repeat(1000)));
  });

  it('deletes only its own fixtures when the smoke check fails', () => {
    const { directory, wheelsDirectory, declaration } = fixture();
    const sentinel = path.join(directory, 'install', 'unrelated', 'keep.txt');
    mkdirSync(path.dirname(sentinel), { recursive: true });
    writeFileSync(sentinel, 'keep');
    const result = runInstaller(directory, { declaration, wheelsDirectory, env: { STUB_SMOKE_FAIL: 'decode failed' } });
    assertCvFailure(result, '\\S+', 'cv smoke');
    assert.equal(readFileSync(sentinel, 'utf8'), 'keep');
    assert.ok(existsSync(path.join(result.installRoot, 'analyst-cv-wheels')), 'the verified wheelhouse is not a fixture');
  });

  it('fails in the cv install stage without a smoke check when the offline install fails', () => {
    const { directory, wheelsDirectory, declaration } = fixture();
    const result = runInstaller(directory, { declaration, wheelsDirectory, env: { STUB_UV_FAIL: '1' } });
    assertCvFailure(result, '\\S+', 'cv install');
    assertRecommendedActionLast(result);
    assert.ok(!result.calls.some((line) => line.startsWith('smoke ')));
  });
});
