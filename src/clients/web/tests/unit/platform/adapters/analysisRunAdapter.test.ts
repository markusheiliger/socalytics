import { createAnalysisRunAdapter } from '../../../../src/platform/adapters/analysisRunAdapter';
import { ids, ok, setup } from './support';

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

  it('refuses a run without a state as incompatible', async () => {
    const { api } = setup(() => ok(200, { items: [{ ...run, state: null }] }));
    await expect(createAnalysisRunAdapter(api).listByMatch(ids.match)).rejects.toMatchObject({ kind: 'incompatible' });
  });
});
