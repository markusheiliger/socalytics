import type { RouteObject } from 'react-router';
import { safeReturnTo, signInPathFor } from '../../../src/session/returnTo';

const routes: RouteObject[] = [
  { path: '/' },
  { path: '/analyst-managers/pair' },
  { path: '/teams/:teamId' },
  { path: '*' },
];

describe('safeReturnTo', () => {
  it('accepts a path of a known route, with its query', () => {
    expect(safeReturnTo('/analyst-managers/pair', routes)).toBe('/analyst-managers/pair');
    expect(safeReturnTo('/teams/t-1', routes)).toBe('/teams/t-1');
    expect(safeReturnTo('/analyst-managers/pair?x=1', routes)).toBe('/analyst-managers/pair?x=1');
  });

  it.each([
    ['//evil.example/analyst-managers/pair'],
    ['///'],
    ['/\\evil.example'],
    ['https://evil.example/'],
    ['javascript:alert(1)'],
    ['analyst-managers/pair'],
    ['/unknown/path'],
    ['/sign-in\n/x'],
    [''],
  ])('refuses %j', (value) => {
    expect(safeReturnTo(value, routes)).toBeUndefined();
  });

  it('refuses a missing value', () => {
    expect(safeReturnTo(null, routes)).toBeUndefined();
    expect(safeReturnTo(undefined, routes)).toBeUndefined();
  });
});

describe('signInPathFor', () => {
  it('carries the guarded path and query, but not home', () => {
    expect(signInPathFor({ pathname: '/analyst-managers/pair', search: '' })).toBe('/sign-in?returnTo=%2Fanalyst-managers%2Fpair');
    expect(signInPathFor({ pathname: '/', search: '' })).toBe('/sign-in');
  });
});
