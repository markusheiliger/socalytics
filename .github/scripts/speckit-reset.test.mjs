import assert from 'node:assert/strict';
import test from 'node:test';

import { main as commandMain, runCommand } from './speckit-commands.mjs';
import { resetEligibility, maintainReset } from './speckit-reset.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { renderSpecLine } from './speckit-prepare-core.mjs';
import {
  RESET_MARKER, implementationComments, implementationRunCurrent, parseAttempts,
  resetState, syncBarrier, unresolvedRepair, renderRepairMarker, renderSyncComment,
} from './speckit-implement-core.mjs';
import { decideForPull, runContinue, runVerifyReport } from './speckit-spec.mjs';
import { runLand, runMergeLand } from './speckit-implement-work.mjs';
import { maintainAmendment, runClosed } from './speckit-amend.mjs';
import { autoContinueCommand, recommendNextSteps, renderDiagnosisComment, validateReport } from './speckit-diagnose.mjs';
import { diagnosisRepo, silent } from './speckit-test-helpers.mjs';

const BASE = 'a'.repeat(40);
const OLD = 'b'.repeat(40);
const NEW = 'c'.repeat(40);
const tasks = '## Phase 1: Setup\n- [ ] T001 Create src/example.js\n- [ ] T002 Test src/example.js\n';
const context = { repository: 'octo/repo', serverUrl: 'https://github.com', branch: 'main' };
const env = { GITHUB_REPOSITORY: 'octo/repo', SPECKIT_DEFAULT_BRANCH: 'main', SPECKIT_PULL: '9', SPECKIT_TWIN: '5' };

function fixture() {
  const client = diagnosisRepo({ amendment: false });
  client.find(5).body = renderSpecLine(context, 'f');
  client.repo.branches.main = BASE;
  client.repo.branches['speckit/f'] = OLD;
  client.repo.commits[BASE] = { tree: { sha: 'base-tree' }, parents: [] };
  client.repo.commits[OLD] = { tree: { sha: 'divergent-tooling-tree' }, parents: [{ sha: BASE }] };
  for (const [file, text] of Object.entries({ 'spec.md': '# F\n', 'plan.md': '# Plan\n', 'tasks.md': tasks, 'checklists/requirements.md': '- [x] Ready\n' })) {
    client.setFile(BASE, `specs/f/${file}`, text);
  }
  client.getGitCommit = async (sha) => structuredClone(client.repo.commits[sha]);
  client.getRefSha = async (ref) => client.repo.refs?.[ref] ?? null;
  client.createEmptyCommit = async (sha, message) => {
    client.repo.commits[NEW] = { tree: client.repo.commits[sha].tree, parents: [{ sha }], message };
    for (const [key, text] of client.repo.files) if (key.startsWith(`${sha}:`)) client.repo.files.set(key.replace(sha, NEW), text);
    return NEW;
  };
  client.resetImplementationRef = async ({ branch, oldHead, newHead, recovery }) => {
    assert.equal(client.repo.branches[branch], oldHead, 'exact-head lease');
    client.repo.refs ??= {};
    assert.equal(client.repo.refs[recovery], undefined, 'create-if-absent recovery lease');
    client.repo.refs[recovery] = oldHead;
    client.repo.branches[branch] = newHead;
    client.refUpdates = (client.refUpdates ?? 0) + 1;
  };
  return client;
}

async function source(client, body = '/speckit reset', actor = 'dev') {
  client.commentAuthor = actor;
  const comment = await client.createComment(9, body);
  client.commentAuthor = undefined;
  return comment;
}

const command = (client, comment, extra = {}) => runCommand({ client,
  env: { ...env, SPECKIT_ACTOR: comment.user.login, SPECKIT_COMMENT_ID: String(comment.id), SPECKIT_COMMENT_BODY: comment.body, ...extra },
  log: silent, now: () => client.clock,
});

test('reset preserves PR and recovery, uses the exact base tree, and starts the first task without old state', async () => {
  const client = fixture();
  await client.createComment(9, renderRepairMarker({ id: 'old', state: 'pending', scope: 'merge', at: client.tick() }));
  await client.createComment(9, renderSyncComment('Old sync'));
  await client.createComment(9, '<!-- speckit-implement:done -->\nDone');
  await client.createComment(9, '<!-- speckit-implement:attempt {"kind":"task","task":"T001","attempt":2,"outcome":"failure"} -->');
  const request = await source(client);
  const result = await command(client, request);
  assert.equal(result.outcome, 'reset');
  assert.equal(client.repo.branches['speckit/f'], NEW);
  assert.equal(client.repo.refs[`refs/tags/speckit-reset/pr-9/${request.id}`], OLD);
  assert.deepEqual(client.repo.commits[NEW].tree, client.repo.commits[BASE].tree);
  assert.deepEqual(client.repo.commits[NEW].parents, [{ sha: BASE }]);
  assert.equal((await client.getPullRequest(9)).draft, true);
  const comments = await client.listIssueComments(9);
  assert.equal(comments.some((item) => item.body.includes('Old sync')), true, 'audit retained');
  assert.equal(unresolvedRepair(comments), null);
  assert.equal(syncBarrier(comments), null);
  assert.deepEqual(parseAttempts(comments), []);
  const decision = await decideForPull(client, { twin: 5, folder: 'f', pullNumber: 9, now: client.clock, context });
  assert.equal(decision.action, 'tasks');
  assert.equal(decision.kind, 'task');
  assert.equal(decision.order[0][0].task, 'T001');
  assert.equal(decision.order[0][0].attempt, 1);
  assert.equal(await client.getFileContent('specs/f/tasks.md', NEW), tasks);
  assert.equal(client.repo.runs.at(-1).workflow, 'speckit-implement-group.yml');
  assert.equal(client.repo.runs.at(-1).ref, 'main');
  assert.equal(client.repo.runs.at(-1).inputs.issue, '5');
  assert.equal(client.repo.runs.at(-1).inputs.pull, '9');
  assert.equal(resetState(comments).completed.dispatched, true);
  assert.equal(await maintainReset(client, env, { implementation: await client.getPullRequest(9), twin: 5, folder: 'f' }), false);
  assert.equal((await command(client, request)).outcome, 'already reset');
  assert.equal(client.refUpdates, 1);
});

test('reset refuses active work, open child PRs, ineligible bases and unauthorized or malformed commands', async () => {
  for (const scenario of ['active', 'queued-command', 'child', 'checked', 'missing', 'checklist', 'unauthorized', 'guidance', 'flag']) {
    const client = fixture();
    if (scenario === 'active') await client.dispatchWorkflow('speckit-implement-group.yml', 'main', { issue: '5', title: 'F' });
    if (scenario === 'queued-command') client.repo.runs.push({ id: 700, workflow: 'speckit-commands.yml',
      status: 'queued', created_at: client.tick(), display_title: 'Spec Kit commands · comment on #9' });
    if (scenario === 'child') client.repo.pulls.push({ number: 11, state: 'open', head: { ref: 'speckit-amend/f' } });
    if (scenario === 'checked') client.setFile(BASE, 'specs/f/tasks.md', tasks.replace('[ ]', '[x]'));
    if (scenario === 'missing') client.repo.files.delete(`${BASE}:specs/f/plan.md`);
    if (scenario === 'checklist') client.setFile(BASE, 'specs/f/checklists/requirements.md', '- [ ] Not ready\n');
    const text = scenario === 'guidance' ? '/speckit reset do it' : scenario === 'flag' ? '/speckit reset --force' : '/speckit reset';
    const request = await source(client, text, scenario === 'unauthorized' ? 'stranger' : 'dev');
    const result = await command(client, request);
    assert.ok(result.rejected || result.handled === false, scenario);
    assert.equal(client.repo.branches['speckit/f'], OLD, scenario);
    assert.equal(client.refUpdates, undefined);
  }
});

test('reset exempts only its own command run from activity refusal', async () => {
  const client = fixture();
  client.repo.runs.push({ id: 700, workflow: 'speckit-commands.yml', status: 'in_progress',
    created_at: client.tick(), display_title: 'Spec Kit commands · comment on #9' });
  assert.equal((await command(client, await source(client), { GITHUB_RUN_ID: '700' })).outcome, 'reset');
});

test('reset is relative to the actual base, and preserves native issue dependencies', async () => {
  const client = fixture();
  client.repo.branches.release = BASE;
  client.repo.pulls[0].base.ref = 'release';
  client.edges.push([5, 6]);
  const result = await command(client, await source(client));
  assert.equal(result.outcome, 'reset');
  assert.equal(resetState(await client.listIssueComments(9)).completed.base, 'release');
  assert.deepEqual(client.edges, [[5, 6]]);
});

test('a concurrent head change is never overwritten, and a recovery collision fails without losing work', async () => {
  for (const collision of [false, true]) {
    const client = fixture();
    const publish = client.resetImplementationRef;
    client.resetImplementationRef = async (state) => {
      if (collision) client.repo.refs = { [state.recovery]: BASE };
      else client.repo.branches[state.branch] = 'd'.repeat(40);
      return publish(state);
    };
    await assert.rejects(command(client, await source(client)));
    assert.equal(client.repo.branches['speckit/f'], collision ? OLD : 'd'.repeat(40));
    assert.equal(client.refUpdates, undefined);
    assert.equal(resetState(await client.listIssueComments(9)).pending, null);
    assert.equal(resetState(await client.listIssueComments(9)).records.at(-1).state, 'failed');
  }
});

test('interruption after atomic replacement is reconciled without a second reset', async () => {
  const client = fixture();
  const get = client.getGitCommit;
  client.getGitCommit = async () => { throw new Error('temporary read failure'); };
  const request = await source(client);
  await assert.rejects(command(client, request), /temporary read failure/);
  assert.ok(resetState(await client.listIssueComments(9)).pending);
  assert.equal(client.repo.branches['speckit/f'], NEW);
  client.getGitCommit = get;
  assert.equal((await command(client, request)).outcome, 'reset');
  assert.equal(client.refUpdates, 1);
});

test('pending reset fences work and old runs cannot publish, verify, or continue after reset', async () => {
  const client = fixture();
  client.repo.runs.push({ id: 700, created_at: client.tick(), status: 'completed' });
  await command(client, await source(client));
  const before = client.comments.length;
  const staleEnv = { ...env, GITHUB_RUN_ID: '700' };
  assert.equal(await implementationRunCurrent(client, 9, staleEnv), false);
  for (const run of [runContinue, runVerifyReport]) assert.equal((await run({ client, env: staleEnv, log: silent })).outcome, 'superseded');
  for (const run of [runLand, runMergeLand]) assert.equal((await run({ client, env: staleEnv, inputs: { pull: 9 } })).outcome, 'superseded');
  assert.equal(client.comments.length, before);
  client.repo.runs.push({ id: 701, created_at: client.tick(), status: 'in_progress' });
  assert.equal(await implementationRunCurrent(client, 9, { GITHUB_RUN_ID: '701' }), true);
});

test('forged reset markers do not discard history, malformed trusted records stop explicitly', () => {
  const forged = { id: 1, user: { login: 'dev' }, body: `${RESET_MARKER}{} -->` };
  assert.deepEqual(implementationComments([forged]), [forged]);
  assert.throws(() => resetState([{ ...forged, user: { login: 'github-actions[bot]' } }]), /invalid/);
});

test('pending reset reconciliation dispatches only the trusted command workflow', async () => {
  const client = fixture();
  const get = client.getGitCommit;
  client.getGitCommit = async () => { throw new Error('offline'); };
  await assert.rejects(command(client, await source(client)));
  client.getGitCommit = get;
  assert.equal(await maintainReset(client, env, { implementation: await client.getPullRequest(9), twin: 5, folder: 'f' }), true);
  assert.equal(client.repo.runs.at(-1).workflow, 'speckit-commands.yml');
  assert.ok(client.repo.runs.at(-1).inputs.reset_comment);
  assert.equal(await commandMain(['command'], { client, env: { ...env, SPECKIT_TARGET: 'pull',
    SPECKIT_RESET_RESUME: client.repo.runs.at(-1).inputs.reset_comment }, log: silent }), 0);
  assert.ok(resetState(await client.listIssueComments(9)).completed);
  assert.equal(client.refUpdates, 1);
});

test('the scheduler automatically recovers failed post-reset dispatch without a second writer command or branch reset', async () => {
  const client = fixture();
  const dispatch = client.dispatchWorkflow.bind(client);
  client.dispatchWorkflow = async () => { throw new Error('dispatch unavailable'); };
  const request = await source(client);
  await assert.rejects(command(client, request), /dispatch unavailable/);
  assert.ok(resetState(await client.listIssueComments(9)).completed);
  client.dispatchWorkflow = dispatch;
  const target = { implementation: await client.getPullRequest(9), twin: 5, folder: 'f' };
  assert.equal(await maintainReset(client, { ...env, SPECKIT_AUTO_CONTINUE: 'false' }, target), true);
  const recovery = client.repo.runs.at(-1);
  assert.equal(recovery.workflow, 'speckit-commands.yml');
  assert.equal(recovery.inputs.reset_comment, String(request.id));
  recovery.display_title = 'Spec Kit commands · feedback on pull request #9';
  assert.equal(await maintainReset(client, env, target), true);
  assert.equal(client.repo.runs.at(-1).id, recovery.id, 'do not queue another active recovery');
  assert.equal(await commandMain(['command'], { client, env: { ...env, SPECKIT_TARGET: 'pull',
    SPECKIT_RESET_RESUME: recovery.inputs.reset_comment, SPECKIT_AUTO_CONTINUE: 'false' }, log: silent }), 0);
  assert.equal(client.repo.runs.at(-1).workflow, 'speckit-implement-group.yml');
  assert.equal(resetState(await client.listIssueComments(9)).completed.dispatched, true);
  assert.equal(await maintainReset(client, env, target), false);
  assert.equal(client.refUpdates, 1);
});

test('pre-reset closed amendments cannot settle or retry the new implementation', async () => {
  const client = fixture();
  const old = { number: 11, state: 'closed', created_at: client.tick(), closed_at: client.tick(),
    head: { ref: 'speckit-amend/f' }, base: { ref: 'speckit/f' }, merged_at: client.tick() };
  client.repo.pulls.push(old);
  await command(client, await source(client));
  const before = client.comments.length;
  assert.equal((await maintainAmendment(client, env, { implementation: await client.getPullRequest(9), twin: 5, folder: 'f' })).outcome, 'superseded');
  assert.equal((await runClosed({ client, env: { ...env, SPECKIT_PULL: '11' }, log: silent })).outcome, 'superseded');
  assert.equal(client.comments.length, before);
});

test('reset activity reads fail closed, and prepared reset blocks normal commands', async () => {
  const client = fixture();
  client.listWorkflowRuns = async () => { throw new Error('activity unavailable'); };
  await assert.rejects(command(client, await source(client)), /activity unavailable/);
  assert.equal(client.repo.branches['speckit/f'], OLD);
  const other = fixture();
  const replace = other.resetImplementationRef;
  other.resetImplementationRef = async (state) => {
    const pending = await decideForPull(other, { twin: 5, folder: 'f', pullNumber: 9, context });
    assert.equal(pending.action, 'resetting');
    assert.equal(await implementationRunCurrent(other, 9), false);
    return replace(state);
  };
  await command(other, await source(other));
});

test('GitHub reset API atomically creates recovery and replaces only the leased implementation ref', async () => {
  const calls = [];
  const client = new GitHubClient({ token: 'test', repository: 'octo/repo',
    fetchImpl: async (url, init) => {
      calls.push({ url, body: init.body ? JSON.parse(init.body) : null });
      return new Response(JSON.stringify(url.endsWith('/graphql') ? { data: { updateRefs: { clientMutationId: null } } } : { node_id: 'R_1' }));
    } });
  await client.resetImplementationRef({ branch: 'speckit/f', oldHead: OLD, newHead: NEW, recovery: 'refs/tags/speckit-reset/pr-9/1' });
  const updates = calls[1].body.variables.input.refUpdates;
  assert.deepEqual(updates, [
    { name: 'refs/tags/speckit-reset/pr-9/1', beforeOid: '0'.repeat(40), afterOid: OLD },
    { name: 'refs/heads/speckit/f', beforeOid: OLD, afterOid: NEW, force: true },
  ]);
  await assert.rejects(client.resetImplementationRef({ branch: 'main', oldHead: OLD, newHead: NEW, recovery: 'refs/tags/speckit-reset/pr-9/1' }), /Invalid/);
});

function resetReport(category = 'outside', confidence = 'high') {
  return { category, confidence, summary: 'The branch-only state needs a clean restart.',
    cause: 'The base is clean, and discarding the divergent implementation removes the obstacle.',
    evidence: ['The implementation branch has divergent state absent from the current base.'],
    options: [{ title: 'Start implementation again from the current base', why: 'Discard the divergent branch-only state rather than repeat it.',
      command: '/speckit reset', target: 'implementation', recommended: true }],
  };
}

test('reset is a legitimate primary recommendation with a destructive warning, never an automatic action', () => {
  for (const category of ['retry', 'outside', 'decision']) {
    for (const confidence of ['low', 'medium', 'high']) {
      const { report, reasons } = validateReport(resetReport(category, confidence));
      assert.deepEqual(reasons, []);
      assert.equal(recommendNextSteps(report).options.find((item) => item.recommended).command, '/speckit reset');
      assert.equal(autoContinueCommand(report, { baseFixReady: true }), null);
      const body = renderDiagnosisComment({ report, round: 1, implementationPull: 9, baseBranch: 'release' });
      assert.match(body, /Destructive restart of #9/);
      assert.match(body, /No reset runs automatically/);
      assert.match(body, /amendments/);
      assert.match(body, /recovery reference/);
      assert.match(body, /release/);
    }
  }
  for (const command of ['/speckit reset --force', '/speckit reset guidance']) {
    const raw = resetReport();
    raw.options[0].command = command;
    assert.ok(validateReport(raw).reasons.length > 0);
  }
  for (const category of ['artifacts', 'unknown']) assert.ok(validateReport(resetReport(category)).reasons.length > 0);
});
