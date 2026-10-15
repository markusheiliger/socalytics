export const DURATION_TIMEOUT_MS = 10_000;

export interface DurationReaderOptions {
  timeoutMs?: number;
  createVideo?: () => HTMLVideoElement;
  createObjectUrl?: (file: Blob) => string;
  revokeObjectUrl?: (url: string) => void;
}

/**
 * Reads the duration of a local video file in seconds (millisecond precision) through a detached,
 * muted video element that is never attached to the document and never played. Resolves to
 * `undefined` when no metadata arrives in time or the duration is not a finite positive number.
 */
export function readVideoDuration(file: Blob, options: DurationReaderOptions = {}): Promise<number | undefined> {
  const timeoutMs = options.timeoutMs ?? DURATION_TIMEOUT_MS;
  const createVideo = options.createVideo ?? (() => document.createElement('video'));
  const createObjectUrl = options.createObjectUrl ?? ((f: Blob) => URL.createObjectURL(f));
  const revokeObjectUrl = options.revokeObjectUrl ?? ((u: string) => URL.revokeObjectURL(u));

  return new Promise((resolve) => {
    const video = createVideo();
    const url = createObjectUrl(file);
    let timer: ReturnType<typeof setTimeout> | undefined;

    const finish = (value: number | undefined) => {
      if (timer !== undefined) clearTimeout(timer);
      video.onloadedmetadata = null;
      video.onerror = null;
      video.removeAttribute('src');
      revokeObjectUrl(url);
      resolve(value);
    };

    video.preload = 'metadata';
    video.muted = true;
    video.onloadedmetadata = () => {
      const { duration } = video;
      finish(Number.isFinite(duration) && duration > 0 ? Math.round(duration * 1000) / 1000 : undefined);
    };
    video.onerror = () => finish(undefined);
    timer = setTimeout(() => finish(undefined), timeoutMs);
    video.src = url;
  });
}
