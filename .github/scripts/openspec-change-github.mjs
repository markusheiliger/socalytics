const API_ROOT = 'https://api.github.com';
const API_VERSION = '2026-03-10';

function assertToken(token, name) {
  if (typeof token !== 'string' || token.trim() === '') {
    throw new Error(`${name} is required`);
  }
}

function redact(value, secrets) {
  let result = value;
  for (const secret of secrets) {
    if (secret) result = result.replaceAll(secret, '***');
  }
  return result;
}

async function responseError(response, secrets) {
  const body = await response.text();
  const detail = body ? `: ${body.slice(0, 2_000)}` : '';
  return new Error(redact(
    `GitHub API ${response.status} ${response.statusText}${detail}`,
    secrets,
  ));
}

export class GitHubChangeClient {
  constructor({
    owner,
    repo,
    repositoryToken,
    agentToken,
    fetchImpl = globalThis.fetch,
  }) {
    if (!owner || !repo) throw new Error('GitHub owner and repository are required');
    assertToken(repositoryToken, 'Repository token');
    if (typeof fetchImpl !== 'function') throw new Error('fetch implementation is required');

    this.owner = owner;
    this.repo = repo;
    this.repositoryToken = repositoryToken;
    this.agentToken = agentToken;
    this.fetch = fetchImpl;
  }

  get repositoryPath() {
    return `/repos/${encodeURIComponent(this.owner)}/${encodeURIComponent(this.repo)}`;
  }

  async request(path, {
    method = 'GET',
    body,
    agent = false,
    expected = [200],
  } = {}) {
    const token = agent ? this.agentToken : this.repositoryToken;
    if (agent) assertToken(token, 'Agent token');
    const response = await this.fetch(`${API_ROOT}${path}`, {
      method,
      headers: {
        Accept: 'application/vnd.github+json',
        Authorization: `Bearer ${token}`,
        'Content-Type': 'application/json',
        'User-Agent': 'socalytics-openspec-change-queue',
        'X-GitHub-Api-Version': API_VERSION,
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });

    if (!expected.includes(response.status)) {
      throw await responseError(response, [this.repositoryToken, this.agentToken]);
    }
    if (response.status === 204) return null;
    return response.json();
  }

  async paginate(path, { agent = false, perPage = 100 } = {}) {
    const separator = path.includes('?') ? '&' : '?';
    const results = [];
    for (let page = 1; ; page += 1) {
      const value = await this.request(
        `${path}${separator}per_page=${perPage}&page=${page}`,
        { agent },
      );
      const items = Array.isArray(value) ? value : value.tasks;
      if (!Array.isArray(items)) {
        throw new Error('Paginated GitHub response did not contain an array');
      }
      results.push(...items);
      if (items.length < perPage) return results;
    }
  }

  listIssueTwins() {
    return this.paginate(
      `${this.repositoryPath}/issues?state=all&labels=${encodeURIComponent('openspec:change')}`,
    );
  }

  createIssue({ title, body, labels = ['openspec:change'] }) {
    return this.request(`${this.repositoryPath}/issues`, {
      method: 'POST',
      body: { title, body, labels },
      expected: [201],
    });
  }

  updateIssue(issueNumber, patch) {
    return this.request(`${this.repositoryPath}/issues/${issueNumber}`, {
      method: 'PATCH',
      body: patch,
    });
  }

  addIssueLabel(issueNumber, label) {
    return this.request(`${this.repositoryPath}/issues/${issueNumber}/labels`, {
      method: 'POST',
      body: { labels: [label] },
    });
  }

  removeIssueLabel(issueNumber, label) {
    return this.request(
      `${this.repositoryPath}/issues/${issueNumber}/labels/${encodeURIComponent(label)}`,
      { method: 'DELETE', expected: [200, 204, 404] },
    );
  }

  createIssueComment(issueNumber, body) {
    return this.request(`${this.repositoryPath}/issues/${issueNumber}/comments`, {
      method: 'POST',
      body: { body },
      expected: [201],
    });
  }

  listIssueComments(issueNumber) {
    return this.paginate(`${this.repositoryPath}/issues/${issueNumber}/comments`);
  }

  updateIssueComment(commentId, body) {
    return this.request(`${this.repositoryPath}/issues/comments/${commentId}`, {
      method: 'PATCH',
      body: { body },
    });
  }

  ensureLabel({ name, color, description }) {
    return this.request(`${this.repositoryPath}/labels/${encodeURIComponent(name)}`, {
      expected: [200, 404],
    }).then((existing) => {
      if (existing?.name) return existing;
      return this.request(`${this.repositoryPath}/labels`, {
        method: 'POST',
        body: { name, color, description },
        expected: [201],
      });
    });
  }

  listBlockedBy(issueNumber) {
    return this.paginate(
      `${this.repositoryPath}/issues/${issueNumber}/dependencies/blocked_by`,
    );
  }

  listBlocking(issueNumber) {
    return this.paginate(
      `${this.repositoryPath}/issues/${issueNumber}/dependencies/blocking`,
    );
  }

  addBlockedBy(issueNumber, blockingIssueId) {
    return this.request(
      `${this.repositoryPath}/issues/${issueNumber}/dependencies/blocked_by`,
      {
        method: 'POST',
        body: { issue_id: blockingIssueId },
        expected: [201],
      },
    );
  }

  removeBlockedBy(issueNumber, blockingIssueId) {
    return this.request(
      `${this.repositoryPath}/issues/${issueNumber}/dependencies/blocked_by/${blockingIssueId}`,
      { method: 'DELETE', expected: [200] },
    );
  }

  listAgentTasks(states = []) {
    const state = states.length > 0
      ? `&state=${encodeURIComponent(states.join(','))}`
      : '';
    return this.paginate(
      `/agents/repos/${encodeURIComponent(this.owner)}/${encodeURIComponent(this.repo)}/tasks?${state.slice(1)}`,
      { agent: true },
    );
  }

  getAgentTask(taskId) {
    return this.request(
      `/agents/repos/${encodeURIComponent(this.owner)}/${encodeURIComponent(this.repo)}/tasks/${encodeURIComponent(taskId)}`,
      { agent: true },
    );
  }

  startAgentTask({
    prompt,
    customAgent,
    baseRef,
    headRef,
    createPullRequest = false,
  }) {
    const body = {
      prompt,
      custom_agent: customAgent,
      base_ref: baseRef,
      create_pull_request: createPullRequest,
    };
    if (headRef) body.head_ref = headRef;
    return this.request(
      `/agents/repos/${encodeURIComponent(this.owner)}/${encodeURIComponent(this.repo)}/tasks`,
      { method: 'POST', body, agent: true, expected: [201] },
    );
  }

  listOpenPullRequestsForHead(headRef) {
    return this.paginate(
      `${this.repositoryPath}/pulls?state=open&head=${encodeURIComponent(`${this.owner}:${headRef}`)}`,
    );
  }

  getPullRequest(number) {
    return this.request(`${this.repositoryPath}/pulls/${number}`);
  }

  getBranch(branch) {
    return this.request(`${this.repositoryPath}/branches/${encodeURIComponent(branch)}`);
  }

  async compareCommits(baseSha, headSha) {
    const value = await this.request(
      `${this.repositoryPath}/compare/${encodeURIComponent(baseSha)}...${encodeURIComponent(headSha)}`,
    );
    if (!Array.isArray(value.files)) {
      throw new Error('GitHub compare response did not contain changed files');
    }
    return value;
  }

  getRepositoryContent(path, ref) {
    return this.request(
      `${this.repositoryPath}/contents/${path.split('/').map(encodeURIComponent).join('/')}?ref=${encodeURIComponent(ref)}`,
    );
  }

  async getTextContent(path, ref) {
    const value = await this.getRepositoryContent(path, ref);
    if (value.type !== 'file' || value.encoding !== 'base64') {
      throw new Error(`GitHub content is not a base64 file: ${path}@${ref}`);
    }
    return Buffer.from(value.content.replaceAll('\n', ''), 'base64').toString('utf8');
  }
}
