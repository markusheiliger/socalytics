import { describe, expect, it } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const workerDirectory = join(process.cwd(), 'src', 'uploads', 'worker');

function importsOf(source: string): string[] {
  return [...source.matchAll(/(?:from|import)\s*\(?\s*['"]([^'"]+)['"]/g)].map((match) => match[1]);
}

describe('upload worker import boundary', () => {
  const files = readdirSync(workerDirectory).filter((name) => name.endsWith('.ts'));

  it('has the documented modules', () => {
    expect(files.sort()).toEqual(['hashing.ts', 'journal.ts', 'protocol.ts', 'transfer.ts', 'uploadWorker.ts']);
  });

  it.each(files)('%s imports nothing from src/platform/', (name) => {
    const imports = importsOf(readFileSync(join(workerDirectory, name), 'utf8'));
    expect(imports.filter((specifier) => /platform|generated|kiota|session|react/.test(specifier))).toEqual([]);
  });

  it.each(files)('%s never stores grant data in IndexedDB or logs it', (name) => {
    const source = readFileSync(join(workerDirectory, name), 'utf8');
    expect(source).not.toMatch(/console\./);
    expect(source).not.toMatch(/fetch\(/);
  });
});
