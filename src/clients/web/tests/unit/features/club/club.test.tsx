import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const club = (over = {}) => ({ id: '00000000-0000-4000-8000-000000000001', displayName: 'Club', version: 1, ...over });

const admin = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })) });

afterEach(() => vi.unstubAllGlobals());

describe('club view', () => {
  it('saves the club details with the loaded version and shows them', async () => {
    const fetchMock = vi.fn(async (_url: string, init?: RequestInit) =>
      init?.method === 'PUT' ? json(200, club({ displayName: 'Renamed', version: 2 })) : json(200, club()),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderApp(admin(), '/club');
    const input = await screen.findByLabelText(/club name/i);
    await userEvent.clear(input);
    await userEvent.type(input, 'Renamed');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await screen.findByText(/club details were saved/i);
    const put = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')!;
    expect(new Headers(put[1]!.headers as HeadersInit).get('If-Match')).toBe('"1"');
    expect(screen.getByLabelText(/club name/i)).toHaveValue('Renamed');
  });

  it('shows the platform reason next to the field', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) =>
        init?.method === 'PUT'
          ? json(400, { status: 400, code: 'validation-failed', errors: [{ field: 'displayName', code: 'required' }] })
          : json(200, club()),
      ),
    );
    renderApp(admin(), '/club');
    const input = await screen.findByLabelText(/club name/i);
    await userEvent.clear(input);
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('This field is required.')).toBeInTheDocument();
    expect(input).toBeInvalid();
  });

  it('offers to reload after a version conflict and reloads the current version', async () => {
    let name = 'Club';
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) =>
        init?.method === 'PUT' ? json(412, { status: 412, code: 'version-conflict' }) : json(200, club({ displayName: name, version: 5 })),
      ),
    );
    renderApp(admin(), '/club');
    await userEvent.type(await screen.findByLabelText(/club name/i), ' B');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/changed by someone else/i);
    name = 'Theirs';
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }));
    expect(await screen.findByDisplayValue('Theirs')).toBeInTheDocument();
  });

  it('is not available to a Coach', async () => {
    renderApp(fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [{ teamId: 't', teamName: 'T', seasonId: 's', role: 'Coach' }] })) }), '/club');
    expect(await screen.findByText(/not permitted/i)).toBeInTheDocument();
  });
});
