import { File } from './nodeFile';
import { describe, expect, it, vi } from 'vitest';
import { compositeDigest, sha256Digest } from '../../../../src/uploads/digests';
import { HashingError, prepare, verify } from '../../../../src/uploads/worker/hashing';

function file(size: number): File {
  const bytes = new Uint8Array(size);
  bytes.forEach((_, i) => (bytes[i] = i % 251));
  return new File([bytes], 'a.mp4', { type: 'video/mp4' });
}

describe('hashing', () => {
  it('hashes a single part and the composite digest', async () => {
    const f = file(10);
    const result = await prepare(f, undefined);
    const expectedPart = await sha256Digest(new Uint8Array(await f.arrayBuffer()));
    expect(result.partCount).toBe(1);
    expect(result.partSizeBytes).toBe(10);
    expect(result.partDigests).toEqual([expectedPart]);
    expect(result.contentDigest).toBe(await compositeDigest(10, [expectedPart]));
  });

  it('hashes parts of the given size one at a time and reports progress', async () => {
    const f = file(25);
    const progress = vi.fn();
    const result = await prepare(f, 10, { onProgress: progress });
    expect(result.partCount).toBe(3);
    expect(progress.mock.calls).toEqual([[10, 25], [20, 25], [25, 25]]);
    const last = await sha256Digest(new Uint8Array(await f.slice(20, 25).arrayBuffer()));
    expect(result.partDigests[2]).toBe(last);
  });

  it('is cancellable', async () => {
    const controller = new AbortController();
    controller.abort();
    await expect(prepare(file(25), 10, { signal: controller.signal })).rejects.toMatchObject({ reason: 'cancelled' });
  });

  it('reports a read error', async () => {
    const broken = { size: 10, slice: () => ({ arrayBuffer: () => Promise.reject(new Error('gone')) }) } as unknown as Blob;
    await expect(prepare(broken, 10)).rejects.toBeInstanceOf(HashingError);
    await expect(prepare(broken, 10)).rejects.toMatchObject({ reason: 'readError' });
  });

  it('rejects an empty file', async () => {
    await expect(prepare(file(0), undefined)).rejects.toMatchObject({ reason: 'readError' });
  });

  it('verifies a matching and a different file', async () => {
    const f = file(25);
    const { contentDigest } = await prepare(f, 10);
    expect(await verify(f, 10, contentDigest)).toBe(true);
    expect(await verify(file(26), 10, contentDigest)).toBe(false);
  });
});
