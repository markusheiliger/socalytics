import { createAnalysisRunAdapter } from '../../../../src/platform/adapters/analysisRunAdapter';
import { bodyOf, header, ids, ok, problem, setup } from './support';

const run = {
  runId: ids.run,
  matchId: ids.match,
  teamId: ids.team,
  recordingSetVersionId: ids.set,
  workflowId: 'default',
  workflowVersion: '1',
  state: 'Running',
  failureReason: null,
  createdAt: '2026-10-01T12:00:00Z',
  runningAt: '2026-10-01T12:01:00Z',
  terminatedAt: null,
  version: 2,
};

describe('analysisRunAdapter', () => {
  it('lists the runs of a match', async () => {
    const { api, calls } = setup(() => ok(200, { items: [run], continuationToken: 'more' }));
    const page = await createAnalysisRunAdapter(api).listByMatch(ids.match, { pageSize: 10 });
    expect(calls[0].url).toContain(`/api/v1/matches/${ids.match}/analysis-runs`);
    expect(calls[0].url).toContain('pageSize=10');
    expect(page.continuationToken).toBe('more');
    expect(page.items[0]).toMatchObject({ runId: ids.run, recordingSetVersionId: ids.set, state: 'Running', workflowId: 'default' });
    expect(page.items[0].failureReason).toBeUndefined();
    expect(page.items[0].runningAt).toBeInstanceOf(Date);
  });

  it('cancels a run with If-Match and the reason and maps the cancellation', async () => {
    const cancelled = { ...run, state: 'Cancelled', cancelledBy: { memberId: ids.team, accountName: 'coach' }, cancellationReason: 'wrong set', version: 3 };
    const { api, calls } = setup(() => ok(200, cancelled));
    const result = await createAnalysisRunAdapter(api).cancel(ids.run, 2, 'wrong set');
    expect(calls[0].url).toContain(`/api/v1/analysis/runs/${ids.run}/cancellation`);
    expect(calls[0].init.method).toBe('POST');
    expect(header(calls[0], 'If-Match')).toBe('"2"');
    expect(bodyOf(calls[0])).toEqual({ reason: 'wrong set' });
    expect(result).toMatchObject({ state: 'Cancelled', version: 3, cancelledBy: { accountName: 'coach' }, cancellationReason: 'wrong set' });
  });

  it('maps a refused cancellation to a failure', async () => {
    const { api } = setup(() => problem(409, 'conflict'));
    await expect(createAnalysisRunAdapter(api).cancel(ids.run, 1)).rejects.toMatchObject({ kind: 'conflict' });
  });

  it('refuses a run without a state as incompatible', async () => {
    const { api } = setup(() => ok(200, { items: [{ ...run, state: null }] }));
    await expect(createAnalysisRunAdapter(api).listByMatch(ids.match)).rejects.toMatchObject({ kind: 'incompatible' });
  });
});
