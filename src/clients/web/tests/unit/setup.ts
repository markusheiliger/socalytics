import '@testing-library/jest-dom/vitest';

// Unmocked platform calls (for example the account view's email query) answer 404 instead of failing the network.
// Node-environment tests keep the real fetch so they can reach local servers.
beforeEach(() => {
  if (typeof window === 'undefined') return;
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify({ status: 404 }), { status: 404, headers: { 'Content-Type': 'application/problem+json' } })),
  );
});
