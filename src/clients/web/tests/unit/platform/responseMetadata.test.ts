import type { Middleware } from '@microsoft/kiota-http-fetchlibrary';
import { createClubDtoFromDiscriminatorValue, type ClubDto } from '../../../src/generated/platform-api/models';
import { sendFor, sendWithMetadataFor } from '../../../src/platform/adapters/kiota';
import { createPlatformApi } from '../../../src/platform/client';
import { PlatformFailure } from '../../../src/platform/failure';
import { ResponseMetadataHandler, ResponseMetadataOption } from '../../../src/platform/middleware/responseMetadata';

const clubBody = { id: '00000000-0000-4000-8000-000000000001', displayName: 'Club', version: 3 };

function setup(respond: () => Response) {
  const api = createPlatformApi({
    getAntiforgeryToken: () => 'csrf-1',
    onSessionOutcome: () => {},
    onNetworkFailure: () => {},
    fetch: async () => respond(),
  });
  return { api, info: () => api.client.api.v1.club.toGetRequestInformation() };
}

const ok = (status: number, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(clubBody), { status, headers: { 'Content-Type': 'application/json', ...headers } });

describe('sendWithMetadataFor', () => {
  it('returns the body, the status, and the ETag', async () => {
    const { api, info } = setup(() => ok(201, { ETag: '"7"' }));
    const result = await sendWithMetadataFor<ClubDto>(api, info(), createClubDtoFromDiscriminatorValue);
    expect(result.body.displayName).toBe('Club');
    expect(result.status).toBe(201);
    expect(result.etag).toBe('"7"');
  });

  it('reports no ETag when the response has none', async () => {
    const { api, info } = setup(() => ok(200));
    const result = await sendWithMetadataFor<ClubDto>(api, info(), createClubDtoFromDiscriminatorValue);
    expect(result.status).toBe(200);
    expect(result.etag).toBeUndefined();
  });

  it('keeps plain requests without the option working', async () => {
    const { api, info } = setup(() => ok(200, { ETag: '"1"' }));
    const body = await sendFor<ClubDto>(api, info(), createClubDtoFromDiscriminatorValue);
    expect(body.displayName).toBe('Club');
  });

  it('maps error responses through toPlatformFailure', async () => {
    const { api, info } = setup(
      () =>
        new Response(JSON.stringify({ status: 412, code: 'version-conflict' }), {
          status: 412,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
    );
    const error = await sendWithMetadataFor<ClubDto>(api, info(), createClubDtoFromDiscriminatorValue).catch((e) => e);
    expect(error).toBeInstanceOf(PlatformFailure);
    expect(error).toMatchObject({ kind: 'conflict' });
  });
});

describe('ResponseMetadataHandler', () => {
  const handler = (response: Response) => {
    const h = new ResponseMetadataHandler();
    h.next = { next: undefined, execute: async () => response } as Middleware;
    return h;
  };

  it('records status and ETag of error responses too', async () => {
    const option = new ResponseMetadataOption();
    await handler(new Response(null, { status: 428, headers: { ETag: '"2"' } })).execute('/x', {}, {
      [option.getKey()]: option,
    });
    expect(option.status).toBe(428);
    expect(option.etag).toBe('"2"');
  });

  it('passes the response through without the option', async () => {
    const response = new Response(null, { status: 200 });
    await expect(handler(response).execute('/x', {})).resolves.toBe(response);
  });
});
