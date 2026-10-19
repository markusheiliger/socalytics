import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';

// Keeps "Where AI runs and where guidance applies" in .github/workflows/README.md in line with the workflows.
const workflowsDir = new URL('../workflows/', import.meta.url);
const read = (name) => readFileSync(new URL(name, workflowsDir), 'utf8').replace(/\r\n/g, '\n');

function workflows() {
  return readdirSync(workflowsDir)
    .filter((name) => /^speckit-.*\.(yml|md)$/.test(name) && !name.endsWith('.lock.yml'))
    .map((file) => {
      const text = read(file);
      return {
        file,
        name: text.match(/^name: (.+)$/m)?.[1].trim(),
        levels: [...new Set([...text.matchAll(/^\s+level: (\w+)$/gm)].map((match) => match[1]))],
        copilot: /actions\/speckit-copilot/.test(text),
        engine: text.match(/^engine: (\w+)$/m)?.[1] ?? null,
      };
    });
}

function aiTable() {
  const readme = read('README.md');
  const block = readme.match(/<!-- speckit-ai:start -->\n([\s\S]*?)\n<!-- speckit-ai:end -->/)?.[1];
  assert.ok(block, 'the README has the speckit-ai table');
  return block.split('\n').filter((line) => line.startsWith('| ') && !/^\| (Flow branch|---)/.test(line)).map((line) => {
    const [branch, workflow, ai, guidance] = line.slice(2, -2).split(' | ');
    return { branch, workflow, ai, guidance };
  });
}

// "Spec Kit implement phase, group" names "Spec Kit implement phase" and "Spec Kit implement group".
function rowWorkflows(cell, names) {
  const [first, ...rest] = cell.split(', ');
  const stem = first.split(' ').slice(0, -1).join(' ');
  return [first, ...rest.map((part) => [`Spec Kit ${part}`, `${stem} ${part}`].find((name) => names.includes(name)) ?? part)];
}

test('the AI table names every Spec Kit workflow, and only existing ones', () => {
  const names = workflows().map((workflow) => workflow.name);
  const covered = new Set();
  for (const row of aiTable()) {
    for (const name of rowWorkflows(row.workflow, names)) {
      assert.ok(names.includes(name), `"${row.branch}" names the unknown workflow "${name}"`);
      covered.add(name);
    }
  }
  assert.deepEqual(names.filter((name) => !covered.has(name)), [], 'every workflow has a row');
});

test('the AI table states each workflow\'s model level or agentic engine, and script-only rows run no AI', () => {
  const all = workflows();
  const names = all.map((workflow) => workflow.name);
  const rows = aiTable();
  for (const workflow of all) {
    const own = rows.filter((row) => rowWorkflows(row.workflow, names).includes(workflow.name));
    if (workflow.copilot) {
      for (const level of workflow.levels) {
        assert.ok(own.some((row) => row.ai.includes(`level \`${level}\``)), `${workflow.file} runs level ${level}, but its row does not say so`);
      }
    } else if (workflow.engine) {
      assert.ok(own.some((row) => row.ai.includes(`agentic (\`${workflow.engine}\` engine)`)), `${workflow.file} is agentic, but its row does not say so`);
    } else {
      assert.ok(own.every((row) => row.ai === 'none (script)'), `${workflow.file} runs no AI, but its row claims some`);
    }
  }
});
