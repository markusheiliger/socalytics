export type ReleaseVersion = readonly [year: number, monthDay: number, time: number];

const shape = /^(\d{4})\.(\d{4})\.(\d{6})$/;

// Parses `YYYY.MMDD.HHMMSS`; any other shape is rejected with undefined.
export function parseVersion(value: unknown): ReleaseVersion | undefined {
  if (typeof value !== 'string') {
    return undefined;
  }
  const match = shape.exec(value);
  if (!match) {
    return undefined;
  }
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

export function isVersion(value: unknown): value is string {
  return parseVersion(value) !== undefined;
}

// Compares as three integers; throws for values that are not release versions.
export function compareVersions(left: string, right: string): number {
  const a = parseVersion(left);
  const b = parseVersion(right);
  if (!a || !b) {
    throw new RangeError('Not a release version (YYYY.MMDD.HHMMSS).');
  }
  for (let i = 0; i < 3; i++) {
    if (a[i] !== b[i]) {
      return a[i] < b[i] ? -1 : 1;
    }
  }
  return 0;
}
