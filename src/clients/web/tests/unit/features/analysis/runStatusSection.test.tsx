import { render, screen, within } from '@testing-library/react';
import { RunStatusSection } from '../../../../src/features/analysis/RunStatusSection';
import type { AnalysisRunStatus } from '../../../../src/platform/models/analysis';
import '../../../../src/i18n';

const run = (over: Partial<AnalysisRunStatus> = {}): AnalysisRunStatus => ({
  runId: 'r-1',
  teamId: 't-1',
  matchId: 'm-1',
  recordingSetVersionId: 'v-1',
  workflowId: 'default',
  workflowVersion: '3',
  state: 'Running',
  createdAt: new Date('2026-10-12T12:00:00Z'),
  runningAt: new Date('2026-10-12T12:05:00Z'),
  requestedCapabilities: ['detect'],
  nodes: [
    {
      nodeKey: 'detect-1',
      capabilityId: 'detect',
      capabilityVersion: '1.2',
      tier: 'LowLevel',
      executionScope: 'Segment',
      state: 'Active',
      jobs: { waiting: 1, ready: 2, claimed: 3, completed: 4, failed: 5, blocked: 6 },
    },
    {
      nodeKey: 'tactics-1',
      capabilityId: 'tactics',
      capabilityVersion: '2.0',
      tier: 'Experimental',
      executionScope: 'Galaxy',
      state: 'Paused',
      blockedByNodeKey: 'detect-1',
      jobs: { waiting: 0, ready: 0, claimed: 0, completed: 0, failed: 0, blocked: 7 },
    },
  ],
  ...over,
});

describe('RunStatusSection', () => {
  it('shows run details and every node with counts', () => {
    render(<RunStatusSection run={run()} />);
    expect(screen.getByText('Running')).toBeInTheDocument();
    expect(screen.getByText('default 3')).toBeInTheDocument();
    expect(screen.getByText('Not yet')).toBeInTheDocument();
    const nodes = within(screen.getByRole('list', { name: 'Capability nodes of the run' })).getAllByRole('listitem', {});
    expect(screen.getByText('detect 1.2')).toBeInTheDocument();
    expect(screen.getByText('Low level')).toBeInTheDocument();
    expect(screen.getByText('Segment')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('Waiting: 1')).toBeInTheDocument();
    expect(screen.getByText('Claimed: 3')).toBeInTheDocument();
    expect(screen.getAllByText('Blocked: 6').length).toBe(1);
    expect(nodes.length).toBeGreaterThan(1);
  });

  it('falls back to the raw value for unknown states, tiers, and scopes and shows the blocking node', () => {
    render(<RunStatusSection run={run({ state: 'Quantum' })} />);
    expect(screen.getByText('Quantum')).toBeInTheDocument();
    expect(screen.getByText('Experimental')).toBeInTheDocument();
    expect(screen.getByText('Galaxy')).toBeInTheDocument();
    expect(screen.getByText('Paused')).toBeInTheDocument();
    expect(screen.getByText('Blocked by')).toBeInTheDocument();
  });

  it('shows the failure reason and end time only when present', () => {
    const { rerender } = render(<RunStatusSection run={run()} />);
    expect(screen.queryByText('Failure reason')).not.toBeInTheDocument();
    rerender(<RunStatusSection run={run({ state: 'Failed', failureReason: 'No capacity', terminatedAt: new Date('2026-10-12T13:00:00Z') })} />);
    expect(screen.getByText('Failure reason')).toBeInTheDocument();
    expect(screen.getByText('No capacity')).toBeInTheDocument();
    expect(screen.queryByText('Not yet')).not.toBeInTheDocument();
  });

  it('announces a state change politely and stays silent on first render', () => {
    const { rerender } = render(<RunStatusSection run={run()} />);
    const live = screen.getByRole('status');
    expect(live).toHaveAttribute('aria-live', 'polite');
    expect(live).toBeEmptyDOMElement();
    rerender(<RunStatusSection run={run({ state: 'Completed' })} />);
    expect(screen.getByRole('status')).toHaveTextContent('The run is now Completed.');
  });

  it('shows a message for a run without nodes', () => {
    render(<RunStatusSection run={run({ nodes: [] })} />);
    expect(screen.getByText('This run has no capability nodes.')).toBeInTheDocument();
  });
});
