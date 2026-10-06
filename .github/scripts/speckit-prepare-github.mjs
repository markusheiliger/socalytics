const API_VERSION = '2026-03-10';
const MAX_ATTEMPTS = 4;

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function nextLink(header) {
  if (!header) return null;
  for (const part of header.split(',')) {
    const match = part.match(/<([^>]+)>;\s*rel="next"/);
    if (match) return match[1];
  }
  return null;
}

export class GitHubError extends Error {
  constructor(method, path, status, body) {
    super(`${method} ${path} failed with HTTP ${status}: ${body.slice(0, 300)}`);
    this.status = status;
  }
}

export class GitHubClient {
  constructor({ token, repository, apiUrl = 'https://api.github.com', graphqlUrl, fetchImpl = fetch, wait = sleep }) {
    if (!token) throw new Error('A GitHub token is required (GITHUB_TOKEN or GH_TOKEN).');
    if (!/^[^/\s]+\/[^/\s]+$/.test(repository ?? '')) {
      throw new Error('GITHUB_REPOSITORY must have the form owner/repo.');
    }
    this.token = token;
    this.repository = repository;
    this.apiUrl = apiUrl.replace(/\/$/, '');
    this.graphqlUrl = graphqlUrl ?? `${this.apiUrl}/graphql`;
    this.fetch = fetchImpl;
    this.wait = wait;
  }

  async raw(method, url, body) {
    for (let attempt = 1; ; attempt += 1) {
      const response = await this.fetch(url, {
        method,
        headers: {
          Accept: 'application/vnd.github+json',
          Authorization: `Bearer ${this.token}`,
          'X-GitHub-Api-Version': API_VERSION,
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
        },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      const limited = response.status === 429
        || (response.status === 403 && (response.headers.get('retry-after') || response.headers.get('x-ratelimit-remaining') === '0'));
      if (limited && attempt < MAX_ATTEMPTS) {
        const retryAfter = Number(response.headers.get('retry-after'));
        await this.wait(Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : 2000 * attempt);
        continue;
      }
      return response;
    }
  }

  async request(method, path, body, { allow = [] } = {}) {
    const url = path.startsWith('http') ? path : `${this.apiUrl}${path}`;
    const response = await this.raw(method, url, body);
    if (!response.ok && !allow.includes(response.status)) {
      throw new GitHubError(method, path, response.status, await response.text());
    }
    if (response.status === 204 || !response.ok) return { status: response.status, data: null, headers: response.headers };
    const text = await response.text();
    return { status: response.status, data: text ? JSON.parse(text) : null, headers: response.headers };
  }

  async paginate(path) {
    const items = [];
    let url = `${this.apiUrl}${path}${path.includes('?') ? '&' : '?'}per_page=100`;
    while (url) {
      const { data, headers } = await this.request('GET', url);
      items.push(...data);
      url = nextLink(headers.get('link'));
    }
    return items;
  }

  repoPath(suffix) {
    return `/repos/${this.repository}${suffix}`;
  }

  async listTwinIssues(label) {
    // The REST issue listing can omit issues for minutes after they change, which would let a sync
    // recreate an existing twin. The GraphQL connection reads issues consistently.
    const query = `query($owner: String!, $name: String!, $label: String!, $after: String) {
      repository(owner: $owner, name: $name) {
        issues(first: 100, after: $after, labels: [$label], states: [OPEN, CLOSED], orderBy: { field: CREATED_AT, direction: ASC }) {
          pageInfo { hasNextPage endCursor }
          nodes { number databaseId title body state stateReason url labels(first: 100) { nodes { name } } }
        }
      }
    }`;
    const [owner, name] = this.repository.split('/');
    const issues = [];
    let after = null;
    do {
      const data = await this.graphql(query, { owner, name, label, after });
      const connection = data.repository.issues;
      for (const node of connection.nodes) {
        issues.push({
          number: node.number,
          id: node.databaseId,
          title: node.title,
          body: node.body,
          state: node.state.toLowerCase(),
          state_reason: node.stateReason ? node.stateReason.toLowerCase() : null,
          html_url: node.url,
          labels: node.labels.nodes.map((labelNode) => ({ name: labelNode.name })),
        });
      }
      after = connection.pageInfo.hasNextPage ? connection.pageInfo.endCursor : null;
    } while (after);
    return issues;
  }

  async graphql(query, variables) {
    const { data } = await this.request('POST', this.graphqlUrl, { query, variables });
    if (data.errors?.length) throw new Error(`GraphQL request failed: ${data.errors.map((error) => error.message).join('; ')}`);
    return data.data;
  }

  async ensureLabel({ name, color, description }) {
    const { status } = await this.request('GET', this.repoPath(`/labels/${encodeURIComponent(name)}`), undefined, { allow: [404] });
    if (status === 404) await this.request('POST', this.repoPath('/labels'), { name, color, description });
  }

  async createIssue({ title, body, labels }) {
    return (await this.request('POST', this.repoPath('/issues'), { title, body, labels })).data;
  }

  async updateIssue(number, fields) {
    return (await this.request('PATCH', this.repoPath(`/issues/${number}`), fields)).data;
  }

  async removeLabel(number, name) {
    await this.request('DELETE', this.repoPath(`/issues/${number}/labels/${encodeURIComponent(name)}`), undefined, { allow: [404] });
  }

  async getIssueLabels(number) {
    return this.paginate(this.repoPath(`/issues/${number}/labels`));
  }

  async listIssueEvents(number) {
    return this.paginate(this.repoPath(`/issues/${number}/events`));
  }

  async listIssueComments(number) {
    return this.paginate(this.repoPath(`/issues/${number}/comments`));
  }

  async getBranchSha(branch) {
    const { status, data } = await this.request('GET', this.repoPath(`/git/ref/heads/${branch}`), undefined, { allow: [404] });
    return status === 404 ? null : data.object.sha;
  }

  async createBranch(branch, sha) {
    await this.request('POST', this.repoPath('/git/refs'), { ref: `refs/heads/${branch}`, sha });
  }

  async updateBranch(branch, sha) {
    await this.request('PATCH', this.repoPath(`/git/refs/heads/${branch}`), { sha, force: false });
  }

  async deleteBranch(branch) {
    await this.request('DELETE', this.repoPath(`/git/refs/heads/${branch}`), undefined, { allow: [404, 422] });
  }

  async aheadBy(base, head) {
    return (await this.request('GET', this.repoPath(`/compare/${base}...${head}`))).data.ahead_by;
  }

  async createEmptyCommit(parentSha, message) {
    const parent = (await this.request('GET', this.repoPath(`/git/commits/${parentSha}`))).data;
    const commit = (await this.request('POST', this.repoPath('/git/commits'), { message, tree: parent.tree.sha, parents: [parentSha] })).data;
    return commit.sha;
  }

  // Creates a branch linked to the issue's Development section, like "Create a branch" in the issue sidebar.
  async createLinkedBranch(issueNodeId, oid, name) {
    const data = await this.graphql(
      `mutation($issueId: ID!, $oid: GitObjectID!, $name: String!) {
        createLinkedBranch(input: { issueId: $issueId, oid: $oid, name: $name }) { linkedBranch { ref { name } } }
      }`,
      { issueId: issueNodeId, oid, name },
    );
    return data.createLinkedBranch?.linkedBranch?.ref?.name ?? null;
  }

  async listPullRequestsForHead(branch) {
    const owner = this.repository.split('/')[0];
    const pulls = await this.paginate(this.repoPath(`/pulls?state=all&head=${encodeURIComponent(`${owner}:${branch}`)}`));
    return pulls.sort((a, b) => Date.parse(b.created_at) - Date.parse(a.created_at));
  }

  async createPullRequest(fields) {
    const { status, data } = await this.request('POST', this.repoPath('/pulls'), fields, { allow: [422] });
    return status === 422 ? null : data;
  }

  async addAssignees(number, assignees) {
    await this.request('POST', this.repoPath(`/issues/${number}/assignees`), { assignees });
  }

  async listCheckRuns(sha, name) {
    const { data } = await this.request('GET', this.repoPath(`/commits/${sha}/check-runs?check_name=${encodeURIComponent(name)}`));
    return data.check_runs;
  }

  async createCheckRun(fields) {
    return (await this.request('POST', this.repoPath('/check-runs'), fields)).data;
  }

  async getIssue(number) {
    const issue = (await this.request('GET', this.repoPath(`/issues/${number}`))).data;
    return { ...issue, labels: issue.labels.map((label) => ({ name: typeof label === 'string' ? label : label.name })) };
  }

  async getPermission(login) {
    const { status, data } = await this.request(
      'GET',
      this.repoPath(`/collaborators/${encodeURIComponent(login)}/permission`),
      undefined,
      { allow: [404] },
    );
    return status === 404 ? { permission: 'none', role_name: 'none' } : data;
  }

  async createComment(number, body) {
    await this.request('POST', this.repoPath(`/issues/${number}/comments`), { body });
  }

  async listBlockedBy(number) {
    return this.paginate(this.repoPath(`/issues/${number}/dependencies/blocked_by`));
  }

  async addBlockedBy(number, blockerIssueId) {
    await this.request('POST', this.repoPath(`/issues/${number}/dependencies/blocked_by`), { issue_id: blockerIssueId });
  }
}
