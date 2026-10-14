import { compositeDigest, sha256Digest } from '../digests';
import { planParts } from '../partPolicy';

export class HashingError extends Error {
  constructor(readonly reason: 'readError' | 'cancelled') {
    super(`Hashing stopped: ${reason}.`);
    this.name = 'HashingError';
  }
}

export interface HashingOptions {
  readonly signal?: AbortSignal;
  readonly onProgress?: (bytesHashed: number, totalBytes: number) => void;
}

export interface PreparedFile {
  readonly partDigests: string[];
  readonly contentDigest: string;
  readonly partSizeBytes: number;
  readonly partCount: number;
}

// Reads and hashes one part at a time, so memory stays at one part.
async function hashParts(file: Blob, partSizeBytes: number, options: HashingOptions): Promise<string[]> {
  if (file.size <= 0 || !Number.isSafeInteger(partSizeBytes) || partSizeBytes <= 0) {
    throw new HashingError('readError');
  }
  const partCount = Math.ceil(file.size / partSizeBytes);
  const digests: string[] = [];
  for (let index = 0; index < partCount; index++) {
    if (options.signal?.aborted) {
      throw new HashingError('cancelled');
    }
    const start = index * partSizeBytes;
    const end = Math.min(start + partSizeBytes, file.size);
    let bytes: ArrayBuffer;
    try {
      bytes = await file.slice(start, end).arrayBuffer();
    } catch {
      throw new HashingError('readError');
    }
    if (options.signal?.aborted) {
      throw new HashingError('cancelled');
    }
    digests.push(await sha256Digest(bytes));
    options.onProgress?.(end, file.size);
  }
  return digests;
}

export async function prepare(file: Blob, partSizeBytes: number | undefined, options: HashingOptions = {}): Promise<PreparedFile> {
  if (file.size <= 0) {
    throw new HashingError('readError');
  }
  const size = partSizeBytes ?? planParts(file.size).partSizeBytes;
  const partDigests = await hashParts(file, size, options);
  return {
    partDigests,
    contentDigest: await compositeDigest(size, partDigests),
    partSizeBytes: size,
    partCount: partDigests.length,
  };
}

// True when the file rehashes to the expected composite digest with the declared part size.
export async function verify(
  file: Blob,
  partSizeBytes: number,
  expectedContentDigest: string,
  options: HashingOptions = {},
): Promise<boolean> {
  const partDigests = await hashParts(file, partSizeBytes, options);
  return (await compositeDigest(partSizeBytes, partDigests)) === expectedContentDigest;
}
