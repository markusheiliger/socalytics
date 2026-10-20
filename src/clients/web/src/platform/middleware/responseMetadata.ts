import type { Middleware } from '@microsoft/kiota-http-fetchlibrary';
import type { RequestOption } from '@microsoft/kiota-abstractions';

const optionKey = 'ResponseMetadataOption';

/** Collects the status and `ETag` of the final response of the request that carries it (research.md § R3). */
export class ResponseMetadataOption implements RequestOption {
  status: number | undefined;
  etag: string | undefined;

  getKey(): string {
    return optionKey;
  }
}

export class ResponseMetadataHandler implements Middleware {
  next: Middleware | undefined;

  async execute(url: string, init: RequestInit, options?: Record<string, RequestOption>): Promise<Response> {
    const response = await this.next!.execute(url, init, options);
    const option = options?.[optionKey];
    if (option instanceof ResponseMetadataOption) {
      option.status = response.status;
      option.etag = response.headers.get('ETag') ?? undefined;
    }
    return response;
  }
}
