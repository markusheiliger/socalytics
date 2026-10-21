import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { vi, type Mock } from 'vitest';
import '../../../../src/i18n';
import { OperationsSummaryPage } from '../../../../src/features/operations/OperationsSummaryPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { OperationalSummary } from '../../../../src/platform/models/operations';

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

vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));
vi.mock('../../../../src/platform/adapters/operationsAdapter', () => ({
  createOperationsAdapter: () => ({ summary: () => summaryFn() }),
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
});
