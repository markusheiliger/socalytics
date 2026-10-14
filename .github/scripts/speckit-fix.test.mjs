import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { renderSpecLine } from './speckit-prepare-core.mjs';
import { resolveImplementRequester } from './speckit-prepare.mjs';
import { resolveAmendment, startRework } from './speckit-amend.mjs';
import { DIAGNOSIS_COMMENT_MARKER, RESUME_COMMENT_MARKER, parseFixEvents, parseGuidance } from './speckit-implement-core.mjs';
import { checkFixSpec, fixStamp, maintainFixSpec, runEvidence, runRecord, settleClosedFix, startFixAuthoring } from './speckit-fix.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const FOLDER = '20261014-100000-fix-upload-expiry';
const PREFIX = `specs/${FOLDER}`;

// The workflow_dispatch inputs an agentic workflow declares in its frontmatter.
function declaredInputs(file) {
  const source = readFileSync(new URL(`../workflows/${file}`, import.meta.url), 'utf8').replaceAll('\r\n', '\n');
  const block = source.match(/\n {4}inputs:\n([\s\S]*?)\n {2}\S/)?.[1] ?? '';
  return [...block.matchAll(/^ {6}([a-z_]+):$/gm)].map((match) => match[1]);
}
const env = (extra = {}) => ({ GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_TWIN: '5', SPECKIT_PULL: '9', SPECKIT_FOLDER: 'f', ...extra });
const SPEC = `# Fix: Upload session expiry test\n\n**Fix for**: #9 (implementation of \`specs/f\`)\n\n## Summary\n\nMake the test independent of runner load.\n`;
const PLAN = '# Plan\n\n## Architecture Updates\n\nNone: the fix changes a test only.\n';
const TASKS = '## Phase 1\n\n- [ ] T001 Use a longer SessionLifetime in UploadSessionExpiryTests\n';

// The implementation pull request #9 of twin #5 (`specs/f`), stopped and diagnosed, and the fix spec pull request #12
// an authoring run created.
function fixRepo({ files = null, spec = SPEC, readme = `| [${FOLDER}](${FOLDER}/) | fix |\n` } = {}) {
  const github = diagnosisRepo({ amendment: false });
  github.issues[0].labels.push({ name: 'speckit:spec' });
  github.issues[0].body = renderSpecLine({ serverUrl: 'https://github.com', repository: 'octo/repo', branch: 'main' }, 'f');
  github.repo.branches[`speckit-fix/${FOLDER}`] = 'sha-fix';
  github.repo.commits['sha-fix'] = { tree: 't3', parents: ['sha-main'] };
  github.setFile('sha-fix', `${PREFIX}/spec.md`, spec);
  github.setFile('sha-fix', `${PREFIX}/plan.md`, PLAN);
  github.setFile('sha-fix', `${PREFIX}/tasks.md`, TASKS);
  github.setFile('sha-fix', `${PREFIX}/checklists/requirements.md`, '- [x] Requirements are testable\n');
  github.setFile('sha-fix', 'specs/README.md', readme);
  github.repo.pulls.push({ number: 12, node_id: 'PR_12', title: 'Fix spec: Upload session expiry', html_url: 'https://github.com/octo/repo/pull/12', body: 'Agent body <!-- speckit-fix:for {"pull":99} -->', draft: true, state: 'open', assignees: [], base: { ref: 'main', sha: 'sha-main' }, head: { ref: `speckit-fix/${FOLDER}`, sha: 'sha-fix', repo: { full_name: 'octo/repo' } }, created_at: github.tick() });
  github.repo.pullFiles[12] = files ?? [`${PREFIX}/spec.md`, `${PREFIX}/plan.md`, `${PREFIX}/tasks.md`, `${PREFIX}/checklists/requirements.md`, 'specs/README.md'];
  return github;
}

async function authored(github, extra = {}) {
  await startFixAuthoring(github, { SPECKIT_DEFAULT_BRANCH: 'main' }, { twin: 5, implementation: await github.getPullRequest(9), guidance: 'Make the test robust', actor: 'dev', now: github.clock });
  const check = github.repo.checkRuns.at(-1);
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-fix-'));
  try {
    const output = path.join(dir, 'agent_output.json');
    writeFileSync(output, JSON.stringify({ items: [{ type: 'speckit_fix', report: JSON.stringify({ folder: FOLDER, summary: 'Lengthens the session lifetime of the expiry test.' }) }] }));
    return await runRecord({ client: github, env: env({ GH_AW_AGENT_OUTPUT: output, SPECKIT_CHECK_RUN: String(check.id), SPECKIT_DETECTION: 'success', SPECKIT_CREATED_PULL: '12', ...extra }), log: silent });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test('records an authored fix spec: links it, reports it, and starts its consistency check', async () => {
  const github = fixRepo();
  const result = await authored(github);
  assert.equal(result.outcome, 'created');
  const pull = github.repo.pulls.find((item) => item.number === 12);
  assert.match(pull.body, /^<!-- speckit-amend:status -->\n⏳ \*\*Being checked for consistency\.\*\*[\s\S]*\*\*Merge\*\* this pull request to add the fix spec to `main`[\s\S]*<!-- speckit-fix:for \{"pull":9,"twin":5,"folder":"f"\} -->\n\nAgent body &lt;!-- speckit-fix:for/, 'the workflow links it; the agent cannot');
  const created = github.comments.filter((item) => item.number === 9).at(-1);
  assert.match(created.body, /^<!-- speckit-fix:created \{"pull":12,"folder":"20261014-100000-fix-upload-expiry"\} -->\n\*\*Fix spec \[Fix spec: Upload session expiry \(#12\)\]\(https:\/\/github\.com\/octo\/repo\/pull\/12\) was authored\*\*[\s\S]*Lengthens the session lifetime/);
  const authoring = github.repo.checkRuns.find((check) => check.name === 'Spec Kit fix spec');
  assert.deepEqual([authoring.status, authoring.conclusion, authoring.external_id], ['completed', 'success', 'speckit:fix-authored']);
  assert.match(github.comments.find((item) => item.number === 12).body, /This fix spec was requested with `\/speckit fix` on #9/);
  const analysis = github.repo.runs.at(-1);
  assert.deepEqual([analysis.workflow, analysis.inputs.kind, analysis.inputs.folder, analysis.inputs.amendment, analysis.inputs.pull], ['speckit-analyze.lock.yml', 'fix', FOLDER, '12', '9']);
  const context = await resolveAmendment(github, env(), 12);
  assert.deepEqual([context.kind, context.folder, context.implementation.number, context.twin], ['fix', FOLDER, 9, 5]);
});

test('a missing, refused, or broken fix spec goes back to the implementation with the next steps', async () => {
  const none = fixRepo();
  const result = await authored(none, { SPECKIT_CREATED_PULL: '' });
  assert.equal(result.outcome, 'none');
  assert.match(none.comments.at(-1).body, /^\*\*No fix spec was created\*\*: Lengthens the session lifetime[\s\S]*\*\*Recommended next step\*\*: Diagnose again[\s\S]*```text\n\/speckit diagnose\n```/);

  const broken = fixRepo({ files: [`${PREFIX}/spec.md`, 'src/platform/Tests/UploadSessionExpiryTests.cs'] });
  assert.equal((await authored(broken)).outcome, 'rejected');
  assert.equal(broken.repo.pulls.find((item) => item.number === 12).state, 'closed');
  assert.equal(broken.repo.branches[`speckit-fix/${FOLDER}`], undefined);
  assert.match(broken.comments.at(-1).body, /^\*\*The authored fix spec #12 was rejected and closed\*\*: it changes files outside `specs\/20261014-100000-fix-upload-expiry\/` and `specs\/README\.md`: `src\/platform\/Tests\/UploadSessionExpiryTests\.cs`/);
  assert.equal(broken.repo.checkRuns.find((check) => check.name === 'Spec Kit fix spec').conclusion, 'failure');
});

test('checks a fix spec: a new, publishable spec folder that names the implementation it unblocks', async () => {
  const github = fixRepo();
  assert.deepEqual((await checkFixSpec(github, { folder: FOLDER, pull: await github.getPullRequest(12), implementationPull: 9 })).reasons, []);

  const unlisted = fixRepo({ readme: '', spec: SPEC.replace('#9', '#8') });
  const reasons = (await checkFixSpec(unlisted, { folder: FOLDER, pull: await unlisted.getPullRequest(12), implementationPull: 9 })).reasons.join('\n');
  assert.match(reasons, /specs\/README\.md does not list specs\/20261014-100000-fix-upload-expiry yet/);
  assert.match(reasons, /spec\.md must name the implementation it unblocks in a line `\*\*Fix for\*\*: #9`/);

  const existing = fixRepo({ files: [`${PREFIX}/spec.md`, 'docs/architecture/overview.md'] });
  existing.setFile('main', `${PREFIX}/spec.md`, SPEC);
  const more = (await checkFixSpec(existing, { folder: FOLDER, pull: await existing.getPullRequest(12), implementationPull: 9 })).reasons.join('\n');
  assert.match(more, /may only add `specs\/20261014-100000-fix-upload-expiry\/` and its row in `specs\/README\.md`, but it changes `docs\/architecture\/overview\.md`/);
  assert.match(more, /already exists on `main`/);
});

test('a fix spec is reworked by Spec Kit fix, and its evidence names the folder and line to use', async () => {
  const github = fixRepo();
  await authored(github);
  const context = await resolveAmendment(github, env(), 12);
  await startRework(github, env(), context, { mode: 'revise', feedback: { items: [{ kind: 'comment', author: 'dev', body: 'Keep the 2 s sweep check.', at: github.tick() }] } });
  const rework = github.repo.runs.at(-1);
  assert.deepEqual([rework.workflow, rework.inputs.mode, rework.inputs.amendment, rework.inputs.folder], ['speckit-fix.lock.yml', 'revise', '12', FOLDER]);
  // GitHub rejects a dispatch with an input the workflow does not declare.
  for (const run of github.repo.runs.filter((item) => item.workflow.endsWith('.lock.yml'))) {
    const declared = declaredInputs(run.workflow.replace('.lock.yml', '.md'));
    assert.deepEqual(Object.keys(run.inputs).filter((name) => !declared.includes(name)), [], `${run.workflow} declares every input it is dispatched with`);
  }
  assert.match(github.comments.filter((item) => item.number === 12).at(-1).body, /\*\*Reworking this fix spec\*\* with the feedback from @dev/);

  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-fix-evidence-'));
  try {
    await github.createComment(9, `${DIAGNOSIS_COMMENT_MARKER}\n**Diagnosis**: a flaky test from another feature.`);
    const { context: author } = await runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_STAMP: '20261014-100000' }), log: silent, now: () => github.clock });
    assert.deepEqual([author.folderPrefix, author.branchPrefix, author.titlePrefix, author.fixForLine], ['20261014-100000-fix-', 'speckit-fix/', 'Fix spec: ', '**Fix for**: #9 (implementation of `specs/f`)']);
    const evidence = readFileSync(path.join(dir, 'evidence.md'), 'utf8');
    assert.match(evidence, /## Guidance from the person who asked for the fix spec\n\nMake the test robust/);
    assert.match(evidence, /## Latest diagnosis\n\n<!-- speckit-implement:diagnosis -->\n\*\*Diagnosis\*\*: a flaky test/);
    const { context: revise } = await runEvidence({ client: github, env: env({ SPECKIT_EVIDENCE_DIR: dir, SPECKIT_MODE: 'revise', SPECKIT_AMENDMENT: '12', SPECKIT_FOLDER: FOLDER, SPECKIT_NOTES: 'Keep the sweep check.' }), log: silent });
    assert.deepEqual([revise.specDir, revise.branch, revise.fixPull, revise.baseBranch], [PREFIX, `speckit-fix/${FOLDER}`, 12, 'main']);
    assert.match(readFileSync(path.join(dir, 'evidence.md'), 'utf8'), /## Feedback on the fix spec to address\n\nKeep the sweep check\./);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('after the merge, the fix spec is linked, requested for implementation, and the implementation resumes once it lands', async () => {
  const github = fixRepo();
  github.permissions.dev = 'write';
  await authored(github);
  github.closePull(12, { merged: true });
  const implementation = await github.getPullRequest(9);
  const maintain = () => maintainFixSpec(github, env(), { folder: 'f', implementation, twin: 5 }, { now: github.clock, report: silent });

  assert.equal((await maintain()).outcome, 'waiting', 'the twin does not exist before Spec Kit prepare ran');
  assert.match(github.comments.filter((item) => item.number === 9).at(-1).body, /^<!-- speckit-fix:merged \{"pull":12,[\s\S]*\*\*Fix spec #12 was merged\*\* into `main` by a person/);
  assert.equal(github.repo.branches[`speckit-fix/${FOLDER}`], undefined);

  github.issues.push({ number: 30, id: 30000, node_id: 'I_30', state: 'open', state_reason: null, title: 'Fix: Upload session expiry', body: renderSpecLine({ serverUrl: 'https://github.com', repository: 'octo/repo', branch: 'main' }, FOLDER), labels: [{ name: 'speckit:spec' }, { name: 'speckit:stage:tasked' }] });
  assert.equal((await maintain()).outcome, 'linked');
  assert.deepEqual(github.edges, [[5, 30]], 'the stopped spec waits for the fix');
  assert.deepEqual(github.find(30).labels.map((label) => label.name).sort(), ['speckit:spec', 'speckit:stage:implement']);
  assert.match(github.comments.find((item) => item.number === 30).body, /^<!-- speckit:flagged-for dev -->\nImplementation requested on behalf of @dev/);
  assert.match(github.comments.filter((item) => item.number === 9).at(-1).body, /^<!-- speckit-fix:linked \{"pull":12,"folder":"20261014-100000-fix-upload-expiry","twin":30\} -->\n\*\*Fix spec #12 is spec twin #30\*\*, requested for implementation on behalf of @dev/);
  assert.equal(github.repo.runs.at(-1).workflow, 'speckit-orchestrate.yml', 'the scheduler starts the fix spec right away');

  // The workflow token set the flag; it counts for the person who asked for the fix.
  github.events.push({ issue: 30, event: 'labeled', label: { name: 'speckit:stage:implement' }, actor: { login: 'github-actions[bot]' }, created_at: github.tick() });
  assert.deepEqual(await resolveImplementRequester(github, 30), { login: 'dev', canWrite: true, labeledAt: github.events.at(-1).created_at });
  await github.createComment(30, '**Blocks** #5: quoted <!-- speckit:flagged-for mallory -->');
  assert.equal((await resolveImplementRequester(github, 30)).login, 'dev', 'only a marker that starts the workflow\'s own comment counts');

  assert.equal((await maintain()).outcome, 'waiting', 'nothing happens while the fix is implemented');
  Object.assign(github.find(30), { state: 'closed', state_reason: 'completed' });
  assert.equal((await maintain()).outcome, 'resumed');
  const resumed = github.comments.filter((item) => item.number === 9).at(-1);
  assert.ok(resumed.body.startsWith(RESUME_COMMENT_MARKER), 'a resume merges the base branch, with the fix, first');
  assert.match(resumed.body, /<!-- speckit-fix:landed \{"pull":12,"twin":30\} -->\n\*\*The fix has landed\*\*: spec twin #30/);
  assert.match(parseGuidance(resumed.body), /^The cause that stopped this implementation was fixed outside this spec by the fix spec specs\/20261014-100000-fix-upload-expiry \(twin #30\)/);
  assert.deepEqual([github.repo.runs.at(-1).workflow, github.repo.runs.at(-1).inputs], ['speckit-implement-spec.yml', { twin: '5', pull: '9', title: 'F' }]);
  assert.equal((await maintain()).outcome, 'done');
  assert.deepEqual(parseFixEvents(github.comments.filter((item) => item.number === 9)).map((event) => event.event), ['requested', 'created', 'merged', 'linked', 'landed']);
});

test('a discarded fix spec, or one that is not implemented, stops for a person; a pending one refuses another', async () => {
  const github = fixRepo();
  await authored(github);
  const implementation = await github.getPullRequest(9);
  const again = await startFixAuthoring(github, { SPECKIT_DEFAULT_BRANCH: 'main' }, { twin: 5, implementation, actor: 'dev', now: github.clock });
  assert.match(again.reason, /fix spec #12 is still open/);
  github.closePull(12);
  const settled = await settleClosedFix(github, env(), { amendment: await github.getPullRequest(12), folder: FOLDER, implementation, actor: 'dev' });
  assert.equal(settled.outcome, 'discarded');
  assert.match(github.comments.at(-1).body, /^<!-- speckit-fix:discarded \{"pull":12,[\s\S]*Fix spec #12 was closed without merging by @dev\.[\s\S]*\*\*Recommended next step\*\*/);
  assert.equal((await settleClosedFix(github, env(), { amendment: await github.getPullRequest(12), folder: FOLDER, implementation })).outcome, 'handled');
  assert.equal((await startFixAuthoring(github, { SPECKIT_DEFAULT_BRANCH: 'main' }, { twin: 5, implementation, actor: 'dev', now: github.clock + 3 * 60 * 60 * 1000 })).started, true, 'a discarded fix spec can be requested again');

  const stopped = fixRepo();
  await authored(stopped);
  stopped.closePull(12, { merged: true });
  stopped.issues.push({ number: 30, id: 30000, node_id: 'I_30', state: 'open', title: 'Fix', body: renderSpecLine({ serverUrl: 'https://github.com', repository: 'octo/repo', branch: 'main' }, FOLDER), labels: [{ name: 'speckit:spec' }] });
  const maintain = () => maintainFixSpec(stopped, env(), { folder: 'f', implementation, twin: 5 }, { now: stopped.clock, report: silent });
  await maintain();
  await maintain();
  Object.assign(stopped.find(30), { state: 'closed', state_reason: 'not_planned' });
  assert.equal((await maintain()).outcome, 'stopped');
  assert.match(stopped.comments.at(-1).body, /^<!-- speckit-fix:stopped [\s\S]*\*\*The fix spec was not implemented\*\*: spec twin #30 was closed without being implemented/);
});

test('fix specs are added to the default branch only, with a UTC folder stamp', async () => {
  const github = fixRepo();
  github.repo.pulls[0].base.ref = 'release';
  const refused = await startFixAuthoring(github, { SPECKIT_DEFAULT_BRANCH: 'main' }, { twin: 5, implementation: await github.getPullRequest(9), actor: 'dev' });
  assert.match(refused.reason, /base branch `release` is not the default branch `main`/);
  assert.equal(fixStamp(Date.parse('2026-10-14T08:23:07Z')), '20261014-082307');
});
