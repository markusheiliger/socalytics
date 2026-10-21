// SemVer 2.0.0 versions without build metadata and the next-version proposal of the Agent Catalog forms
// (research.md R7). The proposal is a hint; the platform validates the version.

export const INITIAL_VERSION = '0.1.0';

export interface SemVer {
  readonly major: number;
  readonly minor: number;
  readonly patch: number;
  readonly prerelease: readonly string[];
}

const NUMERIC = '(0|[1-9]\\d*)';
const IDENTIFIER = '(?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*)';
const SEMVER = new RegExp(`^${NUMERIC}\\.${NUMERIC}\\.${NUMERIC}(?:-(${IDENTIFIER}(?:\\.${IDENTIFIER})*))?$`);

export function parseSemVer(text: string): SemVer | null {
  const match = SEMVER.exec(text);
  if (!match) {
    return null;
  }
  const [major, minor, patch] = [match[1], match[2], match[3]].map(Number);
  if (![major, minor, patch].every(Number.isSafeInteger)) {
    return null;
  }
  return { major, minor, patch, prerelease: match[4] ? match[4].split('.') : [] };
}

export function isSemVer(text: string): boolean {
  return parseSemVer(text) !== null;
}

const NUMERIC_IDENTIFIER = /^\d+$/;

function compareIdentifiers(a: string, b: string): number {
  const aNumeric = NUMERIC_IDENTIFIER.test(a);
  const bNumeric = NUMERIC_IDENTIFIER.test(b);
  if (aNumeric && bNumeric) {
    const difference = Number(a) - Number(b);
    return Math.sign(difference);
  }
  if (aNumeric !== bNumeric) {
    return aNumeric ? -1 : 1;
  }
  return a < b ? -1 : a > b ? 1 : 0;
}

// Precedence of SemVer 2.0.0 section 11: a prerelease ranks below its release.
export function compareSemVer(a: SemVer, b: SemVer): number {
  for (const key of ['major', 'minor', 'patch'] as const) {
    if (a[key] !== b[key]) {
      return a[key] < b[key] ? -1 : 1;
    }
  }
  if (a.prerelease.length === 0 || b.prerelease.length === 0) {
    return a.prerelease.length === b.prerelease.length ? 0 : a.prerelease.length === 0 ? 1 : -1;
  }
  const shared = Math.min(a.prerelease.length, b.prerelease.length);
  for (let index = 0; index < shared; index += 1) {
    const result = compareIdentifiers(a.prerelease[index], b.prerelease[index]);
    if (result !== 0) {
      return result;
    }
  }
  return Math.sign(a.prerelease.length - b.prerelease.length);
}

export function highestVersion(versions: readonly string[]): string | null {
  let highest: { text: string; parsed: SemVer } | null = null;
  for (const text of versions) {
    const parsed = parseSemVer(text);
    if (parsed && (!highest || compareSemVer(parsed, highest.parsed) > 0)) {
      highest = { text, parsed };
    }
  }
  return highest?.text ?? null;
}

// The next patch version above the highest registered version; `0.1.0` when none is registered.
// Versions that are not valid SemVer are ignored.
export function proposeNextVersion(registered: readonly string[]): string {
  const highest = highestVersion(registered);
  const parsed = highest ? parseSemVer(highest) : null;
  return parsed ? `${parsed.major}.${parsed.minor}.${parsed.patch + 1}` : INITIAL_VERSION;
}
