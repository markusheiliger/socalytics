import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { main as commandMain, runCommand } from './speckit-commands.mjs';
import { runReset, maintainReset } from './speckit-reset.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { renderSpecLine } from './speckit-prepare-core.mjs';
import {
  RESET_MARKER, implementationComments, implementationRunCurrent, parseAttempts,
  resetState, syncBarrier, unresolvedRepair, renderRepairMarker, renderSyncComment,
  renderAttemptMarker, renderFixEvent, COPILOT_HANDOFF_MARKER,
} from './speckit-implement-core.mjs';
import { decideForPull, runContinue, runVerifyReport } from './speckit-spec.mjs';
import { runLand, runMergeLand } from './speckit-implement-work.mjs';
import { maintainAmendment, runClosed, startAnalysis, startRework, presentAmendment } from './speckit-amend.mjs';
import { runRecord as recordAuthor, runLand as landAuthor, startFixAuthoring } from './speckit-fix.mjs';
import { autoContinueCommand, recommendNextSteps, renderDiagnosisComment, validateReport, runRecord as recordDiagnosis } from './speckit-diagnose.mjs';
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
  client.cancelledRuns = [];
  client.cancelWorkflowRun = async (id) => {
    client.cancelledRuns.push(id);
    client.repo.runs.find((run) => run.id === id).status = 'completed';
  };
  client.deletedBranches = [];
  client.deleteAmendmentBranch = async (branch, head) => {
    assert.equal(client.repo.branches[branch], head);
    delete client.repo.branches[branch];
    client.deletedBranches.push(branch);
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

function amendment(client, { open = true, orphan = false } = {}) {
  const head = 'd'.repeat(40);
  client.repo.branches['speckit-amend/f'] = head;
  if (!orphan) client.repo.pulls.push({ number: 11, state: open ? 'open' : 'closed', created_at: client.tick(),
    head: { ref: 'speckit-amend/f', sha: head, repo: { full_name: 'octo/repo' } }, base: { ref: 'speckit/f' } });
}

function activeRun(client, id, workflow = 'speckit-implement-group.yml', title = 'Spec Kit implement spec #5') {
  client.repo.runs.push({ id, workflow, display_title: title, status: 'in_progress', created_at: client.tick() });
}

async function prepare(client, request, extra = {}) {
  return runReset({ client, env, actor: request.user.login, number: 9, commentId: request.id, folder: 'f', twin: 5,
    implementation: await client.getPullRequest(9), flags: ['--force'], prepareOnly: true,
    now: () => client.clock, pause: async () => {}, ...extra });
}

test('force preparation stops only owned work before closing amendments; the locked stage resets and restarts', async () => {
  const client = fixture();
  amendment(client);
  activeRun(client, 1);
  activeRun(client, 2, 'speckit-commands.yml', 'Spec Kit commands: feedback on pull request #11');
  activeRun(client, 3, 'speckit-implement-group.yml', 'Spec Kit implement spec #50');
  activeRun(client, 4, 'speckit-orchestrate.yml', 'Spec Kit orchestrate');
  activeRun(client, 5, 'speckit-commands.yml', 'Spec Kit commands: comment on #9');
  const request = await source(client, '/speckit reset --force');
  assert.equal((await prepare(client, request, { env: { ...env, GITHUB_RUN_ID: '5' } })).outcome, 'prepared');
  assert.deepEqual(client.cancelledRuns, [1, 2]);
  assert.equal(client.repo.branches['speckit/f'], OLD);
  assert.equal((await client.getPullRequest(11)).state, 'closed');
  assert.deepEqual(client.deletedBranches, ['speckit-amend/f']);
  assert.equal((await command(client, request, { GITHUB_RUN_ID: '5' })).outcome, 'reset');
  assert.equal(client.repo.branches['speckit/f'], NEW);
  assert.equal(client.repo.runs.at(-1).workflow, 'speckit-implement-group.yml');
  assert.equal(client.repo.branches.main, BASE);
  assert.equal(client.repo.refs[`refs/tags/speckit-reset/pr-9/${request.id}`], OLD);
});

test('force removes only the exact owned leftover or orphan amendment branch', async () => {
  for (const orphan of [false, true]) {
    const client = fixture();
    amendment(client, { open: false, orphan });
    client.repo.branches['speckit-amend/other'] = BASE;
    await prepare(client, await source(client, '/speckit reset --force'));
    assert.deepEqual(client.deletedBranches, ['speckit-amend/f']);
    assert.equal(client.repo.branches['speckit-amend/other'], BASE);
    assert.equal(client.repo.branches['speckit/f'], OLD);
  }
});

test('force refuses authored prerequisite PRs and unconfirmed external handoffs before cancelling anything', async () => {
  for (const blocker of ['authored', 'handoff']) {
    const client = fixture();
    amendment(client);
    activeRun(client, 1);
    if (blocker === 'authored') {
      client.repo.pulls.push({ number: 12, state: 'open', head: { ref: 'speckit-spec/other' } });
      await client.createComment(9, renderFixEvent('created', { pull: 12 }));
    } else await client.createComment(9, COPILOT_HANDOFF_MARKER);
    const result = await prepare(client, await source(client, '/speckit reset --force'));
    assert.ok(result.rejected);
    assert.deepEqual(client.cancelledRuns, []);
    assert.deepEqual(client.deletedBranches, []);
    assert.equal((await client.getPullRequest(11)).state, 'open');
  }
});

test('force waits for terminal cancellation and fails closed on timeout, missing evidence, and permission errors', async () => {
  for (const failure of ['timeout', 'missing', 'permission']) {
    const client = fixture();
    amendment(client);
    activeRun(client, 1);
    client.cancelWorkflowRun = async () => {
      if (failure === 'permission') throw new Error('cancellation forbidden');
      if (failure === 'missing') client.repo.runs = [];
    };
    const request = await source(client, '/speckit reset --force');
    await assert.rejects(prepare(client, request), /Timed out|Cannot confirm|forbidden/);
    assert.equal(client.repo.branches['speckit/f'], OLD);
    assert.equal((await client.getPullRequest(11)).state, 'open');
    assert.deepEqual(client.deletedBranches, []);
    assert.equal(resetState(await client.listIssueComments(9)).pending, null);
  }
});

test('cleanup failures report partial closure and never reset the implementation or silently accept deletion', async () => {
  const client = fixture();
  amendment(client);
  client.deleteAmendmentBranch = async () => {};
  const request = await source(client, '/speckit reset --force');
  await assert.rejects(prepare(client, request), /deletion could not be verified/);
  assert.equal(client.repo.branches['speckit/f'], OLD);
  assert.equal((await client.getPullRequest(11)).state, 'closed');
  const state = resetState(await client.listIssueComments(9)).records.at(-1);
  assert.equal(state.state, 'failed');
  assert.deepEqual(state.cleanup.closed, [11]);
  assert.equal((await maintainAmendment(client, env, { implementation: await client.getPullRequest(9), twin: 5, folder: 'f' })).outcome, 'superseded');
});

test('a concurrent amendment push is preserved rather than deleted by force', async () => {
  const client = fixture();
  amendment(client);
  const update = client.updatePullRequest.bind(client);
  client.updatePullRequest = async (number, fields) => {
    const result = await update(number, fields);
    if (number === 11) client.repo.branches['speckit-amend/f'] = 'e'.repeat(40);
    return result;
  };
  await assert.rejects(prepare(client, await source(client, '/speckit reset --force')), /branch changed/);
  assert.equal(client.repo.branches['speckit-amend/f'], 'e'.repeat(40));
  assert.equal(client.repo.branches['speckit/f'], OLD);
  assert.deepEqual(client.deletedBranches, []);
});

test('the completion boundary excludes old cancellation reports emitted after the pending fence', async () => {
  const client = fixture();
  activeRun(client, 1);
  const cancel = client.cancelWorkflowRun;
  client.cancelWorkflowRun = async (id) => {
    await client.createComment(9, renderAttemptMarker({ kind: 'task', task: 'T001', attempt: 2, outcome: 'failure' }));
    return cancel(id);
  };
  const request = await source(client, '/speckit reset --force');
  await prepare(client, request);
  assert.equal(parseAttempts(await client.listIssueComments(9)).length, 1);
  await command(client, request);
  assert.deepEqual(parseAttempts(await client.listIssueComments(9)), []);
  const decision = await decideForPull(client, { twin: 5, folder: 'f', pullNumber: 9, now: client.clock, context });
  assert.equal(decision.order[0][0].task, 'T001');
  assert.equal(decision.order[0][0].attempt, 1);
});

test('force mode is bound to the writer authorization and preparation is idempotent', async () => {
  const client = fixture();
  amendment(client);
  const request = await source(client, '/speckit reset --force');
  await prepare(client, request);
  await prepare(client, request);
  assert.deepEqual(client.deletedBranches, ['speckit-amend/f']);
  await client.updateComment(request.id, '/speckit reset');
  await assert.rejects(prepare(client, request), /unchanged/);
  await assert.rejects(command(client, { ...request, body: '/speckit reset' }), /force authorization changed/);
  assert.equal(client.refUpdates, undefined);
});

test('trusted force preparation can be resumed through the command CLI without a second writer command', async () => {
  const client = fixture();
  amendment(client);
  const request = await source(client, '/speckit reset --force');
  const resumeEnv = { ...env, SPECKIT_TARGET: 'pull', SPECKIT_RESET_RESUME: String(request.id) };
  assert.equal(await commandMain(['prepare-reset'], { client, env: resumeEnv, log: silent }), 0);
  assert.equal(client.repo.branches['speckit/f'], OLD);
  assert.equal(await commandMain(['command'], { client, env: resumeEnv, log: silent }), 0);
  assert.equal(client.refUpdates, 1);
  assert.equal((await command(client, request)).outcome, 'already reset');
});

test('revoked force authorization terminates interrupted preparation instead of endlessly redispatching', async () => {
  const client = fixture();
  const request = await source(client, '/speckit reset --force');
  await prepare(client, request);
  await client.updateComment(request.id, '/speckit retry');
  await assert.rejects(commandMain(['target'], { client, env: { ...env, SPECKIT_RESET_RESUME: String(request.id) }, log: silent }), /unchanged/);
  const state = resetState(await client.listIssueComments(9));
  assert.equal(state.pending, null);
  assert.equal(state.records.at(-1).state, 'failed');
  assert.equal(await maintainReset(client, env, { implementation: await client.getPullRequest(9), twin: 5, folder: 'f' }), false);
  assert.equal(client.repo.branches['speckit/f'], OLD);
});

test('force workflow preparation precedes the publication lock and locked execution requires its success', () => {
  const yaml = readFileSync(new URL('../workflows/speckit-commands.yml', import.meta.url), 'utf8');
  const prep = yaml.slice(yaml.indexOf('\n  prepare-reset:'), yaml.indexOf('\n  command:'));
  const locked = yaml.slice(yaml.indexOf('\n  command:'), yaml.indexOf('\n  feedback:'));
  assert.match(prep, /needs: target/);
  assert.match(prep, /speckit-reset-control-/);
  assert.doesNotMatch(prep, /speckit-publication-/);
  assert.match(prep, /ref: \$\{\{ github.event.repository.default_branch \}\}/);
  assert.match(locked, /needs: \[target, prepare-reset\]/);
  assert.match(locked, /needs.prepare-reset.outputs.prepared == 'true'/);
  assert.match(locked, /speckit-publication-/);
  assert.doesNotMatch(yaml, /workflows: write/);
});

test('GitHub cleanup APIs cancel normally and delete only a leased amendment ref', async () => {
  const calls = [];
  const client = new GitHubClient({ token: 'test', repository: 'octo/repo', fetchImpl: async (url, init) => {
    calls.push({ url, method: init.method, body: init.body ? JSON.parse(init.body) : null });
    const data = url.endsWith('/graphql') ? { data: { updateRefs: { clientMutationId: null } } } : { node_id: 'R_1' };
    return new Response(JSON.stringify(data));
  } });
  client.getBranchSha = async () => null;
  await client.cancelWorkflowRun(123);
  assert.ok(calls[0].url.endsWith('/actions/runs/123/cancel'));
  assert.equal(calls[0].method, 'POST');
  await client.deleteAmendmentBranch('speckit-amend/f', OLD);
  assert.deepEqual(calls.at(-1).body.variables.input.refUpdates, [
    { name: 'refs/heads/speckit-amend/f', beforeOid: OLD, afterOid: '0'.repeat(40) },
  ]);
  await assert.rejects(client.deleteAmendmentBranch('main', OLD), /Invalid/);
  client.getBranchSha = async () => OLD;
  await assert.rejects(client.deleteAmendmentBranch('speckit-amend/f', OLD), /still exists/);
});

test('cleanup advice stays prose-only while the copyable diagnosis recommendation remains bare reset', async () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-force-advice-'));
  try {
    const client = fixture();
    amendment(client);
    const file = path.join(dir, 'report.json');
    writeFileSync(file, JSON.stringify({ items: [{ type: 'speckit_diagnosis', report: JSON.stringify(resetReport()) }] }));
    const result = await recordDiagnosis({ client, env: { ...env, SPECKIT_FOLDER: 'f',
      GH_AW_AGENT_OUTPUT: file, SPECKIT_DETECTION: 'success' }, log: silent });
    assert.equal(result.report.options.find((item) => item.recommended).command, '/speckit reset');
    assert.match(result.report.options.find((item) => item.recommended).why, /manually type the --force flag/);
    assert.equal(result.continued, null);
    const comment = client.comments.at(-1).body;
    assert.doesNotMatch(comment, /\/speckit reset --force/);
    assert.match(comment, /--force/);
    assert.equal(client.repo.runs.length, 0);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
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
    const text = scenario === 'guidance' ? '/speckit reset do it' : scenario === 'flag' ? '/speckit reset --unknown' : '/speckit reset';
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
  const implementation = await client.getPullRequest(9);
  const amendmentContext = { implementation, amendment: { number: 11, created_at: implementation.created_at } };
  for (const run of [startAnalysis, startRework, presentAmendment]) {
    assert.equal((await run(client, staleEnv, amendmentContext, {})).outcome, 'superseded');
  }
  assert.equal((await startFixAuthoring(client, staleEnv, { implementation, twin: 5 })).started, false);
  const dir = mkdtempSync(path.join(tmpdir(), 'speckit-reset-stale-author-'));
  try {
    for (const run of [recordAuthor, landAuthor]) {
      assert.equal((await run({ client, env: { ...staleEnv, SPECKIT_FOLDER: 'f', SPECKIT_RESULT_DIR: dir }, log: silent })).outcome, 'superseded');
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
  assert.equal(client.comments.length, before);
  client.repo.runs.push({ id: 701, created_at: client.tick(), status: 'in_progress' });
  assert.equal(await implementationRunCurrent(client, 9, { GITHUB_RUN_ID: '701' }), true);
});

test('forged reset markers do not discard history, malformed trusted records stop explicitly', () => {
  const forged = { id: 1, user: { login: 'dev' }, body: `${RESET_MARKER}{} -->` };
  assert.deepEqual(implementationComments([forged]), [forged]);
  assert.throws(() => resetState([{ ...forged, user: { login: 'github-actions[bot]' } }]), /invalid/);
  const record = { state: 'pending', source: 10, oldHead: OLD, baseSha: BASE,
    base: 'main', branch: 'speckit/f', recovery: 'refs/tags/speckit-reset/pr-9/10', force: true,
    actor: 'dev', repository: 'octo/repo', cleanup: { cancellations: [], closed: [], amendments: [],
      branch: { name: 'speckit-amend/f', head: null } } };
  for (const fields of [{ branch: 42 }, { repository: {} }, { cleanup: { ...record.cleanup, amendments: [null] } }]) {
    const body = `${RESET_MARKER}${JSON.stringify({ ...record, ...fields })} -->`;
    assert.throws(() => resetState([{ ...forged, body, user: { login: 'github-actions[bot]' } }]), /record is invalid/);
  }
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
