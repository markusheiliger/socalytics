import { screen, within } from '@testing-library/react';
import { vi } from 'vitest';
import type { ClubRole } from '../../../../../src/platform/models/session';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
const rejected = {
  registrationId: '00000000-0000-4000-8000-000000000051',
  version: 3,
  state: 'rejected',
  deviceFingerprint: 'ABCD-EF12-3456',
  deviceMetadata: { os: 'linux', claimedKeyProtection: 'software', hostLabel: 'lab-pc' },
  submittedAt: '2030-01-01T10:00:00Z',
  decidedAt: '2030-01-01T11:00:00Z',
  decisionReason: 'Unknown machine',
};
const pending = {
  registrationId: '00000000-0000-4000-8000-000000000052',
  version: 1,
  state: 'pending_approval',
  deviceFingerprint: 'FFFF-0000-1111',
  deviceMetadata: { claimedKeyProtection: 'tpm', hostLabel: 'office-pc' },
  submittedAt: '2030-01-02T10:00:00Z',
  approvalExpiresAt: '2030-01-03T10:00:00Z',
};
const as = (...clubRoles: ClubRole[]) => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles })) });

afterEach(() => vi.unstubAllGlobals());

describe('my registration requests', () => {
  it('lists the submitted requests without operating state or connectivity', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, { items: [rejected, pending] })));
    renderApp(as('Registrar'), '/analyst-managers/requests');
    const list = await screen.findByRole('list', { name: 'My registration requests' });
    expect(within(list).getByRole('link', { name: 'lab-pc – Rejected' })).toBeInTheDocument();
    expect(within(list).getByRole('link', { name: 'office-pc – Pending approval' })).toBeInTheDocument();
    expect(within(list).getByText('Reason: Unknown machine')).toBeInTheDocument();
    expect(within(list).getByText('Device fingerprint: ABCD-EF12-3456')).toBeInTheDocument();
    expect(within(list).getByText(/Can be approved until/)).toBeInTheDocument();
    expect(screen.queryByText(/online|offline|operating state/i)).not.toBeInTheDocument();
  });

  it('shows an empty message', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, { items: [] })));
    renderApp(as('Registrar'), '/analyst-managers/requests');
    expect(await screen.findByText('You have not submitted any registration requests.')).toBeInTheDocument();
  });

  it('opens one request read-only', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, rejected)));
    renderApp(as('Registrar'), `/analyst-managers/requests/${rejected.registrationId}`);
    expect(await screen.findByText('Reason: Unknown machine')).toBeInTheDocument();
    expect(screen.getByText('State: Rejected')).toBeInTheDocument();
    expect(screen.getByText('Host label: lab-pc')).toBeInTheDocument();
  });

  it("shows not found for another member's request", async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(404, { code: 'not-found' })));
    renderApp(as('Registrar'), `/analyst-managers/requests/${rejected.registrationId}`);
    expect(await screen.findByRole('alert')).toHaveTextContent('This registration request was not found.');
  });

  it('redirects a Club Admin to the fleet route', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const { router } = renderApp(as('ClubAdmin', 'Registrar'), '/analyst-managers/requests');
    await vi.waitFor(() => expect(router.state.location.pathname).toBe('/analyst-managers'));
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('shows the not-permitted view without a Registrar or Club Admin role', async () => {
    renderApp(as(), '/analyst-managers/requests');
    expect(await screen.findByRole('alert')).toHaveTextContent('not permitted');
  });

  it('offers the entry in navigation to a Registrar without Club Admin only', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, { items: [] })));
    renderApp(as('Registrar'), '/');
    const nav = await screen.findByRole('navigation', { name: 'Main navigation' });
    expect(within(nav).getAllByRole('link').map((a) => a.textContent)).toEqual([
      'Home',
      'My registration requests',
      'Pair a Manager',
      'Account',
    ]);
  });
});
