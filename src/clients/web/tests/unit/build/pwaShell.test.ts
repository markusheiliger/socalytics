// @vitest-environment node
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { emitShellManifest, formatVersion, pwaShell, resolveVersion, versionPlaceholder } from '../../../build/pwaShell';

describe('release version of the build', () => {
  it('uses SOCALYTICS_WEB_VERSION when set', () => {
    expect(resolveVersion({ SOCALYTICS_WEB_VERSION: '2030.0102.030405' }, new Date('2026-10-15T19:41:13Z'))).toBe('2030.0102.030405');
  });

  it('uses the UTC build time otherwise', () => {
    expect(resolveVersion({}, new Date('2026-10-15T19:41:13Z'))).toBe('2026.1015.194113');
    expect(resolveVersion({ SOCALYTICS_WEB_VERSION: '' }, new Date('2026-01-02T03:04:05Z'))).toBe('2026.0102.030405');
    expect(formatVersion(new Date('2026-10-15T23:59:59+02:00'))).toBe('2026.1015.215959');
  });

  it.each(['1.2.3', '2026-10-15', 'latest', '2026.1015.19411'])('rejects the variable value %j', (value) => {
    expect(() => resolveVersion({ SOCALYTICS_WEB_VERSION: value })).toThrow(/YYYY\.MMDD\.HHMMSS/);
  });

  it('exposes the version through define', () => {
    const plugin = pwaShell({ env: { SOCALYTICS_WEB_VERSION: '2030.0102.030405' } });
    const config = (plugin.config as () => { define: Record<string, string> })();
    expect(config.define['import.meta.env.VITE_SOCALYTICS_WEB_VERSION']).toBe('"2030.0102.030405"');
  });
});

describe('shell manifest', () => {
  let dist: string;

  beforeEach(() => {
    dist = mkdtempSync(join(tmpdir(), 'socalytics-shell-'));
    mkdirSync(join(dist, 'assets'));
    writeFileSync(join(dist, 'index.html'), '<html></html>');
    writeFileSync(join(dist, 'assets', 'app.js'), 'console.log(1);');
    writeFileSync(join(dist, 'assets', 'app.css'), 'body{}');
  });

  afterEach(() => rmSync(dist, { recursive: true, force: true }));

  const integrity = (text: string) => `sha384-${createHash('sha384').update(text).digest('base64')}`;
  const read = () => JSON.parse(readFileSync(join(dist, 'shell-manifest.json'), 'utf8')) as { version: string; files: { url: string; integrity: string }[] };

  it('lists every file except sw.js and the manifest with SHA-384 integrity', () => {
    writeFileSync(join(dist, 'sw.js'), 'worker');
    emitShellManifest(dist, '2026.1015.194113');
    const manifest = read();

    expect(manifest.version).toBe('2026.1015.194113');
    expect(manifest.files).toEqual([
      { url: '/assets/app.css', integrity: integrity('body{}') },
      { url: '/assets/app.js', integrity: integrity('console.log(1);') },
      { url: '/index.html', integrity: integrity('<html></html>') },
    ]);
  });

  it('does not list a previous manifest', () => {
    emitShellManifest(dist, '2026.1015.194113');
    emitShellManifest(dist, '2026.1015.194114');
    expect(read().files.map((f) => f.url)).toEqual(['/assets/app.css', '/assets/app.js', '/index.html']);
    expect(read().version).toBe('2026.1015.194114');
  });

  it('stamps the version into sw.js when it exists', () => {
    writeFileSync(join(dist, 'sw.js'), `const v = '${versionPlaceholder}'; const w = '${versionPlaceholder}';`);
    emitShellManifest(dist, '2026.1015.194113');
    expect(readFileSync(join(dist, 'sw.js'), 'utf8')).toBe("const v = '2026.1015.194113'; const w = '2026.1015.194113';");
  });

  it('works without sw.js', () => {
    expect(() => emitShellManifest(dist, '2026.1015.194113')).not.toThrow();
    expect(read().files).toHaveLength(3);
  });
});
