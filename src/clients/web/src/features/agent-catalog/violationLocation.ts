// Maps the JSON Pointer of a platform validation violation to the form location that shows it (research R3).
// Locations are plain keys the forms match against:
//   summary                         unknown or empty pointers (shown above the form)
//   field:<name>                    a top-level field, e.g. field:model
//   entry:<list>:<index>            a list entry; <list> joins nested segments with '.', e.g. entry:dimensions.3.acceptedEvidence:1
//   weights                         the total of a profile's weights or a missing or unknown dimension
//   row:<dimensionId>               a profile row as a whole
//   cell:<dimensionId>:<column>     a profile row column (weight, rationale, provenance)
//   reference:<artifactKey>         an agent version's artifact reference, e.g. reference:promptTemplate

export const SUMMARY_LOCATION = 'summary';

const REFERENCES = new Set(['promptTemplate', 'modelSelection', 'toolPolicy', 'safetyPolicy', 'philosophyProfile', 'modifierPolicy']);
const COLUMNS = new Set(['weight', 'rationale', 'provenance']);
const INDEX = /^(0|[1-9][0-9]*)$/;
const NAME = /^[A-Za-z][A-Za-z0-9]*$/;

function unescapeToken(token: string): string | undefined {
  if (/~(?![01])/.test(token)) return undefined;
  return token.replace(/~1/g, '/').replace(/~0/g, '~');
}

export function violationLocation(pointer: string | null | undefined): string {
  if (!pointer || !pointer.startsWith('/')) return SUMMARY_LOCATION;

  const raw = pointer.slice(1).split('/');
  const segments: string[] = [];
  for (const token of raw) {
    const value = unescapeToken(token);
    if (value === undefined || value === '') return SUMMARY_LOCATION;
    segments.push(value);
  }

  const [first, second, third] = segments;

  if (first === 'weights') {
    if (segments.length === 1) return 'weights';
    if (segments.length === 2) return `row:${second}`;
    return COLUMNS.has(third) ? `cell:${second}:${third}` : `row:${second}`;
  }

  if (first === 'artifacts') {
    return second !== undefined && REFERENCES.has(second) ? `reference:${second}` : SUMMARY_LOCATION;
  }

  if (!NAME.test(first)) return SUMMARY_LOCATION;

  let last = -1;
  for (let i = segments.length - 1; i >= 1; i--) {
    if (INDEX.test(segments[i])) {
      last = i;
      break;
    }
  }
  if (last > 0) {
    const list = segments.slice(0, last);
    if (!list.every((s, i) => (i % 2 === 0 ? NAME.test(s) : INDEX.test(s)))) return SUMMARY_LOCATION;
    return `entry:${list.join('.')}:${segments[last]}`;
  }

  return `field:${first}`;
}
