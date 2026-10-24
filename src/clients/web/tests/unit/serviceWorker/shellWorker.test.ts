import { describe, expect, it, vi } from 'vitest';
import { activateShell, classifyRequest, installShell, respondFromShell, shellCacheName, type ShellCaches } from '../../../src/serviceWorker/shellWorker';

const origin = 'http://app.test';
const version = '2026.1015.194113';
const manifest = {
  version,
  files: [
    { url: '/index.html', integrity: 'sha384-aaa' },
    { url: '/assets/app.js', integrity: 'sha384-bbb' },
  ],
};

function fakeCaches(names: string[] = []) {
  const addAll = vi.fn(async (_requests: Request[]) => undefined);
  const deleted: string[] = [];
  const caches: ShellCaches = {
    open: async () => ({ addAll }),
    keys: async () => names,
    delete: async (name) => {
      deleted.push(name);
      return true;
    },
    match: vi.fn(async () => undefined),
  };
  return { caches, addAll, deleted };
}

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

describe('install', () => {
  it('caches every listed file with its integrity value', async () => {
    const { caches, addAll } = fakeCaches();
    await installShell(version, caches, async () => json(manifest), origin);
    const requests = addAll.mock.calls[0][0];
    expect(requests.map((r) => new URL(r.url).pathname)).toEqual(['/index.html', '/assets/app.js']);
    expect(requests.map((r) => r.integrity)).toEqual(['sha384-aaa', 'sha384-bbb']);
  });

  it('fails when a file cannot be cached and removes the partial cache', async () => {
    const { caches, addAll, deleted } = fakeCaches();
    addAll.mockRejectedValue(new TypeError('integrity mismatch'));
    await expect(installShell(version, caches, async () => json(manifest), origin)).rejects.toThrow('integrity mismatch');
    expect(deleted).toEqual([shellCacheName(version)]);
  });

  it('fails for a missing, malformed, or foreign manifest', async () => {
    const { caches } = fakeCaches();
    await expect(installShell(version, caches, async () => json({}, 404))).rejects.toThrow();
    await expect(installShell(version, caches, async () => json({ version, files: [{ url: 'x', integrity: 'y' }] }))).rejects.toThrow(/malformed/);
    await expect(installShell(version, caches, async () => json({ ...manifest, version: '2026.1015.194114' }))).rejects.toThrow(/different release/);
  });
});

describe('activate', () => {
  it('deletes the other shell caches only', async () => {
    const { caches, deleted } = fakeCaches([shellCacheName(version), 'socalytics-shell-2025.0101.000000', 'other']);
    await activateShell(version, caches);
    expect(deleted).toEqual(['socalytics-shell-2025.0101.000000']);
  });
});

describe('fetch routing', () => {
  const classify = (url: string, mode = 'cors', method = 'GET') => classifyRequest({ method, mode, url: `${origin}${url}` }, origin);

  it('handles navigations and same-origin assets', () => {
    expect(classify('/matches/1', 'navigate')).toBe('navigation');
    expect(classify('/assets/app.js')).toBe('asset');
  });

  it('does not handle API, health, other origins, or non-GET requests', () => {
    expect(classify('/api/v1/clubs')).toBeUndefined();
    expect(classify('/alive')).toBeUndefined();
    expect(classify('/health')).toBeUndefined();
    expect(classify('/assets/app.js', 'cors', 'POST')).toBeUndefined();
    expect(classifyRequest({ method: 'GET', mode: 'cors', url: 'http://other.test/assets/app.js' }, origin)).toBeUndefined();
  });

  it('leaves the analysis run, fact stream, and fact reads unhandled for every method', () => {
    const runId = '0b0c5f4e-6d3a-4a53-9a0e-6f3c2a1d9b11';
    const urls = [
      `/api/v1/analysis/runs/${runId}`,
      `/api/v1/analysis/runs/${runId}/fact-streams`,
      `/api/v1/analysis/runs/${runId}/facts`,
      `/api/v1/analysis/runs/${runId}/facts?stream=lineups&limit=50`,
    ];
    for (const url of urls) {
      for (const method of ['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS']) {
        expect(classify(url, 'cors', method), `${method} ${url}`).toBeUndefined();
        expect(classify(url, 'navigate', method), `${method} ${url} (navigate)`).toBeUndefined();
      }
    }
  });

  it('passes registry API requests to the network and never caches them', () => {
    const registryPaths = [
      '/api/v1/registry/analyst-images',
      '/api/v1/registry/analyst-images?pageSize=20',
      '/api/v1/registry/analyst-profiles/2f6c1c52-0d0b-4a53-9a3f-5c1f3c1c8e11',
      '/api/v1/registry/workflows/2f6c1c52-0d0b-4a53-9a3f-5c1f3c1c8e11',
      '/api/v1/registry/default-workflow',
    ];
    for (const path of registryPaths) {
      expect(classify(path)).toBeUndefined();
      expect(classify(path, 'navigate')).toBeUndefined();
    }
  });

  it('passes workflow operations API requests to the network and never caches them', () => {
    const id = '5d1f0c0e-7b1a-4c35-9c55-2a6f7d4e8b21';
    const paths = [
      '/api/v1/operations/analysis/summary',
      '/api/v1/operations/analysis/summary?windowHours=24',
      '/api/v1/operations/outbox-messages',
      '/api/v1/operations/outbox-messages?status=failed&pageSize=20',
      `/api/v1/operations/outbox-messages/${id}`,
      `/api/v1/operations/outbox-messages/${id}/requeue`,
      '/api/v1/operations/analysis/notification-receipts',
      `/api/v1/operations/analysis/notification-receipts/${id}`,
      '/api/v1/operations/analysis/notification-receipts?pageSize=20',
      '/api/v1/operations/analysis/reconciliations',
    ];
    for (const path of paths) {
      for (const method of ['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE']) {
        expect(classify(path, 'cors', method), `${method} ${path}`).toBeUndefined();
      }
      expect(classify(path, 'navigate')).toBeUndefined();
    }
  });

  it('does not handle triage conversation API paths for any method', () => {
    const paths = [
      '/api/v1/agent-catalog/agents',
      '/api/v1/matches/3f2b8c1e-0000-4000-8000-000000000001/agent-conversations',
      '/api/v1/agent-conversations/3f2b8c1e-0000-4000-8000-000000000002',
      '/api/v1/agent-conversations/3f2b8c1e-0000-4000-8000-000000000002/participants',
      '/api/v1/agent-conversations/3f2b8c1e-0000-4000-8000-000000000002/invocations',
      '/api/v1/agent-conversations/3f2b8c1e-0000-4000-8000-000000000002/turns',
      '/api/v1/agent-invocations/3f2b8c1e-0000-4000-8000-000000000003',
      '/api/v1/agent-invocations/3f2b8c1e-0000-4000-8000-000000000003/lineage',
    ];
    for (const path of paths) {
      for (const method of ['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE']) {
        expect(classify(path, 'cors', method), `${method} ${path}`).toBeUndefined();
      }
    }
  });

  it('does not handle Agent Catalog API paths for any method', () => {
    const id = '4a7c3b2e-0000-4000-8000-000000000004';
    const paths = [
      '/api/v1/agent-catalog/baseline',
      '/api/v1/agent-catalog/agents',
      '/api/v1/agent-catalog/agents?pageSize=20',
      '/api/v1/agent-catalog/agent-versions',
      '/api/v1/agent-catalog/agent-versions/specialist-agent/versions/1.0.0',
      '/api/v1/agent-catalog/agent-versions/specialist-agent/versions/1.0.0/approval',
      '/api/v1/agent-catalog/agent-versions/specialist-agent/versions/1.0.0/retirement',
      '/api/v1/agent-catalog/artifacts/prompt',
      `/api/v1/agent-catalog/artifacts/prompt/${id}/versions/1.0.0`,
      '/api/v1/agent-catalog/enablements',
      '/api/v1/agent-catalog/enablements/specialist-agent',
    ];
    for (const path of paths) {
      for (const method of ['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS']) {
        expect(classify(path, 'cors', method), `${method} ${path}`).toBeUndefined();
        expect(classify(path, 'navigate', method), `${method} ${path} (navigate)`).toBeUndefined();
      }
    }
  });

  it('serves navigations from the cached index.html and falls back to the network', async () => {
    const { caches } = fakeCaches();
    const match = vi.mocked(caches.match);
    const request = new Request(`${origin}/matches/1`);
    match.mockResolvedValueOnce(new Response('shell'));
    const fetcher = vi.fn(async () => new Response('network'));
    expect(await (await respondFromShell(request, 'navigation', version, caches, fetcher)).text()).toBe('shell');
    expect(match).toHaveBeenCalledWith('/index.html', { cacheName: shellCacheName(version), ignoreVary: true });
    expect(fetcher).not.toHaveBeenCalled();

    expect(await (await respondFromShell(request, 'navigation', version, caches, fetcher)).text()).toBe('network');
  });

  it('serves listed assets cache first', async () => {
    const { caches } = fakeCaches();
    const request = new Request(`${origin}/assets/app.js`);
    vi.mocked(caches.match).mockResolvedValueOnce(new Response('cached'));
    const fetcher = vi.fn(async () => new Response('network'));
    expect(await (await respondFromShell(request, 'asset', version, caches, fetcher)).text()).toBe('cached');
    expect(vi.mocked(caches.match)).toHaveBeenCalledWith(request, { cacheName: shellCacheName(version), ignoreVary: true });
  });
});
