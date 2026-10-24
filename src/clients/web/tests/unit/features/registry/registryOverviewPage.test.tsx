import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { vi } from 'vitest';
import '../../../../src/i18n';
import { RegistryOverviewPage } from '../../../../src/features/registry/RegistryOverviewPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import { ADMIN, defaultWorkflow, fakeRegistryAdapter, type FakeRegistryAdapter } from './fixtures';

let adapter: FakeRegistryAdapter;

vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));
vi.mock('../../../../src/platform/adapters/registryAdapter', () => ({ createRegistryAdapter: () => adapter }));
vi.mock('../../../../src/features/registry/memberNames', () => ({
  loadMemberResolver: async () => (id: string | undefined) =>
    id === ADMIN.id ? { id, displayName: 'ada', kind: 'member' } : { id: id ?? '', kind: id ? 'unknown' : 'seed' },
}));

function renderPage() {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter>
        <RegistryOverviewPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('RegistryOverviewPage', () => {
  it('shows the default workflow with its definition link and the list links', async () => {
    adapter = fakeRegistryAdapter();
    renderPage();
    const link = await screen.findByRole('link', { name: 'standard-analysis version 1.0.0' });
    expect(link).toHaveAttribute('href', '/registry/workflows/standard-analysis/versions/1.0.0');
    expect(screen.getByText(`Changed ${defaultWorkflow.changedAt.toLocaleString()} by ada`)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Analyst images' })).toHaveAttribute('href', '/registry/images');
    expect(screen.getByRole('link', { name: 'Analyst profiles' })).toHaveAttribute('href', '/registry/profiles');
    expect(screen.getByRole('link', { name: 'Workflows' })).toHaveAttribute('href', '/registry/workflows');
  });

  it('says that no default is set', async () => {
    adapter = fakeRegistryAdapter({ getDefault: vi.fn(async () => null) });
    renderPage();
    expect(await screen.findByText('No default workflow is set; analysis runs cannot be created until one is.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Workflows' })).toBeInTheDocument();
  });

  it('shows an error and reloads on request', async () => {
    const getDefault = vi.fn<() => Promise<typeof defaultWorkflow | null>>();
    getDefault.mockRejectedValueOnce(new PlatformFailure('unreachable'));
    getDefault.mockResolvedValue(defaultWorkflow);
    adapter = fakeRegistryAdapter({ getDefault });
    renderPage();
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }));
    expect(await screen.findByRole('link', { name: 'standard-analysis version 1.0.0' })).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
