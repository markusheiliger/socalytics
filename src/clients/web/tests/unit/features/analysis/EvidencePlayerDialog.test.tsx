import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { EvidencePlayerDialog } from '../../../../src/features/analysis/EvidencePlayerDialog';
import type { AnalysisEvidenceAdapter } from '../../../../src/platform/adapters/analysisEvidenceAdapter';
import type { EvidenceOutcome, EvidenceResolution } from '../../../../src/platform/models/analysis';
import '../../../../src/i18n';

const fact = { acceptedResultId: 'r1', ordinal: 0, startMs: 62500, endMs: 65000 };

function resolution(parts: EvidenceResolution['parts'], shortened = false): EvidenceOutcome {
  return {
    kind: 'resolution',
    resolution: {
      run: { runId: 'run-1', matchId: 'm', teamId: 't' },
      requested: { fromMs: 57500, toMs: 70000 },
      fact: { startMs: 62500, endMs: 65000 },
      shortened,
      parts,
    },
  };
}

const media = {
  url: 'https://objects.example/a.mp4',
  expiresAt: '2999-01-01T00:00:00Z',
  mediaType: 'video/mp4',
  mediaDigest: 'sha-256:abc',
  mediaSizeBytes: 10,
  startOffsetMs: 0,
  endOffsetMs: 12500,
  recordingVersionId: 'rv',
};

function renderDialog(outcome: EvidenceOutcome, onClose = vi.fn()) {
  const adapter: AnalysisEvidenceAdapter = { resolve: vi.fn(async () => outcome) };
  render(<EvidencePlayerDialog adapter={adapter} runId="run-1" fact={fact} capability="detect" onClose={onClose} />);
  return { adapter, onClose };
}

describe('EvidencePlayerDialog', () => {
  it('shows a titled dialog with a polite live region while resolving and requests the fact with context', async () => {
    const { adapter } = renderDialog(resolution([{ startMs: 57500, endMs: 70000, state: 'available', media }]));
    expect(screen.getByRole('dialog', { name: 'Evidence: detect, 1:02.500–1:05' })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite');
    await waitFor(() => expect(adapter.resolve).toHaveBeenCalled());
    expect(adapter.resolve).toHaveBeenCalledWith('run-1', {
      fact: { acceptedResultId: 'r1', ordinal: 0, contextBeforeMs: 5000, contextAfterMs: 5000 },
    });
  });

  it('renders two video elements with the required attributes and the inside-or-context marker', async () => {
    renderDialog(resolution([{ startMs: 57500, endMs: 70000, state: 'available', media }]));
    expect(await screen.findByText(/Playing the evidence\./)).toBeInTheDocument();
    const videos = document.querySelectorAll('video');
    expect(videos).toHaveLength(2);
    videos.forEach((video) => {
      expect(video).toHaveAttribute('crossorigin', 'anonymous');
      expect(video).toHaveAttribute('preload', 'auto');
      expect(video).toHaveAttribute('playsinline');
    });
    expect(screen.getByText(/This is context before or after the fact\./)).toBeInTheDocument();
  });

  it('releases the media sources when the dialog unmounts', async () => {
    const adapter: AnalysisEvidenceAdapter = {
      resolve: vi.fn(async () => resolution([{ startMs: 57500, endMs: 70000, state: 'available', media }])),
    };
    const { unmount } = render(<EvidencePlayerDialog adapter={adapter} runId="run-1" fact={fact} capability="detect" onClose={vi.fn()} />);
    await screen.findByText(/Playing the evidence\./);
    const videos = Array.from(document.querySelectorAll('video'));
    expect(videos.some((video) => video.getAttribute('src') === media.url)).toBe(true);
    unmount();
    videos.forEach((video) => expect(video.getAttribute('src')).toBeNull());
  });

  it('explains failed and not-covered parts, shows the shortened notice, and offers continue and try again', async () => {
    const { adapter } = renderDialog(
      resolution([{ startMs: 57500, endMs: 62500, state: 'failed', reason: 'toolchain-error' }, { startMs: 62500, endMs: 70000, state: 'available', media }], true),
    );
    expect(await screen.findByText(/could not be prepared \(toolchain-error\)/)).toBeInTheDocument();
    expect(screen.getByText(/shortened to the covered window 0:57\.500–1:10/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue with the next playable part' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => expect(adapter.resolve).toHaveBeenLastCalledWith('run-1', expect.objectContaining({ retryFailed: true })));
  });

  it('shows not-covered windows and raw unknown states', async () => {
    renderDialog(resolution([{ startMs: 57500, endMs: 62500, state: 'not-covered' }]));
    expect(await screen.findByText(/No recording covers 0:57\.500–1:02\.500/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();
  });

  it('shows the unknown part state raw', async () => {
    renderDialog(resolution([{ startMs: 57500, endMs: 62500, state: 'quantum' }]));
    expect(await screen.findByText(/in the state quantum/)).toBeInTheDocument();
  });

  it.each([
    [{ kind: 'notFound' }, /was not found/],
    [{ kind: 'offline' }, /connection is required/],
    [{ kind: 'error' }, /could not be played/],
    [{ kind: 'storageUnavailable' }, /could not be played/],
    [{ kind: 'rateLimited', retryAfterSeconds: 30 }, /Try again in 30 seconds/],
  ] as [EvidenceOutcome, RegExp][])('shows the message of %j', async (outcome, text) => {
    renderDialog(outcome);
    expect(await screen.findByText(text)).toBeInTheDocument();
  });

  it('closes with the Close button and with Escape', async () => {
    const { onClose } = renderDialog({ kind: 'notFound' });
    await screen.findByText(/was not found/);
    await userEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onClose).toHaveBeenCalledTimes(1);
    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});
