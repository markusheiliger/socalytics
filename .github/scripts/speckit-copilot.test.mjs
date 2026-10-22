import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { CopilotUsageError, MODELS_FILE, STANDARD_DENY_TOOLS, agentMessages, buildArgs, exec, execArgs, isRejection, loadLevels, main, parseLevels, resolveSelection } from './speckit-copilot.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const workflowsDir = path.join(here, '..', 'workflows');

const LEVELS = parseLevels({
  levels: {
    light: { model: 'claude-haiku-5.5', effort: 'medium' },
    standard: { model: 'claude-sonnet-5.5', effort: 'high' },
    strong: { model: 'claude-opus-5.5', effort: 'high' },
  },
});

function withTemp(body) {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-copilot-'));
  try {
    return body(dir);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test('resolves each level from the map', () => {
  assert.deepEqual(resolveSelection(LEVELS, { SPECKIT_LEVEL: 'light' }), { level: 'light', model: 'claude-haiku-5.5', effort: 'medium' });
  assert.deepEqual(resolveSelection(LEVELS, { SPECKIT_LEVEL: 'standard' }), { level: 'standard', model: 'claude-sonnet-5.5', effort: 'high' });
  assert.deepEqual(resolveSelection(LEVELS, { SPECKIT_LEVEL: 'strong' }), { level: 'strong', model: 'claude-opus-5.5', effort: 'high' });
});

test('new levels need only a map entry', () => {
  const levels = parseLevels({ levels: { review: { model: 'claude-sonnet-5', effort: 'xhigh' } } });
  assert.deepEqual(resolveSelection(levels, { SPECKIT_LEVEL: 'review' }), { level: 'review', model: 'claude-sonnet-5', effort: 'xhigh' });
});

test('overrides win, and empty or auto values fall back to the CLI default', () => {
  assert.deepEqual(
    resolveSelection(LEVELS, { SPECKIT_LEVEL: 'standard', SPECKIT_MODEL_OVERRIDE: 'claude-opus-5.5', SPECKIT_EFFORT_OVERRIDE: 'xhigh' }),
    { level: 'standard', model: 'claude-opus-5.5', effort: 'xhigh' },
  );
  const levels = parseLevels({ levels: { light: { model: 'auto', effort: ' ' }, bare: {} } });
  assert.deepEqual(resolveSelection(levels, { SPECKIT_LEVEL: 'light' }), { level: 'light', model: '', effort: '' });
  assert.deepEqual(resolveSelection(levels, { SPECKIT_LEVEL: 'bare' }), { level: 'bare', model: '', effort: '' });
  assert.equal(resolveSelection(LEVELS, { SPECKIT_LEVEL: 'light', SPECKIT_MODEL_OVERRIDE: 'auto' }).model, 'claude-haiku-5.5');
});

test('refuses unknown levels, malformed maps, and malformed values', () => {
  assert.throws(() => resolveSelection(LEVELS, { SPECKIT_LEVEL: 'max' }), /Unknown level "max"; models.json defines light, standard, strong/);
  assert.throws(() => resolveSelection(LEVELS, {}), /Unknown level ""/);
  assert.throws(() => resolveSelection(LEVELS, { SPECKIT_LEVEL: 'toString' }), CopilotUsageError);
  assert.throws(() => resolveSelection(LEVELS, { SPECKIT_LEVEL: 'light', SPECKIT_MODEL_OVERRIDE: 'Claude Opus' }), /Invalid model/);
  assert.throws(() => parseLevels({}), /at least one level/);
  assert.throws(() => parseLevels({ levels: [] }), /at least one level/);
  assert.throws(() => parseLevels({ levels: { Light: { model: 'x' } } }), /Invalid level name/);
  assert.throws(() => parseLevels({ levels: { light: 'claude-haiku-5.5' } }), /must be an object/);
  assert.throws(() => parseLevels({ levels: { light: { model: 'x', modle: 'y' } } }), /unknown keys: modle/);
  assert.throws(() => parseLevels({ levels: { light: { model: 5 } } }), /model must be a string/);
  assert.throws(() => parseLevels({ levels: { light: { effort: 'extreme' } } }), /Invalid reasoning effort/);
  withTemp((dir) => {
    const broken = path.join(dir, 'models.json');
    writeFileSync(broken, '{ "levels": ');
    assert.throws(() => loadLevels(broken), /Cannot read/);
    assert.throws(() => loadLevels(path.join(dir, 'missing.json')), /Cannot read/);
  });
});

test('builds inference arguments without tools', () => {
  assert.deepEqual(buildArgs({ mode: 'inference', model: 'claude-haiku-5.5', effort: 'medium' }), [
    '--no-ask-user', '--no-auto-update', '--disable-builtin-mcps', '--secret-env-vars=COPILOT_GITHUB_TOKEN',
    '-s', '--available-tools=', '--no-custom-instructions', '--no-color',
    '--model', 'claude-haiku-5.5', '--reasoning-effort', 'medium',
  ]);
});

test('builds agent arguments with the standard and extra deny lists, budget, and JSON output', () => {
  const args = buildArgs({
    mode: 'agent', model: '', effort: '', denyTools: 'shell(git merge:*)\n\n shell(git push:*) \nshell(git reset:*)', aiCredits: '1000', outputFormat: 'json',
  });
  assert.ok(args.includes('--allow-all-tools'));
  assert.ok(!args.includes('--model') && !args.includes('--reasoning-effort'));
  const denied = args.flatMap((arg, index) => (arg === '--deny-tool' ? [args[index + 1]] : []));
  assert.deepEqual(denied, [...STANDARD_DENY_TOOLS, 'shell(git merge:*)', 'shell(git reset:*)']);
  assert.deepEqual(args.slice(-4), ['--max-ai-credits', '1000', '--output-format', 'json']);
  assert.throws(() => buildArgs({ mode: 'chat' }), /Unknown mode/);
  assert.throws(() => buildArgs({ mode: 'agent', aiCredits: 'lots' }), /Invalid AI-credit budget/);
  assert.throws(() => buildArgs({ mode: 'agent', outputFormat: 'xml' }), /Unknown output format/);
});

test('recognizes the CLI messages for a rejected model or effort only', () => {
  assert.ok(isRejection('Error: Model "claude-nonexistent-9" from --model flag is not available.'));
  assert.ok(isRejection("Execution failed: Reasoning effort 'none' is not supported for model 'claude-haiku-5.5'."));
  assert.ok(!isRejection('Execution failed: rate limit exceeded'));
  assert.ok(!isRejection(''));
});

test('the CLI writes outputs, NUL-separated arguments, and the rejection verdict', () => withTemp((dir) => {
  const output = path.join(dir, 'output.txt');
  writeFileSync(output, '');
  const models = path.join(dir, 'models.json');
  writeFileSync(models, JSON.stringify({ levels: { strong: { model: 'claude-opus-5.5', effort: 'high' } } }));
  let logged = '';
  assert.equal(main(['resolve', '--models', models], { env: { SPECKIT_LEVEL: 'strong', GITHUB_OUTPUT: output }, log: (text) => { logged += text; } }), 0);
  assert.equal(readFileSync(output, 'utf8'), 'model=claude-opus-5.5\neffort=high\n');
  assert.match(logged, /level strong: model claude-opus-5.5, reasoning effort high/);
  assert.throws(() => main(['resolve', '--models'], { env: { SPECKIT_LEVEL: 'strong' } }), CopilotUsageError);

  const argsFile = path.join(dir, 'args');
  const env = { SPECKIT_MODE: 'agent', SPECKIT_MODEL: 'claude-sonnet-5.5', SPECKIT_EFFORT: 'high', SPECKIT_OUTPUT_FORMAT: 'json' };
  main(['args', '--out', argsFile], { env });
  const args = readFileSync(argsFile, 'utf8').split('\0').slice(0, -1);
  assert.deepEqual(args.slice(-6), ['--model', 'claude-sonnet-5.5', '--reasoning-effort', 'high', '--output-format', 'json']);
  main(['args', '--out', argsFile, '--auto'], { env });
  assert.ok(!readFileSync(argsFile, 'utf8').includes('--model'));

  const stderr = path.join(dir, 'stderr');
  writeFileSync(stderr, 'Error: Model "x" from --model flag is not available.\n');
  assert.equal(main(['rejected', path.join(dir, 'missing'), stderr], { env: {} }), 0);
  writeFileSync(stderr, 'Execution failed: timeout\n');
  assert.equal(main(['rejected', stderr], { env: {} }), 1);
  assert.throws(() => main(['args'], { env }), CopilotUsageError);
  assert.throws(() => main(['other'], { env }), CopilotUsageError);
}));

test('exec stands in for the copilot executable of the Spec Kit workflow engine: the level and deny-list replace --yolo and the caller\'s model', () => {
  const env = { SPECKIT_LEVEL: 'standard', SPECKIT_DENY_TOOLS: 'shell(git merge:*)', SPECKIT_AI_CREDITS: '500' };
  const args = execArgs(['-p', '/speckit-plan', '--yolo', '--model', 'gpt-x', '--output-format', 'json'], { levels: LEVELS, env });
  assert.deepEqual(args.slice(0, 2), ['-p', '/speckit-plan']);
  assert.ok(!args.includes('--yolo') && !args.includes('gpt-x'), 'the caller does not choose permissions or model');
  assert.deepEqual(args.slice(2), buildArgs({ mode: 'agent', model: 'claude-sonnet-5.5', effort: 'high', denyTools: 'shell(git merge:*)', aiCredits: '500', outputFormat: 'json' }));
  assert.deepEqual(execArgs(['-p', 'x', '--agent', 'speckit.plan'], { levels: LEVELS, env, auto: true }).slice(0, 4), ['-p', 'x', '--agent', 'speckit.plan']);
  assert.ok(!execArgs(['-p', 'x'], { levels: LEVELS, env, auto: true }).includes('--model'), 'the retry uses the CLI default model');
  assert.throws(() => execArgs(['--yolo'], { levels: LEVELS, env }), /needs the prompt/);
  assert.throws(() => execArgs(['-p', 'x'], { levels: LEVELS, env: { SPECKIT_LEVEL: 'huge' } }), /Unknown level/);
});

test('exec with SPECKIT_AGENT_LOG writes JSONL to the log and shows the agent\'s messages', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-exec-'));
  try {
    const log = path.join(dir, 'agent.jsonl');
    const jsonl = [
      JSON.stringify({ type: 'assistant.message', data: { content: 'Implemented T004.' } }),
      JSON.stringify({ type: 'tool.call', data: {} }),
      '{"partial',
    ].join('\n');
    const calls = [];
    const spawn = (binary, args, options) => {
      calls.push({ binary, args, options });
      return { status: 0, stdout: jsonl, stderr: '' };
    };
    let shown = '';
    const env = { SPECKIT_LEVEL: 'standard', SPECKIT_AGENT_LOG: log, SPECKIT_COPILOT_BINARY: 'copilot-test' };
    const code = exec(['-p', '/speckit-implement T004'], { env, modelsFile: MODELS_FILE, spawn, stdout: { write: (text) => { shown += text; } }, stderr: { write: () => {} } });
    assert.equal(code, 0);
    assert.equal(calls[0].binary, 'copilot-test');
    assert.equal(calls[0].args[calls[0].args.indexOf('--output-format') + 1], 'json', 'the log needs JSONL, which the reports read');
    assert.deepEqual(calls[0].options.stdio, ['inherit', 'pipe', 'pipe']);
    assert.equal(readFileSync(log, 'utf8'), jsonl);
    assert.equal(shown, 'Implemented T004.\n');
    assert.deepEqual(agentMessages(jsonl), ['Implemented T004.']);
    const plain = [];
    exec(['-p', 'x'], { env: { SPECKIT_LEVEL: 'standard' }, modelsFile: MODELS_FILE, spawn: (binary, args, options) => { plain.push({ args, options }); return { status: 0, stdout: '', stderr: '' }; }, stdout: { write: () => {} }, stderr: { write: () => {} } });
    assert.deepEqual(plain[0].options.stdio, ['inherit', 'inherit', 'pipe'], 'without a log the output streams as before');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('the action\'s models.json is valid, and every workflow uses a level it defines (directly or through speckit-workflow)', () => {
  const levels = loadLevels(MODELS_FILE);
  const defined = Object.keys(levels);
  assert.ok(defined.length > 0);
  let uses = 0;
  for (const file of readdirSync(workflowsDir).filter((name) => name.endsWith('.yml') && !name.endsWith('.lock.yml'))) {
    const text = readFileSync(path.join(workflowsDir, file), 'utf8').replaceAll('\r\n', '\n');
    for (const match of text.matchAll(/uses: \.\/tooling\/\.github\/actions\/speckit-(?:copilot|workflow)\n(?:\s+(?!- ).*\n)*?\s+level: ([^\n]+)/g)) {
      uses += 1;
      const value = match[1].trim();
      const levels = value.startsWith('${{') ? [...value.matchAll(/(?:&&|\|\|)\s*'([a-z]+)'/g)].map((m) => m[1]) : [value];
      for (const level of levels) assert.ok(defined.includes(level), `${file} uses level ${level}, which models.json does not define`);
    }
  }
  assert.ok(uses >= 1, 'at least one workflow uses the action');
});
