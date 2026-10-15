const PATTERN = /^(?:(\d+):)?(\d+):(\d{1,2}(?:\.\d{1,3})?)$/;

/** Parses `mm:ss` or `hh:mm:ss` (seconds may carry up to three decimals) into seconds, or undefined when invalid. */
export function parseTime(text: string): number | undefined {
  const match = PATTERN.exec(text.trim());
  if (!match) return undefined;
  const [, hours, minutes, seconds] = match;
  if (Number(seconds) >= 60) return undefined;
  if (hours !== undefined && Number(minutes) >= 60) return undefined;
  const total = Number(hours ?? 0) * 3600 + Number(minutes) * 60 + Number(seconds);
  return Math.round(total * 1000) / 1000;
}

/** Formats seconds as `mm:ss` (below one hour) or `hh:mm:ss`, with milliseconds only when present. */
export function formatTime(seconds: number): string {
  const totalMs = Math.round(seconds * 1000);
  const ms = totalMs % 1000;
  const whole = Math.floor(totalMs / 1000);
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = whole % 60;
  const pad = (n: number) => String(n).padStart(2, '0');
  const tail = `${pad(s)}${ms ? `.${String(ms).padStart(3, '0').replace(/0+$/, '')}` : ''}`;
  return h > 0 ? `${h}:${pad(m)}:${tail}` : `${pad(m)}:${tail}`;
}
