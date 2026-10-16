import { AnonymousAuthenticationProvider } from '@microsoft/kiota-abstractions';
import {
  KiotaClientFactory,
  MiddlewareFactory,
  RetryHandler,
  RetryHandlerOptions,
  type Middleware,
} from '@microsoft/kiota-http-fetchlibrary';
import { DefaultRequestAdapter } from '@microsoft/kiota-bundle';
import { createPlatformApiClient } from '../generated/platform-api/platformApiClient';
import { AntiforgeryHeaderHandler } from './middleware/antiforgery';
import { ConnectivityHandler } from './middleware/connectivity';
import { MinimumVersionHandler, sharedMinimumVersion, type MinimumVersionOptions } from './middleware/minimumVersion';
import { SessionOutcomeHandler, type SessionOutcome } from './middleware/sessionOutcome';

export interface PlatformClientOptions {
  getAntiforgeryToken: () => string | undefined;
  onSessionOutcome: (outcome: SessionOutcome) => void;
  onNetworkFailure: () => void;
  /** Defaults to the current origin; the generated paths already start with `/api/v1`. */
  baseUrl?: string;
  fetch?: (url: string, init: RequestInit) => Promise<Response>;
  /** Defaults to the shared update state of the service worker registration. */
  minimumVersion?: MinimumVersionOptions;
}

// A 429 is shown with its wait time instead of being retried silently.
const noRateLimitRetry = () =>
  new RetryHandler(
    new RetryHandlerOptions({
      shouldRetry: (_delay, _attempt, _url, _init, response) => response.status !== 429,
    }),
  );

export function createPlatformApi(options: PlatformClientOptions) {
  const baseFetch = options.fetch ?? ((url, init) => fetch(url, init));
  const sameOriginFetch = (url: string, init: RequestInit) =>
    baseFetch(url, { ...init, credentials: 'same-origin' });

  const middlewares: Middleware[] = [
    new MinimumVersionHandler(options.minimumVersion ?? sharedMinimumVersion),
    new ConnectivityHandler(options.onNetworkFailure),
    new SessionOutcomeHandler(options.onSessionOutcome),
    new AntiforgeryHeaderHandler(options.getAntiforgeryToken),
    ...MiddlewareFactory.getDefaultMiddlewares(sameOriginFetch).map((m) =>
      m instanceof RetryHandler ? noRateLimitRetry() : m,
    ),
  ];
  const httpClient = KiotaClientFactory.create(sameOriginFetch, middlewares);
  const adapter = new DefaultRequestAdapter(new AnonymousAuthenticationProvider(), undefined, undefined, httpClient);
  adapter.baseUrl = options.baseUrl ?? window.location.origin;
  return { adapter, client: createPlatformApiClient(adapter) };
}

export function createPlatformClient(options: PlatformClientOptions) {
  return createPlatformApi(options).client;
}
