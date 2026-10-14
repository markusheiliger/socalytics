import { act, screen, waitFor } from '@testing-library/react';
import { fakeAdapter, makeSession, renderApp } from '../session/helpers';

describe('connectivity indicator', () => {
  afterEach(() => {
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: true });
  });

  it('shows that the platform is not reachable while offline and clears when back online', async () => {
    renderApp(fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession()) }));
    await screen.findByRole('navigation', { name: 'Main navigation' });
    expect(screen.queryByText(/not reachable/)).not.toBeInTheDocument();
    await act(async () => window.dispatchEvent(new Event('offline')));
    expect(await screen.findByText(/The platform is not reachable/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeDisabled();
    await act(async () => window.dispatchEvent(new Event('online')));
    await waitFor(() => expect(screen.queryByText(/The platform is not reachable/)).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeEnabled();
  });
});
