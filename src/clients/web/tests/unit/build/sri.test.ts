import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const indexPath = resolve(process.cwd(), 'dist/index.html');

// Needs the output of `npm run build`.
describe.skipIf(!existsSync(indexPath))('dist/index.html', () => {
  const html = readFileSync(indexPath, 'utf8');
  const tags = [
    ...(html.match(/<script\b[^>]*\bsrc=[^>]*>/g) ?? []),
    ...(html.match(/<link\b[^>]*\brel="stylesheet"[^>]*>/g) ?? []),
  ];

  it('has scripts and stylesheets', () => {
    expect(tags.length).toBeGreaterThanOrEqual(2);
  });

  it.each(tags)('carries integrity and crossorigin: %s', (tag) => {
    expect(tag).toMatch(/\bintegrity="sha384-[A-Za-z0-9+/=]+"/);
    expect(tag).toMatch(/\bcrossorigin\b/);
  });

  it('has no inline script', () => {
    expect(html).not.toMatch(/<script\b(?![^>]*\bsrc=)[^>]*>\s*\S/);
  });
});
