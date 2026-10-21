import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { DigestText } from '../../../../src/features/registry/DigestText';
import { shortDigest } from '../../../../src/features/registry/digest';
import '../../../../src/i18n';

const full = `sha256:${'0123456789abcdef'.repeat(4)}`;

describe('shortDigest', () => {
  it('keeps the algorithm and the first 12 hexadecimal characters', () => {
    expect(shortDigest(full)).toBe('sha256:0123456789ab');
    expect(shortDigest(`sha512:${'ab'.repeat(64)}`)).toBe('sha512:abababababab');
  });

  it('returns unknown forms and short values unchanged', () => {
    expect(shortDigest('')).toBe('');
    expect(shortDigest('not-a-digest')).toBe('not-a-digest');
    expect(shortDigest('sha256:xyz')).toBe('sha256:xyz');
    expect(shortDigest('sha256:abcd')).toBe('sha256:abcd');
    expect(shortDigest(`registry/image@sha256:${'a'.repeat(64)}`)).toBe(`registry/image@sha256:${'a'.repeat(64)}`);
  });
});

describe('DigestText', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('shows the shortened value and reveals the full value as text', async () => {
    render(<DigestText value={full} />);
    expect(screen.getByText('sha256:0123456789ab')).toBeInTheDocument();
    expect(screen.queryByText(full)).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Show full digest' }));
    expect(screen.getByText(full)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Hide full digest' })).toHaveAttribute('aria-expanded', 'true');

    await userEvent.click(screen.getByRole('button', { name: 'Hide full digest' }));
    expect(screen.queryByText(full)).not.toBeInTheDocument();
  });

  it('copies the full value and announces it', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    render(<DigestText value={full} />);

    await userEvent.click(screen.getByRole('button', { name: 'Copy digest' }));
    expect(writeText).toHaveBeenCalledWith(full);
    expect(await screen.findByRole('status')).toHaveTextContent('Digest copied');
  });

  it('announces a failed copy', async () => {
    const writeText = vi.fn().mockRejectedValue(new Error('denied'));
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    render(<DigestText value={full} />);

    await userEvent.click(screen.getByRole('button', { name: 'Copy digest' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('could not be copied');
  });

  it('renders platform text as text, never markup', () => {
    render(<DigestText value="<b>bold</b>" />);
    expect(screen.getByText('<b>bold</b>')).toBeInTheDocument();
    expect(document.querySelector('b')).toBeNull();
  });
});
