import { screen, waitFor, within } from '@testing-library/react';
import { vi } from 'vitest';
import type { ClubRole, TeamRoleName } from '../../../../../src/platform/models/session';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const MATCH = '00000000-0000-4000-8000-000000000021';
const V1 = '00000000-0000-4000-8000-0000000000a1';
const M1 = '00000000-0000-4000-8000-0000000000b1';
const SET1 = '00000000-0000-4000-8000-0000000000c1';
const SET2 = '00000000-0000-4000-8000-0000000000c2';
const RUN = (n: number) => `00000000-0000-4000-8000-00000000030${n}`;
const digest = (c: string) => `sha256:${c.repeat(64)}`;

const member = (position: number) => ({ position, recordingVersionId: V1, recordingContentDigest: digest('a'), timelineMappingId: M1, timelineMappingDigest: digest('b') });
const lineage = {
  matchId: MATCH,
  teamId: TEAM,
  recordingVersions: [],
  timelineMappings: [],
  recordingSetVersions: [
    { id: SET1, matchId: MATCH, teamId: TEAM, finalizedAt: '2026-10-13T12:00:00Z', members: [member(0)] },
    { id: SET2, matchId: MATCH, teamId: TEAM, finalizedAt: '2026-10-15T12:00:00Z', members: [member(0)] },
  ],
};
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
const runDto = (n: number, set: string, state: string, extra: Record<string, unknown> = {}) => ({
  runId: RUN(n),
  matchId: MATCH,
  teamId: TEAM,
  recordingSetVersionId: set,
  workflowId: `flow${n}`,
  workflowVersion: '1',
  state,
  failureReason: null,
  createdAt: '2026-10-14T12:00:00Z',
  runningAt: null,
  terminatedAt: null,
  version: 2,
  isCurrent: false,
  ...extra,
});
const supersededBy = { runId: RUN(2), recordingSetVersionId: SET2, recordingSetFinalizedAt: '2026-10-15T12:00:00Z' };
const marker = /Superseded by the run of the recording set finalized .+ · .+/;

const asRole = (role: TeamRoleName) =>
  fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [{ teamId: TEAM, teamName: 'First Team', seasonId: SEASON, role }] })) });
const asClub = (role: ClubRole) => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: [role] })) });

function stub(runs: unknown[]) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      if (url.includes('recording-lineage')) return json(200, lineage);
      if (url.includes('analysis-runs')) return json(200, { items: runs, continuationToken: null });
      if (url.includes('/matches/')) return json(200, matchDto);
      return json(200, team);
    }),
  );
}
const runsList = async () => within(await screen.findByRole('list', { name: 'Finalized recording sets' }));
const lineOf = async (n: number) => {
  const list = await runsList();
  await waitFor(() => expect(list.getAllByRole('listitem').length).toBeGreaterThan(0));
  const line = list.getAllByRole('listitem').find((i) => i.textContent?.includes(`flow${n} `));
  if (!line) throw new Error(`no line for run ${n}`);
  return line;
};

afterEach(() => vi.unstubAllGlobals());

describe('run supersession markers', () => {
  it('marks the current run as text inside the line status', async () => {
    stub([runDto(2, SET2, 'Running', { isCurrent: true })]);
    renderApp(asRole('Viewer'), `/matches/${MATCH}`);
    const line = await lineOf(2);
    const status = within(line).getByRole('status');
    expect(status).toHaveTextContent('State: Running');
    expect(status).toHaveTextContent('Current run');
    expect(line).not.toHaveTextContent('Superseded by');
  });

  it('marks a superseded run that ended before the supersession with the superseding set and time', async () => {
    stub([
      runDto(1, SET1, 'Succeeded', { terminatedAt: '2026-10-14T13:00:00Z', supersededBy, supersededAt: '2026-10-15T12:01:00Z' }),
      runDto(2, SET2, 'Running', { isCurrent: true }),
    ]);
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    const line = await lineOf(1);
    const status = within(line).getByRole('status');
    expect(status).toHaveTextContent('State: Succeeded');
    expect(status).toHaveTextContent(marker);
    expect(status).not.toHaveTextContent('Current run');
    expect(await lineOf(2)).toHaveTextContent('Current run');
  });

  it('keeps every superseded run listed in its recording set group', async () => {
    stub([
      runDto(1, SET1, 'Succeeded', { supersededBy, supersededAt: '2026-10-15T12:01:00Z' }),
      runDto(2, SET2, 'Running', { isCurrent: true }),
    ]);
    renderApp(asRole('Viewer'), `/matches/${MATCH}`);
    await lineOf(1);
    const groups = (await runsList()).getAllByRole('list', { name: 'Analysis runs' });
    expect(groups).toHaveLength(2);
    expect(within(groups[1]).getByRole('status')).toHaveTextContent(marker);
    expect(within(groups[0]).getByRole('status')).toHaveTextContent('Current run');
  });

  it('shows a run cancelled by supersession without member, reason, or cancel action', async () => {
    stub([
      runDto(1, SET1, 'Cancelled', { cancellationCause: 'superseded', terminatedAt: '2026-10-15T12:01:00Z', supersededBy, supersededAt: '2026-10-15T12:01:00Z' }),
      runDto(2, SET2, 'Running', { isCurrent: true }),
    ]);
    renderApp(asClub('ClubAdmin'), `/matches/${MATCH}`);
    const line = await lineOf(1);
    const status = within(line).getByRole('status');
    expect(status).toHaveTextContent('Cancelled (superseded)');
    expect(status).toHaveTextContent(marker);
    expect(status).not.toHaveTextContent('Cancelled · by');
    expect(line).not.toHaveTextContent('State:');
    expect(within(line).queryByRole('button', { name: 'Cancel run' })).toBeNull();
  });

  it('keeps the member cancellation details next to the superseded marker', async () => {
    stub([
      runDto(1, SET1, 'Cancelled', {
        cancellationCause: 'member',
        cancelledBy: { memberId: '00000000-0000-4000-8000-0000000000aa', accountName: 'ada' },
        cancellationReason: 'wrong set',
        terminatedAt: '2026-10-14T13:00:00Z',
        supersededBy,
        supersededAt: '2026-10-15T12:01:00Z',
      }),
      runDto(2, SET2, 'Running', { isCurrent: true }),
    ]);
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    const status = within(await lineOf(1)).getByRole('status');
    expect(status).toHaveTextContent(/Cancelled · by ada · .+ · wrong set/);
    expect(status).toHaveTextContent(marker);
    expect(status).not.toHaveTextContent('Cancelled (superseded)');
  });

  it('shows runs without markers as before', async () => {
    stub([runDto(1, SET1, 'Succeeded'), runDto(2, SET2, 'Failed', { failureReason: 'node-failed' })]);
    renderApp(asRole('Viewer'), `/matches/${MATCH}`);
    const line = await lineOf(1);
    expect(within(line).getByRole('status')).toHaveTextContent(/^State: Succeeded$/);
    const list = await runsList();
    expect(list.queryByText(/Current run/)).toBeNull();
    expect(list.queryByText(marker)).toBeNull();
    expect(list.queryByText(/Cancelled \(superseded\)/)).toBeNull();
  });
});
