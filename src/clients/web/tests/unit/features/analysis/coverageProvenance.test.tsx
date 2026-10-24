import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CoverageList } from '../../../../src/features/analysis/CoverageList';
import { ProvenanceDetails, shortenDigest } from '../../../../src/features/analysis/ProvenanceDetails';
import type { Provenance } from '../../../../src/platform/models/analysis';
import '../../../../src/i18n';

const hex = (c: string) => c.repeat(64);

const provenance = (over: Partial<Provenance> = {}): Provenance => ({
  workflow: { id: 'default', version: '3' },
  analystProfile: { id: 'profile-1', digest: `sha256:${hex('a')}` },
  imageDigest: `sha256:${hex('b')}`,
  ...over,
});

describe('CoverageList', () => {
  it('lists windows with the match range and a translated state', () => {
    render(
      <CoverageList
        windows={[
          { startMs: 0, endMs: 60_000, state: 'accepted' },
          { startMs: 60_000, endMs: 120_500, state: 'Missing' },
        ]}
      />,
    );
    const items = within(screen.getByRole('list', { name: 'Coverage windows' })).getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('0:00–1:00: Accepted');
    expect(items[1]).toHaveTextContent('Missing');
  });

  it('shows the raw state when it is unknown and an empty note without windows', () => {
    const { rerender } = render(<CoverageList windows={[{ startMs: 0, endMs: 1000, state: 'quarantined' }]} />);
    expect(screen.getByText(/quarantined/)).toBeInTheDocument();
    rerender(<CoverageList windows={[]} />);
    expect(screen.getByText('The platform reports no coverage windows for this selection.')).toBeInTheDocument();
  });
});

describe('ProvenanceDetails', () => {
  it('shortens digests to the prefix plus 12 hexadecimal characters', () => {
    expect(shortenDigest(`sha256:${hex('a')}`)).toBe(`sha256:${'a'.repeat(12)}`);
    expect(shortenDigest('sha256:abc')).toBe('sha256:abc');
    expect(shortenDigest('opaque')).toBe('opaque');
  });

  it('shows workflow, profile, and image, and no model digest when absent', () => {
    render(<ProvenanceDetails provenance={provenance()} />);
    expect(screen.getByText('default 3')).toBeInTheDocument();
    expect(screen.getByText(`profile-1 (sha256:${'a'.repeat(12)})`)).toBeInTheDocument();
    expect(screen.getByText(`sha256:${'b'.repeat(12)}`)).toBeInTheDocument();
    expect(screen.queryByText('Model digest')).not.toBeInTheDocument();
    expect(screen.queryByText(hex('b'), { exact: false })).not.toBeInTheDocument();
  });

  it('shows the model digest only when present', () => {
    render(<ProvenanceDetails provenance={provenance({ modelDigest: `sha256:${hex('c')}` })} />);
    expect(screen.getByText('Model digest')).toBeInTheDocument();
    expect(screen.getByText(`sha256:${'c'.repeat(12)}`)).toBeInTheDocument();
  });

  it('discloses the full digest and copies it, announcing success', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    render(<ProvenanceDetails provenance={provenance()} />);
    const toggle = screen.getByRole('button', { name: /Show full digest.*Image digest/ });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await userEvent.click(toggle);
    expect(screen.getByText(`sha256:${hex('b')}`)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /Copy.*Image digest/ }));
    expect(writeText).toHaveBeenCalledWith(`sha256:${hex('b')}`);
    expect(screen.getByRole('status')).toHaveTextContent('Copied to the clipboard.');
  });

  it('announces a failed copy', async () => {
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: vi.fn().mockRejectedValue(new Error('denied')) }, configurable: true });
    render(<ProvenanceDetails provenance={provenance()} />);
    await userEvent.click(screen.getByRole('button', { name: /Show full digest.*Analyst profile/ }));
    await userEvent.click(screen.getByRole('button', { name: /Copy.*Analyst profile/ }));
    expect(screen.getByRole('status')).toHaveTextContent('Copying failed. Select the digest and copy it by hand.');
  });
});
