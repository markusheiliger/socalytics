import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { arrangeFleet, fleetQueryState } from '../../../../../src/features/managers/fleetFilter';
import { matchesRevocation } from '../../../../../src/features/managers/fingerprint';
import { toRegistration } from '../../../../../src/platform/adapters/registrationAdapter';
import type { ClubRole } from '../../../../../src/platform/models/session';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
const id = (n: number) => `00000000-0000-4000-8000-0000000001${String(n).padStart(2, '0')}`;
const dto = (n: number, state: string, over: Record<string, unknown> = {}) => ({
  registrationId: id(n),
  version: 1,
  state,
  deviceFingerprint: 'THNN-PKJR',
  deviceMetadata: {
    managerVersion: '1.2.3',
    claimedKeyProtection: 'tpm',
    hostLabel: `host-${n}`,
    ...(n === 1 ? { claimedApplicationProfile: 'headless-server' } : {}),
  },
  createdAt: `2030-01-0${n}T09:00:00Z`,
  ...over,
});
const reg = (n: number, state: string, over: Record<string, unknown> = {}) =>
  toRegistration({ ...dto(n, state, over), createdAt: new Date(`2030-01-0${n}T09:00:00Z`) } as never);
const SELF = id(99);
const as = (...clubRoles: ClubRole[]) => {
  const session = makeSession({ clubRoles });
  return fakeAdapter({ restore: vi.fn().mockResolvedValue({ ...session, member: { ...session.member, id: SELF } }) });
};
const isPost = (input: unknown, init?: RequestInit) => (input instanceof Request ? input.method : init?.method) === 'POST';
const urlOf = (input: unknown) => (input instanceof Request ? input.url : String(input));

afterEach(() => vi.unstubAllGlobals());

describe('fleet filter and ordering', () => {
  const all = [
    reg(1, 'active', { deviceMetadata: { claimedKeyProtection: 'tpm', hostLabel: 'beta' } }),
    reg(2, 'revoked'),
    reg(3, 'pending_approval', { deviceMetadata: { claimedKeyProtection: 'tpm', hostLabel: 'zeta' } }),
    reg(4, 'active', { deviceMetadata: { claimedKeyProtection: 'tpm' } }),
    reg(5, 'active', { deviceMetadata: { claimedKeyProtection: 'tpm', hostLabel: 'Alpha' } }),
    reg(6, 'something-new'),
  ];
  it('shows current registrations by default, pending first, then label case-insensitively with missing labels last', () => {
    expect(arrangeFleet(all, 'current').map((r) => r.registrationId)).toEqual([id(3), id(5), id(1), id(4)]);
  });
  it('filters ended, one state, and all', () => {
    expect(arrangeFleet(all, 'ended').map((r) => r.registrationId)).toEqual([id(2)]);
    expect(arrangeFleet(all, 'active')).toHaveLength(3);
    expect(arrangeFleet(all, 'all')).toHaveLength(6);
  });
  it('uses the API state filter only for one state', () => {
    expect(fleetQueryState('current')).toBeUndefined();
    expect(fleetQueryState('ended')).toBeUndefined();
    expect(fleetQueryState('all')).toBeUndefined();
    expect(fleetQueryState('active')).toBe('active');
  });
  it('orders equal labels by creation time', () => {
    const a = reg(2, 'active', { deviceMetadata: { claimedKeyProtection: 'tpm', hostLabel: 'x' } });
    const b = reg(1, 'active', { deviceMetadata: { claimedKeyProtection: 'tpm', hostLabel: 'X' } });
    expect(arrangeFleet([a, b], 'all').map((r) => r.registrationId)).toEqual([id(1), id(2)]);
  });
});

describe('revocation confirmation', () => {
  it('compares the host label trimmed and exact, or the last 6 fingerprint characters without a label', () => {
    expect(matchesRevocation(' lab-pc ', 'lab-pc', 'THNN-PKJR')).toBe(true);
    expect(matchesRevocation('LAB-PC', 'lab-pc', 'THNN-PKJR')).toBe(false);
    expect(matchesRevocation('nn-pk jr', undefined, 'THNN-PKJR')).toBe(true);
    expect(matchesRevocation('', undefined, 'THNN-PKJR')).toBe(false);
  });
});

describe('fleet page', () => {
  const status = {
    operatingState: 'paused',
    pausedUntil: '2030-01-02T15:30:00Z',
    activeAnalysts: 1,
    maxAnalysts: 4,
    managerVersion: '2.0.0',
    reportedAt: '2030-01-02T10:00:00Z',
  };
  const items = [
    dto(1, 'active', { status, connectivity: 'online', submittedBy: SELF }),
    dto(2, 'active', { connectivity: 'offline' }),
    dto(3, 'pending_approval', { approvalExpiresAt: '2030-01-04T10:00:00Z' }),
    dto(4, 'revoked'),
    dto(5, 'active', {
      deviceMetadata: { claimedKeyProtection: 'quantum', hostLabel: 'h'.repeat(80) },
      status: { operatingState: 'levitating' },
      connectivity: 'teleporting',
    }),
  ];
  const serve = () =>
    vi.stubGlobal('fetch', vi.fn(async (input: unknown) => {
      const state = new URL(urlOf(input)).searchParams.get('state');
      return json(200, { items: items.filter((i) => !state || i.state === state) });
    }));

  it('lists the current fleet with pending first and every field', async () => {
    serve();
    renderApp(as('ClubAdmin'), '/analyst-managers');
    const list = await screen.findByRole('list', { name: 'Analyst Managers' });
    const entries = within(list).getAllByRole('link').map((l) => l.textContent);
    expect(entries[0]).toBe('host-3 – Pending approval');
    expect(entries).not.toContain('host-4 – Revoked');
    expect(within(list).getByText('Connectivity: Online (last report: ' + new Date('2030-01-02T10:00:00Z').toLocaleString() + ')')).toBeInTheDocument();
    expect(within(list).getByText('Operating state: Paused until ' + new Date('2030-01-02T15:30:00Z').toLocaleString())).toBeInTheDocument();
    expect(within(list).getByText('Analysts: 1 of 4')).toBeInTheDocument();
    expect(within(list).getByText('Version: 2.0.0')).toBeInTheDocument();
    expect(within(list).getByText('Submitted by you')).toBeInTheDocument();
    expect(within(list).getByText('Connectivity: Offline (no report yet)')).toBeInTheDocument();
    expect(within(list).getAllByText('Claimed key protection: TPM').length).toBeGreaterThan(0);
    expect(within(list).getAllByText('Profile (claimed): Headless server').length).toBe(1);
    expect(within(list).getAllByText('Profile (claimed): Not reported').length).toBeGreaterThan(0);
  });

  it('shows unknown values and shortens long labels without failing', async () => {
    serve();
    renderApp(as('ClubAdmin'), '/analyst-managers');
    expect(await screen.findByText('Operating state: Unknown')).toBeInTheDocument();
    expect(screen.getByText('Connectivity: Unknown (no report yet)')).toBeInTheDocument();
    expect(screen.getByText('Claimed key protection: Unknown')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /^h{39}… – Active$/ })).toBeInTheDocument();
  });

  it('offers no host-local actions', async () => {
    serve();
    renderApp(as('ClubAdmin'), '/analyst-managers');
    await screen.findByRole('list', { name: 'Analyst Managers' });
    for (const name of [/pause/i, /resume/i, /exit/i, /unregister/i, /autostart/i, /update/i]) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
  });

  it('filters ended and one state', async () => {
    serve();
    const user = userEvent.setup();
    renderApp(as('ClubAdmin'), '/analyst-managers');
    await screen.findByRole('list', { name: 'Analyst Managers' });
    await user.click(screen.getByRole('combobox', { name: 'Show' }));
    await user.click(await screen.findByRole('option', { name: 'Ended' }));
    const list = await screen.findByRole('list', { name: 'Analyst Managers' });
    await waitFor(() => expect(within(list).getAllByRole('link').map((l) => l.textContent)).toEqual(['host-4 – Revoked']));
    await user.click(screen.getByRole('combobox', { name: 'Show' }));
    await user.click(await screen.findByRole('option', { name: 'Pending approval' }));
    await waitFor(() => expect(within(screen.getByRole('list', { name: 'Analyst Managers' })).getAllByRole('link')).toHaveLength(1));
  });
});

describe('revocation', () => {
  const active = dto(7, 'active', { status: { operatingState: 'running', activeAnalysts: 2, maxAnalysts: 4 }, connectivity: 'online' });
  const revoked = { ...active, state: 'revoked', version: 2, connectivity: 'not-applicable', endReason: 'Lost', endedAt: '2030-01-03T10:00:00Z' };
  const open = async (user: ReturnType<typeof userEvent.setup>) => {
    renderApp(as('ClubAdmin'), `/analyst-managers/${id(7)}`);
    await user.click(await screen.findByRole('button', { name: 'Revoke…' }));
    return screen.getByRole('dialog');
  };

  it('requires the reason and the typed host label, shows the consequences, and revokes', async () => {
    let current: object = active;
    const fetchMock = vi.fn(async (input: unknown, init?: RequestInit) => {
      if (isPost(input, init)) current = revoked;
      return json(200, current);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    const dialog = await open(user);
    expect(within(dialog).getByText('Host label: host-7')).toBeInTheDocument();
    expect(within(dialog).getByText('Active Analysts: 2')).toBeInTheDocument();
    expect(within(dialog).getByText(/cannot be undone.*retried by another Manager.*registered again/)).toBeInTheDocument();
    const confirm = within(dialog).getByRole('button', { name: 'Revoke' });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText(/^Reason/), 'Lost');
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText('Type the host label to confirm'), 'host-8');
    expect(confirm).toBeDisabled();
    await user.clear(within(dialog).getByLabelText('Type the host label to confirm'));
    await user.type(within(dialog).getByLabelText('Type the host label to confirm'), 'host-7');
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    expect(await screen.findByText('State: Revoked')).toBeInTheDocument();
    expect(screen.getByText('The registration was revoked.')).toBeInTheDocument();
    expect(screen.queryByText(/^Connectivity:/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Revoke…' })).not.toBeInTheDocument();
  });

  it('asks for the last 6 fingerprint characters without a host label', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, { ...active, deviceMetadata: { claimedKeyProtection: 'tpm' } })));
    const user = userEvent.setup();
    const dialog = await open(user);
    expect(within(dialog).getByText('This Manager has no host label.')).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText(/^Reason/), 'Lost');
    await user.type(within(dialog).getByLabelText('Type the last 6 characters of the fingerprint to confirm'), 'nn-pkjr');
    expect(within(dialog).getByRole('button', { name: 'Revoke' })).toBeEnabled();
  });

  it('shows the platform refusal when the registration is no longer active', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: unknown, init?: RequestInit) =>
      isPost(input, init) ? json(409, { status: 409, code: 'invalid-state-transition' }) : json(200, active)));
    const user = userEvent.setup();
    const dialog = await open(user);
    await user.type(within(dialog).getByLabelText(/^Reason/), 'Lost');
    await user.type(within(dialog).getByLabelText('Type the host label to confirm'), 'host-7');
    await user.click(within(dialog).getByRole('button', { name: 'Revoke' }));
    expect(await screen.findByText(/no longer active and cannot be revoked/)).toBeInTheDocument();
  });
});
