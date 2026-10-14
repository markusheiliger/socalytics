import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const ID = '00000000-0000-4000-8000-000000000002';
const season = (over = {}) => ({
  id: ID,
  name: '2026/27',
  state: 'draft',
  createdAt: '2026-10-12T12:00:00Z',
  activatedAt: null,
  archivedAt: null,
  version: 1,
  ...over,
});
const admin = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: ['ClubAdmin'] })) });

afterEach(() => vi.unstubAllGlobals());

describe('seasons views', () => {
  it('lists seasons and creates a draft season', async () => {
    const seasons = [season({ name: 'Old', state: 'archived', id: '00000000-0000-4000-8000-000000000009' })];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          seasons.push(season({ name: 'New' }));
          return json(201, season({ name: 'New' }));
        }
        return json(200, { items: seasons, continuationToken: null });
      }),
    );
    renderApp(admin(), '/seasons');
    expect(await screen.findByRole('link', { name: 'Old' })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/season name/i), 'New');
    await userEvent.click(screen.getByRole('button', { name: 'Create season' }));
    expect(await screen.findByRole('link', { name: 'New' })).toBeInTheDocument();
    expect(screen.getByLabelText(/season name/i)).toHaveValue('');
  });

  it('shows the platform reason for a refused name', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) =>
        init?.method === 'POST'
          ? json(400, { status: 400, code: 'validation-failed', errors: [{ field: 'name', code: 'required' }] })
          : json(200, { items: [], continuationToken: null }),
      ),
    );
    renderApp(admin(), '/seasons');
    await userEvent.click(await screen.findByRole('button', { name: 'Create season' }));
    expect(await screen.findByText('This field is required.')).toBeInTheDocument();
  });

  it('activates a draft season and then archives it', async () => {
    let current = season();
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        if (init?.method === 'POST') {
          current = season({ state: url.endsWith('/activate') ? 'active' : 'archived', version: current.version + 1 });
          return json(200, current);
        }
        return json(200, url.endsWith(ID) ? current : { items: [current], continuationToken: null });
      }),
    );
    renderApp(admin(), `/seasons/${ID}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Activate' }));
    expect(await screen.findByText(/now active/i)).toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'Archive' }));
    expect(await screen.findByText(/season was archived/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Archive' })).toBeNull();
  });

  it('shows a conflict with a reload action when the platform refuses the transition', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init?: RequestInit) =>
        init?.method === 'POST' ? json(409, { status: 409, code: 'season-already-active' }) : json(200, season()),
      ),
    );
    renderApp(admin(), `/seasons/${ID}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Activate' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/changed by someone else/i);
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
  });
});
