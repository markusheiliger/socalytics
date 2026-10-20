import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { vi } from 'vitest';
import { ImagePage } from '../../../../src/features/registry/ImagePage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { ADMIN, MATCH_DIGEST, SEGMENT_DIGEST, fakeRegistryAdapter, segmentDeclaration, type FakeRegistryAdapter } from './fixtures';
import '../../../../src/i18n';

const state = vi.hoisted(() => ({ adapter: undefined as unknown }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => state.adapter }));
vi.mock('../../../../src/platform/adapters/memberAdapter', () => ({
  createMemberAdapter: () => ({ list: async () => [{ id: '00000000-0000-4000-8000-000000000021', accountName: 'ada' }] }),
}));
vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));

function renderPage(adapter: FakeRegistryAdapter, digest: string) {
  state.adapter = adapter;
  const router = createMemoryRouter([{ path: '/registry/images/:digest', element: <ImagePage /> }], {
    initialEntries: [`/registry/images/${encodeURIComponent(digest)}`],
  });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('ImagePage', () => {
  it('shows every declared field, with names for members', async () => {
    renderPage(fakeRegistryAdapter(), MATCH_DIGEST);
    expect(await screen.findByText('registry.example/analysts/possession')).toBeInTheDocument();
    expect(screen.getByText('possession')).toBeInTheDocument();
    expect(screen.getByText('2.1.0')).toBeInTheDocument();
    expect(screen.getByText('High level')).toBeInTheDocument();
    expect(screen.getByText('Match')).toBeInTheDocument();
    expect(screen.getByText(/ball-tracking 1\.0\.0 \(schema 1\.0\.0\) – Required/)).toBeInTheDocument();
    expect(screen.getByText('Forbidden')).toBeInTheDocument();
    expect(screen.getByText('standard')).toBeInTheDocument();
    expect(screen.getByText('linux/amd64')).toBeInTheDocument();
    expect(screen.getByText('cpu: 2')).toBeInTheDocument();
    expect(screen.getByText('600')).toBeInTheDocument();
    expect(await screen.findByText(ADMIN.displayName!)).toBeInTheDocument();
    expect(screen.getAllByText('yes')).toHaveLength(2);
  });

  it('shows "not declared" for missing manifest fields and "none" for empty lists', async () => {
    const sparse = { ...segmentDeclaration, upstream: undefined, videoAccess: undefined, outputSchemaVersion: undefined, compatibleProfiles: undefined, runtime: undefined, resources: undefined, requirements: undefined, determinism: undefined, manifestText: undefined };
    renderPage(fakeRegistryAdapter({ getImage: vi.fn(async () => sparse) }), SEGMENT_DIGEST);
    await screen.findByText('registry.example/analysts/ball-tracking');
    expect(screen.getAllByText('not declared').length).toBeGreaterThanOrEqual(12);
    await userEvent.click(screen.getByRole('button', { name: 'Stored manifest' }));
    expect(screen.getAllByText('not declared').length).toBeGreaterThanOrEqual(13);
  });

  it('shows unknown values raw', async () => {
    const odd = { ...segmentDeclaration, capability: { ...segmentDeclaration.capability, tier: 'mid-level', executionScope: 'season' }, videoAccess: 'sometimes' };
    renderPage(fakeRegistryAdapter({ getImage: vi.fn(async () => odd) }), SEGMENT_DIGEST);
    expect(await screen.findByText('mid-level')).toBeInTheDocument();
    expect(screen.getByText('season')).toBeInTheDocument();
    expect(screen.getByText('sometimes')).toBeInTheDocument();
  });

  it('shows the full manifest as text on request', async () => {
    renderPage(fakeRegistryAdapter(), SEGMENT_DIGEST);
    await screen.findByText('registry.example/analysts/ball-tracking');
    expect(screen.queryByText(/manifestVersion/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Stored manifest' }));
    expect(screen.getByText(/"manifestVersion": "1.0.0"/)).toBeInTheDocument();
  });

  it('renders markup in platform text as text', async () => {
    const hostile = { ...segmentDeclaration, repository: '<img src=x onerror=alert(1)>' };
    renderPage(fakeRegistryAdapter({ getImage: vi.fn(async () => hostile) }), SEGMENT_DIGEST);
    expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument();
    expect(document.querySelector('img')).toBeNull();
  });

  it('shows not-found for a 404', async () => {
    const getImage = vi.fn(async () => { throw new PlatformFailure('unexpected', { status: 404 }); });
    renderPage(fakeRegistryAdapter({ getImage }), MATCH_DIGEST);
    expect(await screen.findByText('This Analyst image was not found.')).toBeInTheDocument();
  });

  it('shows the not-permitted view for a 403', async () => {
    const getImage = vi.fn(async () => { throw new PlatformFailure('notPermitted', { status: 403 }); });
    renderPage(fakeRegistryAdapter({ getImage }), MATCH_DIGEST);
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
  });
});
