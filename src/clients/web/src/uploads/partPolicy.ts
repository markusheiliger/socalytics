export const MiB = 1024 * 1024;
export const defaultPartSizeBytes = 16 * MiB;
export const maxPartCount = 10_000;

export interface PartPlan {
  readonly partSizeBytes: number;
  readonly partCount: number;
}

export function planParts(totalSizeBytes: number): PartPlan {
  if (!Number.isSafeInteger(totalSizeBytes) || totalSizeBytes <= 0) {
    throw new RangeError('A file must have at least one byte.');
  }
  if (totalSizeBytes <= defaultPartSizeBytes) {
    return { partSizeBytes: totalSizeBytes, partCount: 1 };
  }
  let partSizeBytes = defaultPartSizeBytes;
  if (Math.ceil(totalSizeBytes / partSizeBytes) > maxPartCount) {
    partSizeBytes = Math.ceil(totalSizeBytes / maxPartCount / MiB) * MiB;
  }
  return { partSizeBytes, partCount: Math.ceil(totalSizeBytes / partSizeBytes) };
}

export interface PartRange {
  readonly partNumber: number;
  readonly start: number;
  readonly end: number;
}

// Part numbers are 1-based, as in the platform's upload session; `end` is exclusive.
export function partRange(partNumber: number, totalSizeBytes: number, partSizeBytes: number): PartRange {
  const start = (partNumber - 1) * partSizeBytes;
  if (!Number.isInteger(partNumber) || partNumber < 1 || start >= totalSizeBytes) {
    throw new RangeError(`Part ${partNumber} is outside the file.`);
  }
  return { partNumber, start, end: Math.min(start + partSizeBytes, totalSizeBytes) };
}
