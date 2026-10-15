import { vi } from 'vitest';
import { readVideoDuration } from '../../../../../src/uploads/duration';

function fakeVideo() {
  const video = document.createElement('video');
  const play = vi.spyOn(video, 'play');
  return { video, play };
}

describe('readVideoDuration', () => {
  afterEach(() => vi.useRealTimers());

  it('reads a finite duration, never attaches or plays, and revokes the URL', async () => {
    const { video, play } = fakeVideo();
    const revoke = vi.fn();
    const promise = readVideoDuration(new Blob(['x']), { createVideo: () => video, createObjectUrl: () => 'blob:one', revokeObjectUrl: revoke });
    expect(video.preload).toBe('metadata');
    expect(video.muted).toBe(true);
    Object.defineProperty(video, 'duration', { value: 2700.12349 });
    video.onloadedmetadata?.(new Event('loadedmetadata'));
    await expect(promise).resolves.toBe(2700.123);
    expect(revoke).toHaveBeenCalledWith('blob:one');
    expect(video.isConnected).toBe(false);
    expect(play).not.toHaveBeenCalled();
  });

  it.each([Infinity, NaN, 0])('proposes no duration for %s', async (value) => {
    const { video } = fakeVideo();
    const revoke = vi.fn();
    const promise = readVideoDuration(new Blob(['x']), { createVideo: () => video, createObjectUrl: () => 'blob:two', revokeObjectUrl: revoke });
    Object.defineProperty(video, 'duration', { value });
    video.onloadedmetadata?.(new Event('loadedmetadata'));
    await expect(promise).resolves.toBeUndefined();
    expect(revoke).toHaveBeenCalledWith('blob:two');
  });

  it('gives up after 10 seconds without metadata', async () => {
    vi.useFakeTimers();
    const { video } = fakeVideo();
    const revoke = vi.fn();
    const promise = readVideoDuration(new Blob(['x']), { createVideo: () => video, createObjectUrl: () => 'blob:three', revokeObjectUrl: revoke });
    await vi.advanceTimersByTimeAsync(9_999);
    expect(revoke).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    await expect(promise).resolves.toBeUndefined();
    expect(revoke).toHaveBeenCalledWith('blob:three');
  });

  it('proposes no duration when the file cannot be decoded', async () => {
    const { video } = fakeVideo();
    const promise = readVideoDuration(new Blob(['x']), { createVideo: () => video, createObjectUrl: () => 'blob:four', revokeObjectUrl: vi.fn() });
    video.onerror?.(new Event('error'));
    await expect(promise).resolves.toBeUndefined();
  });
});
