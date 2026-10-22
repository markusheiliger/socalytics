import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { vi, type Mock } from 'vitest';
import '../../../../src/i18n';
import { OutboxMessagePage } from '../../../../src/features/operations/OutboxMessagePage';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { OutboxMessage } from '../../../../src/platform/models/operations';

const ID = '3f2b8c1e-5a4d-4e6f-9b7a-1c2d3e4f5a6b';
const RUN = '11111111-2222-4333-8444-555555555555';

const message = (over: Partial<OutboxMessage> = {}): OutboxMessage => ({
  messageId: ID,
  messageType: 'analysis.ready-work',
  contractVersion: 'v1',
  subject: '<b>bold</b> subject',
  state: 'failed',
  attemptCount: 4,
  publicationRound: 2,
  lastAttemptAt: new Date('2030-01-01T10:00:00Z'),
  nextAttemptAt: undefined,
  failureCategory: 'exotic-failure',
  createdAt: new Date('2030-01-01T09:00:00Z'),
  publishedAt: undefined,
  ageSeconds: 3725,
  publicationLagSeconds: undefined,
  runId: RUN,
  logicalJobId: undefined,
  capabilityId: 'capability-x',
  ...over,
});

let getFn: Mock<(id: string) => Promise<OutboxMessage>>;

vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {}, status: 'signedIn' }) }));
vi.mock('../../../../src/platform/adapters/operationsAdapter', () => ({
  createOperationsAdapter: () => ({ getOutboxMessage: (id: string) => getFn(id) }),
}));

function renderPage(id: string) {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter initialEntries={[`/operations/outbox-messages/${id}`]}>
        <Routes>
          <Route path="/operations/outbox-messages/:messageId" element={<OutboxMessagePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('OutboxMessagePage', () => {
  it('shows every field with absent values, readable durations, and plain-text platform text', async () => {
    getFn = vi.fn<(id: string) => Promise<OutboxMessage>>(async () => message());
    renderPage(ID);
    expect(await screen.findByText('Message ID')).toBeInTheDocument();
    expect(getFn).toHaveBeenCalledWith(ID);
    const value = (label: string) => screen.getByText(label).nextElementSibling as HTMLElement;
    expect(value('Message ID')).toHaveTextContent(ID);
    expect(value('Message type')).toHaveTextContent('Ready work');
    expect(value('Contract version')).toHaveTextContent('v1');
    expect(value('Subject')).toHaveTextContent('<b>bold</b> subject');
    expect(value('Subject').querySelector('b')).toBeNull();
    expect(value('State')).toHaveTextContent('Failed');
    expect(value('Attempt count')).toHaveTextContent('4');
    expect(value('Publication round')).toHaveTextContent('2');
    expect(value('Next attempt')).toHaveTextContent('absent');
    expect(value('Failure category')).toHaveTextContent('exotic-failure');
    expect(value('Published')).toHaveTextContent('absent');
    expect(value('Age')).toHaveTextContent('1 h 2 min');
    expect(value('Publication lag')).toHaveTextContent('absent');
    expect(value('Run ID')).toHaveTextContent(RUN);
    expect(value('Logical job ID')).toHaveTextContent('absent');
    expect(value('Capability ID')).toHaveTextContent('capability-x');
  });

  it('shows unknown types and states raw', async () => {
    getFn = vi.fn<(id: string) => Promise<OutboxMessage>>(async () => message({ messageType: 'exotic.type', state: 'weird' }));
    renderPage(ID);
    expect(await screen.findByText('exotic.type')).toBeInTheDocument();
    expect(screen.getByText('weird')).toBeInTheDocument();
  });

  it('links no identifier and offers no payload or action', async () => {
    getFn = vi.fn<(id: string) => Promise<OutboxMessage>>(async () => message());
    renderPage(ID);
    await screen.findByText('Message ID');
    expect(screen.getAllByRole('link').map((link) => link.getAttribute('href'))).toEqual(['/operations/outbox-messages']);
    expect(screen.queryByText(/payload/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /retry|edit|discard|delete/i })).not.toBeInTheDocument();
  });

  it('copies a full identifier', async () => {
    getFn = vi.fn<(id: string) => Promise<OutboxMessage>>(async () => message());
    const user = userEvent.setup();
    renderPage(ID);
    await screen.findByText('Message ID');
    const writeText = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
    await user.click(screen.getByRole('button', { name: `Copy ${RUN}` }));
    expect(writeText).toHaveBeenCalledWith(RUN);
    expect(await screen.findByText('Copied')).toBeInTheDocument();
  });

  it('shows the not-found view for a malformed identifier without a request', () => {
    getFn = vi.fn<(id: string) => Promise<OutboxMessage>>();
    renderPage('not-a-uuid');
    expect(screen.getByText('This page does not exist.')).toBeInTheDocument();
    expect(getFn).not.toHaveBeenCalled();
  });

  it('shows the not-found view for a 404', async () => {
    getFn = vi.fn<(id: string) => Promise<OutboxMessage>>().mockRejectedValue(new PlatformFailure('incompatible', { status: 404 }));
    renderPage(ID);
    expect(await screen.findByText('This page does not exist.')).toBeInTheDocument();
  });

  it('shows the not-permitted view for a 403', async () => {
    getFn = vi.fn<(id: string) => Promise<OutboxMessage>>().mockRejectedValue(new PlatformFailure('notPermitted', { status: 403 }));
    renderPage(ID);
    expect(await screen.findByRole('alert')).toHaveTextContent(/permission|not permitted|not allowed/i);
    expect(screen.queryByText('Message ID')).not.toBeInTheDocument();
  });

  it('shows a recoverable error with Try again', async () => {
    getFn = vi
      .fn<(id: string) => Promise<OutboxMessage>>()
      .mockRejectedValueOnce(new PlatformFailure('unexpected', { status: 500 }))
      .mockResolvedValue(message());
    renderPage(ID);
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('Message ID')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
