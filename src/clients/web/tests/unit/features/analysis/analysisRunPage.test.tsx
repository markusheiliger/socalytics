import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import type { TeamRoleName } from '../../../../src/platform/models/session';
import { fakeAdapter, makeSession, renderApp } from '../../session/helpers';

vi.mock('../../../../src/features/lineage/AnalysisRunStates', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../../src/features/lineage/AnalysisRunStates')>()),
  RUN_REFRESH_MS: 40,
}));

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const MATCH = '00000000-0000-4000-8000-000000000021';
const OTHER_MATCH = '00000000-0000-4000-8000-000000000022';
const RUN = '00000000-0000-4000-8000-0000000000d1';
const SET = '00000000-0000-4000-8000-0000000000c1';

const matchDto = {
  id: MATCH,
  teamId: TEAM,
  opponent: { name: 'Rivals FC' },
  kickoffAt: '2026-11-01T15:00:00Z',
  homeAway: 'Home',
  competition: 'League',
  createdAt: '2026-10-12T12:00:00Z',
  version: 1,
};
const team = { id: TEAM, seasonId: SEASON, seasonState: 'Active', name: 'First Team', createdAt: '2026-10-12T12:00:00Z', version: 1 };
const runDto = (over: Record<string, unknown> = {}) => ({
  runId: RUN,
  teamId: TEAM,
  matchId: MATCH,
  recordingSetVersionId: SET,
  workflowId: 'default',
  workflowVersion: '3',
  state: 'Completed',
  createdAt: '2026-10-12T12:00:00Z',
  requestedCapabilities: ['detect'],
  nodes: [
    {
      nodeKey: 'detect-1',
      capabilityId: 'detect',
      capabilityVersion: '1.2',
      tier: 'LowLevel',
      executionScope: 'Segment',
      state: 'Completed',
      jobs: { waiting: 0, ready: 0, claimed: 0, completed: 4, failed: 0, blocked: 0 },
    },
  ],
  ...over,
});

const asRole = (role?: TeamRoleName) =>
  fakeAdapter({
    restore: vi.fn().mockResolvedValue(
      makeSession({ teamRoles: role ? [{ teamId: TEAM, teamName: 'First Team', seasonId: SEASON, role }] : [] }),
    ),
  });

function stub(runAnswer: () => Response, match: unknown = matchDto) {
  const runCalls = vi.fn();
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      if (url.includes('/analysis/runs/')) {
        runCalls();
        return runAnswer();
      }
      if (url.includes('/matches/')) return json(200, match);
      return json(200, team);
    }),
  );
  return runCalls;
}

const path = (matchId = MATCH) => `/matches/${matchId}/analysis-runs/${RUN}`;

afterEach(() => vi.unstubAllGlobals());

describe('analysis run page', () => {
  it('shows run details and a back link to the match for a team member', async () => {
    stub(() => json(200, runDto()));
    renderApp(asRole('Viewer'), path());
    expect(await screen.findByText('default 3')).toBeInTheDocument();
    expect(screen.getByText('detect 1.2')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to the match' })).toHaveAttribute('href', `/matches/${MATCH}`);
  });

  it('shows the not-permitted view without a role on the match team', async () => {
    const calls = stub(() => json(200, runDto()));
    renderApp(asRole(), path());
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
    expect(calls).not.toHaveBeenCalled();
  });

  it('shows the not-permitted view when the platform answers 403', async () => {
    stub(() => json(403, { code: 'forbidden' }));
    renderApp(asRole('Coach'), path());
    expect(await screen.findByText('You are not permitted to open this page.')).toBeInTheDocument();
  });

  it('shows the not-found view for an unknown run', async () => {
    stub(() => json(404, { code: 'analysis-run-not-found' }));
    renderApp(asRole('Coach'), path());
    expect(await screen.findByText('This page does not exist.')).toBeInTheDocument();
    expect(screen.getAllByRole('banner')).toHaveLength(1);
  });

  it('shows the not-found view for a run of another match', async () => {
    stub(() => json(200, runDto({ matchId: OTHER_MATCH })));
    renderApp(asRole('Coach'), path());
    expect(await screen.findByText('This page does not exist.')).toBeInTheDocument();
    expect(screen.queryByText('default 3')).toBeNull();
  });

  it('shows a failure with reload for other failures and recovers on reload', async () => {
    let fail = true;
    stub(() => (fail ? json(500, { code: 'boom' }) : json(200, runDto())));
    renderApp(asRole('Coach'), path());
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }));
    expect(await screen.findByText('default 3')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('refreshes while the run is in progress and stops once it ends', async () => {
    let state = 'Running';
    const calls = stub(() => json(200, runDto({ state })));
    renderApp(asRole('Coach'), path());
    await waitFor(() => expect(calls.mock.calls.length).toBeGreaterThan(2));
    state = 'Completed';
    await waitFor(() => expect(screen.getByText('The run is now Completed.')).toBeInTheDocument());
    await new Promise((resolve) => setTimeout(resolve, 150));
    const settled = calls.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(calls.mock.calls.length).toBe(settled);
  });

  it('does not refresh a run that has already ended', async () => {
    const calls = stub(() => json(200, runDto()));
    renderApp(asRole('Coach'), path());
    await screen.findByText('default 3');
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(calls).toHaveBeenCalledTimes(1);
  });
});
