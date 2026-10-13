import { AnonymousAuthenticationProvider } from '@microsoft/kiota-abstractions';
import { KiotaClientFactory, MiddlewareFactory, type Middleware } from '@microsoft/kiota-http-fetchlibrary';
import { DefaultRequestAdapter } from '@microsoft/kiota-bundle';
import { createPlatformApiClient } from '../generated/platform-api/platformApiClient';
import { AntiforgeryHeaderHandler } from './middleware/antiforgery';
import { ConnectivityHandler } from './middleware/connectivity';
import { SessionOutcomeHandler, type SessionOutcome } from './middleware/sessionOutcome';

export interface PlatformClientOptions {
  getAntiforgeryToken: () => string | undefined;
  onSessionOutcome: (outcome: SessionOutcome) => void;
  onNetworkFailure: () => void;
  /** Defaults to the current origin; the generated paths already start with `/api/v1`. */
  baseUrl?: string;
  fetch?: (url: string, init: RequestInit) => Promise<Response>;
}

export function createPlatformClient(options: PlatformClientOptions) {
  const baseFetch = options.fetch ?? ((url, init) => fetch(url, init));
  const sameOriginFetch = (url: string, init: RequestInit) =>
    baseFetch(url, { ...init, credentials: 'same-origin' });

  const middlewares: Middleware[] = [
    new ConnectivityHandler(options.onNetworkFailure),
    new SessionOutcomeHandler(options.onSessionOutcome),
    new AntiforgeryHeaderHandler(options.getAntiforgeryToken),
    ...MiddlewareFactory.getDefaultMiddlewares(sameOriginFetch),
  ];
  const httpClient = KiotaClientFactory.create(sameOriginFetch, middlewares);
  const adapter = new DefaultRequestAdapter(new AnonymousAuthenticationProvider(), undefined, undefined, httpClient);
  adapter.baseUrl = options.baseUrl ?? window.location.origin;
  return createPlatformApiClient(adapter);
}
