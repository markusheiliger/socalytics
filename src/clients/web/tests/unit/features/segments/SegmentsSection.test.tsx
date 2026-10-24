import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi, type Mock } from 'vitest';
import '../../../../src/i18n';
import i18n from '../../../../src/i18n';
import { ConnectivityContext } from '../../../../src/connectivity/connectivityContext';
import { SegmentsSection } from '../../../../src/features/segments/SegmentsSection';
import { segmentsKey } from '../../../../src/features/segments/segmentKeys';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { MaterializedSegment, SegmentList } from '../../../../src/platform/models/segments';

const segment = (over: Partial<MaterializedSegment> = {}): MaterializedSegment => ({
  recordingVersionId: '11111111-2222-3333-4444-555555555555',
  timelineMappingDigest: 'sha256:timeline',
  segmentationPolicyDigest: 'sha256:policy',
  segmentNumber: 3,
  state: 'available',
  mediaSizeBytes: 1_234_567,
  failureReason: undefined,
  availableAt: new Date('2030-01-01T10:00:00Z'),
  inUse: false,
  ...over,
});

let listFn: Mock<() => Promise<SegmentList>>;
const listOf = (...segments: MaterializedSegment[]): SegmentList => ({ matchId: 'm1', segments });

vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));
vi.mock('../../../../src/platform/adapters/segmentAdapter', () => ({
  createSegmentAdapter: () => ({ list: () => listFn(), purge: vi.fn() }),
}));

function renderSection(client = new QueryClient(), unreachable = false, enabled = true) {
  const view = render(
    <QueryClientProvider client={client}>
      <ConnectivityContext.Provider value={{ isUnreachable: unreachable } as never}>
        <SegmentsSection matchId="m1" enabled={enabled} />
      </ConnectivityContext.Provider>
    </QueryClientProvider>,
  );
  return { client, ...view };
}

afterEach(async () => {
  await i18n.changeLanguage('en');
});

describe('SegmentsSection', () => {
  it('shows the loading state, then the empty state', async () => {
    listFn = vi.fn(async () => listOf());
    renderSection();
    expect(screen.getByText('Loading the video segments…')).toBeInTheDocument();
    expect(await screen.findByText(/This match has no video segments yet/)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('lists every state with absent values as text and a summary', async () => {
    listFn = vi.fn(async () =>
      listOf(
        segment(),
        segment({ segmentNumber: 4, state: 'failed', failureReason: 'ffmpeg <b>exited</b>', mediaSizeBytes: undefined, availableAt: undefined, inUse: true }),
        segment({ segmentNumber: 5, state: 'materializing', mediaSizeBytes: undefined, availableAt: undefined, inUse: true }),
        segment({ segmentNumber: 6, state: 'absent', mediaSizeBytes: undefined, availableAt: undefined }),
        segment({ segmentNumber: 7, state: 'exotic' }),
      ),
    );
    renderSection();
    const table = await screen.findByRole('table', { name: 'Materialized video segments of this match' });
    expect(within(table).getByRole('columnheader', { name: 'Segment number' })).toBeInTheDocument();
    expect(within(table).queryByText('Interval')).not.toBeInTheDocument();
    const rows = within(table).getAllByRole('row');
    expect(rows).toHaveLength(6);
    expect(within(rows[2]).getByText('Failed')).toBeInTheDocument();
    expect(within(rows[2]).getByText('ffmpeg <b>exited</b>')).toBeInTheDocument();
    expect(within(rows[2]).getAllByText('Not reported')).toHaveLength(2);
    expect(within(rows[3]).getByText('Materializing')).toBeInTheDocument();
    expect(within(rows[4]).getByText('Absent')).toBeInTheDocument();
    expect(within(rows[5]).getByText('exotic')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Not in use')).toBeInTheDocument();
    expect(within(rows[2]).getByText('In use')).toBeInTheDocument();
    expect(screen.getByText('5 segments, 1 absent, 1 materializing, 1 available, 1 failed, 1 other, 2 in use')).toBeInTheDocument();
    expect(within(rows[1]).getByText('11111111…')).toBeInTheDocument();
  });

  it('formats sizes and times with the active language', async () => {
    listFn = vi.fn(async () => listOf(segment()));
    await i18n.changeLanguage('de');
    renderSection();
    expect(await screen.findByText('1,2 MB')).toBeInTheDocument();
    expect(screen.getByText(new Intl.DateTimeFormat('de', { dateStyle: 'medium', timeStyle: 'medium' }).format(new Date('2030-01-01T10:00:00Z')))).toBeInTheDocument();
  });

  it('keeps the previous list while refreshing and never polls', async () => {
    let release: (value: SegmentList) => void = () => undefined;
    listFn = vi.fn().mockResolvedValueOnce(listOf(segment())).mockImplementationOnce(() => new Promise<SegmentList>((r) => (release = r)));
    renderSection();
    await screen.findByRole('table');
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByRole('button', { name: 'Refreshing…' })).toBeDisabled();
    expect(screen.getByRole('table')).toBeInTheDocument();
    release(listOf(segment(), segment({ segmentNumber: 9 })));
    expect(await screen.findByText('2 segments, 2 available, 0 in use')).toBeInTheDocument();
    expect(listFn).toHaveBeenCalledTimes(2);
  });

  it('offers a retry after a failure and states a refusal', async () => {
    listFn = vi.fn().mockRejectedValueOnce(new PlatformFailure('unexpected')).mockResolvedValueOnce(listOf());
    renderSection();
    expect(await screen.findByText('The video segments could not be loaded.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText(/no video segments yet/)).toBeInTheDocument();
  });

  it('says that only Club Admins can see the segments when the platform refuses', async () => {
    listFn = vi.fn().mockRejectedValue(new PlatformFailure('notPermitted'));
    renderSection();
    expect(await screen.findByText('Only Club Admins can see and manage the video segments of a match.')).toBeInTheDocument();
  });

  it('states the offline condition and disables refresh', async () => {
    listFn = vi.fn(async () => listOf(segment()));
    renderSection(new QueryClient(), true);
    expect(screen.getByText(/needs a connection/)).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Refresh' })).toBeDisabled();
  });

  it('sends no request when disabled', () => {
    listFn = vi.fn(async () => listOf());
    renderSection(new QueryClient(), false, false);
    expect(listFn).not.toHaveBeenCalled();
    expect(screen.queryByRole('heading')).not.toBeInTheDocument();
  });

  it('writes nothing to browser storage and leaves no data in the cache after the view is gone or cleared', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    listFn = vi.fn(async () => listOf(segment()));
    const { client, unmount } = renderSection();
    await screen.findByRole('table');
    expect(setItem).not.toHaveBeenCalled();
    expect(client.getQueryData(segmentsKey('m1'))).toBeDefined();
    client.clear();
    expect(client.getQueryData(segmentsKey('m1'))).toBeUndefined();
    unmount();
    expect(client.getQueryCache().getAll()).toHaveLength(0);
    setItem.mockRestore();
  });
});
