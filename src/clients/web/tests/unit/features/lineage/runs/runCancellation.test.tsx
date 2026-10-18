import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import type { ClubRole, TeamRoleName } from '../../../../../src/platform/models/session';
import { fakeAdapter, makeSession, renderApp } from '../../../session/helpers';

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } });
const SEASON = '00000000-0000-4000-8000-000000000002';
const TEAM = '00000000-0000-4000-8000-000000000003';
const MATCH = '00000000-0000-4000-8000-000000000021';
const V1 = '00000000-0000-4000-8000-0000000000a1';
const M1 = '00000000-0000-4000-8000-0000000000b1';
const SET = '00000000-0000-4000-8000-0000000000c1';
const RUN = (n: number) => `00000000-0000-4000-8000-00000000030${n}`;
const digest = (c: string) => `sha256:${c.repeat(64)}`;

const lineage = {
  matchId: MATCH,
  teamId: TEAM,
  recordingVersions: [],
  timelineMappings: [],
  recordingSetVersions: [
    {
      id: SET,
      matchId: MATCH,
      teamId: TEAM,
      finalizedAt: '2026-10-13T12:00:00Z',
      members: [{ position: 0, recordingVersionId: V1, recordingContentDigest: digest('a'), timelineMappingId: M1, timelineMappingDigest: digest('b') }],
    },
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
const runDto = (n: number, state: string, extra: Record<string, unknown> = {}) => ({
  runId: RUN(n),
  matchId: MATCH,
  teamId: TEAM,
  recordingSetVersionId: SET,
  workflowId: `flow${n}`,
  workflowVersion: '1',
  state,
  failureReason: null,
  createdAt: '2026-10-14T12:00:00Z',
  runningAt: null,
  terminatedAt: null,
  version: 2,
  ...extra,
});
const cancelledRun = (extra: Record<string, unknown> = {}) =>
  runDto(1, 'Cancelled', { terminatedAt: '2026-10-14T13:00:00Z', version: 3, cancelledBy: { memberId: '00000000-0000-4000-8000-0000000000aa', accountName: 'ada' }, ...extra });

const asRole = (role: TeamRoleName) =>
  fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ teamRoles: [{ teamId: TEAM, teamName: 'First Team', seasonId: SEASON, role }] })) });
const asClub = (role: ClubRole) => fakeAdapter({ restore: vi.fn().mockResolvedValue(makeSession({ clubRoles: [role] })) });

/** `refuse` answers the cancellation with a failure, after which the list shows `afterRefusal`. */
function stub(options: { runs?: unknown[]; refuse?: Response; afterRefusal?: unknown[]; cancelled?: unknown } = {}) {
  let runs = options.runs ?? [runDto(1, 'Running')];
  const posts: { url: string; init: RequestInit }[] = [];
  let lists = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      if (init.method === 'POST') {
        posts.push({ url, init });
        if (options.refuse) {
          if (options.afterRefusal) runs = options.afterRefusal;
          return options.refuse;
        }
        const body = options.cancelled ?? cancelledRun();
        runs = runs.map((r) => ((r as { runId: string }).runId === (body as { runId: string }).runId ? body : r));
        return json(200, body);
      }
      if (url.includes('recording-lineage')) return json(200, lineage);
      if (url.includes('analysis-runs')) {
        lists++;
        return json(200, { items: runs, continuationToken: null });
      }
      if (url.includes('/matches/')) return json(200, matchDto);
      return json(200, team);
    }),
  );
  return { posts, lists: () => lists };
}
const header = (init: RequestInit, name: string) => new Headers(init.headers as Record<string, string>).get(name);
const bodyOf = (init: RequestInit) => JSON.parse(new TextDecoder().decode(init.body as ArrayBuffer));
const open = async () => userEvent.click(await screen.findByRole('button', { name: 'Cancel run' }));
const confirm = () => userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel run' }));

afterEach(() => vi.unstubAllGlobals());

describe('cancel action visibility', () => {
  it('is offered to a Coach of the team for Requested and Running runs only', async () => {
    stub({ runs: [runDto(1, 'Requested'), runDto(2, 'Running'), runDto(3, 'Succeeded'), runDto(4, 'Failed'), runDto(5, 'Cancelled')] });
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    const list = await screen.findByRole('list', { name: 'Analysis runs' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(5);
    expect(items.map((i) => within(i).queryByRole('button', { name: 'Cancel run' }) !== null)).toEqual([true, true, false, false, false]);
  });

  it('is offered to a Club Admin', async () => {
    stub();
    renderApp(asClub('ClubAdmin'), `/matches/${MATCH}`);
    expect(await screen.findByRole('button', { name: 'Cancel run' })).toBeInTheDocument();
  });

  it('is not offered to a Viewer of the team', async () => {
    stub();
    renderApp(asRole('Viewer'), `/matches/${MATCH}`);
    expect(await screen.findByText(/State: Running/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel run' })).toBeNull();
  });
});

describe('cancel dialog', () => {
  it('focuses dismiss, explains the consequences, and returns focus to the action on Escape without a request', async () => {
    const { posts } = stub();
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    const action = await screen.findByRole('button', { name: 'Cancel run' });
    await userEvent.click(action);
    const dialog = await screen.findByRole('dialog', { name: 'Cancel this analysis run?' });
    expect(dialog).toHaveTextContent('cannot be undone');
    expect(dialog).toHaveTextContent('already accepted remain');
    expect(within(dialog).getByRole('button', { name: 'Keep the run' })).toHaveFocus();
    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(action).toHaveFocus());
    expect(posts).toHaveLength(0);
    expect(screen.getByRole('list', { name: 'Analysis runs' })).toHaveTextContent('State: Running');
  });

  it('changes nothing when dismissed', async () => {
    const { posts } = stub();
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    await open();
    await userEvent.click(screen.getByRole('button', { name: 'Keep the run' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(posts).toHaveLength(0);
    expect(screen.getByRole('button', { name: 'Cancel run' })).toBeInTheDocument();
  });

  it('limits the reason to 500 characters and disables confirmation beyond it', async () => {
    const { posts } = stub();
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    await open();
    const dialog = await screen.findByRole('dialog');
    const reason = within(dialog).getByLabelText('Reason (optional)');
    const button = within(dialog).getByRole('button', { name: 'Cancel run' });
    fireEvent.change(reason, { target: { value: 'x'.repeat(500) } });
    expect(dialog).toHaveTextContent('500 / 500 characters');
    expect(button).toBeEnabled();
    fireEvent.change(reason, { target: { value: 'x'.repeat(501) } });
    expect(dialog).toHaveTextContent('too long: 501 of at most 500');
    expect(button).toBeDisabled();
    fireEvent.click(button);
    expect(posts).toHaveLength(0);
  });
});

describe('cancelling a run', () => {
  it('sends If-Match and the trimmed reason and shows the cancelled run with the reason as text', async () => {
    const reason = 'wrong <b>set</b>';
    const { posts } = stub({ cancelled: cancelledRun({ cancellationReason: reason }) });
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    await open();
    await userEvent.type(screen.getByLabelText('Reason (optional)'), `  ${reason}  `);
    await confirm();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(posts).toHaveLength(1);
    expect(posts[0].url).toContain(`/api/v1/analysis/runs/${RUN(1)}/cancellation`);
    expect(header(posts[0].init, 'If-Match')).toBe('"2"');
    expect(bodyOf(posts[0].init)).toEqual({ reason });
    const list = screen.getByRole('list', { name: 'Analysis runs' });
    await waitFor(() => expect(list).toHaveTextContent(/Cancelled · by ada · .+ · wrong <b>set<\/b>/));
    expect(list.querySelector('b')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Cancel run' })).toBeNull();
  });

  it('sends no reason when none was entered and omits it from the display', async () => {
    const { posts } = stub();
    renderApp(asClub('ClubAdmin'), `/matches/${MATCH}`);
    await open();
    await confirm();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    const list = screen.getByRole('list', { name: 'Analysis runs' });
    await waitFor(() => expect(list).toHaveTextContent(/Cancelled · by ada/));
    expect(bodyOf(posts[0].init)).toEqual({ reason: null });
    expect(list).not.toHaveTextContent('State:');
    expect(list).not.toHaveTextContent(/Cancelled · by ada · [^·]+ · [^·]+Workflow/);
  });

  it.each([412, 409])('shows the conflict message and refreshes the list after a %i', async (status) => {
    const { lists } = stub({
      refuse: json(status, { status, code: 'conflict' }),
      afterRefusal: [runDto(1, 'Succeeded', { terminatedAt: '2026-10-14T13:00:00Z', version: 3 })],
    });
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    await open();
    const before = lists();
    await confirm();
    expect(await screen.findByText(/changed by someone else/)).toBeInTheDocument();
    await waitFor(() => expect(lists()).toBeGreaterThan(before));
    await waitFor(() => expect(screen.getByRole('list', { name: 'Analysis runs' })).toHaveTextContent('State: Succeeded'));
    expect(screen.queryByRole('button', { name: 'Cancel run' })).toBeNull();
  });

  it('shows a refusal of permission and closes the dialog', async () => {
    stub({ refuse: json(403, { status: 403, code: 'forbidden' }) });
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    await open();
    await confirm();
    expect(await screen.findByRole('alert')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('keeps the dialog open and shows the platform reason error for a rejected reason', async () => {
    stub({ refuse: json(400, { status: 400, code: 'validation-failed', errors: [{ field: 'reason', code: 'too-long' }] }) });
    renderApp(asRole('Coach'), `/matches/${MATCH}`);
    await open();
    await userEvent.type(screen.getByLabelText('Reason (optional)'), 'because');
    await confirm();
    expect(await within(screen.getByRole('dialog')).findByText('This value is too long.')).toBeInTheDocument();
  });
});
