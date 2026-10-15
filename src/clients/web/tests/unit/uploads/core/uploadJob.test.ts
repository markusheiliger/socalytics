import {
  InvalidUploadTransitionError,
  canTransition,
  finalStates,
  transition,
  type UploadJobState,
} from '../../../../src/uploads/uploadJob';

describe('upload job states', () => {
  it('follows the happy path', () => {
    const path: UploadJobState[] = [
      'selected',
      'preparing',
      'ready',
      'starting',
      'queued',
      'transferring',
      'awaitingMapping',
      'completing',
      'completed',
    ];
    let status = { state: path[0] } as ReturnType<typeof transition>;
    for (const next of path.slice(1)) {
      status = transition(status, next);
      expect(status).toEqual({ state: next });
    }
  });

  it('skips awaiting mapping when the mapping exists', () => {
    expect(canTransition('transferring', 'completing')).toBe(true);
  });

  it('pauses with a reason and resumes', () => {
    const paused = transition({ state: 'transferring' }, 'paused', 'retriesExhausted');
    expect(paused).toEqual({ state: 'paused', reason: 'retriesExhausted' });
    expect(transition(paused, 'queued')).toEqual({ state: 'queued' });
  });

  it('requires a matching reason for paused, failed, and refused', () => {
    expect(() => transition({ state: 'transferring' }, 'paused')).toThrow(InvalidUploadTransitionError);
    expect(() => transition({ state: 'transferring' }, 'paused', 'fileChanged')).toThrow(InvalidUploadTransitionError);
    expect(() => transition({ state: 'queued' }, 'failed', 'unreachable')).toThrow(InvalidUploadTransitionError);
    expect(() => transition({ state: 'completing' }, 'refused')).toThrow(InvalidUploadTransitionError);
    expect(() => transition({ state: 'queued' }, 'transferring', 'unreachable')).toThrow(InvalidUploadTransitionError);
  });

  it('refuses completion and queues again', () => {
    const refused = transition({ state: 'completing' }, 'refused', 'partsIncomplete');
    expect(refused.reason).toBe('partsIncomplete');
    expect(transition(refused, 'queued')).toEqual({ state: 'queued' });
    expect(transition({ state: 'completing' }, 'refused', 'partMismatch').reason).toBe('partMismatch');
  });

  it('verifies a reselected file after a reload', () => {
    const preparing = transition({ state: 'needsFile' }, 'preparing');
    expect(transition(preparing, 'queued')).toEqual({ state: 'queued' });
    expect(transition(preparing, 'refused', 'fileMismatch')).toEqual({ state: 'refused', reason: 'fileMismatch' });
  });

  it('cancels while preparing', () => {
    expect(transition({ state: 'preparing' }, 'cancelled')).toEqual({ state: 'cancelled' });
  });

  it.each(['queued', 'transferring', 'paused', 'awaitingMapping', 'completing', 'needsFile', 'starting'] as const)(
    'ends %s as abandoned, expired, or notPermitted',
    (from) => {
      expect(canTransition(from, 'abandoned')).toBe(true);
      expect(canTransition(from, 'expired')).toBe(true);
      expect(canTransition(from, 'notPermitted')).toBe(true);
      expect(transition({ state: from }, 'failed', 'objectMismatch').state).toBe('failed');
    },
  );

  it('leaves a final state never', () => {
    for (const from of finalStates) {
      expect(canTransition(from, 'queued')).toBe(false);
      expect(canTransition(from, 'abandoned')).toBe(false);
    }
  });

  it('offers only abandon when not permitted', () => {
    expect(transition({ state: 'notPermitted' }, 'abandoned')).toEqual({ state: 'abandoned' });
    expect(canTransition('notPermitted', 'queued')).toBe(false);
    expect(canTransition('notPermitted', 'failed')).toBe(false);
  });

  it('rejects skipping states', () => {
    expect(canTransition('selected', 'transferring')).toBe(false);
    expect(canTransition('queued', 'completed')).toBe(false);
  });
});
