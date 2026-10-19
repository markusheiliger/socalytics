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

const provenance = { workflow: { id: 'default', version: '3' }, analystProfile: { id: 'p', digest: 'sha256:a' }, imageDigest: 'sha256:b' };
const streamsDto = {
  items: [
    {
      nodeKey: 'detect-1',
      capability: { id: 'detect', version: '1.2' },
      schemaVersion: '1',
      outputType: 'foundational-facts',
      executionScope: 'Segment',
      required: true,
      factCount: 1,
      provenance,
      coverage: { accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 },
    },
  ],
};
const factsDto = {
  run: { runId: RUN, matchId: MATCH, state: 'Completed' },
  stream: { nodeKey: 'detect-1', capability: { id: 'detect', version: '1.2' }, schemaVersion: '1', outputType: 'foundational-facts', executionScope: 'Segment', provenance },
  range: { fromMs: 0, toMs: 100000 },
  items: [{ acceptedResultId: '00000000-0000-4000-8000-0000000000e1', ordinal: 0, kind: 'pass', startMs: 1000, endMs: 2000, confidence: 0.5, payload: { a: 1 } }],
  coverage: [{ startMs: 0, endMs: 60000, state: 'accepted' }],
};

function stub(runAnswer: () => Response, match: unknown = matchDto, factUrls: string[] = []) {
  const runCalls = vi.fn();
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      if (url.includes('/fact-streams')) return json(200, streamsDto);
      if (url.includes('/facts')) {
        factUrls.push(url);
        return json(200, factsDto);
      }
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

  it('lists the streams and selects one from and to the address', async () => {
    const factUrls: string[] = [];
    stub(() => json(200, runDto()), matchDto, factUrls);
    const { router } = renderApp(asRole('Coach'), path());
    await userEvent.click(await screen.findByRole('button', { name: 'Show facts of detect-1' }));
    expect(await screen.findByText('pass')).toBeInTheDocument();
    expect(router.state.location.search).toBe('?stream=detect-1');
    await userEvent.type(screen.getByLabelText('From'), '0:10');
    await userEvent.type(screen.getByLabelText('To'), '0:20');
    await userEvent.click(screen.getByRole('button', { name: 'Apply range' }));
    await waitFor(() => expect(router.state.location.search).toBe('?stream=detect-1&fromMs=10000&toMs=20000'));
    await waitFor(() => expect(factUrls.at(-1)).toContain('fromMs=10000'));
    await router.navigate(-1);
    await waitFor(() => expect(router.state.location.search).toBe('?stream=detect-1'));
  });

  it('opens the stream and range of the address', async () => {
    const factUrls: string[] = [];
    stub(() => json(200, runDto()), matchDto, factUrls);
    renderApp(asRole('Viewer'), `${path()}?stream=detect-1&fromMs=5000&toMs=9000`);
    expect(await screen.findByText('pass')).toBeInTheDocument();
    expect(factUrls[0]).toContain('fromMs=5000');
    expect(factUrls[0]).toContain('toMs=9000');
    expect(screen.getByLabelText('From')).toHaveValue('0:05');
    expect(screen.getByLabelText('To')).toHaveValue('0:09');
  });

  it('explains an unknown stream and a malformed range without a fact request', async () => {
    const factUrls: string[] = [];
    stub(() => json(200, runDto()), matchDto, factUrls);
    const first = renderApp(asRole('Coach'), `${path()}?stream=nope`);
    expect(await screen.findByText(/not part of this run/)).toBeInTheDocument();
    first.router.dispose();
    renderApp(asRole('Coach'), `${path()}?stream=detect-1&fromMs=9000&toMs=5000`);
    expect(await screen.findByText(/range is not valid/)).toBeInTheDocument();
    expect(factUrls).toHaveLength(0);
  });

  it('offers new results of a running run and restarts at the first page on reload', async () => {
    let count = 1;
    let factCalls = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url.includes('/fact-streams')) return json(200, { items: [{ ...streamsDto.items[0], factCount: count }] });
        if (url.includes('/facts')) {
          factCalls += 1;
          return json(200, factsDto);
        }
        if (url.includes('/analysis/runs/')) return json(200, runDto({ state: 'Running' }));
        if (url.includes('/matches/')) return json(200, matchDto);
        return json(200, team);
      }),
    );
    renderApp(asRole('Coach'), `${path()}?stream=detect-1`);
    expect(await screen.findByText('pass')).toBeInTheDocument();
    expect(screen.queryByText('New results are available for this stream.')).toBeNull();
    const before = factCalls;
    count = 2;
    expect(await screen.findByText('New results are available for this stream.')).toBeInTheDocument();
    expect(screen.getByText('pass')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Reload the facts' }));
    await waitFor(() => expect(factCalls).toBeGreaterThan(before));
    await waitFor(() => expect(screen.queryByText('New results are available for this stream.')).toBeNull());
    expect(await screen.findByText('pass')).toBeInTheDocument();
  });

  it('shows the stream list for a stream that is not in the run', async () => {
    stub(() => json(200, runDto()));
    renderApp(asRole('Coach'), `${path()}?stream=gone`);
    expect(await screen.findByText(/not part of this run/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show facts of detect-1' })).toBeInTheDocument();
  });
});
