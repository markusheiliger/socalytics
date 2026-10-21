import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FactStreamList } from '../../../../src/features/analysis/FactStreamList';
import type { FactStreamSummary } from '../../../../src/platform/models/analysis';
import '../../../../src/i18n';

const stream = (over: Partial<FactStreamSummary> = {}): FactStreamSummary => ({
  nodeKey: 'detect-1',
  capabilityId: 'detect',
  capabilityVersion: '1.2',
  schemaVersion: '2',
  outputType: 'foundational-facts',
  executionScope: 'Segment',
  required: true,
  factCount: 42,
  provenance: { workflow: { id: 'default', version: '3' }, analystProfile: { id: 'p', digest: 'sha256:a' }, imageDigest: 'sha256:b' },
  coverage: { accepted: 3, missing: 1, failed: 0, blocked: 2, skipped: 4 },
  ...over,
});

describe('FactStreamList', () => {
  it('shows capability, schema version, output type, fact count, and coverage counts per stream', () => {
    render(<FactStreamList streams={[stream(), stream({ nodeKey: 'x-1', capabilityId: 'x', outputType: 'hologram', factCount: 7 })]} onSelect={() => {}} />);
    const entries = within(screen.getByRole('list', { name: 'Fact streams of the run' })).getAllByRole('button');
    expect(entries).toHaveLength(2);
    expect(screen.getByText('detect 1.2')).toBeInTheDocument();
    expect(screen.getByText('Foundational facts')).toBeInTheDocument();
    expect(screen.getByText('hologram')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getAllByText('Accepted: 3').length).toBe(2);
    expect(screen.getAllByText('Skipped: 4').length).toBe(2);
  });

  it('selects by keyboard and marks the selected stream', async () => {
    const onSelect = vi.fn();
    const { rerender } = render(<FactStreamList streams={[stream(), stream({ nodeKey: 'x-1' })]} onSelect={onSelect} />);
    const second = screen.getByRole('button', { name: 'Show facts of x-1' });
    expect(second).toHaveAttribute('aria-pressed', 'false');
    second.focus();
    await userEvent.keyboard('{Enter}');
    expect(onSelect).toHaveBeenCalledWith('x-1');
    rerender(<FactStreamList streams={[stream(), stream({ nodeKey: 'x-1' })]} selectedNodeKey="x-1" onSelect={onSelect} />);
    expect(screen.getByRole('button', { name: 'Show facts of x-1' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Show facts of detect-1' })).toHaveAttribute('aria-pressed', 'false');
    expect(screen.getByText('Selected')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Show facts of detect-1' }));
    expect(onSelect).toHaveBeenCalledWith('detect-1');
  });

  it('shows the empty state', () => {
    render(<FactStreamList streams={[]} onSelect={() => {}} />);
    expect(screen.getByText('This run has no fact streams.')).toBeInTheDocument();
  });
});
