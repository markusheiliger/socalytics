import type { Middleware } from '@microsoft/kiota-http-fetchlibrary';
import type { RequestOption } from '@microsoft/kiota-abstractions';

export class ConnectivityHandler implements Middleware {
  next: Middleware | undefined;

  constructor(private readonly onNetworkFailure: () => void) {}

  async execute(url: string, init: RequestInit, options?: Record<string, RequestOption>): Promise<Response> {
    let response: Response;
    try {
      response = await this.next!.execute(url, init, options);
    } catch (error) {
      if (error instanceof TypeError) this.onNetworkFailure();
      throw error;
    }
    if (response.status >= 502 && response.status <= 504) this.onNetworkFailure();
    return response;
  }
}
