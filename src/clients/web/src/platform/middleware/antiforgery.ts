import type { Middleware } from '@microsoft/kiota-http-fetchlibrary';
import type { RequestOption } from '@microsoft/kiota-abstractions';
import { setHeader } from './headers';

export const ANTIFORGERY_HEADER = 'X-CSRF-Token';
const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

export class AntiforgeryHeaderHandler implements Middleware {
  next: Middleware | undefined;

  constructor(private readonly getToken: () => string | undefined) {}

  execute(url: string, init: RequestInit, options?: Record<string, RequestOption>): Promise<Response> {
    const token = this.getToken();
    if (token && UNSAFE_METHODS.has((init.method ?? 'GET').toUpperCase())) {
      setHeader(init, ANTIFORGERY_HEADER, token);
    }
    return this.next!.execute(url, init, options);
  }
}
