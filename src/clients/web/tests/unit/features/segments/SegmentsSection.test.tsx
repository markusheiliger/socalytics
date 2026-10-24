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
let purgeFn: Mock<() => Promise<{ purged: number; inUse: number }>>;
const listOf = (...segments: MaterializedSegment[]): SegmentList => ({ matchId: 'm1', segments });

vi.mock('../../../../src/session/sessionContext', () => ({ useSession: () => ({ platform: {} }) }));
vi.mock('../../../../src/platform/adapters/segmentAdapter', () => ({
  createSegmentAdapter: () => ({ list: () => listFn(), purge: () => purgeFn() }),
}));

function renderSection(client = new QueryClient(), unreachable = false, enabled = true) {
  const view = render(
    <QueryClientProvider client={client}>
      <ConnectivityContext.Provider value={{ isUnreachable: unreachable } as never}>
        <SegmentsSection matchId="m1" enabled={enabled} opponent="FC Gegner" />
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

  it('shows the not-permitted state inside the area without data, purge, or retry', async () => {
    listFn = vi.fn().mockRejectedValue(new PlatformFailure('notPermitted', { status: 403 }));
    renderSection();
    expect(await screen.findByRole('alert')).toHaveTextContent('Only Club Admins can see and manage the video segments of a match.');
    expect(screen.getByRole('heading', { name: 'Video segments' })).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Purge unused segments' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();
    expect(screen.queryByText(/no video segments yet/)).not.toBeInTheDocument();
  });

  it('shows no segment data, error, or empty list for an ended session', async () => {
    listFn = vi.fn().mockRejectedValue(new PlatformFailure('sessionEnded', { status: 401 }));
    renderSection();
    await vi.waitFor(() => expect(listFn).toHaveBeenCalled());
    await vi.waitFor(() => expect(screen.queryByText('Loading the video segments…')).not.toBeInTheDocument());
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByText(/no video segments yet/)).not.toBeInTheDocument();
    expect(screen.queryByText('The video segments could not be loaded.')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Purge unused segments' })).not.toBeInTheDocument();
  });

  it('withdraws a stale list when a later refresh is refused or the session ended', async () => {
    listFn = vi.fn().mockResolvedValueOnce(listOf(segment())).mockRejectedValueOnce(new PlatformFailure('sessionEnded', { status: 401 }));
    renderSection();
    await screen.findByRole('table');
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await vi.waitFor(() => expect(screen.queryByRole('table')).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Purge unused segments' })).not.toBeInTheDocument();
  });

  it.each([
    ['a network failure', new PlatformFailure('unreachable')],
    ['a server error', new PlatformFailure('unexpected', { status: 500 })],
  ])('shows an error with a retry, never an empty list, for %s', async (_name, failure) => {
    listFn = vi.fn().mockRejectedValueOnce(failure).mockResolvedValueOnce(listOf(segment()));
    renderSection();
    expect(await screen.findByRole('alert')).toHaveTextContent('The video segments could not be loaded.');
    expect(screen.queryByText(/no video segments yet/)).not.toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('table')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('disables the retry while offline', async () => {
    listFn = vi.fn().mockRejectedValue(new PlatformFailure('unreachable'));
    renderSection(new QueryClient(), true);
    expect(await screen.findByRole('button', { name: 'Try again' })).toBeDisabled();
    expect(screen.getByText(/needs a connection/)).toBeInTheDocument();
  });

  it('states the offline condition and disables refresh', async () => {
    listFn = vi.fn(async () => listOf(segment()));
    renderSection(new QueryClient(), true);
    expect(screen.getByText(/needs a connection/)).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Refresh' })).toBeDisabled();
  });

  it('states the on-demand rematerialization in the area note', async () => {
    listFn = vi.fn(async () => listOf());
    renderSection();
    expect(await screen.findByText(/rematerialized on demand/)).toBeInTheDocument();
  });

  it('offers the purge only while something is purgeable', async () => {
    listFn = vi.fn(async () => listOf(segment({ inUse: true }), segment({ segmentNumber: 4, state: 'absent' }), segment({ segmentNumber: 5, state: 'materializing' })));
    renderSection();
    await screen.findByRole('table');
    expect(screen.queryByRole('button', { name: 'Purge unused segments' })).not.toBeInTheDocument();
    expect(screen.getByText('Nothing to purge: every segment is in use or already absent.')).toBeInTheDocument();
  });

  it('offers the purge for an unused available or failed segment', async () => {
    listFn = vi.fn(async () => listOf(segment(), segment({ segmentNumber: 4, state: 'failed', failureReason: 'x', inUse: false })));
    renderSection();
    expect(await screen.findByRole('button', { name: 'Purge unused segments' })).toBeEnabled();
    expect(screen.queryByText(/Nothing to purge/)).not.toBeInTheDocument();
  });

  it('disables purge and refresh while offline', async () => {
    listFn = vi.fn(async () => listOf(segment()));
    renderSection(new QueryClient(), true);
    expect(await screen.findByRole('button', { name: 'Purge unused segments' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeDisabled();
  });

  it('disables purge and refresh while a purge is pending and reads the list again after it succeeds', async () => {
    let release: (value: { purged: number; inUse: number }) => void = () => undefined;
    purgeFn = vi.fn(() => new Promise((r) => (release = r)));
    listFn = vi.fn().mockResolvedValueOnce(listOf(segment())).mockResolvedValue(listOf(segment({ state: 'absent', mediaSizeBytes: undefined, availableAt: undefined })));
    renderSection();
    await userEvent.click(await screen.findByRole('button', { name: 'Purge unused segments' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/FC Gegner/)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Purge' }));
    expect(await within(dialog).findByText('Purging the unused segments…')).toBeInTheDocument();
    expect(purgeFn).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Purge unused segments', hidden: true })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Refresh', hidden: true })).toBeDisabled();
    release({ purged: 1, inUse: 0 });
    expect(await within(dialog).findByText('1 segments purged, 0 kept because they are in use.')).toBeInTheDocument();
    await vi.waitFor(() => expect(listFn).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('1 segments, 1 absent, 0 in use')).toBeInTheDocument();
  });

  it('reads the list again after a failed purge', async () => {
    purgeFn = vi.fn().mockRejectedValue(new PlatformFailure('unexpected'));
    listFn = vi.fn(async () => listOf(segment()));
    renderSection();
    await userEvent.click(await screen.findByRole('button', { name: 'Purge unused segments' }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Purge' }));
    expect(await screen.findByText(/The segments could not be purged/)).toBeInTheDocument();
    await vi.waitFor(() => expect(listFn).toHaveBeenCalledTimes(2));
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
