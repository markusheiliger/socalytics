import { request as playwrightRequest, type APIRequestContext } from '@playwright/test';

const PLATFORM_URL = 'http://127.0.0.1:4174';
const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
// The password session.spec.ts changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';

type SeasonItem = { id: string; state: string };
type SeasonPage = { items: SeasonItem[]; continuationToken?: string | null };

async function expectOk(response: Awaited<ReturnType<APIRequestContext['get']>>, what: string) {
  if (!response.ok()) {
    throw new Error(`${what} failed with status ${response.status()}: ${await response.text()}`);
  }
}

/**
 * Signs in as the Club Admin through the API and archives every active season, so the club's single active season is
 * free for the next spec. Idempotent and independent of UI timing.
 */
export async function archiveActiveSeasons(baseURL: string = PLATFORM_URL): Promise<void> {
  const api = await playwrightRequest.newContext({ baseURL });
  try {
    const signIn = await api.post('/api/v1/session', {
      data: { accountName: ADMIN_ACCOUNT, password: ADMIN_CHANGED_PASSWORD },
    });
    await expectOk(signIn, 'Club Admin sign-in');
    const { antiforgeryToken } = (await signIn.json()) as { antiforgeryToken: string };
    // The session cookie is Secure, which the request context's cookie jar does not keep over http, so it is sent by hand.
    const cookie = signIn
      .headersArray()
      .filter((h) => h.name.toLowerCase() === 'set-cookie')
      .map((h) => h.value.split(';')[0])
      .find((c) => c.startsWith('__Host-socalytics-session='));
    if (!cookie) throw new Error('Club Admin sign-in returned no session cookie');
    const headers = { Cookie: cookie, 'X-CSRF-Token': antiforgeryToken };

    const active: SeasonItem[] = [];
    let continuationToken: string | null | undefined;
    do {
      const query = new URLSearchParams({ pageSize: '200' });
      if (continuationToken) query.set('continuationToken', continuationToken);
      const list = await api.get(`/api/v1/seasons?${query}`, { headers });
      await expectOk(list, 'Listing seasons');
      const page = (await list.json()) as SeasonPage;
      active.push(...page.items.filter((s) => s.state === 'active'));
      continuationToken = page.continuationToken;
    } while (continuationToken);

    for (const season of active) {
      const archived = await api.post(`/api/v1/seasons/${season.id}/archive`, { headers });
      await expectOk(archived, `Archiving season ${season.id}`);
    }

    await api.delete('/api/v1/session', { headers });
  } finally {
    await api.dispose();
  }
}
