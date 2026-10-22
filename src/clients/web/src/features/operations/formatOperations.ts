export interface FormattedTimestamp {
  /** Local time for display. */
  text: string;
  /** Exact ISO 8601 value for tooltips and accessible descriptions. */
  iso: string;
}

export interface FormattedDuration {
  /** Largest sensible units, for example `2 h 5 min`. */
  text: string;
  /** Exact value in seconds, for tooltips and accessible descriptions. */
  exact: string;
}

/** Absent or invalid values yield `undefined`; callers show the `operations:absent` text. */
export function formatTimestamp(value: Date | undefined): FormattedTimestamp | undefined {
  if (!value || Number.isNaN(value.getTime())) return undefined;
  return {
    text: value.toLocaleString('en', { dateStyle: 'medium', timeStyle: 'medium' }),
    iso: value.toISOString(),
  };
}

const UNITS: ReadonlyArray<readonly [string, number]> = [
  ['d', 86_400],
  ['h', 3_600],
  ['min', 60],
  ['s', 1],
];

/** Formats seconds as at most two adjacent units (seconds, minutes, hours, days). */
export function formatDuration(seconds: number | undefined): FormattedDuration | undefined {
  if (seconds === undefined || !Number.isFinite(seconds)) return undefined;
  const total = Math.max(0, Math.floor(seconds));
  const exact = `${total} s`;
  if (total < 60) return { text: `${total} s`, exact };

  const start = UNITS.findIndex(([, size]) => total >= size);
  const parts: string[] = [];
  let rest = total;
  for (const [label, size] of UNITS.slice(start, start + 2)) {
    const amount = Math.floor(rest / size);
    rest -= amount * size;
    if (amount > 0) parts.push(`${amount} ${label}`);
  }
  return { text: parts.join(' '), exact };
}

/** Words known values through `translate`; unknown values stay raw. */
export function enumerationText(
  value: string | undefined,
  known: readonly string[],
  translate: (value: string) => string,
): string | undefined {
  if (value === undefined || value === '') return undefined;
  return known.includes(value) ? translate(value) : value;
}
