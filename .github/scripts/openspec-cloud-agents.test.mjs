import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const specialistNames = [
  'soca-architect',
  'soca-auditor',
  'soca-designer',
  'soca-developer',
  'soca-strategist',
  'soca-verifier',
];

function frontmatter(name) {
  const content = readFileSync(
    new URL(`../agents/${name}.agent.md`, import.meta.url),
    'utf8',
  );
  const match = content.match(/^---\r?\n([\s\S]*?)\r?\n---/);
  assert.ok(match, `${name} must have YAML frontmatter`);
  return match[1];
}

test('keeps hidden SocAlytics specialists compatible with Copilot cloud agent', () => {
  for (const name of specialistNames) {
    const yaml = frontmatter(name);
    assert.match(yaml, new RegExp(`^name: "${name}"$`, 'm'));
    assert.match(yaml, /^user-invocable: false$/m);
    assert.doesNotMatch(
      yaml,
      /^model:\s*\[/m,
      `${name} cannot use an array-valued model in GitHub cloud-agent frontmatter`,
    );
  }
});

test('keeps the OpenSpec cloud orchestrator able to dispatch exact specialists', () => {
  const yaml = frontmatter('openspec-cloud');
  assert.match(yaml, /^tools: \[read, search, edit, execute, agent\]$/m);
  const content = readFileSync(
    new URL('../agents/openspec-cloud.agent.md', import.meta.url),
    'utf8',
  );
  for (const name of specialistNames) {
    assert.match(content, new RegExp(name));
  }
  assert.match(content, /never\s+delegate an owned task back to `OpenSpec Cloud`/);
});
