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
//   STUB_SMOKE_TIMEOUT, STUB_SMOKE_OUTPUT (replaces the smoke line).
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
[ -z "\${STUB_SMOKE_FAIL:-}" ] || { echo "stub smoke failure" >&2; exit 1; }
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
