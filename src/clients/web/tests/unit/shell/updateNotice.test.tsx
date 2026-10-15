import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import '../../../src/i18n';
import { UpdateNotice } from '../../../src/shell/UpdateNotice';
import { registerServiceWorker, resetUpdateState } from '../../../src/serviceWorker/registration';

describe('update notice', () => {
  afterEach(() => {
    resetUpdateState();
    vi.unstubAllEnvs();
  });

  it('shows nothing while current', () => {
    render(<UpdateNotice />);
    expect(screen.queryByText('A new version of SocAlytics is available.')).not.toBeInTheDocument();
  });

  it('shows the notice when a newer worker waits and "Update" activates it', async () => {
    vi.stubEnv('VITE_SOCALYTICS_WEB_VERSION', '2026.1015.100000');
    render(<UpdateNotice />);
    const postMessage = vi.fn((message: { type: string }, transfer?: Transferable[]) => {
      if (message.type === 'VERSION') (transfer?.[0] as MessagePort).postMessage({ version: '2026.1015.110000' });
    });
    const registration = { waiting: { postMessage }, update: vi.fn(async () => undefined), addEventListener: vi.fn() };
    await act(async () => {
      await registerServiceWorker({ serviceWorker: { controller: {}, register: async () => registration } }, (url) => url);
    });
    expect(await screen.findByText('A new version of SocAlytics is available.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Update' }));
    expect(postMessage).toHaveBeenCalledWith({ type: 'SKIP_WAITING' });
  });
});
