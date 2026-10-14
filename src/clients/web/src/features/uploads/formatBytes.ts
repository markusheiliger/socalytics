const UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];

/** Binary size with one decimal above bytes, for example `40.0 MiB`. */
export function formatBytes(bytes: number): string {
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return unit === 0 ? `${value} B` : `${value.toFixed(1)} ${UNITS[unit]}`;
}
