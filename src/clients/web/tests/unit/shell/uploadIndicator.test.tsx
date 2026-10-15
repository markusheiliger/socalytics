import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { vi } from 'vitest';
import { UploadIndicator } from '../../../src/shell/UploadIndicator';
import { UploadContext } from '../../../src/uploads/uploadContext';
import type { UploadJob } from '../../../src/uploads/uploadCoordinator';
import type { UploadRepository } from '../../../src/uploads/uploadRepository';
import { shouldGuardLeave } from '../../../src/uploads/useLeaveGuard';
import type { UploadJobState } from '../../../src/uploads/uploadJob';
import '../../../src/i18n';

function job(localId: string, state: UploadJobState, overrides: Partial<UploadJob> = {}): UploadJob {
  return {
    localId,
    matchId: 'match-1',
    teamId: 'team-1',
    memberId: 'm-1',
    fileName: `${localId}.mp4`,
    fileSizeBytes: 100,
    fileLastModified: 0,
    displayName: localId,
    contentType: 'video/mp4',
    confirmedParts: [],
    bytesHashed: 0,
    bytesConfirmed: 0,
    bytesInFlight: 0,
    startKey: 'start',
    completionKey: 'completion',
    state,
    transferred: false,
    ...overrides,
  };
}

function fakeRepository(initial: readonly UploadJob[]) {
  let jobs = initial;
  const listeners = new Set<() => void>();
  const repository = {
    subscribe: (listener: () => void) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getSnapshot: () => jobs,
  } as unknown as UploadRepository;
  return {
    repository,
    set(next: readonly UploadJob[]) {
      jobs = next;
      listeners.forEach((listener) => listener());
    },
  };
}

function renderIndicator(repository?: UploadRepository) {
  return render(
    <MemoryRouter>
      <UploadContext.Provider value={repository}>
        <UploadIndicator />
      </UploadContext.Provider>
    </MemoryRouter>,
  );
}

function beforeUnload() {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
}

describe('upload indicator', () => {
  it('renders nothing without uploads or without a signed-in member', () => {
    const { container } = renderIndicator(undefined);
    expect(container).toBeEmptyDOMElement();
    const empty = renderIndicator(fakeRepository([job('done', 'completed')]).repository);
    expect(empty.container.querySelector('section')).toBeNull();
  });

  it('shows active and queued counts with the overall progress and lists each upload in a panel', () => {
    const { repository } = fakeRepository([
      job('main', 'transferring', { bytesConfirmed: 50 }),
      job('second', 'queued'),
      job('old', 'completed'),
    ]);
    renderIndicator(repository);
    const toggle = screen.getByRole('button', { name: 'Uploads: 1 active, 1 queued, 25%' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.getByRole('progressbar', { name: 'Overall upload progress' })).toHaveAttribute('aria-valuenow', '25');
    fireEvent.click(toggle);
    const items = screen.getAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent('main (100 B) – Transferring');
    expect(items[1]).toHaveTextContent('second (100 B) – Waiting in the queue');
    expect(screen.getAllByRole('link', { name: /Open the match of/ })[0]).toHaveAttribute('href', '/matches/match-1');
  });

  it('follows changes of the uploads', () => {
    const fake = fakeRepository([job('main', 'transferring')]);
    renderIndicator(fake.repository);
    expect(screen.getByRole('button', { name: /1 active, 0 queued, 0%/ })).toBeInTheDocument();
    act(() => fake.set([job('main', 'transferring', { bytesConfirmed: 100 })]));
    expect(screen.getByRole('button', { name: /1 active, 0 queued, 100%/ })).toBeInTheDocument();
    act(() => fake.set([job('main', 'completed')]));
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});

describe('leave guard', () => {
  it.each(['transferring', 'starting', 'completing'] as const)('asks for confirmation while an upload is %s', (state) => {
    const { unmount } = renderIndicator(fakeRepository([job('main', state)]).repository);
    expect(beforeUnload()).toBe(true);
    unmount();
    expect(beforeUnload()).toBe(false);
  });

  it.each(['queued', 'paused', 'awaitingMapping', 'preparing', 'completed'] as const)('does not ask while an upload is %s', (state) => {
    renderIndicator(fakeRepository([job('main', state)]).repository);
    expect(beforeUnload()).toBe(false);
  });

  it('stops asking once the transfer ends', () => {
    const fake = fakeRepository([job('main', 'transferring')]);
    renderIndicator(fake.repository);
    expect(beforeUnload()).toBe(true);
    act(() => fake.set([job('main', 'awaitingMapping')]));
    expect(beforeUnload()).toBe(false);
  });

  it('decides from the job states', () => {
    expect(shouldGuardLeave([{ state: 'queued' }, { state: 'completing' }])).toBe(true);
    expect(shouldGuardLeave([{ state: 'paused' }])).toBe(false);
    expect(vi.isMockFunction(shouldGuardLeave)).toBe(false);
  });
});
