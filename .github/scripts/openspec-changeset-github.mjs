const API_VERSION = '2022-11-28';

function encodePath(value) {
  return String(value).split('/').map(encodeURIComponent).join('/');
}

export class GitHubClient {
  constructor({ repository, token, agentToken, apiUrl = 'https://api.github.com', fetchImpl = fetch }) {
    if (!repository?.includes('/')) {
      throw new Error('repository must be in owner/name form');
    }
    this.repository = repository;
    this.token = token;
    this.agentToken = agentToken;
    this.apiUrl = apiUrl.replace(/\/$/, '');
    this.fetchImpl = fetchImpl;
  }

  async request(path, { method = 'GET', body, token = this.token } = {}) {
    if (!token) {
      throw new Error('GitHub token is required');
    }
    const response = await this.fetchImpl(`${this.apiUrl}${path}`, {
      method,
      headers: {
        Accept: 'application/vnd.github+json',
        Authorization: `Bearer ${token}`,
        'X-GitHub-Api-Version': API_VERSION,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });

    const text = await response.text();
    const payload = text ? JSON.parse(text) : undefined;
    if (!response.ok) {
      const message = payload?.message ?? text ?? response.statusText;
      throw new Error(`GitHub API ${method} ${path} failed (${response.status}): ${message}`);
    }
    return payload;
  }

  async listChangesetIssues() {
    const path = `/repos/${this.repository}/issues?state=open&labels=${encodeURIComponent('openspec:changeset')}&per_page=100`;
    const issues = await this.request(path);
    return issues.filter((issue) => {
      if (issue.pull_request) {
        return false;
      }
      const labels = issue.labels.map((label) => typeof label === 'string' ? label : label.name);
      return labels.some((label) => ['changeset:ready', 'changeset:running', 'changeset:attention'].includes(label));
    });
  }

  getIssue(issueNumber) {
    return this.request(`/repos/${this.repository}/issues/${issueNumber}`);
  }

  listSubIssues(issueNumber) {
    return this.request(`/repos/${this.repository}/issues/${issueNumber}/sub_issues?per_page=100`);
  }

  listIssueComments(issueNumber) {
    return this.request(`/repos/${this.repository}/issues/${issueNumber}/comments?per_page=100`);
  }

  listPullRequests() {
    return this.request(`/repos/${this.repository}/pulls?state=all&sort=updated&direction=desc&per_page=100`);
  }

  createIssueComment(issueNumber, body) {
    return this.request(`/repos/${this.repository}/issues/${issueNumber}/comments`, {
      method: 'POST',
      body: { body },
    });
  }

  updateIssueComment(commentId, body) {
    return this.request(`/repos/${this.repository}/issues/comments/${commentId}`, {
      method: 'PATCH',
      body: { body },
    });
  }

  updateIssue(issueNumber, body) {
    return this.request(`/repos/${this.repository}/issues/${issueNumber}`, {
      method: 'PATCH',
      body,
    });
  }

  getBranch(branch) {
    return this.request(`/repos/${this.repository}/branches/${encodeURIComponent(branch)}`);
  }

  compareCommits(base, head) {
    return this.request(`/repos/${this.repository}/compare/${encodeURIComponent(base)}...${encodeURIComponent(head)}`);
  }

  async ensureLabel(name, color, description) {
    try {
      await this.request(`/repos/${this.repository}/labels`, {
        method: 'POST',
        body: { name, color, description },
      });
    } catch (error) {
      if (!error.message.includes('(422)')) {
        throw error;
      }
    }
  }

  setIssueLabels(issueNumber, labels) {
    return this.request(`/repos/${this.repository}/issues/${issueNumber}/labels`, {
      method: 'PUT',
      body: { labels },
    });
  }

  createAgentTask({ prompt, baseRef = 'main', headRef, createPullRequest = false }) {
    if (!this.agentToken) {
      throw new Error('COPILOT_AGENT_TOKEN is required to dispatch cloud-agent tasks');
    }
    return this.request(`/agents/repos/${this.repository}/tasks`, {
      method: 'POST',
      token: this.agentToken,
      body: {
        prompt,
        base_ref: baseRef,
        ...(headRef ? { head_ref: headRef } : {}),
        create_pull_request: createPullRequest,
      },
    });
  }

  getAgentTask(taskId) {
    if (!this.agentToken) {
      throw new Error('COPILOT_AGENT_TOKEN is required to read cloud-agent tasks');
    }
    return this.request(`/agents/repos/${this.repository}/tasks/${encodeURIComponent(taskId)}`, {
      token: this.agentToken,
    });
  }

  async enablePullRequestAutoMerge(pullRequestNodeId) {
    if (!pullRequestNodeId) {
      throw new Error('Pull request node ID is required to enable auto-merge');
    }
    const payload = await this.request('/graphql', {
      method: 'POST',
      body: {
        query: `mutation EnableAutoMerge($pullRequestId: ID!) {
          enablePullRequestAutoMerge(input: {
            pullRequestId: $pullRequestId,
            mergeMethod: SQUASH
          }) {
            pullRequest { number }
          }
        }`,
        variables: { pullRequestId: pullRequestNodeId },
      },
    });
    if (payload?.errors?.length) {
      throw new Error(`GitHub GraphQL auto-merge failed: ${payload.errors.map((error) => error.message).join('; ')}`);
    }
    return payload?.data?.enablePullRequestAutoMerge?.pullRequest;
  }
}

export function taskBranchArtifact(task) {
  const artifact = task?.artifacts?.find((candidate) => candidate?.provider === 'github' && candidate?.type === 'branch');
  if (!artifact?.data?.head_ref || !artifact?.data?.base_ref) {
    return null;
  }
  return {
    headRef: artifact.data.head_ref,
    baseRef: artifact.data.base_ref,
  };
}

export function repositoryParts(repository) {
  const [owner, name, extra] = repository.split('/');
  if (!owner || !name || extra) {
    throw new Error('Repository must be in owner/name form');
  }
  return { owner: encodePath(owner), name: encodePath(name) };
}

export function normalizeAgentTask(task) {
  return {
    id: task?.id ?? task?.task_id ?? task?.task?.id ?? null,
    state: task?.state ?? task?.status ?? task?.task?.state ?? null,
    branch: taskBranchArtifact(task),
  };
}