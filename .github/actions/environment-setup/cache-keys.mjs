import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { appendFileSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

export function dependencyInputs(files, ecosystem) {
  return files.filter((file) => {
    const name = path.posix.basename(file).toLowerCase();
    if (ecosystem === 'npm') return ['package.json', 'package-lock.json', 'npm-shrinkwrap.json', '.npmrc'].includes(name);
    return /\.(?:csproj|fsproj|vbproj|props|targets|sln|slnx)$/.test(name)
      || ['global.json', 'nuget.config', 'packages.lock.json', 'dotnet-tools.json'].includes(name);
  }).sort();
}

export function cacheKey(ecosystem, versions, inputs) {
  const hash = createHash('sha256');
  for (const [name, content] of inputs) {
    hash.update(name).update('\0').update(content).update('\0');
  }
  return `socalytics-downloads-v1-${ecosystem}-${versions.os}-${versions.arch}-${versions.runtime}-${hash.digest('hex')}`;
}

export function toolPins(setup, copilot) {
  const required = (pattern, source) => {
    const match = source.match(pattern);
    if (!match) throw new Error(`Cannot read a pinned tool version: ${pattern}`);
    return match[1];
  };
  return {
    npm: [
      `markdownlint-cli2@${required(/markdownlint-cli2@([\d.]+)/, setup)}`,
      `markdown-link-check@${required(/markdown-link-check@([\d.]+)/, setup)}`,
      `playwright@${required(/playwright@([\d.]+)/, setup)}`,
      `@github/copilot@${required(/cli-version:[\s\S]*?default:\s*([\d.]+)/, copilot)}`,
    ],
    kiota: required(/Microsoft\.OpenApi\.Kiota --version ([\d.]+)/, setup),
  };
}

export function main(workspace) {
  const root = path.resolve(workspace);
  const files = execFileSync('git', ['-C', root, 'ls-files', '-z', '--cached', '--others', '--exclude-standard'], { encoding: 'utf8' })
    .split('\0').filter(Boolean);
  const setup = readFileSync(path.join(here, 'action.yml'), 'utf8');
  const copilot = readFileSync(path.join(here, '..', 'speckit-copilot', 'action.yml'), 'utf8');
  const pins = toolPins(setup, copilot);
  const sdk = process.env.CACHE_DOTNET_VERSION;
  if (!sdk) throw new Error('The SDK version must come from the trusted setup-dotnet step');
  const npm = execFileSync('npm', ['--version'], { cwd: here, encoding: 'utf8' }).trim();
  const versions = { os: process.env.RUNNER_OS, arch: process.env.RUNNER_ARCH };
  const inputs = (ecosystem) => dependencyInputs([...new Set(files)], ecosystem)
    .map((file) => [file, readFileSync(path.join(root, file))]);
  const outputs = {
    'nuget-key': cacheKey('nuget', { ...versions, runtime: sdk }, [
      ...inputs('nuget'), ['kiota-version', pins.kiota],
    ]),
    'npm-key': cacheKey('npm', { ...versions, runtime: `${process.versions.node}-${npm}` }, [
      ...inputs('npm'), ['npm-tools', pins.npm.join(' ')],
    ]),
    'nuget-path': path.join(homedir(), '.nuget', 'packages'),
    'npm-path': path.join(homedir(), '.npm', '_cacache'),
    'npm-tools': pins.npm.join(' '),
    'kiota-version': pins.kiota,
  };
  appendFileSync(process.env.GITHUB_OUTPUT, Object.entries(outputs).map(([key, value]) => `${key}=${value}\n`).join(''));
  appendFileSync(process.env.GITHUB_ENV, `NUGET_PACKAGES=${outputs['nuget-path']}\nNPM_CONFIG_CACHE=${path.join(homedir(), '.npm')}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv[2]);
