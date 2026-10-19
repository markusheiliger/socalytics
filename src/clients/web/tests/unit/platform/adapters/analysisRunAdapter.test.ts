import { createAnalysisRunAdapter } from '../../../../src/platform/adapters/analysisRunAdapter';
import { ids, ok, problem, setup } from './support';

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

  describe('getRun', () => {
    const jobs = { waiting: 1, ready: 2, claimed: 3, completed: 4, failed: 5, blocked: 6 };
    const node = {
      nodeId: ids.version,
      nodeKey: 'detect',
      capabilityId: 'ball-detection',
      capabilityVersion: '1.0',
      tier: 'Match',
      executionScope: 'Recording',
      state: 'Running',
      blockedByNodeKey: null,
      jobs,
    };
    const status = { ...run, requestedCapabilities: ['ball-detection'], nodes: [node] };

    it('maps the run status with its nodes and job counts', async () => {
      const { api, calls } = setup(() => ok(200, status));
      const outcome = await createAnalysisRunAdapter(api).getRun(ids.run);
      expect(calls[0].url).toContain(`/api/v1/analysis/runs/${ids.run}`);
      expect(outcome.kind).toBe('found');
      if (outcome.kind !== 'found') return;
      expect(outcome.run).toMatchObject({ runId: ids.run, teamId: ids.team, matchId: ids.match, state: 'Running', requestedCapabilities: ['ball-detection'] });
      expect(outcome.run.failureReason).toBeUndefined();
      expect(outcome.run.createdAt).toBeInstanceOf(Date);
      expect(outcome.run.nodes).toEqual([
        { nodeKey: 'detect', capabilityId: 'ball-detection', capabilityVersion: '1.0', tier: 'Match', executionScope: 'Recording', state: 'Running', blockedByNodeKey: undefined, jobs },
      ]);
    });

    it('keeps the failure reason and blocking node', async () => {
      const failed = { ...status, state: 'Failed', failureReason: 'node-failed', nodes: [{ ...node, state: 'Blocked', blockedByNodeKey: 'prepare' }] };
      const { api } = setup(() => ok(200, failed));
      const outcome = await createAnalysisRunAdapter(api).getRun(ids.run);
      expect(outcome).toMatchObject({ kind: 'found', run: { failureReason: 'node-failed', nodes: [{ blockedByNodeKey: 'prepare' }] } });
    });

    it('answers runNotFound for a 404', async () => {
      const { api } = setup(() => problem(404, 'not-found'));
      await expect(createAnalysisRunAdapter(api).getRun(ids.run)).resolves.toEqual({ kind: 'runNotFound' });
    });

    it('throws notPermitted for a 403', async () => {
      const { api } = setup(() => problem(403, 'forbidden'));
      await expect(createAnalysisRunAdapter(api).getRun(ids.run)).rejects.toMatchObject({ kind: 'notPermitted', status: 403 });
    });

    it('refuses a missing required field or node count as incompatible', async () => {
      const noState = setup(() => ok(200, { ...status, state: null }));
      await expect(createAnalysisRunAdapter(noState.api).getRun(ids.run)).rejects.toMatchObject({ kind: 'incompatible' });
      const noNodes = setup(() => ok(200, { ...status, nodes: null }));
      await expect(createAnalysisRunAdapter(noNodes.api).getRun(ids.run)).rejects.toMatchObject({ kind: 'incompatible' });
      const noCount = setup(() => ok(200, { ...status, nodes: [{ ...node, jobs: { ...jobs, failed: null } }] }));
      await expect(createAnalysisRunAdapter(noCount.api).getRun(ids.run)).rejects.toMatchObject({ kind: 'incompatible' });
    });
  });
});
