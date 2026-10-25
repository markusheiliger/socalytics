const SIZE_UNITS = ['byte', 'kilobyte', 'megabyte', 'gigabyte', 'terabyte'] as const;

/** A size in the largest decimal unit that keeps the value at or above one, formatted for the given language. */
export function formatSegmentSize(bytes: number, language: string): string {
  let value = Math.max(0, bytes);
  let unit = 0;
  while (value >= 1000 && unit < SIZE_UNITS.length - 1) {
    value /= 1000;
    unit += 1;
  }
  return new Intl.NumberFormat(language, {
    style: 'unit',
    unit: SIZE_UNITS[unit],
    unitDisplay: 'short',
    maximumFractionDigits: unit === 0 ? 0 : 1,
  }).format(value);
}

export function formatSegmentTime(value: Date, language: string): string {
  return new Intl.DateTimeFormat(language, { dateStyle: 'medium', timeStyle: 'medium' }).format(value);
}

/** The first eight characters of an identifier, for the table; the full value is available on demand. */
export function shortenId(value: string): string {
  return value.length > 8 ? `${value.slice(0, 8)}…` : value;
}
