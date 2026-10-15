import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { AddRecordingsPanel } from '../../../../../src/features/uploads/AddRecordingsPanel';
import { UploadList } from '../../../../../src/features/uploads/UploadList';
import type { UploadJob } from '../../../../../src/uploads/uploadCoordinator';
import type { UploadRepository } from '../../../../../src/uploads/uploadRepository';
import '../../../../../src/i18n';

vi.mock('../../../../../src/uploads/duration', () => ({ readVideoDuration: vi.fn().mockResolvedValue(120) }));

const repo = (over: Partial<UploadRepository> = {}): UploadRepository =>
  ({
    journalAvailable: () => true,
    addFiles: vi.fn().mockReturnValue({ added: ['a'], refused: [] }),
    retry: vi.fn(),
    removeFromQueue: vi.fn(),
    setMapping: vi.fn(),
    abandon: vi.fn(),
    reselectFile: vi.fn(),
    acknowledgeExpiry: vi.fn(),
    ...over,
  }) as unknown as UploadRepository;

const job = (over: Partial<UploadJob> = {}): UploadJob => ({
  localId: 'j1',
  matchId: 'm',
  teamId: 't',
  memberId: 'u',
  fileName: 'a.mp4',
  fileSizeBytes: 40 * 1024 * 1024,
  fileLastModified: 0,
  displayName: 'Main camera',
  contentType: 'video/mp4',
  confirmedParts: [],
  bytesHashed: 0,
  bytesConfirmed: 0,
  bytesInFlight: 0,
  startKey: 'k1',
  completionKey: 'k2',
  state: 'queued',
  transferred: false,
  ...over,
});

describe('AddRecordingsPanel', () => {
  it('refuses non-video files and adds video files with name and description', async () => {
    const repository = repo();
    render(<AddRecordingsPanel repository={repository} matchId="m" teamId="t" />);
    const input = screen.getByLabelText('Video files');
    expect(input).toHaveAttribute('accept', 'video/*');
    await userEvent.upload(input, [new File(['x'], 'notes.txt', { type: 'text/plain' }), new File(['abc'], 'a.mp4', { type: 'video/mp4' })], { applyAccept: false });
    expect(screen.getByRole('alert')).toHaveTextContent('notes.txt is not a video file');
    await userEvent.clear(screen.getByLabelText('Display name for a.mp4'));
    await userEvent.type(screen.getByLabelText('Display name for a.mp4'), 'Main camera');
    await userEvent.type(screen.getByLabelText('Description for a.mp4 (optional)'), 'Wide');
    await userEvent.click(screen.getByRole('button', { name: 'Add to upload queue' }));
    const [matchId, teamId, inputs] = vi.mocked(repository.addFiles).mock.calls[0];
    expect([matchId, teamId]).toEqual(['m', 't']);
    expect(inputs).toMatchObject([{ displayName: 'Main camera', description: 'Wide', durationSeconds: 120 }]);
  });
});

describe('UploadList', () => {
  it('shows progress, the queue state, and removes a preparing file', async () => {
    const repository = repo();
    render(<UploadList repository={repository} jobs={[job({ state: 'preparing', bytesHashed: 20 * 1024 * 1024 })]} knownDigests={new Set()} canUpload />);
    expect(screen.getByRole('progressbar', { name: 'Preparing Main camera' })).toHaveAttribute('aria-valuenow', '50');
    await userEvent.click(screen.getByRole('button', { name: 'Remove Main camera from the queue' }));
    expect(repository.removeFromQueue).toHaveBeenCalledWith('j1');
  });

  it('offers retry for a paused file', async () => {
    const repository = repo();
    render(<UploadList repository={repository} jobs={[job({ state: 'paused', reason: 'retriesExhausted', uploadSessionId: 's' })]} knownDigests={new Set()} canUpload />);
    expect(screen.getByRole('status')).toHaveTextContent('Paused');
    await userEvent.click(screen.getByRole('button', { name: 'Retry Main camera' }));
    expect(repository.retry).toHaveBeenCalledWith('j1');
  });

  it('warns when the digest matches a recording version or another pending job, but not for completed jobs', () => {
    const summary = { totalSizeBytes: 1, partSizeBytes: 1, partCount: 1, contentDigest: 'sha-256-parts:1:1:abc' };
    const { rerender } = render(<UploadList repository={repo()} jobs={[job({ summary })]} knownDigests={new Set([summary.contentDigest])} canUpload />);
    expect(screen.getByText(/already uploaded or uploading/)).toBeInTheDocument();
    rerender(<UploadList repository={repo()} jobs={[job({ summary }), job({ localId: 'j2', summary })]} knownDigests={new Set()} canUpload />);
    expect(screen.getAllByText(/already uploaded or uploading/)).toHaveLength(2);
    rerender(<UploadList repository={repo()} jobs={[job({ summary, state: 'completed' })]} knownDigests={new Set([summary.contentDigest])} canUpload />);
    expect(screen.queryByText(/already uploaded or uploading/)).not.toBeInTheDocument();
  });

  it('saves a mapping once the match time is entered', async () => {
    const repository = repo();
    render(<UploadList repository={repository} jobs={[job({ state: 'awaitingMapping', durationSeconds: 100, transferred: true })]} knownDigests={new Set()} canUpload />);
    await userEvent.type(screen.getByLabelText('Match time at media start (Span 1)'), '10:00');
    await userEvent.click(screen.getByRole('button', { name: 'Save mapping for Main camera' }));
    expect(repository.setMapping).toHaveBeenCalledWith('j1', [{ mediaStartSeconds: 0, mediaEndSeconds: 100, matchStartSeconds: 600 }]);
  });
});
