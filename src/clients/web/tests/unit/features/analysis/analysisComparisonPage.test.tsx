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

const mocks = vi.hoisted(() => ({ getMatch: vi.fn(), getTeam: vi.fn(), listByMatch: vi.fn() }));

vi.mock('../../../../src/platform/adapters/matchAdapter', () => ({ createMatchAdapter: () => ({ get: mocks.getMatch }) }));
vi.mock('../../../../src/platform/adapters/teamAdapter', () => ({ createTeamAdapter: () => ({ get: mocks.getTeam }) }));
vi.mock('../../../../src/platform/adapters/analysisRunAdapter', () => ({ createAnalysisRunAdapter: () => ({ listByMatch: mocks.listByMatch }) }));

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
