import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { vi, type Mock } from 'vitest';
import '../../../../src/i18n';
import { OperationsSummaryPage } from '../../../../src/features/operations/OperationsSummaryPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { OperationalSummary, ReconciliationOutcome } from '../../../../src/platform/models/operations';

const summary = (over: Partial<OperationalSummary> = {}): OperationalSummary => ({
  workflowDefinition: { state: 'resolvable' },
  runsByState: [
    { state: 'Running', count: 3 },
    { state: 'PartiallyCompleted', count: 2 },
    { state: 'Exotic', count: 1 },
  ],
  runsWithBlockedNodes: 4,
  staleAttempts: 5,
  rejectedNotifications: 6,
  outboxPending: 7,
  outboxFailed: 8,
  oldestPendingAgeSeconds: 125,
  observedAt: new Date('2030-01-01T10:00:00Z'),
  ...over,
});

let summaryFn: Mock<() => Promise<OperationalSummary>>;
let reconcileFn: Mock<() => Promise<ReconciliationOutcome>>;

vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));
vi.mock('../../../../src/platform/adapters/operationsAdapter', () => ({
  createOperationsAdapter: () => ({ summary: () => summaryFn(), reconcile: () => reconcileFn() }),
}));

function renderPage() {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter>
        <OperationsSummaryPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('OperationsSummaryPage', () => {
  it('shows every value, the observation time, and the list links', async () => {
    summaryFn = vi.fn<() => Promise<OperationalSummary>>(async () => summary());
    renderPage();
    expect(await screen.findByText('The default workflow definition resolves.')).toBeInTheDocument();
    const runs = screen.getByRole('heading', { name: 'Runs by state' }).nextElementSibling as HTMLElement;
    expect(within(runs).getByText('Running').nextElementSibling).toHaveTextContent('3');
    expect(within(runs).getByText('Partially completed').nextElementSibling).toHaveTextContent('2');
    expect(within(runs).getByText('Exotic').nextElementSibling).toHaveTextContent('1');
    expect(screen.getByText('Runs with blocked nodes').nextElementSibling).toHaveTextContent('4');
    expect(screen.getByText('Stale attempts').nextElementSibling).toHaveTextContent('5');
    expect(screen.getByText('Oldest pending message').nextElementSibling).toHaveTextContent('2 min 5 s');
    expect(screen.getByRole('status')).toHaveTextContent(/^Observed at /);
    expect(screen.getByRole('link', { name: 'Show rejected notifications' })).toHaveAttribute(
      'href',
      '/operations/notification-receipts?outcome=rejected',
    );
    expect(screen.getByRole('link', { name: 'Show pending outgoing messages' })).toHaveAttribute('href', '/operations/outbox-messages?state=pending');
    expect(screen.getByRole('link', { name: 'Show failed outgoing messages' })).toHaveAttribute('href', '/operations/outbox-messages?state=failed');
  });

  it('says that nothing is pending', async () => {
    summaryFn = vi.fn<() => Promise<OperationalSummary>>(async () => summary({ oldestPendingAgeSeconds: undefined, outboxPending: 0 }));
    renderPage();
    expect(await screen.findByText('Nothing is pending')).toBeInTheDocument();
  });

  it('states that new runs cannot start when the definition does not resolve', async () => {
    summaryFn = vi.fn(async () =>
      summary({ workflowDefinition: { state: 'unresolvable', category: 'no-default', since: new Date('2030-01-01T09:00:00Z') } }),
    );
    renderPage();
    expect(await screen.findByText(/New analysis runs cannot start until the default workflow resolves/)).toBeInTheDocument();
    expect(screen.getByText('no-default')).toBeInTheDocument();
    expect(screen.getByText('Since')).toBeInTheDocument();
  });

  it('refreshes and keeps the values visible meanwhile', async () => {
    let release: (value: OperationalSummary) => void = () => {};
    summaryFn = vi.fn<() => Promise<OperationalSummary>>().mockResolvedValueOnce(summary());
    renderPage();
    await screen.findByText('Stale attempts');
    summaryFn.mockReturnValueOnce(new Promise<OperationalSummary>((resolve) => (release = resolve)));
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText(/Refreshing…/)).toBeInTheDocument();
    expect(screen.getByText('Stale attempts')).toBeInTheDocument();
    release(summary({ staleAttempts: 9, observedAt: new Date('2030-01-01T10:05:00Z') }));
    expect(await screen.findByText('9')).toBeInTheDocument();
    expect(summaryFn).toHaveBeenCalledTimes(2);
  });

  it.each([
    ['unreachable', new PlatformFailure('unreachable', { status: 503 })],
    ['rateLimited', new PlatformFailure('rateLimited', { status: 429 })],
    ['unexpected', new PlatformFailure('unexpected', { status: 500 })],
  ])('shows a recoverable error for %s and keeps the last values', async (_name, failure) => {
    summaryFn = vi.fn<() => Promise<OperationalSummary>>().mockResolvedValueOnce(summary()).mockRejectedValueOnce(failure).mockResolvedValueOnce(summary({ staleAttempts: 11 }));
    renderPage();
    await screen.findByText('Stale attempts');
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByText('Stale attempts')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('11')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows an error with Try again when the first read fails', async () => {
    summaryFn = vi.fn<() => Promise<OperationalSummary>>().mockRejectedValueOnce(new PlatformFailure('unexpected', { status: 500 })).mockResolvedValue(summary());
    renderPage();
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Stale attempts')).toBeInTheDocument();
  });

  it('shows the not-permitted view for a refused read', async () => {
    summaryFn = vi.fn<() => Promise<OperationalSummary>>().mockRejectedValue(new PlatformFailure('notPermitted', { status: 403 }));
    renderPage();
    expect(await screen.findByRole('alert')).toHaveTextContent(/permission|not permitted|not allowed/i);
    expect(screen.queryByRole('button', { name: 'Refresh' })).not.toBeInTheDocument();
  });

  it.each([
    ['zero', summary({ staleAttempts: 0, runsWithBlockedNodes: 0, rejectedNotifications: 0, outboxPending: 0, outboxFailed: 0, runsByState: [] })],
    ['non-zero', summary()],
  ])('always offers Run reconciliation with %s counts', async (_name, value) => {
    summaryFn = vi.fn<() => Promise<OperationalSummary>>(async () => value);
    renderPage();
    await screen.findByText('Stale attempts');
    expect(screen.getByRole('button', { name: 'Run reconciliation' })).toBeEnabled();
  });

  it('offers Run reconciliation when the summary cannot be read', async () => {
    summaryFn = vi.fn<() => Promise<OperationalSummary>>().mockRejectedValue(new PlatformFailure('unexpected', { status: 500 }));
    renderPage();
    await screen.findByRole('alert');
    expect(screen.getByRole('button', { name: 'Run reconciliation' })).toBeInTheDocument();
  });

  it('reads the summary again after a successful reconciliation', async () => {
    summaryFn = vi.fn<() => Promise<OperationalSummary>>().mockResolvedValueOnce(summary()).mockResolvedValueOnce(summary({ staleAttempts: 9 }));
    reconcileFn = vi.fn(async () => ({
      readyJobsRepublished: 1,
      notificationsRepublished: 0,
      startedAt: new Date('2030-01-01T10:00:00Z'),
      completedAt: new Date('2030-01-01T10:00:05Z'),
    }));
    renderPage();
    await screen.findByText('Stale attempts');
    await userEvent.click(screen.getByRole('button', { name: 'Run reconciliation' }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Run reconciliation' }));
    expect(await screen.findByText('1 ready jobs and 0 notifications were queued again')).toBeInTheDocument();
    expect(await screen.findByText('9')).toBeInTheDocument();
    expect(reconcileFn).toHaveBeenCalledTimes(1);
    expect(summaryFn).toHaveBeenCalledTimes(2);
  });

  it('does not read the summary again when the reconciliation is cancelled or refused', async () => {
    summaryFn = vi.fn<() => Promise<OperationalSummary>>(async () => summary());
    reconcileFn = vi.fn<() => Promise<ReconciliationOutcome>>().mockRejectedValue(new PlatformFailure('unexpected', { status: 500 }));
    renderPage();
    await screen.findByText('Stale attempts');
    await userEvent.click(screen.getByRole('button', { name: 'Run reconciliation' }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Run reconciliation' }));
    expect(await screen.findByText(/The reconciliation did not complete/)).toBeInTheDocument();
    expect(summaryFn).toHaveBeenCalledTimes(1);
  });
});
