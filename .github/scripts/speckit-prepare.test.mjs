import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { PENDING_LABEL, TWIN_LABEL, parseTwinFolder } from './speckit-prepare-core.mjs';
import { GitHubClient } from './speckit-prepare-github.mjs';
import { discoverSpecs, main, runApply, runSync } from './speckit-prepare.mjs';

class FakeGitHub {
  constructor(issues = []) {
    this.issues = issues;
    this.labels = new Set();
    this.comments = [];
    this.edges = [];
    this.nextNumber = 100;
  }

  async listTwinIssues(label) {
    return this.issues.filter((issue) => issue.labels.some((l) => l.name === label)).map((issue) => structuredClone(issue));
  }

  async ensureLabel({ name }) {
    this.labels.add(name);
  }

  async createIssue({ title, body, labels }) {
    const number = this.nextNumber++;
    const issue = { number, id: number * 1000, state: 'open', state_reason: null, title, body, labels: labels.map((name) => ({ name })) };
    this.issues.push(issue);
    return structuredClone(issue);
  }

  find(number) {
    return this.issues.find((issue) => issue.number === number);
  }

  async updateIssue(number, fields) {
    this.updates = [...(this.updates ?? []), { number, fields: structuredClone(fields) }];
    const { labels, ...rest } = fields;
    Object.assign(this.find(number), rest);
    if (labels) this.find(number).labels = labels.map((name) => ({ name }));
  }

  async removeLabel(number, name) {
    this.removals = [...(this.removals ?? []), { number, name }];
    const issue = this.find(number);
    issue.labels = issue.labels.filter((label) => label.name !== name);
  }

  async getIssueLabels(number) {
    return structuredClone(this.find(number).labels);
  }

  async createComment(number, body) {
    this.comments.push({ number, body });
  }

  async listBlockedBy(number) {
    return this.edges.filter(([blocked]) => blocked === number).map(([, blocker]) => structuredClone(this.find(blocker)));
  }

  async addBlockedBy(number, blockerId) {
    this.edges.push([number, this.issues.find((issue) => issue.id === blockerId).number]);
  }
}

function makeRepo(folders) {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-prepare-'));
  mkdirSync(path.join(root, 'specs'));
  writeFileSync(path.join(root, 'specs', 'README.md'), '# index\n');
  for (const folder of folders) {
    mkdirSync(path.join(root, 'specs', folder));
    writeFileSync(
      path.join(root, 'specs', folder, 'spec.md'),
      `# Feature Specification: ${folder} title\n\n**Input**: User description: "${folder} summary"\n\n## Assumptions\n\n- **Dependencies**: none.\n`,
    );
  }
  return root;
}

function envFor(root) {
  return { GITHUB_REPOSITORY: 'octo/repo', GITHUB_STEP_SUMMARY: path.join(root, 'summary.md'), GITHUB_OUTPUT: path.join(root, 'output.txt') };
}

const silent = () => {};

async function syncRepo(github, root, options = {}) {
  return runSync({ client: github, rootDir: root, env: envFor(root), dryRun: false, promptFile: path.join(root, 'prompt.txt'), log: silent, ...options });
}

test('discovers only spec folders that contain spec.md', () => {
  const root = makeRepo(['20261005-130700-a']);
  mkdirSync(path.join(root, 'specs', 'empty'));
  try {
    assert.deepEqual([...discoverSpecs(root).keys()], ['20261005-130700-a']);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('creates twins with labels and writes the prompt for pending twins', async () => {
  const root = makeRepo(['20261005-130700-a', '20261005-130701-b']);
  try {
    const github = new FakeGitHub();
    const result = await syncRepo(github, root);
    assert.equal(result.exitCode, 0);
    assert.deepEqual(github.issues.map((issue) => parseTwinFolder(issue.body)), ['20261005-130700-a', '20261005-130701-b']);
    assert.ok(github.issues.every((issue) => issue.labels.map((l) => l.name).join() === `${TWIN_LABEL},${PENDING_LABEL},speckit:stage:specified`));
    assert.ok(['speckit:stage:specified', 'speckit:stage:discarded', PENDING_LABEL, TWIN_LABEL].every((name) => github.labels.has(name)));
    assert.match(readFileSync(path.join(root, 'prompt.txt'), 'utf8'), /### 20261005-130700-a \(NEW\)/);
    assert.match(readFileSync(path.join(root, 'output.txt'), 'utf8'), /pending=true/);

    const second = await syncRepo(github, root);
    assert.equal(second.plan.create.length + second.plan.update.length, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('writes the prompt for twins created in this run even when listings lag', async () => {
  const root = makeRepo(['20261005-130700-a']);
  try {
    const github = new FakeGitHub();
    const stale = github.listTwinIssues.bind(github);
    let calls = 0;
    github.listTwinIssues = async (label) => (calls++ === 0 ? stale(label) : []);
    const result = await syncRepo(github, root);
    assert.deepEqual(result.pending, ['20261005-130700-a']);
    assert.match(readFileSync(path.join(root, 'prompt.txt'), 'utf8'), /20261005-130700-a \(NEW\)/);
    assert.equal(calls, 1);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('moves the stage label as plan.md and tasks.md appear and tasks get checked', async () => {
  const folder = '20261005-130700-a';
  const root = makeRepo([folder]);
  const stageOf = (github) => github.issues[0].labels.map((label) => label.name).filter((name) => name.startsWith('speckit:stage:'));
  try {
    const github = new FakeGitHub();
    await syncRepo(github, root);
    github.issues[0].labels.push({ name: 'human' });
    assert.deepEqual(stageOf(github), ['speckit:stage:specified']);

    writeFileSync(path.join(root, 'specs', folder, 'plan.md'), '# plan\n');
    await syncRepo(github, root);
    assert.deepEqual(stageOf(github), ['speckit:stage:planned']);

    const tasksPath = path.join(root, 'specs', folder, 'tasks.md');
    writeFileSync(tasksPath, '- [ ] T001 one\n- [ ] T002 two\n');
    await syncRepo(github, root);
    assert.deepEqual(stageOf(github), ['speckit:stage:tasked']);

    writeFileSync(tasksPath, '- [x] T001 one\n- [ ] T002 two\n');
    await syncRepo(github, root);
    assert.deepEqual(stageOf(github), ['speckit:stage:implementing']);

    writeFileSync(tasksPath, '- [x] T001 one\n- [X] T002 two\n');
    const result = await syncRepo(github, root);
    assert.deepEqual(result.plan.relabel.map((item) => item.stage), ['implemented']);
    assert.deepEqual(stageOf(github), ['speckit:stage:implemented']);
    assert.ok(github.issues[0].labels.some((label) => label.name === 'human'));

    const again = await syncRepo(github, root);
    assert.equal(again.plan.relabel.length + again.plan.update.length, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('changes stage labels in one request and keeps labels added concurrently', async () => {
  const folder = '20261005-130700-a';
  const root = makeRepo([folder]);
  try {
    const github = new FakeGitHub();
    await syncRepo(github, root);
    writeFileSync(path.join(root, 'specs', folder, 'plan.md'), '# plan\n');
    const listed = github.listTwinIssues.bind(github);
    github.listTwinIssues = async (label) => {
      const issues = await listed(label);
      github.issues[0].labels.push({ name: 'added-meanwhile' });
      return issues;
    };
    github.updates = [];
    github.removals = [];
    await syncRepo(github, root);
    assert.equal(github.updates.length, 1);
    assert.deepEqual(github.updates[0].fields.labels, [TWIN_LABEL, PENDING_LABEL, 'added-meanwhile', 'speckit:stage:planned']);
    assert.deepEqual(github.removals, []);

    rmSync(path.join(root, 'specs', folder), { recursive: true });
    github.listTwinIssues = listed;
    github.updates = [];
    await syncRepo(github, root);
    assert.equal(github.updates.length, 1);
    assert.deepEqual(github.updates[0].fields, {
      state: 'closed',
      state_reason: 'not_planned',
      labels: [TWIN_LABEL, 'added-meanwhile', 'speckit:stage:discarded'],
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('dry run reports planned work without mutating issues', async () => {
  const root = makeRepo(['20261005-130700-a']);
  try {
    const github = new FakeGitHub();
    const result = await runSync({ client: github, rootDir: root, env: envFor(root), dryRun: true, log: silent });
    assert.equal(github.issues.length, 0);
    assert.deepEqual(result.pending, ['20261005-130700-a']);
    assert.match(readFileSync(path.join(root, 'summary.md'), 'utf8'), /Would create twin/);
    assert.match(readFileSync(path.join(root, 'output.txt'), 'utf8'), /pending=false/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('closes twins of removed folders and reopens them when the folder returns', async () => {
  const root = makeRepo(['20261005-130700-a']);
  try {
    const github = new FakeGitHub();
    await syncRepo(github, root);
    rmSync(path.join(root, 'specs', '20261005-130700-a'), { recursive: true });
    await syncRepo(github, root);
    assert.equal(github.issues[0].state, 'closed');
    assert.equal(github.issues[0].state_reason, 'not_planned');
    assert.deepEqual(github.issues[0].labels.map((label) => label.name), [TWIN_LABEL, 'speckit:stage:discarded']);
    assert.match(github.comments.at(-1).body, /no longer exists[\s\S]*discarded/);

    rmSync(root, { recursive: true, force: true });
    const restored = makeRepo(['20261005-130700-a']);
    await syncRepo(github, restored);
    assert.equal(github.issues[0].state, 'open');
    assert.equal(github.issues.length, 1);
    assert.deepEqual(github.issues[0].labels.map((label) => label.name), [TWIN_LABEL, 'speckit:stage:specified']);
    rmSync(restored, { recursive: true, force: true });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('fail-safe: an unreadable twin blocks creates and fails the run', async () => {
  const root = makeRepo(['20261005-130700-a']);
  try {
    const github = new FakeGitHub([{ number: 1, id: 1000, state: 'open', title: 'x', body: 'damaged', labels: [{ name: TWIN_LABEL }] }]);
    const result = await syncRepo(github, root);
    assert.equal(result.exitCode, 1);
    assert.equal(github.issues.length, 1);
    assert.match(readFileSync(path.join(root, 'summary.md'), 'utf8'), /Unreadable twins[\s\S]*#1[\s\S]*Skipped creating a twin/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('apply adds validated links, comments, and clears the pending label', async () => {
  const root = makeRepo(['20261005-130700-a', '20261005-130701-b']);
  try {
    const github = new FakeGitHub();
    await syncRepo(github, root);
    const outputFile = path.join(root, 'inference.txt');
    writeFileSync(outputFile, '{"links":[{"blocked":"20261005-130701-b","blockedBy":"20261005-130700-a","reason":"b builds on a."}]}');
    const result = await runApply({ client: github, rootDir: root, env: envFor(root), outputFile, log: silent });
    assert.equal(result.exitCode, 0);
    assert.deepEqual(github.edges, [[101, 100]]);
    assert.ok(github.issues.every((issue) => !issue.labels.some((label) => label.name === PENDING_LABEL)));
    assert.match(github.comments.find((c) => c.number === 101).body, /Blocked by #100/);
    assert.match(github.comments.find((c) => c.number === 100).body, /Blocks #101/);

    writeFileSync(outputFile, 'garbage');
    assert.equal((await runApply({ client: github, rootDir: root, env: envFor(root), outputFile, log: silent })).exitCode, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('apply rejects invalid output and keeps the pending label for a retry', async () => {
  const root = makeRepo(['20261005-130700-a', '20261005-130701-b']);
  try {
    const github = new FakeGitHub();
    await syncRepo(github, root);
    const outputFile = path.join(root, 'inference.txt');
    writeFileSync(outputFile, '{"links":[{"blocked":"20261005-130701-b","blockedBy":"unknown","reason":"r"}]}');
    const result = await runApply({ client: github, rootDir: root, env: envFor(root), outputFile, log: silent });
    assert.equal(result.exitCode, 1);
    assert.equal(github.edges.length, 0);
    assert.ok(github.issues.every((issue) => issue.labels.some((label) => label.name === PENDING_LABEL)));
    assert.match(readFileSync(path.join(root, 'summary.md'), 'utf8'), /rejected/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('apply skips links that already exist after a partial run', async () => {
  const root = makeRepo(['20261005-130700-a', '20261005-130701-b']);
  try {
    const github = new FakeGitHub();
    await syncRepo(github, root);
    github.edges.push([101, 100]);
    const outputFile = path.join(root, 'inference.txt');
    writeFileSync(outputFile, '{"links":[{"blocked":"20261005-130701-b","blockedBy":"20261005-130700-a","reason":"r"}]}');
    assert.equal((await runApply({ client: github, rootDir: root, env: envFor(root), outputFile, log: silent })).exitCode, 0);
    assert.deepEqual(github.edges, [[101, 100]]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('apply ignores cross-repository blockers that share a twin issue number', async () => {
  const root = makeRepo(['20261005-130700-a', '20261005-130701-b']);
  try {
    const github = new FakeGitHub();
    await syncRepo(github, root);
    github.listBlockedBy = async (number) => (number === 100 ? [{ number: 101, id: 999_999 }] : []);
    const outputFile = path.join(root, 'inference.txt');
    writeFileSync(outputFile, '{"links":[{"blocked":"20261005-130701-b","blockedBy":"20261005-130700-a","reason":"r"}]}');
    assert.equal((await runApply({ client: github, rootDir: root, env: envFor(root), outputFile, log: silent })).exitCode, 0);
    assert.deepEqual(github.edges, [[101, 100]]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('main validates arguments', async () => {
  const client = new FakeGitHub();
  await assert.rejects(() => main(['sync'], { env: {}, client }), /--prompt-file/);
  await assert.rejects(() => main(['apply'], { env: {}, client }), /--output-file/);
  await assert.rejects(() => main(['nope'], { env: {}, client }), /Usage/);
});

function response(status, body, headers = {}) {
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: new Map(Object.entries(headers)),
    text: async () => (body === undefined ? '' : JSON.stringify(body)),
  };
}

test('GitHub client lists twins through GraphQL with pagination and retries rate limits', async () => {
  const calls = [];
  const node = (number, extra = {}) => ({
    number,
    databaseId: number * 10,
    title: `t${number}`,
    body: 'b',
    state: 'CLOSED',
    stateReason: 'NOT_PLANNED',
    url: `https://github.com/octo/repo/issues/${number}`,
    labels: { nodes: [{ name: 'speckit:spec' }] },
    ...extra,
  });
  const page = (nodes, hasNextPage, endCursor = null) => ({ data: { repository: { issues: { pageInfo: { hasNextPage, endCursor }, nodes } } } });
  const responses = [
    response(429, { message: 'slow down' }, { 'retry-after': '1' }),
    response(200, page([node(1)], true, 'c1')),
    response(200, page([node(2, { state: 'OPEN', stateReason: null })], false)),
  ];
  const client = new GitHubClient({
    token: 't',
    repository: 'octo/repo',
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      return responses.shift();
    },
    wait: async () => {},
  });
  const issues = await client.listTwinIssues('speckit:spec');
  assert.deepEqual(issues.map((issue) => [issue.number, issue.id, issue.state, issue.state_reason]), [
    [1, 10, 'closed', 'not_planned'],
    [2, 20, 'open', null],
  ]);
  assert.equal(calls[0].url, 'https://api.github.com/graphql');
  assert.equal(calls[0].init.headers.Authorization, 'Bearer t');
  assert.deepEqual(JSON.parse(calls[2].init.body).variables, { owner: 'octo', name: 'repo', label: 'speckit:spec', after: 'c1' });
});

test('GitHub client paginates REST lists and surfaces GraphQL errors', async () => {
  const calls = [];
  const responses = [
    response(200, [{ number: 1, id: 1 }], { link: '<https://api.github.com/next>; rel="next"' }),
    response(200, [{ number: 2, id: 2 }]),
    response(200, { errors: [{ message: 'bad query' }] }),
  ];
  const client = new GitHubClient({
    token: 't',
    repository: 'octo/repo',
    fetchImpl: async (url) => {
      calls.push(url);
      return responses.shift();
    },
  });
  assert.deepEqual((await client.listBlockedBy(7)).map((issue) => issue.number), [1, 2]);
  assert.match(calls[0], /\/repos\/octo\/repo\/issues\/7\/dependencies\/blocked_by\?per_page=100$/);
  assert.equal(calls[1], 'https://api.github.com/next');
  await assert.rejects(() => client.listTwinIssues('x'), /bad query/);
});

test('GitHub client creates missing labels and surfaces errors', async () => {
  const calls = [];
  const responses = [response(404, { message: 'Not Found' }), response(201, { name: 'x' }), response(500, { message: 'boom' })];
  const client = new GitHubClient({
    token: 't',
    repository: 'octo/repo',
    fetchImpl: async (url, init) => {
      calls.push({ url, method: init.method, body: init.body });
      return responses.shift();
    },
  });
  await client.ensureLabel({ name: 'speckit:spec', color: '000000', description: 'd' });
  assert.equal(calls[1].method, 'POST');
  assert.match(calls[1].body, /"name":"speckit:spec"/);
  await assert.rejects(() => client.addBlockedBy(1, 2), /HTTP 500/);
  assert.throws(() => new GitHubClient({ token: '', repository: 'octo/repo' }), /token/);
  assert.throws(() => new GitHubClient({ token: 't', repository: 'nope' }), /owner\/repo/);
});
