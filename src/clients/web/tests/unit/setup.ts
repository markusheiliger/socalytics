import '@testing-library/jest-dom/vitest';

// Unmocked platform calls (for example the account view's email query) answer 404 instead of failing the network.
beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify({ status: 404 }), { status: 404, headers: { 'Content-Type': 'application/problem+json' } })),
  );
});
