// Step "Check the spec issue" of every Spec Kit workflow with an `issue` input: the input must name a spec twin, an
// issue (not a pull request) labelled speckit:spec. The step sets the output `spec` (true or false) and fails
// otherwise, so the jobs that depend on it never work on another issue.
import { pathToFileURL } from 'node:url';

import { TWIN_LABEL } from './speckit-prepare-core.mjs';
import { createGitHubClient, setOutput } from './speckit-script-runtime.mjs';

export async function checkSpecIssue(client, value) {
  const text = String(value ?? '').trim();
  if (!/^[1-9][0-9]{0,9}$/.test(text)) return { ok: false, reason: 'The input `issue` is not an issue number' };
  const number = Number(text);
  let issue;
  try {
    issue = await client.getIssue(number);
  } catch (error) {
    return { ok: false, reason: `Issue #${number} could not be read (${String(error?.message ?? error).split('\n')[0]})` };
  }
  if (issue?.pull_request) return { ok: false, reason: `#${number} is a pull request, not a spec twin issue` };
  const labels = (issue?.labels ?? []).map((label) => (typeof label === 'string' ? label : label?.name));
  if (!labels.includes(TWIN_LABEL)) return { ok: false, reason: `#${number} is not a spec twin issue: it is not labelled \`${TWIN_LABEL}\`` };
  return { ok: true, number };
}

export async function main(argv, { env = process.env, client, log = console.log } = {}) {
  const [command] = argv;
  if (command !== 'check') throw new Error('Usage: speckit-issue.mjs check (the issue number comes from SPECKIT_ISSUE)');
  const result = await checkSpecIssue(client ?? createGitHubClient(env), env.SPECKIT_ISSUE);
  setOutput(env, 'spec', String(result.ok));
  if (!result.ok) {
    log(`::error::${result.reason}.`);
    return 1;
  }
  log(`#${result.number} is a spec twin issue (labelled \`${TWIN_LABEL}\`).`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(error.message);
      process.exitCode = 2;
    },
  );
}
