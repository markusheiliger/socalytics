import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import ts from 'typescript';
import shell from '../../../src/i18n/en/shell.json';

const roots = ['src/features', 'src/shell'];

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : path.endsWith('.tsx') ? [path] : [];
  });
}

/** Non-blank JSX text nodes and literal text attributes users read. */
function literals(file: string): string[] {
  const source = ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const found: string[] = [];
  const visit = (node: ts.Node) => {
    if (ts.isJsxText(node) && node.text.trim() !== '') found.push(node.text.trim());
    if (
      ts.isJsxAttribute(node) &&
      ['label', 'aria-label', 'title', 'placeholder', 'alt'].includes(node.name.getText()) &&
      node.initializer &&
      ts.isStringLiteral(node.initializer)
    ) {
      found.push(node.initializer.text);
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return found.map((text) => `${file}: ${text}`);
}

describe('translation keys', () => {
  it('has no literal text in JSX of features and shell', () => {
    expect(roots.flatMap(files).flatMap(literals)).toEqual([]);
  });

  it('defines the keys the navigation uses', () => {
    expect(Object.keys(shell.nav)).toEqual(['label', 'home', 'club', 'seasons', 'teams', 'members', 'analystManagers', 'registry', 'agentCatalog', 'myRequests', 'pairManager', 'account', 'analystManagersPending']);
  });
});
