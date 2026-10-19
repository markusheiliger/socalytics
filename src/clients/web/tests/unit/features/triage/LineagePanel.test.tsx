import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '../../../../src/i18n';
import { LineagePanel } from '../../../../src/features/triage/LineagePanel';
import { TriageFailure, type AgentConversationAdapter } from '../../../../src/platform/adapters/agentConversationAdapter';
import { PlatformFailure } from '../../../../src/platform/failure';
import { toLineageKind, type InvocationLineage } from '../../../../src/platform/models/agents';

function lineage(over: Partial<InvocationLineage> = {}): InvocationLineage {
  return {
    invocationId: 'inv-1',
    policyVersion: 'p1',
    etag: 'e1',
    entries: [
      { sequence: 1, kind: toLineageKind('submitted'), recordedAt: '2026-10-19T10:00:00Z', details: {} },
      {
        sequence: 2,
        kind: toLineageKind('claimed'),
        attemptNumber: 1,
        recordedAt: '2026-10-19T10:00:05Z',
        details: { worker: '<b>w1</b>' },
      },
      { sequence: 3, kind: toLineageKind('mystery-kind'), recordedAt: '2026-10-19T10:00:09Z', details: {} },
    ],
    ...over,
  };
}

function setup(getLineage: ReturnType<typeof vi.fn>, props: { changeCount?: number; ended?: boolean } = {}) {
  const adapter = { getLineage } as unknown as AgentConversationAdapter;
  const view = render(<LineagePanel adapter={adapter} invocationId="inv-1" {...props} />);
  const rerender = (next: { changeCount?: number; ended?: boolean }) =>
    view.rerender(<LineagePanel adapter={adapter} invocationId="inv-1" {...next} />);
  return { rerender };
}

describe('LineagePanel', () => {
  it('shows entries in order with worded and raw kinds, attempt, time, and details as text', async () => {
    const getLineage = vi.fn(async () => ({ kind: 'changed' as const, value: lineage(), etag: 'e1' }));
    setup(getLineage);
    const items = await screen.findAllByRole('listitem');
    expect(items[0]).toHaveTextContent('Question submitted');
    expect(items[1]).toHaveTextContent('Claimed by the agent');
    expect(items[1]).toHaveTextContent('Attempt 1');
    expect(items[1]).toHaveTextContent('worker: <b>w1</b>');
    expect(items[1].querySelector('b')).toBeNull();
    expect(screen.getByText('mystery-kind')).toBeInTheDocument();
    expect(screen.getByText('Policy version p1')).toBeInTheDocument();
    expect(getLineage).toHaveBeenCalledTimes(1);
  });

  it('names the declared execution references', async () => {
    const value = lineage({
      advice: {
        text: 'x',
        evidenceReferences: [],
        executionReferences: { model: 'm-1', promptRef: 'prompt/7' },
      },
    });
    setup(vi.fn(async () => ({ kind: 'changed' as const, value, etag: 'e1' })));
    expect(await screen.findByText('Model: m-1')).toBeInTheDocument();
    expect(screen.getByText('Prompt reference: prompt/7')).toBeInTheDocument();
    expect(screen.queryByText(/^Runtime:/)).toBeNull();
  });

  it('refetches conditionally when the change counter grows and keeps the lineage on unchanged', async () => {
    const getLineage = vi
      .fn()
      .mockResolvedValueOnce({ kind: 'changed', value: lineage(), etag: 'e1' })
      .mockResolvedValueOnce({ kind: 'unchanged' });
    const { rerender } = setup(getLineage, { changeCount: 0 });
    await screen.findByText('Question submitted');
    rerender({ changeCount: 1 });
    await waitFor(() => expect(getLineage).toHaveBeenCalledTimes(2));
    expect(getLineage).toHaveBeenLastCalledWith('inv-1', 'e1');
    expect(screen.getByText('Question submitted')).toBeInTheDocument();
  });

  it('stops refreshing once the turn ended', async () => {
    const getLineage = vi.fn(async () => ({ kind: 'changed' as const, value: lineage(), etag: 'e1' }));
    const { rerender } = setup(getLineage, { changeCount: 1, ended: true });
    await screen.findByText('Question submitted');
    rerender({ changeCount: 2, ended: true });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(getLineage).toHaveBeenCalledTimes(1);
  });

  it('shows a failure with a retry', async () => {
    const getLineage = vi
      .fn()
      .mockRejectedValueOnce(
        new TriageFailure({ kind: 'platform', failureKind: 'unreachable' }, new PlatformFailure('unreachable')),
      )
      .mockResolvedValueOnce({ kind: 'changed', value: lineage(), etag: 'e1' });
    setup(getLineage);
    expect(await screen.findByRole('alert')).toHaveTextContent('Triage conversations need a connection');
    screen.getByRole('button', { name: 'Try again' }).click();
    expect(await screen.findByText('Question submitted')).toBeInTheDocument();
  });
});
