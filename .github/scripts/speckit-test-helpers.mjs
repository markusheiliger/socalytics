import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

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
  }

  // Simulates a person adding a label in the GitHub UI, including the issue event that records who did it.
  humanLabel(number, name, login) {
    const issue = this.find(number);
    if (!issue.labels.some((label) => label.name === name)) issue.labels.push({ name });
    this.events.push({ issue: number, event: 'labeled', label: { name }, actor: { login } });
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
    const issue = { number, id: number * 1000, state: 'open', state_reason: null, title, body, labels: labels.map((name) => ({ name })) };
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
    this.comments.push({ number, body });
  }

  async listBlockedBy(number) {
    return this.edges.filter(([blocked]) => blocked === number).map(([, blocker]) => structuredClone(this.find(blocker)));
  }

  async addBlockedBy(number, blockerId) {
    this.edges.push([number, this.issues.find((issue) => issue.id === blockerId).number]);
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
