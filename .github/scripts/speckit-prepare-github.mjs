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
  constructor({ token, repository, apiUrl = 'https://api.github.com', fetchImpl = fetch, wait = sleep }) {
    if (!token) throw new Error('A GitHub token is required (GITHUB_TOKEN or GH_TOKEN).');
    if (!/^[^/\s]+\/[^/\s]+$/.test(repository ?? '')) {
      throw new Error('GITHUB_REPOSITORY must have the form owner/repo.');
    }
    this.token = token;
    this.repository = repository;
    this.apiUrl = apiUrl.replace(/\/$/, '');
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
    const issues = await this.paginate(this.repoPath(`/issues?state=all&labels=${encodeURIComponent(label)}`));
    return issues.filter((issue) => !issue.pull_request);
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
