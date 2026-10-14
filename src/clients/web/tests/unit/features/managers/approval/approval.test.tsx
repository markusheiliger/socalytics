import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { lastCharacters, matchesFingerprint, normalizeFingerprint } from '../../../../../src/features/managers/fingerprint';
import type { ClubRole } from '../../../../../src/platform/models/session';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
const ID = '00000000-0000-4000-8000-000000000071';
const pending = {
  registrationId: ID,
  version: 1,
  state: 'pending_approval',
  deviceFingerprint: 'THNN-PKJR',
  deviceMetadata: { os: 'linux', osVersion: '6.1', architecture: 'x64', managerVersion: '1.2.3', claimedKeyProtection: 'software', hostLabel: 'lab-pc' },
  pairingOrigin: { sourceAddress: '10.0.0.7', requestedAt: '2030-01-01T09:00:00Z' },
  approvalNotice: 'Approve only known machines.',
  submittedBy: '00000000-0000-4000-8000-000000000014',
  submittedAt: '2030-01-01T10:00:00Z',
  approvalExpiresAt: '2030-01-03T10:00:00Z',
};
const as = (...clubRoles: ClubRole[]) => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles })) });
const isPost = (input: unknown, init?: RequestInit) => (input instanceof Request ? input.method : init?.method) === 'POST';
const CONFIRMATION = 'Last 6 characters of the fingerprint';

afterEach(() => vi.unstubAllGlobals());

describe('fingerprint confirmation', () => {
  it('normalizes to upper-case letters and digits and takes the last 6 characters', () => {
    expect(normalizeFingerprint(' th-nn pk.jr ')).toBe('THNNPKJR');
    expect(lastCharacters('THNN-PKJR')).toBe('NNPKJR');
    expect(matchesFingerprint('nn-pk jr', 'THNN-PKJR')).toBe(true);
    expect(matchesFingerprint('', 'THNN-PKJR')).toBe(false);
    expect(matchesFingerprint('NNPKJ', 'THNN-PKJR')).toBe(false);
    expect(matchesFingerprint('THNNPKJR', 'THNN-PKJR')).toBe(false);
  });
});

describe('pending requests and decisions', () => {
  it('lists pending requests and shows the count in the navigation', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, { items: [pending] })));
    renderApp(as('ClubAdmin'), '/analyst-managers');
    expect(await screen.findByRole('link', { name: 'lab-pc – Pending approval' })).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: 'Main navigation' });
    expect(await within(nav).findByRole('link', { name: 'Analyst Managers (1 pending)' })).toBeInTheDocument();
  });

  it('hides the count when nothing is pending', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, { items: [] })));
    renderApp(as('ClubAdmin'), '/analyst-managers');
    expect(await screen.findByText('No registration requests are waiting for approval.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Analyst Managers' })).toBeInTheDocument();
  });

  it('shows every field, the warnings, and the approval notice', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, pending)));
    renderApp(as('ClubAdmin'), `/analyst-managers/${ID}`);
    expect(await screen.findByText('Device fingerprint: THNN-PKJR')).toBeInTheDocument();
    expect(screen.getByText('Host label: lab-pc')).toBeInTheDocument();
    expect(screen.getByText('Operating system: linux 6.1')).toBeInTheDocument();
    expect(screen.getByText('Architecture: x64')).toBeInTheDocument();
    expect(screen.getByText('Manager version: 1.2.3')).toBeInTheDocument();
    expect(screen.getByText('Pairing request origin: 10.0.0.7')).toBeInTheDocument();
    expect(screen.getByText('Approve only known machines.')).toBeInTheDocument();
    expect(screen.getByText('Approve only Managers you can physically identify.')).toBeInTheDocument();
    expect(screen.getByText(/claims a software-backed key/)).toBeInTheDocument();
    expect(screen.getByText(/claimed by the Manager and is not verified/)).toBeInTheDocument();
  });

  it('enables approval only for the matching last 6 characters, then approves', async () => {
    const approved = { ...pending, state: 'approved', version: 2 };
    let current: object = pending;
    vi.stubGlobal('fetch', vi.fn(async (input: unknown, init?: RequestInit) => {
      if (isPost(input, init)) current = approved;
      return json(200, current);
    }));
    const user = userEvent.setup();
    renderApp(as('ClubAdmin'), `/analyst-managers/${ID}`);
    await user.click(await screen.findByRole('button', { name: 'Approve…' }));
    const dialog = await screen.findByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: 'Approve' });
    expect(confirm).toBeDisabled();
    const field = within(dialog).getByLabelText(CONFIRMATION);
    await user.type(field, 'abcdef');
    expect(within(dialog).getByText('This does not match the fingerprint of this request.')).toBeInTheDocument();
    expect(confirm).toBeDisabled();
    await user.clear(field);
    await user.type(field, 'nn-pkjr');
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    expect(await screen.findByText(/Approved\. The request now waits/)).toBeInTheDocument();
    expect(await screen.findByText('State: Approved')).toBeInTheDocument();
  });

  it('requires a reason to reject', async () => {
    const rejected = { ...pending, state: 'rejected', version: 2, decisionReason: 'Unknown machine' };
    let current: object = pending;
    vi.stubGlobal('fetch', vi.fn(async (input: unknown, init?: RequestInit) => {
      if (isPost(input, init)) current = rejected;
      return json(200, current);
    }));
    const user = userEvent.setup();
    renderApp(as('ClubAdmin'), `/analyst-managers/${ID}`);
    await user.click(await screen.findByRole('button', { name: 'Reject…' }));
    const dialog = await screen.findByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: 'Reject' });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText(/Reason/), '   ');
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText(/Reason/), 'Unknown machine');
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    expect(await screen.findByText('The request was rejected.')).toBeInTheDocument();
    expect(await screen.findByText('Reason: Unknown machine')).toBeInTheDocument();
  });

  it('refuses reasons over 500 characters', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json(200, pending)));
    const user = userEvent.setup();
    renderApp(as('ClubAdmin'), `/analyst-managers/${ID}`);
    await user.click(await screen.findByRole('button', { name: 'Reject…' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByLabelText(/Reason/));
    await user.paste('x'.repeat(501));
    expect(within(dialog).getByRole('button', { name: 'Reject' })).toBeDisabled();
    expect(within(dialog).getByText('The reason may have at most 500 characters.')).toBeInTheDocument();
  });

  it('shows the refusal and the current state when the request was decided meanwhile', async () => {
    let decided = false;
    vi.stubGlobal('fetch', vi.fn(async (input: unknown, init?: RequestInit) => {
      if (isPost(input, init)) {
        decided = true;
        return json(409, { status: 409, code: 'invalid-state-transition' });
      }
      return json(200, decided ? { ...pending, state: 'rejected', version: 2 } : pending);
    }));
    const user = userEvent.setup();
    renderApp(as('ClubAdmin'), `/analyst-managers/${ID}`);
    await user.click(await screen.findByRole('button', { name: 'Approve…' }));
    await user.type(await screen.findByLabelText(CONFIRMATION), 'NNPKJR');
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Approve' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('can no longer be decided');
    await waitFor(() => expect(screen.getByText('State: Rejected')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Approve…' })).not.toBeInTheDocument();
  });

  it('keeps the fleet routes away from members without the Club Admin role', async () => {
    vi.stubGlobal('fetch', vi.fn());
    renderApp(as('Registrar'), '/analyst-managers');
    expect(await screen.findByRole('alert')).toHaveTextContent('not permitted');
  });
});
