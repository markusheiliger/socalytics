import { spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// Logic of the speckit-copilot composite action (.github/actions/speckit-copilot): resolves the model and reasoning
// effort of a strength level from the action's models.json, builds the Copilot CLI arguments for a mode, and
// recognizes a rejected model or effort, after which the action retries once with the CLI's default model.

export class CopilotUsageError extends Error {}

export const MODELS_FILE = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'actions', 'speckit-copilot', 'models.json');
export const MODES = ['inference', 'agent'];
// The values Copilot CLI accepts for --reasoning-effort.
export const EFFORTS = ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];
const LEVEL_NAME = /^[a-z][a-z0-9-]*$/;
const MODEL_ID = /^[a-z0-9][a-z0-9.-]*$/;
const COMMON_ARGS = ['--no-ask-user', '--no-auto-update', '--disable-builtin-mcps', '--secret-env-vars=COPILOT_GITHUB_TOKEN'];
const INFERENCE_ARGS = ['-s', '--available-tools=', '--no-custom-instructions', '--no-color'];
// Agents may run any tool except these; callers add more through the action's deny-tools input.
export const STANDARD_DENY_TOOLS = ['shell(git push:*)', 'shell(git commit:*)', 'shell(gh:*)', 'shell(curl:*)', 'shell(wget:*)'];
// Messages of Copilot CLI 1.0.x when the configured model or effort is not available; they end the run before the
// agent starts, so retrying is safe.
const REJECTION = [/from --model flag is not available/i, /Reasoning effort '[^']*' is not supported for model/i];

function clean(value) {
  const text = (value ?? '').trim();
  return text === 'auto' ? '' : text;
}

function check(kind, value, source) {
  if (kind === 'model' && value && !MODEL_ID.test(value)) {
    throw new CopilotUsageError(`Invalid model "${value}" from ${source}.`);
  }
  if (kind === 'effort' && value && !EFFORTS.includes(value)) {
    throw new CopilotUsageError(`Invalid reasoning effort "${value}" from ${source}; expected one of ${EFFORTS.join(', ')}.`);
  }
  return value;
}

// Validates the parsed models.json and returns its levels: { name: { model, effort } } with '' for the CLI default.
export function parseLevels(config) {
  const levels = config?.levels;
  if (!levels || typeof levels !== 'object' || Array.isArray(levels) || Object.keys(levels).length === 0) {
    throw new CopilotUsageError('models.json must define at least one level under "levels".');
  }
  const result = {};
  for (const [name, entry] of Object.entries(levels)) {
    if (!LEVEL_NAME.test(name)) throw new CopilotUsageError(`Invalid level name "${name}" in models.json.`);
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) {
      throw new CopilotUsageError(`Level "${name}" in models.json must be an object with model and effort.`);
    }
    const unknown = Object.keys(entry).filter((key) => !['model', 'effort'].includes(key));
    if (unknown.length > 0) throw new CopilotUsageError(`Level "${name}" in models.json has unknown keys: ${unknown.join(', ')}.`);
    for (const key of ['model', 'effort']) {
      if (entry[key] !== undefined && typeof entry[key] !== 'string') {
        throw new CopilotUsageError(`Level "${name}" in models.json: ${key} must be a string.`);
      }
    }
    result[name] = {
      model: check('model', clean(entry.model), `level ${name}`),
      effort: check('effort', clean(entry.effort), `level ${name}`),
    };
  }
  return result;
}

export function loadLevels(file = MODELS_FILE) {
  let config;
  try {
    config = JSON.parse(readFileSync(file, 'utf8'));
  } catch (error) {
    throw new CopilotUsageError(`Cannot read ${file}: ${error.message}`);
  }
  return parseLevels(config);
}

// Overrides win over the level's entry; an empty value or 'auto' means the CLI decides.
export function resolveSelection(levels, env) {
  const level = (env.SPECKIT_LEVEL ?? '').trim();
  if (!Object.hasOwn(levels, level)) {
    throw new CopilotUsageError(`Unknown level "${level}"; models.json defines ${Object.keys(levels).join(', ')}.`);
  }
  const overrideModel = clean(env.SPECKIT_MODEL_OVERRIDE);
  const overrideEffort = clean(env.SPECKIT_EFFORT_OVERRIDE);
  return {
    level,
    model: overrideModel ? check('model', overrideModel, 'the model input') : levels[level].model,
    effort: overrideEffort ? check('effort', overrideEffort, 'the effort input') : levels[level].effort,
  };
}

export function buildArgs({ mode, model = '', effort = '', denyTools = '', aiCredits = '', outputFormat = 'text' }) {
  if (!MODES.includes(mode)) throw new CopilotUsageError(`Unknown mode "${mode}"; expected one of ${MODES.join(', ')}.`);
  if (!['text', 'json'].includes(outputFormat)) throw new CopilotUsageError(`Unknown output format "${outputFormat}".`);
  const credits = (aiCredits ?? '').trim();
  if (credits && !/^[1-9]\d*$/.test(credits)) throw new CopilotUsageError(`Invalid AI-credit budget "${credits}".`);
  const args = [...COMMON_ARGS];
  if (mode === 'inference') {
    args.push(...INFERENCE_ARGS);
  } else {
    const extra = denyTools.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
    args.push('--allow-all-tools');
    for (const tool of [...STANDARD_DENY_TOOLS, ...extra.filter((tool) => !STANDARD_DENY_TOOLS.includes(tool))]) {
      args.push('--deny-tool', tool);
    }
  }
  if (model) args.push('--model', model);
  if (effort) args.push('--reasoning-effort', effort);
  if (credits) args.push('--max-ai-credits', credits);
  if (outputFormat === 'json') args.push('--output-format', 'json');
  return args;
}

export function isRejection(text) {
  return REJECTION.some((pattern) => pattern.test(text ?? ''));
}

// The Copilot CLI arguments for a call the Spec Kit workflow engine makes (`-p <prompt> [--yolo] [--model <id>]
// [--output-format json] [--agent <name>]`), when this script stands in for the `copilot` executable
// (SPECKIT_INTEGRATION_COPILOT_EXECUTABLE): the prompt, the agent, and the output format are kept; the permissions and
// the model always come from the level (SPECKIT_LEVEL) and the deny-list, like every other Copilot CLI run of the
// Spec Kit workflows, so `--yolo` and a model of the caller are dropped.
export function execArgs(incoming, { levels, env, auto = false }) {
  const valueOf = (flag) => {
    const index = incoming.indexOf(flag);
    return index >= 0 ? incoming[index + 1] : undefined;
  };
  const prompt = valueOf('-p') ?? valueOf('--prompt');
  if (typeof prompt !== 'string') throw new CopilotUsageError('exec needs the prompt as -p <prompt>.');
  const agent = valueOf('--agent');
  const { model, effort } = resolveSelection(levels, env);
  return [
    '-p',
    prompt,
    ...(agent ? ['--agent', agent] : []),
    ...buildArgs({
      mode: 'agent',
      model: auto ? '' : model,
      effort: auto ? '' : effort,
      denyTools: env.SPECKIT_DENY_TOOLS ?? '',
      aiCredits: env.SPECKIT_AI_CREDITS ?? '',
      outputFormat: valueOf('--output-format') === 'json' ? 'json' : 'text',
    }),
  ];
}

// Runs one Copilot CLI call for the workflow engine; standard error is passed through after the run, so a rejected
// model or effort can be retried once with the CLI's default model, as the action does.
function exec(incoming, { env, modelsFile, spawn = spawnSync }) {
  const levels = loadLevels(modelsFile);
  const binary = env.SPECKIT_COPILOT_BINARY || 'copilot';
  const run = (auto) => {
    const result = spawn(binary, execArgs(incoming, { levels, env, auto }), { stdio: ['inherit', 'inherit', 'pipe'], encoding: 'utf8' });
    process.stderr.write(result.stderr ?? '');
    return result;
  };
  let result = run(false);
  if (result.status !== 0 && isRejection(result.stderr)) {
    process.stderr.write('::warning::Copilot CLI rejected the configured model or reasoning effort; retrying once with its default model.\n');
    result = run(true);
  }
  if (result.error) throw new CopilotUsageError(`Cannot run ${binary}: ${result.error.message}`);
  return result.status ?? 1;
}

function readIfPresent(file) {
  return file && existsSync(file) ? readFileSync(file, 'utf8') : '';
}

export function main(argv, { env = process.env, modelsFile = MODELS_FILE, log = (text) => process.stdout.write(text) } = {}) {
  const [command, ...rest] = argv;
  if (command === 'resolve') {
    const file = rest.includes('--models') ? rest[rest.indexOf('--models') + 1] : modelsFile;
    if (!file) throw new CopilotUsageError('Usage: speckit-copilot.mjs resolve [--models <file>]');
    const { level, model, effort } = resolveSelection(loadLevels(file), env);
    log(`::notice::Copilot CLI level ${level}: model ${model || 'auto (CLI default)'}, reasoning effort ${effort || 'default'}\n`);
    if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `model=${model}\neffort=${effort}\n`);
    return 0;
  }
  if (command === 'args') {
    const out = rest[rest.indexOf('--out') + 1];
    if (!rest.includes('--out') || !out) throw new CopilotUsageError('Usage: speckit-copilot.mjs args --out <file> [--auto]');
    const auto = rest.includes('--auto');
    const args = buildArgs({
      mode: (env.SPECKIT_MODE ?? '').trim(),
      model: auto ? '' : clean(env.SPECKIT_MODEL),
      effort: auto ? '' : clean(env.SPECKIT_EFFORT),
      denyTools: env.SPECKIT_DENY_TOOLS ?? '',
      aiCredits: env.SPECKIT_AI_CREDITS ?? '',
      outputFormat: (env.SPECKIT_OUTPUT_FORMAT ?? 'text').trim() || 'text',
    });
    writeFileSync(out, args.map((arg) => `${arg}\0`).join(''));
    return 0;
  }
  if (command === 'rejected') {
    return rest.some((file) => isRejection(readIfPresent(file))) ? 0 : 1;
  }
  if (command === 'exec') return exec(rest, { env, modelsFile });
  throw new CopilotUsageError('Usage: speckit-copilot.mjs resolve [--models <file>] | args --out <file> [--auto] | rejected <file>... | exec -p <prompt> [...]');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exitCode = error instanceof CopilotUsageError ? 2 : 1;
  }
}
