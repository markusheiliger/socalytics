import { request as playwrightRequest, type APIRequestContext, type Page } from '@playwright/test';
import { expect, test as base } from '../support/fixtures';

const ADMIN_ACCOUNT = process.env.SOCALYTICS_E2E_ADMIN_ACCOUNT ?? 'club-admin';
const ADMIN_INITIAL_PASSWORD = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
// The password the session test (session.spec.ts) changes the initial password to.
const ADMIN_CHANGED_PASSWORD = 'E2e-Changed-Password-0002!';
const MEMBER_PASSWORD = 'E2e-Member-Password-0003!';

// contracts/routes-and-roles.md: Club Admin sees every route, Coach and Viewer only Home, Teams, and Account.
const NAVIGATION = {
  ClubAdmin: ['Home', 'Club', 'Seasons', 'Teams', 'Members', 'Analyst Managers', 'Analyst Registry', 'Operations', 'Agent catalog', 'Pair a Manager', 'Account'],
  Coach: ['Home', 'Teams', 'Account'],
  Viewer: ['Home', 'Teams', 'Account'],
} as const;

type Role = keyof typeof NAVIGATION;

const test = base.extend<{ secrets: string[] }>({
  secrets: async ({}, use) => {
    await use([ADMIN_INITIAL_PASSWORD, ADMIN_CHANGED_PASSWORD, MEMBER_PASSWORD]);
  },
});

test.describe.configure({ mode: 'serial' });

interface ApiSession {
  context: APIRequestContext;
  csrf: string;
  // The session cookie is Secure, which the Node request context does not store over http.
  cookie: string;
}

async function postJson(session: ApiSession, path: string, body: unknown, method: 'POST' | 'PUT' = 'POST') {
  const response = await session.context.fetch(`/api/v1${path}`, {
    method,
    data: body,
    headers: { 'X-CSRF-Token': session.csrf, Cookie: session.cookie },
  });
  expect(response.ok(), `${method} ${path} answered ${response.status()}`).toBe(true);
  return response.status() === 204 ? undefined : await response.json();
}

async function trySignIn(baseURL: string, password: string) {
  const context = await playwrightRequest.newContext({ baseURL });
  const response = await context.post('/api/v1/session', { data: { accountName: ADMIN_ACCOUNT, password } });
  if (!response.ok()) {
    await context.dispose();
    return undefined;
  }
  const info = await response.json();
  const setCookie = response.headersArray().find((h) => h.name.toLowerCase() === 'set-cookie')?.value ?? '';
  return { context, info, cookie: setCookie.split(';')[0] };
}

// Signs in as the first Club Admin; a pending initial-password change is completed through the API first.
async function signInAsClubAdmin(baseURL: string): Promise<ApiSession> {
  let attempt = await trySignIn(baseURL, ADMIN_CHANGED_PASSWORD);
  if (!attempt) {
    attempt = await trySignIn(baseURL, ADMIN_INITIAL_PASSWORD);
    expect(attempt, 'the first Club Admin can sign in with a known e2e password').toBeDefined();
    if (attempt!.info.passwordChangeRequired) {
      await postJson(
        { context: attempt!.context, csrf: attempt!.info.antiforgeryToken, cookie: attempt!.cookie },
        '/me/password',
        { currentPassword: ADMIN_INITIAL_PASSWORD, newPassword: ADMIN_CHANGED_PASSWORD },
      );
      await attempt!.context.dispose();
      attempt = await trySignIn(baseURL, ADMIN_CHANGED_PASSWORD);
      expect(attempt, 'the Club Admin can sign in with the changed password').toBeDefined();
    }
  }
  return { context: attempt!.context, csrf: attempt!.info.antiforgeryToken, cookie: attempt!.cookie };
}

async function createMember(admin: ApiSession, baseURL: string, accountName: string, teamId: string, role: Role) {
  const created = await postJson(admin, '/members', { accountName });
  const redeemContext = await playwrightRequest.newContext({ baseURL });
  const redeemed = await redeemContext.post('/api/v1/credentials/redeem', {
    data: {
      accountName,
      credential: created.setPasswordCredential.credential,
      newPassword: MEMBER_PASSWORD,
    },
  });
  await redeemContext.dispose();
  expect(redeemed.ok(), 'the member sets the password with the credential code').toBe(true);
  await postJson(admin, `/members/${created.member.id}/team-roles/${teamId}`, { role: role.toLowerCase() }, 'PUT');
}

async function signInThroughUi(page: Page, accountName: string, password: string) {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in$/);
  await page.getByLabel('Sign-in name').fill(accountName);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
}

const run = Date.now().toString(36);
const accounts = { Coach: `e2e-coach-${run}`, Viewer: `e2e-viewer-${run}` };

test.beforeAll(async ({ baseURL }) => {
  const admin = await signInAsClubAdmin(baseURL!);
  try {
    const season = await postJson(admin, '/seasons', { name: `E2E navigation ${run}` });
    const team = await postJson(admin, `/seasons/${season.id}/teams`, { name: `E2E team ${run}` });
    await createMember(admin, baseURL!, accounts.Coach, team.id, 'Coach');
    await createMember(admin, baseURL!, accounts.Viewer, team.id, 'Viewer');
  } finally {
    await admin.context.dispose();
  }
});

test('Club Admin navigation offers every route', async ({ page }) => {
  await signInThroughUi(page, ADMIN_ACCOUNT, ADMIN_CHANGED_PASSWORD);
  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  await expect(nav.getByRole('link')).toHaveText([...NAVIGATION.ClubAdmin]);
});

for (const role of ['Coach', 'Viewer'] as const) {
  test(`${role} navigation offers exactly the allowed routes`, async ({ page }) => {
    await signInThroughUi(page, accounts[role], MEMBER_PASSWORD);
    const nav = page.getByRole('navigation', { name: 'Main navigation' });
    await expect(nav.getByRole('link')).toHaveText([...NAVIGATION[role]]);

    await page.goto('/members');
    await expect(page.getByRole('alert')).toContainText('not permitted');
    await expect(nav.getByRole('link')).toHaveText([...NAVIGATION[role]]);
  });
}
