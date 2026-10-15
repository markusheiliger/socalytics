import { createWriteStream } from 'node:fs';
import { mkdir } from 'node:fs/promises';
import { once } from 'node:events';
import path from 'node:path';

export const MiB = 1024 * 1024;
export const GiB = 1024 * MiB;

const BLOCK = MiB;

/**
 * Writes a deterministic video-like file of exactly `sizeBytes` into `directory` and returns its path and media type.
 * The same size always yields the same content; every 1 MiB block differs so that parts have distinct digests.
 */
export async function writeTestVideo(
  directory: string,
  sizeBytes: number,
  name = 'recording.mp4',
): Promise<{ path: string; name: string; sizeBytes: number; mimeType: 'video/mp4' }> {
  await mkdir(directory, { recursive: true });
  const filePath = path.join(directory, name);
  const out = createWriteStream(filePath);

  const base = Buffer.alloc(BLOCK);
  let state = 0x2545f491;
  for (let i = 0; i < BLOCK; i++) {
    state ^= state << 13;
    state ^= state >>> 17;
    state ^= state << 5;
    base[i] = state & 0xff;
  }

  for (let offset = 0, index = 0; offset < sizeBytes; offset += BLOCK, index++) {
    const block = Buffer.from(base.subarray(0, Math.min(BLOCK, sizeBytes - offset)));
    if (block.length >= 8) block.writeBigUInt64LE(BigInt(index), 0);
    if (!out.write(block)) await once(out, 'drain');
  }
  out.end();
  await once(out, 'finish');
  return { path: filePath, name, sizeBytes, mimeType: 'video/mp4' };
}

/** Convenience for tests: `await writeTestVideo(testInfo.outputPath('files'), 40 * MiB)`. */
export const FORTY_MIB = 40 * MiB;
