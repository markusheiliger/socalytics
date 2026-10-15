import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';
import { SignOutButton } from '../../../../../src/features/account/SignOutButton';
import { PendingUploads } from '../../../../../src/features/uploads/PendingUploads';
import { ConnectivityProvider } from '../../../../../src/connectivity/ConnectivityProvider';
import { SessionContext, type SessionApi } from '../../../../../src/session/sessionContext';
import type { UploadJob } from '../../../../../src/uploads/uploadCoordinator';
import { UploadContext } from '../../../../../src/uploads/uploadContext';
import type { UploadRepository } from '../../../../../src/uploads/uploadRepository';
import '../../../../../src/i18n';

const repo = (over: Partial<UploadRepository> = {}): UploadRepository =>
  ({
    reselectFile: vi.fn(),
    abandon: vi.fn(),
    acknowledgeExpiry: vi.fn(),
    retry: vi.fn(),
    addFiles: vi.fn().mockReturnValue({ added: ['x'], refused: [] }),
    purgeMember: vi.fn().mockResolvedValue(undefined),
    subscribe: () => () => undefined,
    getSnapshot: () => [],
    ...over,
  }) as unknown as UploadRepository;

const job = (over: Partial<UploadJob> = {}): UploadJob => ({
  localId: 'j1',
  matchId: 'm',
  teamId: 't',
  memberId: 'u',
  fileName: 'a.mp4',
  fileSizeBytes: 40,
  fileLastModified: 0,
  displayName: 'Main camera',
  contentType: 'video/mp4',
  summary: { totalSizeBytes: 40, partSizeBytes: 10, partCount: 4, contentDigest: 'd' },
  uploadSessionId: 's',
  sessionExpiresAt: '2030-01-01T00:00:00Z',
  confirmedParts: [1, 2],
  bytesHashed: 0,
  bytesConfirmed: 0,
  bytesInFlight: 0,
  startKey: 'k1',
  completionKey: 'k2',
  state: 'needsFile',
  transferred: false,
  ...over,
});

describe('PendingUploads', () => {
  it('lists a remembered upload with progress and expiry, and resumes with the reselected file', async () => {
    const repository = repo();
    render(<PendingUploads repository={repository} jobs={[job()]} canUpload />);
    expect(screen.getByText('Main camera (40 B)', { exact: false })).toBeInTheDocument();
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50');
    expect(screen.getByText(/upload session expires on/)).toBeInTheDocument();
    const file = new File(['x'], 'a.mp4', { type: 'video/mp4' });
    await userEvent.upload(screen.getByLabelText('Choose the file again for Main camera', { selector: 'input' }), file, { applyAccept: false });
    expect(repository.reselectFile).toHaveBeenCalledWith('j1', file);
  });

  it('shows a refused file and abandons with the expiry explanation', async () => {
    const repository = repo();
    render(<PendingUploads repository={repository} jobs={[job({ state: 'refused', reason: 'fileMismatch' })]} canUpload />);
    expect(screen.getByRole('status')).toHaveTextContent('not the file that was selected');
    expect(screen.getByText(/platform discards the stored parts when the upload expires/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Abandon Main camera' }));
    expect(repository.abandon).toHaveBeenCalledWith('j1');
  });

  it('offers to start again for an expired upload and to dismiss it', async () => {
    const repository = repo();
    render(<PendingUploads repository={repository} jobs={[job({ state: 'expired' })]} canUpload />);
    const file = new File(['x'], 'a.mp4', { type: 'video/mp4' });
    await userEvent.upload(screen.getByLabelText('Start again with Main camera', { selector: 'input' }), file, { applyAccept: false });
    expect(repository.addFiles).toHaveBeenCalledWith('m', 't', [expect.objectContaining({ file, displayName: 'Main camera' })]);
    expect(repository.acknowledgeExpiry).toHaveBeenCalledWith('j1');
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss Main camera' }));
    expect(repository.acknowledgeExpiry).toHaveBeenCalledTimes(2);
  });

  it('shows an upload running in another tab and one that is not permitted with abandon only', () => {
    render(<PendingUploads repository={repo()} jobs={[job({ state: 'paused', reason: 'runningElsewhere' }), job({ localId: 'j2', state: 'notPermitted' })]} canUpload />);
    expect(screen.getByText(/running in another tab/)).toBeInTheDocument();
    expect(screen.getByText(/no longer permitted/)).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /^Abandon/ })).toHaveLength(2);
    expect(screen.queryAllByLabelText(/Choose the file again/)).toHaveLength(0);
  });

  it('shows nothing for jobs that are not recoverable', () => {
    const { container } = render(<PendingUploads repository={repo()} jobs={[job({ state: 'transferring' })]} canUpload />);
    expect(container).toBeEmptyDOMElement();
  });
});

describe('SignOutButton with uploads', () => {
  const renderButton = (repository: UploadRepository, jobs: readonly UploadJob[]) => {
    const signOut = vi.fn().mockResolvedValue(undefined);
    const session = { status: 'signedIn', signOut } as unknown as SessionApi;
    const withJobs = { ...repository, getSnapshot: () => jobs } as UploadRepository;
    render(
      <QueryClientProvider client={new QueryClient()}>
        <ConnectivityProvider>
        <SessionContext.Provider value={session}>
          <UploadContext.Provider value={withJobs}>
            <SignOutButton />
          </UploadContext.Provider>
        </SessionContext.Provider>
        </ConnectivityProvider>
      </QueryClientProvider>,
    );
    return signOut;
  };

  it('purges the member records before ending the session', async () => {
    const order: string[] = [];
    const repository = repo({ purgeMember: vi.fn().mockImplementation(async () => void order.push('purge')) });
    const signOut = renderButton(repository, []);
    signOut.mockImplementation(async () => void order.push('signOut'));
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(order).toEqual(['purge', 'signOut']);
  });

  it('asks for confirmation while an upload is transferring', async () => {
    const repository = repo();
    const signOut = renderButton(repository, [job({ state: 'transferring' })]);
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(signOut).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Keep uploading' }));
    expect(signOut).not.toHaveBeenCalled();
    expect(repository.purgeMember).not.toHaveBeenCalled();
    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }));
    await userEvent.click(screen.getByRole('button', { name: 'Stop uploads and sign out' }));
    expect(repository.purgeMember).toHaveBeenCalled();
    expect(signOut).toHaveBeenCalled();
  });
});
