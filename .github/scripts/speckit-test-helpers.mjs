import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

import { STEPS, renderStepRunName } from './speckit-implement-core.mjs';

// In-memory stand-in for GitHubClient used by the speckit tooling tests.
export class FakeGitHub {
  constructor(issues = []) {
    this.issues = issues;
    this.labels = new Set();
    this.comments = [];
    this.edges = [];
    this.updates = [];
    this.removals = [];
    this.permissions = {};
    this.events = [];
    this.nextNumber = 100;
    this.clock = Date.parse('2026-10-01T00:00:00Z');
  }

  // Deterministic, strictly increasing timestamps for events, pull requests, and closings.
  tick() {
    this.clock += 1000;
    return new Date(this.clock).toISOString();
  }

  // Simulates a person adding a label in the GitHub UI, including the issue event that records who did it.
  humanLabel(number, name, login) {
    const issue = this.find(number);
    if (!issue.labels.some((label) => label.name === name)) issue.labels.push({ name });
    this.events.push({ issue: number, event: 'labeled', label: { name }, actor: { login }, created_at: this.tick() });
  }

  // Simulates a person closing (or merging) an implementation pull request.
  closePull(number, { merged = false } = {}) {
    const pull = this.repo.pulls.find((item) => item.number === number);
    pull.state = 'closed';
    pull.closed_at = this.tick();
    pull.merged_at = merged ? pull.closed_at : null;
  }

  async listIssueEvents(number) {
    return structuredClone(this.events.filter((event) => event.issue === number));
  }

  async listTwinIssues(label) {
    return this.issues.filter((issue) => issue.labels.some((l) => l.name === label)).map((issue) => structuredClone(issue));
  }

  async ensureLabel({ name }) {
    this.labels.add(name);
  }

  async createIssue({ title, body, labels }) {
    const number = this.nextNumber++;
    const issue = { number, id: number * 1000, node_id: `I_${number}`, state: 'open', state_reason: null, title, body, labels: labels.map((name) => ({ name })) };
    this.issues.push(issue);
    return structuredClone(issue);
  }

  find(number) {
    return this.issues.find((issue) => issue.number === number);
  }

  async getIssue(number) {
    return structuredClone(this.find(number));
  }

  async getPermission(login) {
    const role = this.permissions[login] ?? 'none';
    return { permission: role === 'maintain' ? 'write' : role, role_name: role };
  }

  async updateIssue(number, fields) {
    this.updates.push({ number, fields: structuredClone(fields) });
    const { labels, ...rest } = fields;
    Object.assign(this.find(number), rest);
    if (labels) this.find(number).labels = labels.map((name) => ({ name }));
  }

  async removeLabel(number, name) {
    this.removals.push({ number, name });
    const issue = this.find(number);
    issue.labels = issue.labels.filter((label) => label.name !== name);
  }

  async getIssueLabels(number) {
    return structuredClone(this.find(number).labels);
  }

  async createComment(number, body) {
    this.comments.push({ number, body, user: { login: this.commentAuthor ?? 'github-actions[bot]' }, created_at: this.tick() });
  }

  async listBlockedBy(number) {
    return this.edges.filter(([blocked]) => blocked === number).map(([, blocker]) => structuredClone(this.find(blocker)));
  }

  async addBlockedBy(number, blockerId) {
    this.edges.push([number, this.issues.find((issue) => issue.id === blockerId).number]);
  }

  // --- implementation workspace (branches, commits, pull requests, check runs) ---

  get repo() {
    if (!this.repoState) {
      this.repoState = {
        branches: { main: 'sha-main' },
        commits: { 'sha-main': { tree: 'tree-main', parents: [] } },
        pulls: [],
        checkRuns: [],
        linked: [],
        runs: [],
        files: new Map(),
        reviewRequests: [],
        merges: [],
        pullCommits: {},
        pullFiles: {},
        nextSha: 1,
        nextCheckRun: 1,
        nextRun: 1,
      };
    }
    return this.repoState;
  }

  // Test helper: content of a file at a branch name or commit SHA.
  setFile(ref, filePath, content) {
    this.repo.files.set(`${ref}:${filePath}`, content);
  }

  async getFileContent(filePath, ref) {
    const branch = Object.entries(this.repo.branches).find(([, sha]) => sha === ref)?.[0];
    return this.repo.files.get(`${ref}:${filePath}`)
      ?? this.repo.files.get(`${this.repo.branches[ref]}:${filePath}`)
      ?? (branch ? this.repo.files.get(`${branch}:${filePath}`) : undefined)
      ?? null;
  }

  async getPullRequest(number) {
    const pull = this.repo.pulls.find((item) => item.number === number);
    return structuredClone({ ...pull, head: { ...pull.head, sha: this.repo.branches[pull.head.ref] ?? pull.head.sha } });
  }

  async updatePullRequest(number, fields) {
    Object.assign(this.repo.pulls.find((item) => item.number === number), fields);
  }

  async markReadyForReview(nodeId) {
    this.repo.pulls.find((item) => item.node_id === nodeId).draft = false;
  }

  async requestReviewers(number, reviewers) {
    this.repo.reviewRequests.push({ number, reviewers });
  }

  async updateCheckRun(id, fields) {
    Object.assign(this.repo.checkRuns.find((run) => run.id === id), structuredClone(fields));
  }

  async listWorkflowRuns(workflowFile, since) {
    return structuredClone(this.repo.runs
      .filter((run) => run.workflow === workflowFile && Date.parse(run.created_at) >= Date.parse(since))
      .sort((a, b) => b.id - a.id));
  }

  async dispatchWorkflow(workflowFile, ref, inputs) {
    const id = this.repo.nextRun++;
    const step = Object.entries(STEPS).find(([, entry]) => entry.file === workflowFile)?.[0];
    this.repo.runs.push({
      id,
      workflow: workflowFile,
      ref,
      inputs: structuredClone(inputs),
      display_title: step ? renderStepRunName({ step, twin: inputs.twin, task: inputs.task, attempt: inputs.attempt }) : workflowFile,
      status: 'queued',
      conclusion: null,
      created_at: this.tick(),
    });
  }

  // Test helper: marks a workflow run as completed.
  completeRun(id, conclusion = 'failure') {
    Object.assign(this.repo.runs.find((run) => run.id === id), { status: 'completed', conclusion });
  }

  async mergePullRequest(number, { sha, merge_method, commit_title, commit_message }) {
    const pull = this.repo.pulls.find((item) => item.number === number);
    if (this.mergeRefusal) return { merged: false, message: this.mergeRefusal };
    if (this.repo.branches[pull.head.ref] !== sha) return { merged: false, message: 'Head branch was modified' };
    const merged = `sha-${this.repo.nextSha++}`;
    this.repo.merges.push({ number, sha, merge_method, commit_title, commit_message, merged });
    this.closePull(number, { merged: true });
    return { merged: true, sha: merged };
  }

  async listPullRequestCommits(number) {
    return structuredClone(this.repo.pullCommits[number] ?? []);
  }

  async listPullRequestFiles(number) {
    return (this.repo.pullFiles[number] ?? []).map((file) => (typeof file === 'string' ? { filename: file } : file));
  }

  async getWorkflowRun(id) {
    const run = this.repo.runs.find((candidate) => String(candidate.id) === String(id));
    return run ? structuredClone(run) : null;
  }

  async listIssueComments(number) {
    return structuredClone(this.comments.filter((comment) => comment.number === number));
  }

  async getBranchSha(branch) {
    return this.repo.branches[branch] ?? null;
  }

  async createBranch(branch, sha) {
    this.repo.branches[branch] = sha;
  }

  async updateBranch(branch, sha) {
    this.repo.branches[branch] = sha;
  }

  async deleteBranch(branch) {
    delete this.repo.branches[branch];
  }

  async aheadBy(base, head) {
    const baseSha = this.repo.branches[base];
    let count = 0;
    for (let sha = this.repo.branches[head]; sha && sha !== baseSha; sha = this.repo.commits[sha]?.parents[0]) count += 1;
    return count;
  }

  async createEmptyCommit(parentSha, message) {
    const sha = `sha-${this.repo.nextSha++}`;
    this.repo.commits[sha] = { tree: this.repo.commits[parentSha].tree, parents: [parentSha], message };
    return sha;
  }

  async createLinkedBranch(issueNodeId, oid, name) {
    if (this.failLinkedBranch) throw new Error('linking not permitted');
    this.repo.branches[name] = oid;
    this.repo.linked.push({ issueNodeId, name });
    return name;
  }

  async listPullRequestsForHead(branch) {
    return structuredClone(this.repo.pulls.filter((pull) => pull.head.ref === branch).sort((a, b) => b.number - a.number));
  }

  async createPullRequest({ title, head, base, body, draft }) {
    if (this.failCreatePull) throw new Error(this.failCreatePull);
    if (this.repo.pulls.some((pull) => pull.head.ref === head && pull.state === 'open')) throw new Error('A pull request already exists');
    const number = this.nextNumber++;
    const pull = { number, node_id: `PR_${number}`, title, body, draft, base: { ref: base }, head: { ref: head, sha: this.repo.branches[head], repo: { full_name: 'octo/repo' } }, state: 'open', merged_at: null, closed_at: null, created_at: this.tick(), assignees: [] };
    this.repo.pulls.push(pull);
    return structuredClone(pull);
  }

  async addAssignees(number, assignees) {
    if (this.failAssign) throw new Error('assignee rejected');
    const pull = this.repo.pulls.find((item) => item.number === number);
    pull.assignees.push(...assignees);
  }

  async listCheckRuns(sha, name) {
    return structuredClone(this.repo.checkRuns.filter((run) => run.head_sha === sha && run.name === name));
  }

  async createCheckRun(fields) {
    const run = { id: this.repo.nextCheckRun++, started_at: this.tick(), ...structuredClone(fields) };
    this.repo.checkRuns.push(run);
    return structuredClone(run);
  }
}

export const SPEC_TEMPLATE = (folder) =>
  `# Feature Specification: ${folder} title\n\n**Input**: User description: "${folder} summary"\n\n## Assumptions\n\n- **Dependencies**: none.\n`;

// Creates a temporary repository root with one spec folder per entry; entries may add plan, tasks, and checklists.
export function makeRepo(folders) {
  const root = mkdtempSync(path.join(tmpdir(), 'speckit-'));
  mkdirSync(path.join(root, 'specs'));
  writeFileSync(path.join(root, 'specs', 'README.md'), '# index\n');
  for (const item of folders) {
    const { folder, plan = false, tasks = null, checklists = {} } = typeof item === 'string' ? { folder: item } : item;
    const dir = path.join(root, 'specs', folder);
    mkdirSync(dir);
    writeFileSync(path.join(dir, 'spec.md'), SPEC_TEMPLATE(folder));
    if (plan) writeFileSync(path.join(dir, 'plan.md'), '# plan\n');
    if (tasks !== null) writeFileSync(path.join(dir, 'tasks.md'), tasks);
    if (Object.keys(checklists).length > 0) {
      mkdirSync(path.join(dir, 'checklists'));
      for (const [name, content] of Object.entries(checklists)) writeFileSync(path.join(dir, 'checklists', name), content);
    }
  }
  return root;
}

export function envFor(root) {
  return { GITHUB_REPOSITORY: 'octo/repo', GITHUB_STEP_SUMMARY: path.join(root, 'summary.md'), GITHUB_OUTPUT: path.join(root, 'output.txt') };
}

export const silent = () => {};
