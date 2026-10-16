import type { Middleware } from '@microsoft/kiota-http-fetchlibrary';
import type { RequestOption } from '@microsoft/kiota-abstractions';
import { PlatformFailure } from '../failure';
import { isUpdateBlocked, requireUpdate } from '../../serviceWorker/registration';
import { compareVersions, isVersion } from '../../serviceWorker/version';

export const minimumVersionHeader = 'SocAlytics-Minimum-Web-Version';

export interface MinimumVersionOptions {
  running: () => string | undefined;
  isBlocked: () => boolean;
  onRequired: (minimum: string) => void;
}

export const sharedMinimumVersion: MinimumVersionOptions = {
  running: () => import.meta.env?.VITE_SOCALYTICS_WEB_VERSION,
  isBlocked: isUpdateBlocked,
  onRequired: requireUpdate,
};

/**
 * Enters the required state when the platform's minimum Web UI version is higher than the running one; every later
 * request fails without being sent.
 */
export class MinimumVersionHandler implements Middleware {
  next: Middleware | undefined;

  constructor(private readonly options: MinimumVersionOptions) {}

  async execute(url: string, init: RequestInit, requestOptions?: Record<string, RequestOption>): Promise<Response> {
    if (this.options.isBlocked()) {
      throw new PlatformFailure('updateRequired');
    }
    const response = await this.next!.execute(url, init, requestOptions);
    const minimum = response.headers.get(minimumVersionHeader);
    const running = this.options.running();
    if (isVersion(minimum) && isVersion(running) && compareVersions(minimum, running) > 0) {
      this.options.onRequired(minimum);
    }
    return response;
  }
}
