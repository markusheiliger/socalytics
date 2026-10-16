import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });

const calls = () => (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls as [string | Request, RequestInit][];
const urlOf = (c: [string | Request, RequestInit]) => (typeof c[0] === 'string' ? c[0] : c[0].url);

const stubFetch = (items: unknown[]) =>
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string | Request) => {
      const url = typeof input === 'string' ? input : input.url;
      if (url.includes('/notification-preferences/')) {
        return json(200, { type: 'registration-pending', enabled: false, accountMessage: false });
      }
      if (url.endsWith('/notification-preferences')) return json(200, { items });
      return json(404, { status: 404 });
    }),
  );

describe('notification preferences of the account view', () => {
  const signedIn = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) });

  it('shows account messages as always sent and a toggle for the others', async () => {
    stubFetch([
      { type: 'email-confirmation', enabled: true, accountMessage: true },
      { type: 'registration-pending', enabled: true, accountMessage: false },
    ]);
    renderApp(signedIn(), '/account');
    expect(await screen.findByText(/always sent/i)).toBeInTheDocument();
    const toggle = await screen.findByRole('switch', { name: /waiting for approval/i });
    expect(toggle).toBeChecked();
    expect(screen.getAllByRole('switch')).toHaveLength(1);
  });

  it('sends the new value when a toggle is switched', async () => {
    stubFetch([{ type: 'registration-pending', enabled: true, accountMessage: false }]);
    renderApp(signedIn(), '/account');
    await userEvent.click(await screen.findByRole('switch', { name: /waiting for approval/i }));
    const put = calls().find((c) => c[1].method === 'PUT' && urlOf(c).includes('/notification-preferences/registration-pending'));
    expect(put).toBeDefined();
    expect(new TextDecoder().decode(put?.[1].body as ArrayBuffer)).toBe('{"enabled":false}');
  });

  it('says so when no type applies', async () => {
    stubFetch([]);
    renderApp(signedIn(), '/account');
    expect(await screen.findByText('No notifications apply to your roles.')).toBeInTheDocument();
  });
});
