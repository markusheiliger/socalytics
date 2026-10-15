import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { isIdenticalToNewest } from '../../../../../src/features/lineage/FinalizePage';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const MATCH = '00000000-0000-4000-8000-000000000021';
const V1 = '00000000-0000-4000-8000-0000000000a1';
const V2 = '00000000-0000-4000-8000-0000000000a2';
const M1 = '00000000-0000-4000-8000-0000000000b1';
const M1NEW = '00000000-0000-4000-8000-0000000000b3';
const M2 = '00000000-0000-4000-8000-0000000000b2';
const SET = '00000000-0000-4000-8000-0000000000c1';
const RUN = '00000000-0000-4000-8000-0000000000d1';
const digest = (c: string) => `sha256:${c.repeat(64)}`;

const version = (id: string, name: string) => ({
  id,
  matchId: MATCH,
  teamId: TEAM,
  uploadSessionId: '00000000-0000-4000-8000-0000000000e1',
  recording: { displayName: name, contentType: 'video/mp4' },
  totalSizeBytes: 1024,
  partSizeBytes: 1024,
  partCount: 1,
  contentDigest: digest('a'),
  createdAt: '2026-10-12T12:00:00Z',
});
const mapping = (id: string, versionId: string, createdAt: string) => ({
  id,
  recordingVersionId: versionId,
  matchId: MATCH,
  spans: [{ mediaStartSeconds: 0, mediaEndSeconds: 60, matchStartSeconds: 600 }],
  digest: digest('b'),
  createdAt,
});
const setOf = (members: [string, string][]) => ({
  id: SET,
  matchId: MATCH,
  teamId: TEAM,
  finalizedAt: '2026-10-13T12:00:00Z',
  members: members.map(([v, m], position) => ({ position, recordingVersionId: v, recordingContentDigest: digest('a'), timelineMappingId: m, timelineMappingDigest: digest('b') })),
});
const lineage = (sets: unknown[] = []) => ({
  matchId: MATCH,
  teamId: TEAM,
  recordingVersions: [version(V1, 'Main camera'), version(V2, 'Second angle')],
  timelineMappings: [mapping(M1, V1, '2026-10-12T12:00:00Z'), mapping(M1NEW, V1, '2026-10-12T13:00:00Z'), mapping(M2, V2, '2026-10-12T12:00:00Z')],
  recordingSetVersions: sets,
});
const run = (state: string, extra = {}) => ({
  runId: RUN,
  teamId: TEAM,
  matchId: MATCH,
  recordingSetVersionId: SET,
  workflowId: 'default',
  workflowVersion: '1',
  state,
  createdAt: '2026-10-13T12:01:00Z',
  runningAt: null,
  terminatedAt: null,
  failureReason: null,
  version: 1,
  ...extra,
});
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
const coach = () => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [{ teamId: TEAM, teamName: 'First Team', seasonId: SEASON, role: 'Coach' }] })) });

interface Setup {
  sets?: unknown[];
  runs?: unknown[];
  post?: (init: RequestInit) => Response | never;
}
function stub({ sets = [], runs = [], post }: Setup = {}) {
  const posts: RequestInit[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      if (init.method === 'POST') {
        posts.push(init);
        return post ? post(init) : json(201, setOf([[V1, M1NEW]]));
      }
      if (url.includes('recording-lineage')) return json(200, lineage(sets));
      if (url.includes('analysis-runs')) return json(200, { items: runs, continuationToken: null });
      if (url.includes('/matches/')) return json(200, matchDto);
      return json(200, team);
    }),
  );
  return posts;
}
const bodyOf = (init: RequestInit) => JSON.parse(new TextDecoder().decode(init.body as ArrayBuffer));
const keyOf = (init: RequestInit) => new Headers(init.headers as Record<string, string>).get('Idempotency-Key');

afterEach(() => vi.unstubAllGlobals());

describe('isIdenticalToNewest', () => {
  const set = setOf([[V1, M1], [V2, M2]]) as never;
  it('compares order, versions, and mappings with the newest set', () => {
    const sets = [{ ...(set as object), finalizedAt: new Date('2026-10-13T12:00:00Z'), members: [{ position: 1, recordingVersionId: V2, timelineMappingId: M2 }, { position: 0, recordingVersionId: V1, timelineMappingId: M1 }] }] as never;
    expect(isIdenticalToNewest([{ recordingVersionId: V1, timelineMappingId: M1 }, { recordingVersionId: V2, timelineMappingId: M2 }], sets)).toBe(true);
    expect(isIdenticalToNewest([{ recordingVersionId: V2, timelineMappingId: M2 }, { recordingVersionId: V1, timelineMappingId: M1 }], sets)).toBe(false);
    expect(isIdenticalToNewest([{ recordingVersionId: V1, timelineMappingId: M1NEW }, { recordingVersionId: V2, timelineMappingId: M2 }], sets)).toBe(false);
    expect(isIdenticalToNewest([], [])).toBe(false);
  });
});

describe('finalize view', () => {
  it('sends the chosen order with the newest mapping as default and one key per dialog, reused on retry', async () => {
    let attempts = 0;
    const posts = stub({ post: () => {
        if (++attempts === 1) throw new TypeError('Failed to fetch');
        return json(201, setOf([[V2, M2], [V1, M1NEW]]));
      },
    });
    renderApp(coach(), `/matches/${MATCH}/finalize`);
    await userEvent.click(await screen.findByLabelText('Include Second angle'));
    await userEvent.click(screen.getByLabelText('Include Main camera'));
    expect(screen.getByLabelText('Timeline mapping for Main camera')).toHaveValue(M1NEW);
    await userEvent.click(screen.getByRole('button', { name: 'Review and finalize' }));
    await userEvent.click(screen.getByRole('button', { name: 'Finalize' }));
    await screen.findByRole('button', { name: 'Try again' });
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(posts).toHaveLength(2));
    expect(bodyOf(posts[0])).toEqual({ members: [{ recordingVersionId: V2, timelineMappingId: M2 }, { recordingVersionId: V1, timelineMappingId: M1NEW }] });
    expect(keyOf(posts[0])).toBeTruthy();
    expect(keyOf(posts[1])).toBe(keyOf(posts[0]));
  });

  it('does not send an empty selection', async () => {
    const posts = stub();
    renderApp(coach(), `/matches/${MATCH}/finalize`);
    expect(await screen.findByText('Select at least one recording.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review and finalize' })).toBeDisabled();
    expect(posts).toHaveLength(0);
  });

  it('requires explicit confirmation for a selection identical to the newest set; cancelling sends nothing', async () => {
    const posts = stub({ sets: [setOf([[V1, M1NEW]])] });
    renderApp(coach(), `/matches/${MATCH}/finalize`);
    await userEvent.click(await screen.findByLabelText('Include Main camera'));
    await userEvent.click(screen.getByRole('button', { name: 'Review and finalize' }));
    expect(screen.getByRole('alert')).toHaveTextContent('starts another analysis');
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(posts).toHaveLength(0);
    await userEvent.click(await screen.findByRole('button', { name: 'Review and finalize' }));
    await userEvent.click(screen.getByRole('button', { name: 'Finalize and start another analysis' }));
    await waitFor(() => expect(posts).toHaveLength(1));
  });

  it('shows the platform refusal and creates nothing client side', async () => {
    stub({ post: () => json(400, { code: 'recording-set-too-large' }) });
    renderApp(coach(), `/matches/${MATCH}/finalize`);
    await userEvent.click(await screen.findByLabelText('Include Main camera'));
    await userEvent.click(screen.getByRole('button', { name: 'Review and finalize' }));
    await userEvent.click(screen.getByRole('button', { name: 'Finalize' }));
    expect(await screen.findByText(/The platform refused the input/)).toBeInTheDocument();
  });
});

describe('analysis run states', () => {
  it('groups runs under their set with state, workflow, timestamps, and failure reason', async () => {
    stub({ sets: [setOf([[V1, M1]])], runs: [run('Failed', { failureReason: 'workflow-failed', terminatedAt: '2026-10-13T12:05:00Z' })] });
    renderApp(coach(), `/matches/${MATCH}`);
    const sets = await screen.findByRole('list', { name: 'Finalized recording sets' });
    const status = await within(sets).findByRole('status');
    expect(status).toHaveTextContent('State: Failed');
    expect(sets).toHaveTextContent('Workflow default 1');
    expect(sets).toHaveTextContent('Reason: workflow-failed');
    expect(sets).toHaveTextContent('Ended');
  });

  it('says so when a set has no run yet', async () => {
    stub({ sets: [setOf([[V1, M1]])] });
    renderApp(coach(), `/matches/${MATCH}`);
    expect(await screen.findByText('No analysis run has been created for this set yet.')).toBeInTheDocument();
  });

  it('refetches every 10 s only while a run is Requested or Running', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      stub({ sets: [setOf([[V1, M1]])], runs: [run('Running')] });
      renderApp(coach(), `/matches/${MATCH}`);
      await screen.findByText(/State: Running/);
      const count = () => vi.mocked(fetch).mock.calls.filter(([url]) => String(url).includes('analysis-runs')).length;
      const before = count();
      await vi.advanceTimersByTimeAsync(10_500);
      expect(count()).toBeGreaterThan(before);
    } finally {
      vi.useRealTimers();
    }
  });

  it('does not refetch once all runs are terminal', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      stub({ sets: [setOf([[V1, M1]])], runs: [run('Completed')] });
      renderApp(coach(), `/matches/${MATCH}`);
      await screen.findByText(/State: Completed/);
      const count = () => vi.mocked(fetch).mock.calls.filter(([url]) => String(url).includes('analysis-runs')).length;
      const before = count();
      await vi.advanceTimersByTimeAsync(25_000);
      expect(count()).toBe(before);
    } finally {
      vi.useRealTimers();
    }
  });
});
