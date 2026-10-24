import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ConnectivityContext } from '../../../../src/connectivity/connectivityContext';
import { AnalysisComparisonPage } from '../../../../src/features/analysis/AnalysisComparisonPage';
import { PlatformFailure } from '../../../../src/platform/failure';
import type { AnalysisRunSummary } from '../../../../src/platform/models/recordings';
import { SessionContext, type SessionApi } from '../../../../src/session/sessionContext';
import '../../../../src/i18n';
import { makeSession } from '../../session/helpers';

const mocks = vi.hoisted(() => ({ getMatch: vi.fn(), getTeam: vi.fn(), listByMatch: vi.fn(), getRun: vi.fn(), listStreams: vi.fn(), getSet: vi.fn() }));

vi.mock('../../../../src/platform/adapters/matchAdapter', () => ({ createMatchAdapter: () => ({ get: mocks.getMatch }) }));
vi.mock('../../../../src/platform/adapters/teamAdapter', () => ({ createTeamAdapter: () => ({ get: mocks.getTeam }) }));
vi.mock('../../../../src/platform/adapters/analysisRunAdapter', () => ({ createAnalysisRunAdapter: () => ({ listByMatch: mocks.listByMatch, getRun: mocks.getRun }) }));
vi.mock('../../../../src/platform/adapters/analysisFactAdapter', () => ({ createAnalysisFactAdapter: () => ({ listStreams: mocks.listStreams }) }));
vi.mock('../../../../src/platform/adapters/recordingLineageAdapter', () => ({ createRecordingLineageAdapter: () => ({ getSet: mocks.getSet }) }));

const MATCH = '33333333-3333-4333-8333-333333333333';
const TEAM = '44444444-4444-4444-8444-444444444444';
const SET_A = '55555555-5555-4555-8555-555555555555';
const SET_B = '66666666-6666-4666-8666-666666666666';
const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const summary = (n: number, state: string, over: Partial<AnalysisRunSummary> = {}): AnalysisRunSummary => ({
  runId: id(n),
  recordingSetVersionId: SET_A,
  workflowId: 'default',
  workflowVersion: '3',
  state,
  createdAt: new Date('2026-10-12T12:00:00Z'),
  version: 1,
  isCurrent: false,
  ...over,
});

function Probe() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname + location.search}</output>;
}

function renderPage(search = '', options: { unreachable?: boolean; role?: 'Coach' | 'none' } = {}) {
  const session = makeSession({
    teamRoles: options.role === 'none' ? [] : [{ teamId: TEAM, teamName: 'First', seasonId: 's', role: 'Coach' }],
  });
  const api = { platform: {}, session, status: 'signedIn' } as unknown as SessionApi;
  const unreachable = Boolean(options.unreachable);
  const connectivity = { status: unreachable ? ('unreachable' as const) : ('online' as const), isUnreachable: unreachable, reportNetworkFailure: () => {} };
  const wrap = (children: ReactNode) => (
    <QueryClientProvider client={new QueryClient()}>
      <ConnectivityContext.Provider value={connectivity}>
        <SessionContext.Provider value={api}>
          <MemoryRouter initialEntries={[`/matches/${MATCH}/analysis-comparison${search}`]}>
            <Routes>
              <Route path="/matches/:matchId/analysis-comparison" element={children} />
            </Routes>
            <Probe />
          </MemoryRouter>
        </SessionContext.Provider>
      </ConnectivityContext.Provider>
    </QueryClientProvider>
  );
  return render(wrap(<AnalysisComparisonPage />));
}

const optionTexts = (label: string) => within(screen.getByLabelText(label)).getAllByRole('option').map((option) => option.textContent ?? '');

beforeEach(() => {
  vi.resetAllMocks();
  mocks.getMatch.mockResolvedValue({ id: MATCH, teamId: TEAM });
  mocks.getTeam.mockResolvedValue({ id: TEAM });
});

describe('analysis comparison selection', () => {
  it('lists only terminal runs with recognizable context and preselects nothing', async () => {
    mocks.listByMatch.mockResolvedValue({
      items: [summary(1, 'Completed', { isCurrent: true }), summary(2, 'Running'), summary(3, 'Failed', { recordingSetVersionId: SET_B, workflowVersion: '2' }), summary(4, 'Mystery')],
      continuationToken: undefined,
    });
    renderPage();
    await screen.findByText('All 4 run(s) of this match are loaded.');
    const options = optionTexts('Left run');
    expect(options).toHaveLength(3);
    expect(options[1]).toContain(`Completed · run ${id(1)} · current run · recording set ${SET_A} · workflow default 3`);
    expect(options[2]).toContain(`Failed · run ${id(3)} · recording set ${SET_B} · workflow default 2`);
    expect(screen.getByLabelText('Left run')).toHaveValue('');
    expect(screen.getByLabelText('Right run')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Compare the runs' })).toBeDisabled();
    expect(screen.getByText(/Only runs in a known finished state/)).toBeInTheDocument();
  });

  it('keeps insufficient history unconfirmed until every page is loaded, then reaches older pages', async () => {
    const user = userEvent.setup();
    mocks.listByMatch
      .mockResolvedValueOnce({ items: [summary(1, 'Completed')], continuationToken: 'next' })
      .mockResolvedValueOnce({ items: [summary(2, 'Cancelled')], continuationToken: undefined });
    renderPage();
    await screen.findByRole('button', { name: 'Load older runs' });
    expect(screen.queryByText(/Two finished runs are needed/)).not.toBeInTheDocument();
    expect(screen.queryByText(/of this match are loaded/)).not.toBeInTheDocument();
    expect(mocks.listByMatch).toHaveBeenCalledWith(MATCH, undefined);

    await user.click(screen.getByRole('button', { name: 'Load older runs' }));
    await screen.findByText('All 2 run(s) of this match are loaded.');
    expect(mocks.listByMatch).toHaveBeenLastCalledWith(MATCH, { continuationToken: 'next' });
    expect(optionTexts('Right run')).toHaveLength(3);
    expect(screen.queryByRole('button', { name: 'Load older runs' })).not.toBeInTheDocument();
  });

  it('states insufficient history only after the pages are exhausted', async () => {
    mocks.listByMatch.mockResolvedValue({ items: [summary(1, 'Completed'), summary(2, 'Running')], continuationToken: undefined });
    renderPage();
    expect(await screen.findByText('All runs of this match are loaded, and 1 of them can be compared. Two finished runs are needed.')).toBeInTheDocument();
    expect(screen.getByLabelText('Left run')).toHaveValue('');
  });

  it('shows a labelled retry after a failed page and then continues from the same token', async () => {
    const user = userEvent.setup();
    mocks.listByMatch
      .mockResolvedValueOnce({ items: [summary(1, 'Completed')], continuationToken: 'next' })
      .mockRejectedValueOnce(new PlatformFailure('incompatible'))
      .mockResolvedValueOnce({ items: [summary(2, 'Completed')], continuationToken: undefined });
    renderPage();
    await user.click(await screen.findByRole('button', { name: 'Load older runs' }));
    await user.click(await screen.findByRole('button', { name: 'Retry loading runs' }));
    await screen.findByText('All 2 run(s) of this match are loaded.');
    expect(mocks.listByMatch).toHaveBeenNthCalledWith(3, MATCH, { continuationToken: 'next' });
    expect(screen.queryByText(/Two finished runs are needed/)).not.toBeInTheDocument();
  });

  it('preserves a review-origin left id that is absent from the loaded pages and never replaces it', async () => {
    mocks.listByMatch.mockResolvedValue({ items: [summary(1, 'Completed'), summary(2, 'Completed')], continuationToken: 'next' });
    renderPage(`?left=${id(9)}`);
    await screen.findByRole('button', { name: 'Load older runs' });
    expect(screen.getByLabelText('Left run')).toHaveValue(id(9));
    expect(optionTexts('Left run')).toContain(`Run ${id(9)} (selected; not in the loaded runs)`);
    expect(screen.getByLabelText('Right run')).toHaveValue('');
  });

  it('permits editing either side and writes only the two identifiers to the address', async () => {
    const user = userEvent.setup();
    mocks.listByMatch.mockResolvedValue({ items: [summary(1, 'Completed'), summary(2, 'Failed'), summary(3, 'Completed')], continuationToken: undefined });
    renderPage(`?left=${id(1)}`);
    await screen.findByText('All 3 run(s) of this match are loaded.');
    expect(screen.getByLabelText('Left run')).toHaveValue(id(1));
    await user.selectOptions(screen.getByLabelText('Left run'), id(3));
    await user.selectOptions(screen.getByLabelText('Right run'), id(2));
    await user.click(screen.getByRole('button', { name: 'Compare the runs' }));
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(`?left=${id(3)}&right=${id(2)}`));
  });

  it('refuses identical sides', async () => {
    const user = userEvent.setup();
    mocks.listByMatch.mockResolvedValue({ items: [summary(1, 'Completed'), summary(2, 'Completed')], continuationToken: undefined });
    renderPage();
    await screen.findByText('All 2 run(s) of this match are loaded.');
    await user.selectOptions(screen.getByLabelText('Left run'), id(1));
    await user.selectOptions(screen.getByLabelText('Right run'), id(1));
    expect(screen.getByText('Choose two different runs.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Compare the runs' })).toBeDisabled();
  });

  it('explains an invalid address selection without replacing it', async () => {
    mocks.listByMatch.mockResolvedValue({ items: [summary(1, 'Completed')], continuationToken: undefined });
    renderPage(`?right=${id(1)}`);
    expect(await screen.findByText('A right run needs a left run. Choose the left run first.')).toBeInTheDocument();
    expect(await screen.findByLabelText('Left run')).toHaveValue('');
  });

  it('reads no runs and shows a connection message while offline', async () => {
    renderPage('', { unreachable: true });
    expect(await screen.findByText(/A connection is required to compare analysis runs/)).toBeInTheDocument();
    expect(mocks.listByMatch).not.toHaveBeenCalled();
    expect(screen.queryByLabelText('Left run')).not.toBeInTheDocument();
  });

  it('shows the not-permitted view and reads no runs without access to the team', async () => {
    renderPage('', { role: 'none' });
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
    expect(mocks.listByMatch).not.toHaveBeenCalled();
  });

  it('discards the loaded pages when the page unmounts', async () => {
    mocks.listByMatch.mockResolvedValue({ items: [summary(1, 'Completed')], continuationToken: undefined });
    const view = renderPage();
    await screen.findByText(/Two finished runs are needed/);
    view.unmount();
    expect(screen.queryByLabelText('Left run')).not.toBeInTheDocument();
  });
});

const status = (n: number, setId: string, over: Record<string, unknown> = {}) => ({
  runId: id(n),
  teamId: TEAM,
  matchId: MATCH,
  recordingSetVersionId: setId,
  workflowId: 'default',
  workflowVersion: '3',
  state: 'Completed',
  createdAt: new Date('2026-10-12T12:00:00Z'),
  isCurrent: false,
  requestedCapabilities: ['cap'],
  nodes: [{ nodeKey: 'n1', capabilityId: 'cap', capabilityVersion: '1.0.0', tier: 'a', executionScope: 'match', state: 'Completed', jobs: { waiting: 0, ready: 0, claimed: 0, completed: 1, failed: 0, blocked: 0 } }],
  ...over,
});

const stream = (factCount: number) => ({
  nodeKey: 'n1',
  capabilityId: 'cap',
  capabilityVersion: '1.0.0',
  schemaVersion: '1',
  outputType: 'foundational-facts',
  executionScope: 'match',
  required: true,
  factCount,
  provenance: { workflow: { id: 'default', version: '3' }, analystProfile: { id: 'prof', digest: 'sha256:' + 'a'.repeat(64) }, imageDigest: 'sha256:' + 'b'.repeat(64) },
  coverage: { accepted: 1, missing: 2, failed: 3, blocked: 4, skipped: 5 },
});

const recordingSet = (setId: string) => ({
  id: setId,
  matchId: MATCH,
  teamId: TEAM,
  finalizedAt: new Date('2026-10-11T10:00:00Z'),
  members: [{ position: 1, recordingVersionId: 'rv-' + setId.slice(0, 4), recordingContentDigest: 'sha256:c1', timelineMappingId: 'tm1', timelineMappingDigest: 'sha256:d1' }],
});

describe('analysis comparison initial presentation', () => {
  beforeEach(() => {
    mocks.listByMatch.mockResolvedValue({ items: [], continuationToken: undefined });
    mocks.getSet.mockImplementation(async (_match: string, setId: string) => recordingSet(setId));
  });

  it('shows each side independently with identity, history, recording set, links, and streams', async () => {
    const successor = { runId: id(9), recordingSetVersionId: SET_B, recordingSetFinalizedAt: new Date('2026-10-13T10:00:00Z') };
    const runs: Record<string, unknown> = {
      [id(1)]: status(1, SET_A, { supersededBy: successor, supersededAt: new Date('2026-10-13T11:00:00Z') }),
      [id(2)]: status(2, SET_B, { isCurrent: true }),
    };
    mocks.getRun.mockImplementation(async (runId: string) => ({ kind: 'found', run: runs[runId] }));
    mocks.listStreams.mockImplementation(async (runId: string) => ({ kind: 'found', streams: [stream(runId === id(1) ? 0 : 7)] }));
    renderPage(`?left=${id(1)}&right=${id(2)}`);

    const left = await screen.findByRole('region', { name: 'Left run' });
    const right = screen.getByRole('region', { name: 'Right run' });
    expect(within(left).getByText(id(1))).toBeInTheDocument();
    expect(within(left).getByText(/Superseded by/)).toBeInTheDocument();
    expect(within(left).getByText(new RegExp(`run ${id(9)} \\(recording set ${SET_B}`))).toBeInTheDocument();
    expect(within(left).getByText('Superseded at')).toBeInTheDocument();
    expect(within(left).getByText(SET_A)).toBeInTheDocument();
    expect(within(left).getAllByText('default 3').length).toBeGreaterThan(0);
    expect(within(left).getByRole('link', { name: 'Open the detailed review of the Left run' })).toHaveAttribute('href', `/matches/${MATCH}/analysis-runs/${id(1)}`);
    expect(within(left).getByText('Facts').nextSibling).toHaveTextContent('0');
    expect(within(right).getByText('Facts').nextSibling).toHaveTextContent('7');
    expect(within(right).getByRole('link', { name: 'Open the detailed review of the Right run' })).toHaveAttribute('href', `/matches/${MATCH}/analysis-runs/${id(2)}`);
    expect(within(right).getByText('Yes')).toBeInTheDocument();
    for (const label of ['Accepted: 1', 'Missing: 2', 'Failed: 3', 'Blocked: 4', 'Skipped: 5']) expect(within(left).getByText(label)).toBeInTheDocument();
    expect(within(left).getByText('Provenance')).toBeInTheDocument();
    expect(screen.getByText(/do not establish that both runs analyzed the same full inputs/)).toBeInTheDocument();
  });

  it('retains the non-current, non-superseded state', async () => {
    mocks.getRun.mockImplementation(async (runId: string) => ({ kind: 'found', run: status(runId === id(1) ? 1 : 2, SET_A) }));
    mocks.listStreams.mockResolvedValue({ kind: 'found', streams: [] });
    renderPage(`?left=${id(1)}&right=${id(2)}`);
    const left = await screen.findByRole('region', { name: 'Left run' });
    expect(within(left).getByText('Not the current run and not superseded')).toBeInTheDocument();
    expect(within(left).getByText(/reported an empty list of fact streams/)).toBeInTheDocument();
  });

  it('renders no result details until the whole pair is ready', async () => {
    mocks.getRun.mockImplementation(async (runId: string) => ({ kind: 'found', run: status(runId === id(1) ? 1 : 2, SET_A) }));
    mocks.listStreams.mockImplementation(async (runId: string) => {
      if (runId === id(2)) throw new PlatformFailure('incompatible');
      return { kind: 'found', streams: [stream(1)] };
    });
    renderPage(`?left=${id(1)}&right=${id(2)}`);
    expect(await screen.findByText(/does not fit this comparison/)).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Left run' })).not.toBeInTheDocument();
    expect(screen.queryByText('Fact streams of the Left run')).not.toBeInTheDocument();
  });

  it('explains an in-progress side instead of comparing', async () => {
    mocks.getRun.mockImplementation(async (runId: string) => ({ kind: 'found', run: status(runId === id(1) ? 1 : 2, SET_A, runId === id(2) ? { state: 'Running' } : {}) }));
    renderPage(`?left=${id(1)}&right=${id(2)}`);
    expect(await screen.findByText('The Right run is still in progress and cannot be compared yet.')).toBeInTheDocument();
    expect(mocks.listStreams).not.toHaveBeenCalled();
  });
  describe('honest differences and availability', () => {
    const node = (nodeKey: string, over: Record<string, unknown> = {}) => ({
      nodeKey, capabilityId: 'cap', capabilityVersion: '1.0.0', tier: 'a', executionScope: 'match', state: 'Completed',
      jobs: { waiting: 0, ready: 0, claimed: 0, completed: 1, failed: 0, blocked: 0 }, ...over,
    });
    const dep = (key: string, over: Record<string, unknown> = {}) => ({ upstreamNodeKey: key, coverageMode: 'complete', accepted: 2, missing: 0, failed: 0, blocked: 0, skipped: 0, ...over });
    const withCoverage = (factCount: number, coverage: Record<string, number>, over: Record<string, unknown> = {}) => ({ ...stream(factCount), coverage, ...over });
    const setup = (left: { run?: Record<string, unknown>; streams: unknown[] }, right: { run?: Record<string, unknown>; streams: unknown[] }) => {
      const runs: Record<string, unknown> = {
        [id(1)]: status(1, SET_A, left.run),
        [id(2)]: status(2, SET_B, right.run),
      };
      mocks.getRun.mockImplementation(async (runId: string) => ({ kind: 'found', run: runs[runId] }));
      mocks.listStreams.mockImplementation(async (runId: string) => ({ kind: 'found', streams: runId === id(1) ? left.streams : right.streams }));
      renderPage(`?left=${id(1)}&right=${id(2)}`);
    };

    it('aligns a unique pair, shows differences, pair context and permanent wording', async () => {
      const r = withCoverage(5, { accepted: 3, missing: 0, failed: 0, blocked: 0, skipped: 0 }, { schemaVersion: '2' });
      setup(
        { run: { nodes: [node('n1', { inputCoverage: [dep('up1'), dep('up2')] })] }, streams: [withCoverage(0, { accepted: 0, missing: 0, failed: 0, blocked: 0, skipped: 4 })] },
        { run: { nodes: [node('n1', { inputCoverage: [dep('up1', { coverageMode: 'weird', minimumAcceptedFraction: 0.5 }), dep('up3')] })] }, streams: [r] },
      );
      const align = await screen.findByRole('region', { name: 'Alignment by capability and scope' });
      expect(within(align).getByText(/navigation aid/)).toBeInTheDocument();
      expect(within(align).getByText('Schema version: left 1, right 2')).toBeInTheDocument();
      expect(within(align).getByText(/Every expected unit was skipped/)).toBeInTheDocument();
      expect(within(align).getByText(/Facts with complete reported coverage\./)).toBeInTheDocument();
      expect(within(align).getByText(/Dependency up1: differs in policy, minimum accepted fraction/)).toBeInTheDocument();
      expect(within(align).getByText(/weird \(unsupported policy, shown as reported\)/, { exact: false })).toBeInTheDocument();
      expect(within(align).getByText(/Dependency up2: reported only by the left run/)).toBeInTheDocument();
      expect(within(align).getByText(/Dependency up3: reported only by the right run/)).toBeInTheDocument();
      expect(screen.getByText(/The recording sets differ/)).toBeInTheDocument();
      expect(screen.getByText(/never shows that two runs had equal full inputs/)).toBeInTheDocument();
      expect(screen.queryByText(/better|worse/i, { selector: 'li' })).not.toBeInTheDocument();
    });

    it('explains unmatched and ambiguous streams without fabricated counterparts', async () => {
      const a = withCoverage(1, { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 }, { nodeKey: 'a1' });
      const b = withCoverage(1, { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 }, { nodeKey: 'a2' });
      const c = withCoverage(1, { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 }, { nodeKey: 'c1', capabilityId: 'other' });
      setup(
        { run: { nodes: [node('a1'), node('a2')] }, streams: [a, b] },
        { run: { nodes: [node('c1', { capabilityId: 'other' })] }, streams: [c] },
      );
      const align = await screen.findByRole('region', { name: 'Alignment by capability and scope' });
      expect(within(align).getByText(/Left only: a1/)).toBeInTheDocument();
      expect(within(align).getByText(/Left only: a2/)).toBeInTheDocument();
      expect(within(align).getByText(/Right only: c1/)).toBeInTheDocument();
      expect(within(align).getAllByText(/no pairing was guessed/)).toHaveLength(2);
      expect(within(align).getByText(/No counterpart values are shown/)).toBeInTheDocument();
    });

    it('distinguishes not-reported and empty input coverage and explains availability per stream', async () => {
      setup(
        { run: { nodes: [node('n1')] }, streams: [withCoverage(0, { accepted: 0, missing: 0, failed: 0, blocked: 0, skipped: 0 })] },
        { run: { nodes: [node('n1', { inputCoverage: [] })] }, streams: [withCoverage(0, { accepted: 2, missing: 1, failed: 0, blocked: 0, skipped: 0 })] },
      );
      const left = await screen.findByRole('region', { name: 'Left run' });
      const right = screen.getByRole('region', { name: 'Right run' });
      expect(within(left).getByText(/Missing\/null input coverage means not reported, not zero coverage/)).toBeInTheDocument();
      expect(within(left).getByText(/No expected execution units were reported/)).toBeInTheDocument();
      expect(within(right).getByText(/reported an empty dependency list/)).toBeInTheDocument();
      expect(within(right).getByText(/absence of facts proves nothing/)).toBeInTheDocument();
      expect(within(right).getByText('Facts').nextSibling).toHaveTextContent('0');
    });

    it('states an explicit empty stream list and keeps model digest absence explicit', async () => {
      setup({ streams: [] }, { streams: [] });
      expect((await screen.findAllByText(/reported an empty list of fact streams/)).length).toBe(2);
      expect(screen.getByText('There are no streams to align.')).toBeInTheDocument();
    });
  });
});
