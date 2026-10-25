import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { vi, type Mock } from 'vitest';
import '../../../../src/i18n';
import { PurgeSegmentsDialog } from '../../../../src/features/segments/PurgeSegmentsDialog';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { PurgeOutcome } from '../../../../src/platform/models/segments';

let purgeFn: Mock<(matchId: string) => Promise<PurgeOutcome>>;

vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));
vi.mock('../../../../src/platform/adapters/segmentAdapter', () => ({
  createSegmentAdapter: () => ({ purge: (matchId: string) => purgeFn(matchId) }),
}));

const outcome = (over: Partial<PurgeOutcome> = {}): PurgeOutcome => ({ matchId: 'm1', purged: 3, inUse: 1, ...over });

function Host({ onClose, onFinished }: { onClose: () => void; onFinished: () => void }) {
  const [open, setOpen] = useState(false);
  return (
    <QueryClientProvider client={new QueryClient()}>
      <button onClick={() => setOpen(true)}>Purge unused segments</button>
      <PurgeSegmentsDialog
        open={open}
        matchId="m1"
        opponent="FC Rivals"
        onClose={() => {
          setOpen(false);
          onClose();
        }}
        onFinished={onFinished}
      />
    </QueryClientProvider>
  );
}

async function openDialog() {
  const onClose = vi.fn();
  const onFinished = vi.fn();
  render(<Host onClose={onClose} onFinished={onFinished} />);
  await userEvent.click(screen.getByRole('button', { name: 'Purge unused segments' }));
  return { onClose, onFinished };
}

describe('PurgeSegmentsDialog', () => {
  it('states the match and the consequences and sends nothing when cancelled', async () => {
    purgeFn = vi.fn(async () => outcome());
    const { onClose } = await openDialog();
    expect(screen.getByText(/match against FC Rivals/)).toBeInTheDocument();
    expect(screen.getByText(/become absent/)).toBeInTheDocument();
    expect(screen.getByText(/kept and reported/)).toBeInTheDocument();
    expect(screen.getByText(/rematerialized on demand/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(purgeFn).not.toHaveBeenCalled();
  });

  it('sends one request under repeated confirms, blocks dismissal, and shows the counts', async () => {
    let resolve!: (value: PurgeOutcome) => void;
    purgeFn = vi.fn(() => new Promise<PurgeOutcome>((r) => (resolve = r)));
    const { onFinished, onClose } = await openDialog();
    const confirm = screen.getByRole('button', { name: 'Purge' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    expect(await screen.findByText('Purging the unused segments…')).toBeInTheDocument();
    expect(confirm).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(onClose).not.toHaveBeenCalled();
    resolve(outcome());
    expect(await screen.findByText('3 segments purged, 1 kept because they are in use.')).toBeInTheDocument();
    expect(screen.getByText(/Purged segments are rematerialized on demand/)).toBeInTheDocument();
    expect(purgeFn).toHaveBeenCalledTimes(1);
    expect(onFinished).toHaveBeenCalledTimes(1);
  });

  it('reports zero purged as a normal outcome', async () => {
    purgeFn = vi.fn(async () => outcome({ purged: 0, inUse: 2 }));
    await openDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Purge' }));
    expect(await screen.findByText('0 segments purged, 2 kept because they are in use.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it.each([
    ['unreachable', /could not be reached/],
    ['unexpected', /did not complete the purge/],
  ] as const)('explains a %s failure and retries with a new request', async (kind, cause) => {
    purgeFn = vi.fn(async () => {
      throw new PlatformFailure(kind, { status: 503 });
    });
    const { onFinished } = await openDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Purge' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(cause);
    expect(alert).toHaveTextContent(/already purged by the call stay purged/);
    expect(purgeFn).toHaveBeenCalledTimes(1);
    await userEvent.click(screen.getByRole('button', { name: 'Try the purge again' }));
    await waitFor(() => expect(purgeFn).toHaveBeenCalledTimes(2));
    expect(onFinished).toHaveBeenCalledTimes(2);
  });

  it('shows a refusal without a retry action', async () => {
    purgeFn = vi.fn(async () => {
      throw new PlatformFailure('notPermitted', { status: 403 });
    });
    await openDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Purge' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/Only Club Admins/);
    expect(screen.queryByRole('button', { name: 'Try the purge again' })).not.toBeInTheDocument();
  });

  it('returns focus to the purge button on close', async () => {
    purgeFn = vi.fn(async () => outcome());
    await openDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Purge' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Purge unused segments' })).toHaveFocus());
  });
});
