const API_ROOT = 'https://api.github.com';
const COPILOT_API_ROOT = 'https://api.githubcopilot.com';
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

export function decodeAgentSessionFinalResponse(eventStream) {
  if (typeof eventStream !== 'string') {
    throw new Error('Agent session log must be a string');
  }
  const responses = [];
  let current = '';
  let currentComplete = false;
  let streamComplete = false;

  const completeCurrent = () => {
    if (current === '') return;
    responses.push(current);
    current = '';
    currentComplete = false;
  };

  for (const line of eventStream.split(/\r?\n/)) {
    const dataLine = line.match(/^data:\s?(.*)$/);
    if (!dataLine) continue;
    const data = dataLine[1];
    if (streamComplete) {
      throw new Error('Agent session log contains events after stream completion');
    }
    if (data === '[DONE]') {
      completeCurrent();
      streamComplete = true;
      continue;
    }
    let event;
    try {
      event = JSON.parse(data);
    } catch (error) {
      throw new Error(`Agent session log contains invalid event JSON: ${error.message}`);
    }
    if (event === null || typeof event !== 'object' || Array.isArray(event)) {
      throw new Error('Agent session log event must be an object');
    }
    if (event.choices !== undefined && !Array.isArray(event.choices)) {
      throw new Error('Agent session log event.choices must be an array');
    }
    const choices = event.choices ?? [];
    if (choices.some((choice) => choice === null
      || typeof choice !== 'object'
      || Array.isArray(choice))) {
      throw new Error('Agent session log choices must be objects');
    }
    const contentChoices = choices.filter(
      (choice) => typeof choice.delta?.content === 'string'
        || choice.delta?.role === 'assistant'
        || choice.finish_reason != null,
    );
    const indexes = new Set(contentChoices.map((choice) => choice.index ?? 0));
    if (indexes.size > 1) {
      throw new Error('Agent session log contains ambiguous assistant response choices');
    }
    for (const choice of contentChoices) {
      if (choice.delta?.role === 'assistant' && current !== '') {
        if (!currentComplete) {
          throw new Error('Agent session log contains an incomplete assistant response');
        }
        completeCurrent();
      }
      if (typeof choice.delta?.content === 'string') {
        if (currentComplete) completeCurrent();
        current += choice.delta.content;
      }
      if (choice.finish_reason != null) {
        if (current === '') {
          throw new Error('Agent session log completed an assistant response without content');
        }
        currentComplete = true;
      }
    }
  }
  if (!streamComplete) {
    throw new Error('Agent session log is incomplete');
  }
  if (responses.length === 0) {
    throw new Error('Agent session log did not contain assistant content');
  }
  return responses.at(-1);
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

  async getAgentSessionLog(sessionId) {
    assertToken(this.agentToken, 'Agent token');
    const response = await this.fetch(
      `${COPILOT_API_ROOT}/agents/sessions/${encodeURIComponent(sessionId)}/logs`,
      {
        headers: {
          Accept: 'application/vnd.github.nebula-preview',
          Authorization: `Bearer ${this.agentToken}`,
          'Copilot-Integration-Id': 'copilot-4-cli',
          'User-Agent': 'socalytics-openspec-change-queue',
          'X-GitHub-Api-Version': '2026-01-09',
        },
      },
    );
    if (!response.ok) {
      throw await responseError(response, [this.repositoryToken, this.agentToken]);
    }
    return decodeAgentSessionFinalResponse(await response.text());
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
