import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

export const SYNTHETIC_VIDEO_DURATION_SECONDS = 10;
export const SYNTHETIC_VIDEO_SIZE = 64;
export const SYNTHETIC_VIDEO_FRAME_RATE = 25;

export interface SyntheticVideo {
  path: string;
  name: string;
  mimeType: 'video/mp4';
  sizeBytes: number;
  durationSeconds: number;
  bytes(): Buffer;
}

let generated: SyntheticVideo | undefined;

function runFfmpeg(args: string[]): string {
  try {
    return execFileSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', ...args], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    });
  } catch (error) {
    const failure = error as NodeJS.ErrnoException & { stderr?: string };
    if (failure.code === 'ENOENT') {
      throw new Error(
        'Synthetic video: `ffmpeg` is not on PATH. The evidence browser tests need the pinned FFmpeg of the ' +
          'Analyst and Media Verification Environment (with libvpx-vp9).',
      );
    }
    throw new Error(`Synthetic video: ffmpeg failed: ${failure.stderr?.trim() || failure.message}`);
  }
}

function assertVp9Encoder(): void {
  const encoders = runFfmpeg(['-encoders']);
  if (!/\blibvpx-vp9\b/.test(encoders)) {
    throw new Error(
      'Synthetic video: the `ffmpeg` on PATH does not list the `libvpx-vp9` encoder, so the browser tests cannot ' +
        'produce media that Playwright Chromium decodes. Use the pinned FFmpeg of the Analyst and Media ' +
        'Verification Environment.',
    );
  }
}

/**
 * Returns a 10-second 64x64 `testsrc2` VP9 MP4 with a keyframe every second and the `moov` atom up front,
 * generated on first use and reused for the rest of the test run (per process).
 */
export function getSyntheticVideo(): SyntheticVideo {
  if (generated) return generated;

  assertVp9Encoder();

  const directory = mkdtempSync(path.join(tmpdir(), 'socalytics-synthetic-video-'));
  const name = 'synthetic-evidence.mp4';
  const filePath = path.join(directory, name);

  runFfmpeg([
    '-y',
    '-f',
    'lavfi',
    '-i',
    `testsrc2=size=${SYNTHETIC_VIDEO_SIZE}x${SYNTHETIC_VIDEO_SIZE}:rate=${SYNTHETIC_VIDEO_FRAME_RATE}:duration=${SYNTHETIC_VIDEO_DURATION_SECONDS}`,
    '-c:v',
    'libvpx-vp9',
    '-pix_fmt',
    'yuv420p',
    '-g',
    String(SYNTHETIC_VIDEO_FRAME_RATE),
    '-keyint_min',
    String(SYNTHETIC_VIDEO_FRAME_RATE),
    '-sc_threshold',
    '0',
    '-an',
    '-movflags',
    '+faststart',
    filePath,
  ]);

  const content = readFileSync(filePath);
  generated = {
    path: filePath,
    name,
    mimeType: 'video/mp4',
    sizeBytes: content.length,
    durationSeconds: SYNTHETIC_VIDEO_DURATION_SECONDS,
    bytes: () => Buffer.from(content),
  };
  return generated;
}
