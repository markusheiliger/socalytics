import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, type Mock } from 'vitest';
import '../../../../src/i18n';
import { ReconcileDialog } from '../../../../src/features/operations/ReconcileDialog';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { ReconciliationOutcome } from '../../../../src/platform/models/operations';

let reconcileFn: Mock<() => Promise<ReconciliationOutcome>>;

vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));
vi.mock('../../../../src/platform/adapters/operationsAdapter', () => ({
  createOperationsAdapter: () => ({ reconcile: () => reconcileFn() }),
}));

const outcome = (over: Partial<ReconciliationOutcome> = {}): ReconciliationOutcome => ({
  readyJobsRepublished: 3,
  notificationsRepublished: 2,
  startedAt: new Date('2030-01-01T10:00:00Z'),
  completedAt: new Date('2030-01-01T10:00:05Z'),
  ...over,
});

function renderDialog() {
  const onClose = vi.fn();
  const onCompleted = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient()}>
      <ReconcileDialog open onClose={onClose} onCompleted={onCompleted} />
    </QueryClientProvider>,
  );
  return { onClose, onCompleted };
}

describe('ReconcileDialog', () => {
  it('states what reconciliation does and sends nothing when cancelled', async () => {
    reconcileFn = vi.fn(async () => outcome());
    const { onClose } = renderDialog();
    expect(screen.getByText(/queues every currently eligible ready job and unpublished notification/)).toBeInTheDocument();
    expect(screen.getByText(/never authorizes work, drops jobs, publishes blocked work, or changes accepted results/)).toBeInTheDocument();
    expect(screen.getByText(/It may take a while\./)).toBeInTheDocument();
    expect(screen.getByText(/recorded in the audit trail/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(reconcileFn).not.toHaveBeenCalled();
  });

  it('sends one request under repeated clicks and shows the counts and times', async () => {
    let resolve!: (value: ReconciliationOutcome) => void;
    reconcileFn = vi.fn(() => new Promise<ReconciliationOutcome>((r) => (resolve = r)));
    const { onCompleted } = renderDialog();
    const confirm = screen.getByRole('button', { name: 'Run reconciliation' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    expect(await screen.findByText('Reconciliation is running…')).toBeInTheDocument();
    expect(confirm).toBeDisabled();
    resolve(outcome());
    expect(await screen.findByText('3 ready jobs and 2 notifications were queued again')).toBeInTheDocument();
    expect(screen.getByText(/^Started /)).toBeInTheDocument();
    expect(screen.getByText(/^Completed /)).toBeInTheDocument();
    expect(reconcileFn).toHaveBeenCalledTimes(1);
    expect(onCompleted).toHaveBeenCalledTimes(1);
  });

  it('says that nothing needed to be queued again', async () => {
    reconcileFn = vi.fn(async () => outcome({ readyJobsRepublished: 0, notificationsRepublished: 0 }));
    renderDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Run reconciliation' }));
    expect(await screen.findByText('Nothing needed to be queued again')).toBeInTheDocument();
  });

  it.each(['unreachable', 'unexpected'] as const)('explains a %s refusal without a second request', async (kind) => {
    reconcileFn = vi.fn(async () => {
      throw new PlatformFailure(kind);
    });
    const { onCompleted } = renderDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Run reconciliation' }));
    expect(await screen.findByText(/The reconciliation did not complete/)).toBeInTheDocument();
    expect(reconcileFn).toHaveBeenCalledTimes(1);
    expect(onCompleted).not.toHaveBeenCalled();
  });

  it('shows the not-permitted view for a refused member', async () => {
    reconcileFn = vi.fn(async () => {
      throw new PlatformFailure('notPermitted');
    });
    renderDialog();
    await userEvent.click(screen.getByRole('button', { name: 'Run reconciliation' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/not permitted|permission|allowed/i);
    expect(reconcileFn).toHaveBeenCalledTimes(1);
  });
});
