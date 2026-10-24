import { isRunTerminal } from '../../../../src/platform/models/analysis';
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

  it('maps the supersession fields of a current run sent as null', async () => {
    const current = { ...run, isCurrent: true, supersededBy: null, supersededAt: null, cancellationCause: null };
    const { api } = setup(() => ok(200, { items: [current] }));
    const [item] = (await createAnalysisRunAdapter(api).listByMatch(ids.match)).items;
    expect(item.isCurrent).toBe(true);
    expect(item.supersededBy).toBeUndefined();
    expect(item.supersededAt).toBeUndefined();
    expect(item.cancellationCause).toBeUndefined();
  });

  it('defaults isCurrent to false and tolerates absent supersession fields', async () => {
    const { api } = setup(() => ok(200, { items: [run] }));
    const [item] = (await createAnalysisRunAdapter(api).listByMatch(ids.match)).items;
    expect(item.isCurrent).toBe(false);
    expect(item.supersededBy).toBeUndefined();
    expect(item.cancellationCause).toBeUndefined();
  });

  it('maps a superseded run with its successor and cause', async () => {
    const superseded = {
      ...run,
      state: 'Cancelled',
      isCurrent: false,
      cancellationCause: 'superseded',
      supersededAt: '2026-10-02T08:00:00Z',
      supersededBy: { runId: ids.version, recordingSetVersionId: ids.set, recordingSetFinalizedAt: '2026-10-02T07:59:00Z' },
    };
    const { api } = setup(() => ok(200, { items: [superseded] }));
    const [item] = (await createAnalysisRunAdapter(api).listByMatch(ids.match)).items;
    expect(item.cancellationCause).toBe('superseded');
    expect(item.supersededAt).toBeInstanceOf(Date);
    expect(item.supersededBy).toMatchObject({ runId: ids.version, recordingSetVersionId: ids.set });
    expect(item.supersededBy?.recordingSetFinalizedAt).toBeInstanceOf(Date);
  });

  it('passes the member cancellation cause through, maps it on cancel, and drops unknown causes', async () => {
    const cancelled = { ...run, state: 'Cancelled', cancellationCause: 'member', isCurrent: false, version: 3 };
    const { api } = setup(() => ok(200, cancelled));
    expect((await createAnalysisRunAdapter(api).cancel(ids.run, 2)).cancellationCause).toBe('member');
    const other = setup(() => ok(200, { items: [{ ...run, cancellationCause: 'other' }] }));
    expect((await createAnalysisRunAdapter(other.api).listByMatch(ids.match)).items[0].cancellationCause).toBeUndefined();
  });

  it('maps a refused cancellation to a failure', async () => {
    const { api } = setup(() => problem(409, 'conflict'));
    await expect(createAnalysisRunAdapter(api).cancel(ids.run, 1)).rejects.toMatchObject({ kind: 'conflict' });
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
    const status = { ...run, isCurrent: true, supersededBy: null, supersededAt: null, requestedCapabilities: ['ball-detection'], nodes: [node] };

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

    it('retains current, supersession, and input coverage facts', async () => {
      const coverage = { upstreamNodeKey: 'prepare', coverageMode: 'future-policy', minimumAcceptedFraction: 0.5, accepted: 3, missing: 1, failed: 0, blocked: 0, skipped: 2 };
      const superseded = {
        ...status,
        isCurrent: false,
        supersededAt: '2026-10-02T08:00:00Z',
        supersededBy: { runId: ids.version, recordingSetVersionId: ids.set, recordingSetFinalizedAt: '2026-10-02T07:59:00Z' },
        nodes: [{ ...node, inputCoverage: [coverage] }, { ...node, nodeKey: 'empty', inputCoverage: [] }, { ...node, nodeKey: 'none', inputCoverage: null }],
      };
      const { api } = setup(() => ok(200, superseded));
      const outcome = await createAnalysisRunAdapter(api).getRun(ids.run);
      if (outcome.kind !== 'found') throw new Error('expected found');
      expect(outcome.run.isCurrent).toBe(false);
      expect(outcome.run.supersededBy).toMatchObject({ runId: ids.version, recordingSetVersionId: ids.set });
      expect(outcome.run.supersededBy?.recordingSetFinalizedAt).toBeInstanceOf(Date);
      expect(outcome.run.supersededAt).toBeInstanceOf(Date);
      expect(outcome.run.nodes[0].inputCoverage).toEqual([coverage]);
      expect(outcome.run.nodes[1].inputCoverage).toEqual([]);
      expect(outcome.run.nodes[2].inputCoverage).toBeUndefined();
    });

    it('keeps a non-current run without supersession and does not invent a time', async () => {
      const { api } = setup(() => ok(200, { ...status, isCurrent: false }));
      const outcome = await createAnalysisRunAdapter(api).getRun(ids.run);
      expect(outcome).toMatchObject({ kind: 'found', run: { isCurrent: false, supersededBy: undefined, supersededAt: undefined } });
    });

    it('refuses a missing isCurrent and an incomplete supersession as incompatible', async () => {
      const noFlag = setup(() => ok(200, { ...status, isCurrent: null }));
      await expect(createAnalysisRunAdapter(noFlag.api).getRun(ids.run)).rejects.toMatchObject({ kind: 'incompatible' });
      const partial = setup(() => ok(200, { ...status, supersededBy: { runId: ids.version, recordingSetVersionId: ids.set } }));
      await expect(createAnalysisRunAdapter(partial.api).getRun(ids.run)).rejects.toMatchObject({ kind: 'incompatible' });
    });

    it('refuses invalid input coverage as incompatible', async () => {
      const good = { upstreamNodeKey: 'prepare', coverageMode: 'all', minimumAcceptedFraction: null, accepted: 1, missing: 0, failed: 0, blocked: 0, skipped: 0 };
      const bad: Record<string, unknown>[] = [
        { ...good, upstreamNodeKey: '' },
        { ...good, coverageMode: null },
        { ...good, minimumAcceptedFraction: 1.5 },
        { ...good, minimumAcceptedFraction: -0.1 },
        { ...good, accepted: -1 },
        { ...good, missing: 1.5 },
        { ...good, skipped: null },
        { ...good, failed: 2 ** 60 },
      ];
      for (const entry of bad) {
        const { api } = setup(() => ok(200, { ...status, nodes: [{ ...node, inputCoverage: [entry] }] }));
        await expect(createAnalysisRunAdapter(api).getRun(ids.run)).rejects.toMatchObject({ kind: 'incompatible' });
      }
      const valid = setup(() => ok(200, { ...status, nodes: [{ ...node, inputCoverage: [good] }] }));
      await expect(createAnalysisRunAdapter(valid.api).getRun(ids.run)).resolves.toMatchObject({ kind: 'found' });
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

describe('isRunTerminal', () => {
  it('is true only for the known terminal states', () => {
    for (const state of ['Completed', 'PartiallyCompleted', 'Failed', 'Cancelled', 'completed']) expect(isRunTerminal(state)).toBe(true);
    for (const state of ['Requested', 'Running', 'Quantum', '', undefined]) expect(isRunTerminal(state)).toBe(false);
  });
});
