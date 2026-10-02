import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

import {
  CHECKPOINT_TRAILER,
  parseCheckpointTrailer,
  parseRunStateText,
  renderChangeMarker,
  renderCheckpointTrailer,
} from './openspec-change-core.mjs';
import {
  admitChange,
  buildAgentPrompt,
  createContext,
  creditChange,
  dispatchChange,
  finalizeChange,
  findCheckpoint,
  notifyGate,
  prepareAgentPrompt,
  defaultPlatformTestResult,
  validateEvidence,
  observe,
  plan,
  publish,
  readRunState,
  summarizeTestLog,
} from './openspec-change-orchestrator.mjs';
import { LIFECYCLE_CHECK_NAME } from './openspec-change-state.mjs';

const REPOSITORY = 'markusheiliger/socalytics';
const CHANGE = 'add-club';
const implementation = readFileSync(new URL('../../openspec/capabilities/implementation.md', import.meta.url), 'utf8');
const verification = readFileSync(new URL('../../openspec/capabilities/verification.md', import.meta.url), 'utf8');
const TASKS = [
  '## 1. Work',
  '',
  '- [ ] 1.1 Build the first thing. Capabilities: implementation.',
  '- [ ] 1.2 Check the second thing. Capabilities: verification.',
  '',
].join('\n');

class FakeGitHub {
  constructor() {
    this.owner = 'markusheiliger';
    this.repo = 'socalytics';
    this.counter = 1000;
    this.commits = new Map();
    this.branches = new Map();
    this.issues = new Map();
    this.prs = new Map();
    this.comments = new Map();
    this.checkRuns = [];
    this.agentTasks = [];
    this.workflowRuns = [];
    this.returnRunDetails = true;
    this.permissions = { alice: 'write', mallory: 'read' };
    this.blockedBy = new Map();
    this.events = new Map();
    this.labels = new Set();
    this.reactions = [];
    this.started = [];
    const root = this.commit({
      message: 'initial',
      parents: [],
      files: new Map([
        [`openspec/changes/${CHANGE}/tasks.md`, TASKS],
        [`openspec/changes/${CHANGE}/proposal.md`, '# Proposal'],
        ['openspec/capabilities/implementation.md', implementation],
        ['openspec/capabilities/verification.md', verification],
      ]),
    });
    this.branches.set('main', root);
  }

  id() {
    this.counter += 1;
    return this.counter;
  }

  sha() {
    return this.id().toString(16).padStart(40, '0');
  }

  commit({ message, parents, files, author = 'copilot-swe-agent' }) {
    const sha = this.sha();
    this.commits.set(sha, { sha, message, parents, files, author, tree: `tree-${sha}` });
    return sha;
  }

  resolve(ref) {
    return this.branches.get(ref) ?? ref;
  }

  push(branch, { message, files = {}, remove = [], author }) {
    const parent = this.branches.get(branch);
    const next = new Map(this.commits.get(parent).files);
    for (const [path, text] of Object.entries(files)) next.set(path, text);
    for (const path of remove) next.delete(path);
    const sha = this.commit({ message, parents: [parent], files: next, author });
    this.branches.set(branch, sha);
    for (const pr of this.prs.values()) if (pr.head.ref === branch) pr.head.sha = sha;
    return sha;
  }

  addTwin(number, { labels = ['openspec:change'], state = 'open', ref = CHANGE, lifecycle = 'active' } = {}) {
    const path = lifecycle === 'active' ? `openspec/changes/${ref}` : `openspec/changes/archive/2026-10-01-${ref}`;
    const body = renderChangeMarker({ repository: REPOSITORY, ref, lifecycle, gitRef: 'main', path });
    this.issues.set(number, { id: number * 10, number, state, body, labels: labels.map((name) => ({ name })) });
  }

  // --- issues and comments
  async listIssueTwins() {
    return [...this.issues.values()].filter((issue) => issue.labels.some((label) => label.name === 'openspec:change'));
  }

  async getIssue(number) {
    return this.issues.get(number) ?? this.prs.get(number);
  }

  async updateIssue(number, patch) {
    Object.assign(this.issues.get(number), patch);
  }

  async addIssueLabel(number, label) {
    const target = this.issues.get(number) ?? this.prs.get(number);
    if (!target.labels.some((entry) => entry.name === label)) target.labels.push({ name: label });
  }

  async removeIssueLabel(number, label) {
    const target = this.issues.get(number) ?? this.prs.get(number);
    target.labels = target.labels.filter((entry) => entry.name !== label);
  }

  async ensureLabel({ name }) {
    this.labels.add(name);
  }

  async listIssueComments(number) {
    return [...(this.comments.get(number) ?? [])];
  }

  async createIssueComment(number, body) {
    const comment = {
      id: this.id(), body, user: { login: 'github-actions[bot]', type: 'Bot' }, created_at: 'x', updated_at: 'x',
    };
    this.comments.set(number, [...(this.comments.get(number) ?? []), comment]);
    return comment;
  }

  humanComment(number, login, body, { edited = false } = {}) {
    const comment = {
      id: this.id(), body, user: { login, type: 'User' }, created_at: 'a', updated_at: edited ? 'b' : 'a',
    };
    this.comments.set(number, [...(this.comments.get(number) ?? []), comment]);
    return comment;
  }

  async updateIssueComment(id, body) {
    for (const list of this.comments.values()) {
      const comment = list.find((entry) => entry.id === id);
      if (comment) {
        comment.body = body;
        return { ...comment };
      }
    }
    throw new Error(`comment ${id} not found`);
  }

  async addCommentReaction(id, content) {
    this.reactions.push({ id, content });
  }

  async listIssueEvents(number) {
    return this.events.get(number) ?? [];
  }

  async listBlockedBy(number) {
    return (this.blockedBy.get(number) ?? []).map((blocker) => this.issues.get(blocker));
  }

  async getCollaboratorPermission(login) {
    return this.permissions[login] ?? 'none';
  }

  // --- git
  async getBranch(name) {
    const sha = this.branches.get(name);
    const commit = this.commits.get(sha);
    return { name, commit: { sha, commit: { message: commit.message }, parents: commit.parents.map((parent) => ({ sha: parent })) } };
  }

  async branchExists(name) {
    return this.branches.has(name);
  }

  async getGitCommit(sha) {
    const commit = this.commits.get(sha);
    this.gitCommitReads = (this.gitCommitReads ?? 0) + 1;
    return {
      sha,
      tree: { sha: commit.tree },
      message: commit.message,
      parents: commit.parents.map((parent) => ({ sha: parent })),
      author: { name: commit.author },
    };
  }

  async createGitCommit({ message, parents }) {
    const files = new Map(this.commits.get(parents[0]).files);
    return { sha: this.commit({ message, parents, files, author: 'github-actions[bot]' }) };
  }

  async createGitRef(branch, sha) {
    this.branches.set(branch, sha);
  }

  async compareCommits(base, head) {
    const before = this.commits.get(base).files;
    const after = this.commits.get(head).files;
    const files = new Set([...before.keys(), ...after.keys()]);
    return { files: [...files].filter((path) => before.get(path) !== after.get(path)).map((filename) => ({ filename })) };
  }

  async getOptionalTextContent(path, ref) {
    return this.commits.get(this.resolve(ref))?.files.get(path) ?? null;
  }

  async getTextContent(path, ref) {
    const text = await this.getOptionalTextContent(path, ref);
    if (text === null) throw new Error(`missing ${path}@${ref}`);
    return text;
  }

  async listDirectory(path, ref) {
    const files = this.commits.get(this.resolve(ref)).files;
    const entries = new Map();
    for (const file of files.keys()) {
      if (!file.startsWith(`${path}/`)) continue;
      const [name, ...rest] = file.slice(path.length + 1).split('/');
      entries.set(name, { name, path: `${path}/${name}`, type: rest.length > 0 ? 'dir' : 'file' });
    }
    return [...entries.values()];
  }

  // --- pull requests
  async createPullRequest({ title, body, head, base, draft }) {
    const number = this.id();
    const pr = {
      number,
      title,
      body,
      draft,
      state: 'open',
      merged_at: null,
      labels: [],
      html_url: `https://github.com/${REPOSITORY}/pull/${number}`,
      user: { login: 'github-actions[bot]' },
      head: { ref: head, sha: this.branches.get(head), repo: { full_name: REPOSITORY } },
      base: { ref: base, sha: this.branches.get(base) },
    };
    this.prs.set(number, pr);
    return pr;
  }

  async getPullRequest(number) {
    return this.prs.get(number);
  }

  async updatePullRequest(number, patch) {
    Object.assign(this.prs.get(number), patch);
  }

  async listOpenPullRequests() {
    return [...this.prs.values()].filter((pr) => pr.state === 'open');
  }

  async listRecentlyClosedPullRequests() {
    return [...this.prs.values()].filter((pr) => pr.state === 'closed');
  }

  async listOpenPullRequestsForHead(branch) {
    return [...this.prs.values()].filter((pr) => pr.state === 'open' && pr.head.ref === branch);
  }

  // --- checks
  async listCheckRuns(sha, name) {
    return this.checkRuns.filter((run) => run.head_sha === sha && run.name === name).map((run) => structuredClone(run));
  }

  async createCheckRun(body) {
    const run = { id: this.id(), app: { slug: 'github-actions' }, ...structuredClone(body) };
    this.checkRuns.push(run);
    return structuredClone(run);
  }

  async updateCheckRun(id, body) {
    const run = this.checkRuns.find((entry) => entry.id === id);
    Object.assign(run, structuredClone(body));
    return structuredClone(run);
  }

  // --- agent tasks
  async startAgentTask(request) {
    const task = {
      id: `task-${this.id()}`,
      state: 'queued',
      html_url: `https://github.com/${REPOSITORY}/copilot/tasks/x`,
      created_at: new Date().toISOString(),
      artifacts: [{ type: 'branch', data: { head_ref: request.headRef, base_ref: request.baseRef } }],
    };
    this.agentTasks.push(task);
    this.started.push({ ...request, id: task.id });
    return { ...task };
  }

  async getAgentTask(id) {
    return { ...this.agentTasks.find((task) => task.id === id) };
  }

  async listAgentTasks() {
    return this.agentTasks.map((task) => ({ ...task }));
  }

  async dispatchWorkflow(workflow, ref, inputs) {
    const run = {
      id: this.id(),
      workflow,
      ref,
      inputs,
      status: 'queued',
      conclusion: null,
      display_title: `OpenSpec agent · #${inputs.pr} · ${inputs.dispatch_id}`,
      created_at: new Date().toISOString(),
      html_url: `https://github.com/${REPOSITORY}/actions/runs/${this.counter}`,
    };
    this.workflowRuns.push(run);
    return this.returnRunDetails ? { workflow_run_id: run.id, run_url: 'x', html_url: run.html_url } : null;
  }

  async getWorkflowRun(id) {
    return { ...this.workflowRuns.find((run) => String(run.id) === String(id)) };
  }

  async listWorkflowRuns() {
    return this.workflowRuns.map((run) => ({ ...run }));
  }
}

function setup({ validator = async () => {}, runtime = 'copilot' } = {}) {
  const github = new FakeGitHub();
  github.addTwin(4, { labels: ['openspec:change', 'openspec:enqueued'] });
  github.events.set(4, [{ event: 'labeled', label: { name: 'openspec:enqueued' }, actor: { login: 'alice', type: 'User' } }]);
  let clock = Date.parse('2026-10-02T10:00:00Z');
  const ctx = createContext({
    env: { GITHUB_REPOSITORY: REPOSITORY, GITHUB_TOKEN: 'x', OPENSPEC_AGENT_RUNTIME: runtime },
    client: github,
    now: () => new Date(clock),
    validator,
  });
  ctx.log = () => {};
  const tick = (minutes = 1) => {
    clock += minutes * 60_000;
  };
  return { github, ctx, tick };
}

function lifecycleState(github, pr) {
  const runs = github.checkRuns.filter((run) => run.name === LIFECYCLE_CHECK_NAME && run.head_sha === pr.head.sha);
  return parseRunStateText(runs.sort((left, right) => right.id - left.id)[0].output.text);
}

function logTitles(github, pr) {
  return (github.comments.get(pr.number) ?? [])
    .map((comment) => comment.body.match(/^### (.+)$/m)?.[1])
    .filter(Boolean);
}

function trailer(fields) {
  return renderCheckpointTrailer({ change: CHANGE, summary: 'Did the work.', validation: 'tests passed', ...fields });
}

async function admitted() {
  const env = setup();
  const buckets = await plan(env.ctx);
  assert.deepEqual(buckets.admit, [CHANGE]);
  await admitChange(env.ctx, CHANGE);
  await publish(env.ctx, [CHANGE]);
  const pr = [...env.github.prs.values()][0];
  return { ...env, pr };
}

test('builds an agent prompt with the binding skill, rules, and a valid trailer example', () => {
  const prompt = buildAgentPrompt({
    $schema: '.github/scripts/schemas/change-dispatch-v2.schema.json',
    change: CHANGE,
    operation: 'verify',
    issue: 4,
    pr: 21,
    branch: `openspec/${CHANGE}`,
    baseRef: 'main',
    expectedHeadSha: 'a'.repeat(40),
    attempt: 1,
    answers: [{ question: 'Deny?', text: 'Yes.', by: 'alice' }],
  });
  assert.match(prompt, /^Execute the OpenSpec verify step described below on this pull request branch now\./);
  assert.match(prompt, /not a review comment, and it requires you to act/);
  assert.match(prompt, /openspec-verify-change\/SKILL\.md/);
  assert.match(prompt, /Do not change any files/);
  assert.match(prompt, /A \(alice\): Yes\./);
  const example = prompt.split('\n').find((line) => line.trim().startsWith(CHECKPOINT_TRAILER)).trim();
  assert.equal(parseCheckpointTrailer(example).operation, 'verify');
});

test('admission swaps the queue label for the processing label before publish runs', async () => {
  const env = setup();
  await plan(env.ctx);
  await admitChange(env.ctx, CHANGE);
  assert.deepEqual(env.github.issues.get(4).labels.map((label) => label.name).sort(), ['openspec:change', 'openspec:processing']);
});

test('admits an enqueued change: branch, draft PR, state check run, log, labels, and first session', async () => {
  const { github, pr } = await admitted();
  assert.equal(pr.draft, true);
  assert.equal(pr.head.ref, `openspec/${CHANGE}`);
  assert.match(pr.body, /Refs #4/);
  const state = lifecycleState(github, pr);
  assert.equal(state.status, 'running');
  assert.equal(state.requestedBy, 'alice');
  assert.equal(state.current.task.id, '1.1');
  assert.equal(github.started.length, 1);
  assert.equal(github.started[0].headRef, `openspec/${CHANGE}`);
  assert.equal(github.started[0].customAgent, 'openspec');
  assert.match(github.started[0].prompt, /Execute only task 1\.1/);
  const issue = github.issues.get(4);
  assert.deepEqual(issue.labels.map((label) => label.name).sort(), ['openspec:change', 'openspec:processing']);
  assert.match(github.comments.get(4)[0].body, /Processing started in #/);
  const titles = logTitles(github, pr);
  assert.match(github.comments.get(pr.number)[0].body, /^## 🧭 OpenSpec · `add-club`/);
  assert.ok(titles.some((title) => title.startsWith('📥 #1 · Started')));
  assert.ok(titles.some((title) => title.startsWith('▶️ #2 · Apply 1.1')));
  assert.deepEqual(pr.labels.map((label) => label.name), ['openspec:stage:apply']);
});

test('credits a pushed checkpoint, then plans the next task', async () => {
  const { github, ctx, pr, tick } = await admitted();
  tick(5);
  github.push(pr.head.ref, {
    message: `Build the first thing\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'complete' })}`,
    files: { [`openspec/changes/${CHANGE}/tasks.md`]: TASKS.replace('- [ ] 1.1', '- [x] 1.1'), 'src/a.cs': 'x' },
  });
  const observed = await observe(ctx);
  assert.deepEqual(observed.credit, [CHANGE]);
  await creditChange(ctx, CHANGE);
  const state = lifecycleState(github, pr);
  assert.equal(state.status, 'ready');
  assert.deepEqual(state.credited.map((entry) => entry.task), ['1.1']);
  assert.ok(logTitles(github, pr).some((title) => title.startsWith('✅ #2 · Apply 1.1')));
  assert.ok(github.checkRuns.some((run) => run.name === 'OpenSpec apply 1.1' && run.conclusion === 'success'));
  const buckets = await plan(ctx);
  assert.deepEqual(buckets.apply, [CHANGE]);
  await dispatchChange(ctx, CHANGE, 'apply');
  assert.match(github.started.at(-1).prompt, /Execute only task 1\.2/);
});

test('rejects a checkbox-only task that changed other files and retries it', async () => {
  const { github, ctx, pr, tick } = await admitted();
  github.push(pr.head.ref, {
    message: `x\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'complete' })}`,
    files: { [`openspec/changes/${CHANGE}/tasks.md`]: TASKS.replace('- [ ] 1.1', '- [x] 1.1') },
  });
  await creditChange(ctx, CHANGE);
  await dispatchChange(ctx, CHANGE, 'apply');
  tick(2);
  github.push(pr.head.ref, {
    message: `y\n\n${trailer({ operation: 'apply', task: '1.2', verdict: 'complete' })}`,
    files: {
      [`openspec/changes/${CHANGE}/tasks.md`]: TASKS.replace('- [ ] 1.1', '- [x] 1.1').replace('- [ ] 1.2', '- [x] 1.2'),
      'src/sneaky.cs': 'x',
    },
  });
  await creditChange(ctx, CHANGE);
  const state = lifecycleState(github, pr);
  assert.equal(state.current.attempt, 2);
  assert.ok(logTitles(github, pr).some((title) => /Apply 1\.2 .*retrying/.test(title)));
  assert.ok(github.checkRuns.some((run) => run.name === 'OpenSpec apply 1.2' && run.conclusion === 'failure'
    && /may only check its box/.test(run.output.summary)));
});

test('opens a decision gate, announces it once, and resumes after an authorized answer', async () => {
  const { github, ctx, pr } = await admitted();
  github.push(pr.head.ref, {
    message: `stop\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'needs_decision', question: 'Deny or resolve?' })}`,
  });
  await creditChange(ctx, CHANGE);
  assert.deepEqual((await plan(ctx)).gate, [CHANGE]);
  await notifyGate(ctx, CHANGE);
  await publish(ctx, [CHANGE]);
  assert.deepEqual((await plan(ctx)).gate, []);
  const gateComment = github.comments.get(pr.number).find((comment) => /decision needed · @alice/.test(comment.body));
  assert.ok(gateComment);
  assert.match(github.comments.get(4).at(-1).body, /waiting for input/);
  assert.ok(github.issues.get(4).labels.some((label) => label.name === 'openspec:needs-attention'));
  let state = lifecycleState(github, pr);
  assert.equal(state.gate.log, gateComment.id);

  github.humanComment(pr.number, 'mallory', '/openspec answer Resolve it.');
  github.humanComment(pr.number, 'alice', '/openspec answer Deny it.', { edited: true });
  const answer = github.humanComment(pr.number, 'alice', '/openspec answer Deny ambiguous Seasons.');
  await observe(ctx);
  state = lifecycleState(github, pr);
  assert.equal(state.status, 'ready');
  assert.deepEqual(state.answers.map((entry) => entry.text), ['Deny ambiguous Seasons.']);
  assert.equal(state.commandCursor, answer.id);
  assert.deepEqual(github.reactions.map((reaction) => reaction.content), ['confused', 'confused', '+1']);
  const titles = logTitles(github, pr);
  assert.equal(titles.filter((title) => title.includes('Command not accepted')).length, 2);
  assert.ok(titles.some((title) => title.includes('Decision recorded')));

  await dispatchChange(ctx, CHANGE, 'apply');
  assert.match(github.started.at(-1).prompt, /A \(alice\): Deny ambiguous Seasons\./);
  await observe(ctx);
  assert.equal(lifecycleState(github, pr).commandCursor, answer.id);
});

test('state survives human pushes and lives only in the workflow check run', async () => {
  const { github, ctx, pr } = await admitted();
  github.push(pr.head.ref, {
    message: `x\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'partial' })}`,
  });
  await creditChange(ctx, CHANGE);
  const before = lifecycleState(github, pr);
  const humanSha = github.push(pr.head.ref, { message: 'human fix', author: 'alice' });
  await observe(ctx);
  const after = lifecycleState(github, pr);
  assert.equal(after.headSha, humanSha);
  assert.equal(after.revision, before.revision + 1);
  assert.ok(logTitles(github, pr).some((title) => title.includes('New commits on the branch')));
  const superseded = github.checkRuns.filter((run) => run.name === LIFECYCLE_CHECK_NAME && run.output.title.startsWith('Superseded'));
  assert.ok(superseded.length > 0);

  const overview = github.comments.get(pr.number)[0];
  overview.body = 'vandalized';
  assert.equal((await readRunState(ctx, pr)).state.revision, after.revision);
  await publish(ctx);
  assert.ok(github.comments.get(pr.number).some((comment) => /openspec:overview change=add-club/.test(comment.body)));

  github.checkRuns.push({
    id: 999999, app: { slug: 'someone-else' }, name: LIFECYCLE_CHECK_NAME, head_sha: humanSha, external_id: CHANGE,
    output: { text: '```json\n{}\n```' },
  });
  assert.equal((await readRunState(ctx, pr)).state.revision, after.revision);
});

test('finds checkpoints only after the start commit and flags mismatches', () => {
  const state = {
    change: CHANGE,
    current: { operation: 'apply', task: { id: '1.1' }, startSha: 'b' },
  };
  const commit = (sha, message) => ({ sha, commit: { message } });
  assert.deepEqual(findCheckpoint(state, [commit('a', 'x')]), { historyChanged: true });
  assert.equal(findCheckpoint(state, [commit('b', 'x'), commit('c', 'no trailer')]).newCommits, 1);
  assert.match(
    findCheckpoint(state, [commit('b', 'x'), commit('c', trailer({ operation: 'apply', task: '1.2', verdict: 'complete' }))]).checkpointError,
    /expected add-club apply 1\.1/,
  );
  assert.equal(
    findCheckpoint(state, [commit('b', 'x'), commit('c', trailer({ operation: 'apply', task: '1.1', verdict: 'complete' }))]).checkpointSha,
    'c',
  );
});

test('holds blocked or capped changes back and refuses unauthorized enqueues', async () => {
  const { github, ctx } = setup();
  github.addTwin(6, { ref: 'add-base', state: 'open' });
  github.blockedBy.set(4, [6]);
  assert.deepEqual((await plan(ctx)).admit, []);
  github.addTwin(6, { ref: 'add-base', state: 'closed', lifecycle: 'archived' });
  assert.deepEqual((await plan(ctx)).admit, [CHANGE]);
  ctx.maxActive = 0;
  assert.deepEqual((await plan(ctx)).admit, []);
  ctx.maxActive = Infinity;

  github.events.set(4, [{ event: 'labeled', label: { name: 'openspec:enqueued' }, actor: { login: 'mallory', type: 'User' } }]);
  await admitChange(ctx, CHANGE);
  assert.equal(github.prs.size, 0);
  assert.ok(!github.issues.get(4).labels.some((label) => label.name === 'openspec:enqueued'));
  assert.match(github.comments.get(4)[0].body, /Only users with write access/);
});

test('runs verify with a review gate, then sync, archive, merge gate, and merge finalize', async () => {
  const { github, ctx, pr } = await admitted();
  const done = TASKS.replace('- [ ] 1.1', '- [x] 1.1').replace('- [ ] 1.2', '- [x] 1.2');
  github.push(pr.head.ref, {
    message: `x\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'complete' })}`,
    files: { [`openspec/changes/${CHANGE}/tasks.md`]: TASKS.replace('- [ ] 1.1', '- [x] 1.1') },
  });
  await creditChange(ctx, CHANGE);
  await dispatchChange(ctx, CHANGE, 'apply');
  github.push(pr.head.ref, {
    message: `y\n\n${trailer({ operation: 'apply', task: '1.2', verdict: 'complete' })}`,
    files: { [`openspec/changes/${CHANGE}/tasks.md`]: done },
  });
  await creditChange(ctx, CHANGE);
  assert.deepEqual((await plan(ctx)).verify, [CHANGE]);
  await dispatchChange(ctx, CHANGE, 'verify');
  github.push(pr.head.ref, {
    message: `v\n\n${trailer({
      operation: 'verify',
      verdict: 'complete',
      findings: { critical: 0, warning: 0, suggestion: 1, items: [{ severity: 'suggestion', text: 'Rename x.' }] },
    })}`,
  });
  await creditChange(ctx, CHANGE);
  assert.equal(lifecycleState(github, pr).gate.kind, 'review');
  await notifyGate(ctx, CHANGE);
  github.humanComment(pr.number, 'alice', '/openspec approve');
  await observe(ctx);
  assert.deepEqual((await plan(ctx)).sync, [CHANGE]);
  await dispatchChange(ctx, CHANGE, 'sync');
  github.push(pr.head.ref, { message: `s\n\n${trailer({ operation: 'sync', verdict: 'complete' })}`, files: { 'openspec/specs/x/spec.md': 'x' } });
  await creditChange(ctx, CHANGE);
  await dispatchChange(ctx, CHANGE, 'archive');
  const archive = `openspec/changes/archive/2026-10-02-${CHANGE}`;
  github.push(pr.head.ref, {
    message: `a\n\n${trailer({ operation: 'archive', verdict: 'complete' })}`,
    files: { [`${archive}/tasks.md`]: done },
    remove: [`openspec/changes/${CHANGE}/tasks.md`, `openspec/changes/${CHANGE}/proposal.md`],
  });
  await creditChange(ctx, CHANGE);
  let state = lifecycleState(github, pr);
  assert.equal(state.gate.kind, 'merge');
  const lifecycle = github.checkRuns.filter((run) => run.name === LIFECYCLE_CHECK_NAME && run.head_sha === pr.head.sha).at(-1);
  assert.equal(lifecycle.conclusion, 'success');
  await notifyGate(ctx, CHANGE);
  await publish(ctx);
  assert.ok(github.issues.get(4).labels.some((label) => label.name === 'openspec:awaiting-review'));

  pr.state = 'closed';
  pr.merged_at = '2026-10-02T12:00:00Z';
  pr.closed_at = '2026-10-02T12:00:00Z';
  github.branches.set('main', pr.head.sha);
  assert.deepEqual((await plan(ctx)).finalize, [CHANGE]);
  await finalizeChange(ctx, CHANGE);
  state = lifecycleState(github, pr);
  assert.equal(state.outcome, 'merged');
  assert.equal(github.issues.get(4).state, 'closed');
  assert.equal(github.issues.get(4).state_reason, 'completed');
  assert.deepEqual(github.issues.get(4).labels.map((label) => label.name), ['openspec:change']);
  assert.ok(logTitles(github, pr).some((title) => title.includes('Merged')));
  assert.deepEqual((await plan(ctx)).finalize, []);
});

test('abort closes the pull request and returns the issue to idle', async () => {
  const { github, ctx, pr } = await admitted();
  github.humanComment(pr.number, 'alice', '/openspec abort');
  await observe(ctx);
  assert.deepEqual((await plan(ctx)).finalize, [CHANGE]);
  await finalizeChange(ctx, CHANGE);
  assert.equal(pr.state, 'closed');
  assert.equal(lifecycleState(github, pr).outcome, 'aborted');
  assert.deepEqual(github.issues.get(4).labels.map((label) => label.name), ['openspec:change']);
  assert.match(github.comments.get(4).at(-1).body, /was aborted/);
});

test('recovers an interrupted dispatch by adopting the started session', async () => {
  const { github, ctx, pr, tick } = await admitted();
  const run = github.checkRuns.filter((entry) => entry.name === LIFECYCLE_CHECK_NAME).at(-1);
  const state = parseRunStateText(run.output.text);
  const dispatching = { ...state, status: 'dispatching', current: { ...state.current, session: null } };
  run.output.text = run.output.text.replace(JSON.stringify(state, null, 2), JSON.stringify(dispatching, null, 2));
  tick(1);
  await observe(ctx);
  const recovered = lifecycleState(github, pr);
  assert.equal(recovered.status, 'running');
  assert.equal(recovered.current.session.id, github.agentTasks[0].id);
});

test('ignores irrelevant events without touching any run', async () => {
  const { ctx } = setup();
  assert.deepEqual(await observe(ctx, { eventName: 'issue_comment', payload: { issue: { pull_request: {} }, comment: { body: 'hi' } } }), {
    relevant: false,
    credit: [],
    touched: [],
    tests: [],
  });
});

test('judges evidence against the first attempt baseline so retries cannot hide changes', async () => {
  const { github, ctx, pr } = await admitted();
  github.push(pr.head.ref, {
    message: `x\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'partial' })}`,
    files: { [`openspec/changes/${CHANGE}/tasks.md`]: TASKS.replace('- [ ] 1.1', '- [x] 1.1').replace('- [ ] 1.2', '- [x] 1.2') },
  });
  await creditChange(ctx, CHANGE);
  assert.equal(lifecycleState(github, pr).current.attempt, 2);
  await dispatchChange(ctx, CHANGE, 'apply');
  github.push(pr.head.ref, { message: `y\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'complete' })}` });
  await creditChange(ctx, CHANGE);
  const state = lifecycleState(github, pr);
  assert.equal(state.status, 'gated');
  assert.match(state.gate.reason, /task 1\.2 changed its checkbox/);
});

test('never resurrects stale state after the branch is reset to an older commit', async () => {
  const { github, ctx, pr } = await admitted();
  const partial = github.push(pr.head.ref, { message: `x\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'partial' })}` });
  await creditChange(ctx, CHANGE);
  await dispatchChange(ctx, CHANGE, 'apply');
  github.push(pr.head.ref, { message: `y\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'partial' })}` });
  await creditChange(ctx, CHANGE);
  assert.equal(lifecycleState(github, pr).status, 'gated');
  github.branches.set(pr.head.ref, partial);
  pr.head.sha = partial;
  assert.equal(await readRunState(ctx, pr), null);
  const started = github.started.length;
  await observe(ctx);
  assert.ok(github.comments.get(pr.number).some((comment) => comment.body.includes('OpenSpec state is missing')));
  assert.equal(github.started.length, started);
});

test('restores the initial state after a reset to the start commit without replaying commands', async () => {
  const { github, ctx, pr } = await admitted();
  const start = pr.head.sha;
  github.humanComment(pr.number, 'alice', '/openspec abort');
  github.push(pr.head.ref, { message: `x\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'partial' })}` });
  await creditChange(ctx, CHANGE);
  github.branches.set(pr.head.ref, start);
  pr.head.sha = start;
  for (const run of github.checkRuns) {
    if (run.head_sha === start) run.output = { title: 'Superseded', summary: 's', text: 'Superseded.' };
  }
  await observe(ctx);
  const restored = lifecycleState(github, pr);
  assert.equal(restored.status, 'ready');
  assert.equal(restored.outcome, null);
  assert.ok(restored.commandCursor > 0);
});

test('reads state from the head without listing every pull request commit', async () => {
  const { github, ctx, pr } = await admitted();
  for (let index = 0; index < 5; index += 1) github.push(pr.head.ref, { message: `wip ${index}` });
  github.gitCommitReads = 0;
  const { state } = await readRunState(ctx, pr);
  assert.equal(state.change, CHANGE);
  assert.ok(github.gitCommitReads <= 6);
});

test('skips comment scans for push wake-ups unless a gate is waiting', async () => {
  const { github, ctx, pr } = await admitted();
  github.humanComment(pr.number, 'alice', '/openspec abort');
  await observe(ctx, { eventName: 'push', payload: {} });
  assert.equal(lifecycleState(github, pr).status, 'running');
  await observe(ctx, { eventName: 'issue_comment', payload: { issue: { number: pr.number, pull_request: {} }, comment: { body: '/openspec abort' } } });
  assert.equal(lifecycleState(github, pr).outcome, 'aborted');
});

test('runs sessions as agentic workflow runs and builds their prompt from the state', async () => {
  const env = setup({ runtime: 'actions' });
  const { github, ctx } = env;
  ctx.newDispatchId = () => 'd1';
  await plan(ctx);
  await admitChange(ctx, CHANGE);
  const pr = [...github.prs.values()][0];
  assert.equal(github.started.length, 0);
  assert.equal(github.workflowRuns.length, 1);
  const [run] = github.workflowRuns;
  assert.equal(run.workflow, 'openspec-agent.lock.yml');
  assert.deepEqual(run.inputs, { pr: String(pr.number), dispatch_id: 'd1', branch: `openspec/${CHANGE}`, step: 'apply 1.1' });
  let state = lifecycleState(github, pr);
  assert.deepEqual(state.current.session, { runtime: 'actions', id: String(run.id), state: 'queued', url: run.html_url });
  assert.ok(logTitles(github, pr).some((title) => title.startsWith('▶️ #2 · Apply 1.1')));
  assert.match(github.comments.get(pr.number).find((comment) => comment.body.includes('▶️')).body, /\[Agent run\]\(.*actions\/runs/);

  const prepared = await prepareAgentPrompt(ctx, { pr: pr.number, dispatchId: 'd1' });
  assert.equal(prepared.headSha, pr.head.sha);
  assert.match(prepared.prompt, /Execute only task 1\.1/);
  assert.match(prepared.prompt, /do not run git push; you have no push access/);
  assert.match(prepared.prompt, /call the `wake_controller` tool exactly once/);
  assert.match(prepared.prompt, /call the `run_platform_tests` tool/);
  await assert.rejects(prepareAgentPrompt(ctx, { pr: pr.number, dispatchId: 'other' }), /no dispatched session other/);

  run.status = 'completed';
  run.conclusion = 'failure';
  assert.deepEqual((await observe(ctx)).credit, [CHANGE]);
  await creditChange(ctx, CHANGE);
  state = lifecycleState(github, pr);
  assert.equal(state.current.attempt, 2);
  assert.match(state.current.feedback, /ended \(failed\) without a checkpoint/);
  ctx.newDispatchId = () => 'd2';
  await dispatchChange(ctx, CHANGE, 'apply');
  const retry = await prepareAgentPrompt(ctx, { pr: pr.number, dispatchId: 'd2' });
  assert.match(retry.prompt, /The previous attempt \(1\) did not finish/);
  assert.match(retry.prompt, /> The agent session ended \(failed\) without a checkpoint commit\./);
  github.humanComment(pr.number, 'alice', 'push');
  github.push(pr.head.ref, { message: 'moved' });
  await assert.rejects(prepareAgentPrompt(ctx, { pr: pr.number, dispatchId: 'd2' }), /moved to/);
});

test('recovers an agentic workflow run when the dispatch API returns no run id', async () => {
  const env = setup({ runtime: 'actions' });
  const { github, ctx, tick } = env;
  github.returnRunDetails = false;
  ctx.newDispatchId = () => 'd9';
  await plan(ctx);
  await admitChange(ctx, CHANGE);
  const pr = [...github.prs.values()][0];
  assert.equal(lifecycleState(github, pr).status, 'dispatching');
  tick(1);
  await observe(ctx);
  const state = lifecycleState(github, pr);
  assert.equal(state.status, 'running');
  assert.equal(state.current.session.id, String(github.workflowRuns[0].id));
});

test('fails a checkpoint whose isolated platform test job failed and passes its log to the next attempt', async () => {
  const { github, ctx, pr } = await admitted();
  const requests = [];
  ctx.platformTestResult = async (_, request) => {
    requests.push(request);
    return { ok: false, reason: 'the platform tests failure at abc', feedback: 'Failed MigrationTests.Applies [2 s]\nExpected 3 but was 2' };
  };
  const checkpointSha = github.push(pr.head.ref, {
    message: `x\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'complete' })}`,
    files: { [`openspec/changes/${CHANGE}/tasks.md`]: TASKS.replace('- [ ] 1.1', '- [x] 1.1'), 'src/platform/A.cs': 'x' },
  });
  assert.deepEqual((await observe(ctx)).tests, [{ change: CHANGE, sha: checkpointSha }]);
  await creditChange(ctx, CHANGE);
  assert.deepEqual(requests, [{ change: CHANGE, sha: checkpointSha }]);
  const state = lifecycleState(github, pr);
  assert.equal(state.current.attempt, 2);
  assert.match(state.current.feedback, /Expected 3 but was 2/);
  const entry = github.comments.get(pr.number).find((comment) => comment.body.includes('retrying')).body;
  assert.match(entry, /Details passed to the next attempt[\s\S]*Expected 3 but was 2/);
});

test('shows platform test failures on the pull request checks and in the stopped entry', async () => {
  const { github, ctx, pr } = await admitted();
  ctx.platformTestResult = async () => ({
    ok: false,
    reason: 'the platform tests failed at abc',
    feedback: 'Failed test: MigrationTests.Applies\nError message:\n  Expected 3 but was 2',
    job: { conclusion: 'failure', url: 'https://github.com/x/actions/runs/1/job/2' },
  });
  for (const attempt of [1, 2]) {
    const checkpointSha = github.push(pr.head.ref, {
      message: `x${attempt}\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'complete' })}`,
      files: { [`openspec/changes/${CHANGE}/tasks.md`]: TASKS.replace('- [ ] 1.1', '- [x] 1.1'), 'src/platform/A.cs': `x${attempt}` },
    });
    await observe(ctx);
    await creditChange(ctx, CHANGE);
    const testCheck = github.checkRuns.find((run) => run.name === 'OpenSpec platform tests' && run.head_sha === checkpointSha);
    assert.equal(testCheck.conclusion, 'failure');
    assert.equal(testCheck.details_url, 'https://github.com/x/actions/runs/1/job/2');
    assert.match(testCheck.output.summary, /\[Test job log\]\(https:\/\/github\.com\/x\/actions\/runs\/1\/job\/2\)[\s\S]*Expected 3 but was 2/);
    const operationCheck = github.checkRuns.filter((run) => run.name === 'OpenSpec apply 1.1' && run.head_sha === checkpointSha).at(-1);
    assert.match(operationCheck.output.summary, /\*\*Details:\*\*[\s\S]*Expected 3 but was 2/);
    if (attempt === 1) await dispatchChange(ctx, CHANGE);
  }
  assert.equal(lifecycleState(github, pr).gate.kind, 'failure');
  const stopped = github.comments.get(pr.number).find((comment) => comment.body.includes(': stopped')).body;
  assert.match(stopped, /the platform tests failed at abc[\s\S]*Failure details[\s\S]*Expected 3 but was 2/);
});

test('rejects unknown agent runtimes', () => {
  assert.throws(() => createContext({ env: { GITHUB_REPOSITORY: REPOSITORY, GITHUB_TOKEN: 'x', OPENSPEC_AGENT_RUNTIME: 'lambda' } }), /must be one of actions, copilot/);
  assert.equal(createContext({ env: { GITHUB_REPOSITORY: REPOSITORY, GITHUB_TOKEN: 'x' } }).runtime, 'actions');
});

test('skips platform tests for checkpoints that do not touch the platform', async () => {
  const { github, ctx, pr } = await admitted();
  ctx.platformTestResult = async () => { throw new Error('must not run'); };
  github.push(pr.head.ref, {
    message: `x\n\n${trailer({ operation: 'apply', task: '1.1', verdict: 'complete' })}`,
    files: { [`openspec/changes/${CHANGE}/tasks.md`]: TASKS.replace('- [ ] 1.1', '- [x] 1.1'), 'docs/a.md': 'x' },
  });
  assert.deepEqual((await observe(ctx)).tests, []);
  await creditChange(ctx, CHANGE);
  const state = lifecycleState(github, pr);
  assert.equal(state.status, 'ready');
  assert.deepEqual(state.credited.map((entry) => entry.task), ['1.1']);
});

test('reads the isolated test job conclusion, not anything the tests wrote', async () => {
  const { ctx } = setup();
  const previous = process.env.GITHUB_RUN_ID;
  process.env.GITHUB_RUN_ID = '42';
  try {
    const sha = 'f'.repeat(40);
    ctx.client.listRunJobs = async () => [{ name: `Run platform tests (${CHANGE}, ${sha})`, conclusion: 'failure', html_url: 'https://jobs/1' }];
    const failed = await defaultPlatformTestResult(ctx, { change: CHANGE, sha });
    assert.equal(failed.ok, false);
    assert.equal(failed.reason, 'the platform tests failed at fffffff');
    assert.deepEqual(failed.job, { conclusion: 'failure', url: 'https://jobs/1' });
    ctx.client.listRunJobs = async () => [{ name: `Run platform tests (${CHANGE}, ${sha})`, conclusion: 'cancelled' }];
    assert.equal((await defaultPlatformTestResult(ctx, { change: CHANGE, sha })).reason, 'the platform tests ended as cancelled at fffffff');
    ctx.client.listRunJobs = async () => [{ name: `Run platform tests (${CHANGE}, ${sha})`, conclusion: 'success', html_url: 'https://jobs/2' }];
    assert.deepEqual(await defaultPlatformTestResult(ctx, { change: CHANGE, sha }), { ok: true, job: { conclusion: 'success', url: 'https://jobs/2' } });
    ctx.client.listRunJobs = async () => [];
    assert.match((await defaultPlatformTestResult(ctx, { change: CHANGE, sha })).reason, /did not run for fffffff/);
  } finally {
    if (previous === undefined) delete process.env.GITHUB_RUN_ID; else process.env.GITHUB_RUN_ID = previous;
  }
});

test('rejects sync and archive checkpoints that change code', async () => {
  const { github, ctx } = setup();
  const base = github.branches.get('main');
  const after = github.commit({ message: 'x', parents: [base], files: new Map([...github.commits.get(base).files, ['src/platform/B.cs', 'x']]) });
  for (const operation of ['sync', 'archive']) {
    const state = { change: CHANGE, branch: 'openspec/x', current: { operation, task: null, startSha: base, baselineSha: base } };
    const result = await validateEvidence(ctx, state, after);
    assert.match(result.reason, new RegExp(`${operation} may only change openspec/ and docs/, but changed src/platform/B\\.cs`));
  }
});

const DOTNET_TEST_FAILURE_LOG = [
  'Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 36 s - SocAlytics.Platform.Persistence.Tests.dll (net10.0)',
  '[xUnit.net 00:02:30.45]     SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts [FAIL]',
  '  Failed SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts [2 m 29 s]',
  '  Error Message:',
  '   System.Threading.Tasks.TaskCanceledException : The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.',
  '---- System.TimeoutException : The operation was canceled.',
  '  Stack Trace:',
  '     at System.Net.Http.HttpClient.HandleFailure(Exception e, Boolean telemetryStarted)',
  '   at Aspire.Hosting.ApplicationModel.ResourceNotificationService.WatchAsync(CancellationToken cancellationToken) in /_/src/Aspire.Hosting/ResourceNotificationService.cs:line 771',
  '   at SocAlytics.Platform.Host.Tests.PlatformHostTests.WaitForHealthyEndpointAsync(HttpClient client) in /home/runner/work/socalytics/socalytics/src/platform/Tests/PlatformHostTests.cs:line 82',
  '   at SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts() in /home/runner/work/socalytics/socalytics/src/platform/Tests/PlatformHostTests.cs:line 58',
  '   at SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts() in /home/runner/work/socalytics/socalytics/src/platform/Tests/PlatformHostTests.cs:line 58',
  '--- End of stack trace from previous location ---',
  '----- Inner Stack Trace -----',
  '   at System.Net.Http.HttpConnection.SendAsync(HttpRequestMessage request) in /home/runner/work/socalytics/socalytics/src/x.cs:line 1',
  '  Standard Output Messages:',
  '   [api] Application is shutting down...',
  '  Passed SocAlytics.Platform.Host.Tests.PlatformHostTests.ServesOpenApi [1 s]',
  'Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6, Duration: 2 m 29 s - SocAlytics.Platform.Host.Tests.dll (net10.0)',
].join('\n');

test('summarizes failed platform tests with their messages, own frames, and output', () => {
  const summary = summarizeTestLog(DOTNET_TEST_FAILURE_LOG);
  assert.equal(summary, [
    'Failed test: SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts',
    'Error message:',
    '  System.Threading.Tasks.TaskCanceledException : The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.',
    '  ---- System.TimeoutException : The operation was canceled.',
    'Stack (repository frames):',
    '  at SocAlytics.Platform.Host.Tests.PlatformHostTests.WaitForHealthyEndpointAsync(HttpClient client) in src/platform/Tests/PlatformHostTests.cs:line 82',
    '  at SocAlytics.Platform.Host.Tests.PlatformHostTests.AppHostRestarts() in src/platform/Tests/PlatformHostTests.cs:line 58',
    'Test output:',
    '  [api] Application is shutting down...',
    '',
    'Test assemblies:',
    'Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 36 s - SocAlytics.Platform.Persistence.Tests.dll (net10.0)',
    'Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6, Duration: 2 m 29 s - SocAlytics.Platform.Host.Tests.dll (net10.0)',
  ].join('\n'));
});

test('summarizes build errors first, bounds the summary, and falls back to the log tail', () => {
  const build = summarizeTestLog('Restore complete\n/src/Api/Program.cs(3,1): error CS0103: The name x does not exist [/src/Api/Api.csproj]\nBuild FAILED.');
  assert.equal(build, 'Build errors:\n/src/Api/Program.cs(3,1): error CS0103: The name x does not exist [/src/Api/Api.csproj]');
  const bounded = summarizeTestLog(DOTNET_TEST_FAILURE_LOG, 120);
  assert.equal(bounded.length, 120);
  assert.match(bounded, /^Failed test: .*…$/s);
  assert.equal(summarizeTestLog('line 1\nline 2\nThe test host crashed'), 'line 1\nline 2\nThe test host crashed');
});
