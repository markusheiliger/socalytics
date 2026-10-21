import { describe, expect, it } from 'vitest';
import {
  compareSemVer,
  highestVersion,
  isSemVer,
  parseSemVer,
  proposeNextVersion,
} from '../../../../src/features/agent-catalog/versionProposal';

describe('parseSemVer', () => {
  it('parses release and prerelease versions', () => {
    expect(parseSemVer('1.2.3')).toEqual({ major: 1, minor: 2, patch: 3, prerelease: [] });
    expect(parseSemVer('0.1.0-rc.1')).toEqual({ major: 0, minor: 1, patch: 0, prerelease: ['rc', '1'] });
  });

  it.each(['', '1', '1.2', '1.2.3.4', '01.2.3', '1.02.3', '1.2.03', 'v1.2.3', '1.2.3+build', '1.2.3-', '1.2.3-01', '1.2.3-a..b', '-1.2.3', '1.2.3 '])(
    'rejects %j',
    (text) => {
      expect(parseSemVer(text)).toBeNull();
      expect(isSemVer(text)).toBe(false);
    },
  );

  it('rejects numbers beyond the safe integer range', () => {
    expect(parseSemVer('99999999999999999999.0.0')).toBeNull();
  });
});

describe('compareSemVer', () => {
  const compare = (a: string, b: string) => compareSemVer(parseSemVer(a)!, parseSemVer(b)!);

  it('orders numerically, not lexically', () => {
    expect(compare('1.10.0', '1.9.0')).toBe(1);
    expect(compare('2.0.0', '10.0.0')).toBe(-1);
    expect(compare('1.0.0', '1.0.0')).toBe(0);
  });

  it('follows the SemVer 2.0.0 prerelease precedence', () => {
    const ordered = ['1.0.0-alpha', '1.0.0-alpha.1', '1.0.0-alpha.beta', '1.0.0-beta', '1.0.0-beta.2', '1.0.0-beta.11', '1.0.0-rc.1', '1.0.0'];
    for (let index = 1; index < ordered.length; index += 1) {
      expect(compare(ordered[index - 1], ordered[index])).toBe(-1);
      expect(compare(ordered[index], ordered[index - 1])).toBe(1);
    }
  });
});

describe('highestVersion', () => {
  it('returns the highest valid version and ignores invalid ones', () => {
    expect(highestVersion(['1.2.0', '1.10.0', 'junk', '1.9.9'])).toBe('1.10.0');
    expect(highestVersion(['junk'])).toBeNull();
    expect(highestVersion([])).toBeNull();
  });
});

describe('proposeNextVersion', () => {
  it('proposes 0.1.0 for a new identifier', () => {
    expect(proposeNextVersion([])).toBe('0.1.0');
  });

  it('proposes the next patch version above the highest registered version', () => {
    expect(proposeNextVersion(['0.1.0'])).toBe('0.1.1');
    expect(proposeNextVersion(['1.2.3', '1.10.0', '1.9.9'])).toBe('1.10.1');
    expect(proposeNextVersion(['2.0.0', '1.99.99'])).toBe('2.0.1');
  });

  it('does not depend on the order of the list', () => {
    expect(proposeNextVersion(['1.0.5', '1.0.2', '1.0.9'])).toBe('1.0.10');
  });

  it('proposes a version above a prerelease and a release of the same core', () => {
    expect(proposeNextVersion(['1.0.0-rc.1'])).toBe('1.0.1');
    expect(proposeNextVersion(['1.0.0-rc.1', '1.0.0'])).toBe('1.0.1');
  });

  it('ignores versions that are not SemVer 2.0.0 without build metadata', () => {
    expect(proposeNextVersion(['not-a-version', '1.2.3+build'])).toBe('0.1.0');
    expect(proposeNextVersion(['not-a-version', '1.2.3'])).toBe('1.2.4');
  });
});
