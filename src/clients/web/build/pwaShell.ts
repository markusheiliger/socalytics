import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import type { Plugin } from 'vite';
import { isVersion } from '../src/serviceWorker/version';

export const versionPlaceholder = '__SOCALYTICS_WEB_VERSION__';

export interface ShellFile {
  url: string;
  integrity: string;
}

export interface ShellManifest {
  version: string;
  files: ShellFile[];
}

const excluded = new Set(['sw.js', 'shell-manifest.json']);

// `YYYY.MMDD.HHMMSS` of the UTC time.
export function formatVersion(date: Date): string {
  const p = (n: number, width = 2) => String(n).padStart(width, '0');
  return `${p(date.getUTCFullYear(), 4)}.${p(date.getUTCMonth() + 1)}${p(date.getUTCDate())}.${p(date.getUTCHours())}${p(date.getUTCMinutes())}${p(date.getUTCSeconds())}`;
}

export function resolveVersion(env: Record<string, string | undefined>, now: Date = new Date()): string {
  const configured = env.SOCALYTICS_WEB_VERSION;
  if (configured === undefined || configured === '') {
    return formatVersion(now);
  }
  if (!isVersion(configured)) {
    throw new Error('SOCALYTICS_WEB_VERSION must have the shape YYYY.MMDD.HHMMSS.');
  }
  return configured;
}

function listFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true })
    .flatMap((entry) => (entry.isDirectory() ? listFiles(join(directory, entry.name)) : [join(directory, entry.name)]))
    .sort();
}

export function integrityOf(content: Uint8Array): string {
  return `sha384-${createHash('sha384').update(content).digest('base64')}`;
}

// Stamps the version into dist/sw.js when it exists, then writes dist/shell-manifest.json.
export function emitShellManifest(outDir: string, version: string): ShellManifest {
  const swPath = join(outDir, 'sw.js');
  if (existsSync(swPath)) {
    writeFileSync(swPath, readFileSync(swPath, 'utf8').replaceAll(versionPlaceholder, version));
  }

  const files = listFiles(outDir)
    .map((path) => relative(outDir, path).split(sep).join('/'))
    .filter((name) => !excluded.has(name))
    .map((name) => ({ url: `/${name}`, integrity: integrityOf(readFileSync(join(outDir, name))) }));

  const manifest: ShellManifest = { version, files };
  writeFileSync(join(outDir, 'shell-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  return manifest;
}

export function pwaShell(options: { env?: Record<string, string | undefined>; now?: () => Date } = {}): Plugin {
  const version = resolveVersion(options.env ?? process.env, options.now?.());
  let outDir = 'dist';
  let building = false;

  return {
    name: 'socalytics-pwa-shell',
    config: () => ({ define: { 'import.meta.env.VITE_SOCALYTICS_WEB_VERSION': JSON.stringify(version) } }),
    configResolved(config) {
      outDir = resolve(config.root, config.build.outDir);
      building = config.command === 'build';
    },
    closeBundle: {
      order: 'post',
      sequential: true,
      handler() {
        // Runs after the bundle is written; the dev and preview servers must not touch dist/.
        if (building && existsSync(outDir)) {
          emitShellManifest(outDir, version);
        }
      },
    },
  };
}
