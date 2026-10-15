import { matchRoutes, type RouteObject } from 'react-router';

/** Parameter of the sign-in route that carries the path to return to. */
export const RETURN_TO_PARAM = 'returnTo';

/**
 * Returns `value` only when it is a same-origin path that starts with exactly one `/` and matches a known route;
 * otherwise `undefined`, so callers go home.
 */
export function safeReturnTo(value: string | null | undefined, routes: RouteObject[]): string | undefined {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) return undefined;
  if (/[\u0000-\u001f]/.test(value)) return undefined;
  let url: URL;
  try {
    url = new URL(value, 'http://return.invalid');
  } catch {
    return undefined;
  }
  if (url.origin !== 'http://return.invalid') return undefined;
  const matches = matchRoutes(routes, url.pathname);
  if (!matches || matches.some((match) => match.route.path === '*')) return undefined;
  return value;
}

export function signInPathFor(location: { pathname: string; search: string }): string {
  const path = location.pathname + location.search;
  return path === '/' ? '/sign-in' : `/sign-in?${RETURN_TO_PARAM}=${encodeURIComponent(path)}`;
}
