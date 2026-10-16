import type { Page, Route } from '@playwright/test';
import { expect, test } from '../support/fixtures';

const connectionRequired = /A connection is required/;

async function startOnce(page: Page, api: { signInAs(role: 'Coach'): void }) {
  api.signInAs('Coach');
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
  await page.reload();
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible();
}

// Registered after the simulated API, so it takes precedence and fails every platform request like a lost network.
async function cutNetwork(page: Page, requests: string[]) {
  await page.context().setOffline(true);
  const handler = (route: Route) => {
    requests.push(`${route.request().method()} ${new URL(route.request().url()).pathname}`);
    return route.abort('internetdisconnected');
  };
  await page.route('**/api/v1/**', handler);
  return () => page.unroute('**/api/v1/**', handler);
}

test('an offline start shows the shell and connection required on every route, with no data', async ({ page, api }) => {
  await startOnce(page, api);
  const attempted: string[] = [];
  await cutNetwork(page, attempted);

  for (const path of ['/', '/teams', '/members', '/matches/00000000-0000-4000-8000-000000000099', '/sign-in', '/account']) {
    await page.goto(path);
    await expect(page.getByRole('heading', { level: 1, name: 'SocAlytics' })).toBeVisible();
    await expect(page.getByText(connectionRequired)).toBeVisible();
    await expect(page.getByRole('heading', { level: 2 })).toHaveCount(0);
    await expect(page.getByText(/First Team|Simulated Club|admin|coach/i)).toHaveCount(0);
  }

  const stored = await page.evaluate(async () => {
    const urls: string[] = [];
    for (const name of await caches.keys()) {
      urls.push(...(await (await caches.open(name)).keys()).map((request) => new URL(request.url).pathname));
    }
    return urls;
  });
  expect(stored.some((url) => url.startsWith('/api/'))).toBe(false);
  expect(attempted.every((request) => request.startsWith('GET '))).toBe(true);
});

test('on reconnect a valid session continues and an ended session goes to sign-in', async ({ page, api }) => {
  await startOnce(page, api);
  const attempted: string[] = [];
  const restore = await cutNetwork(page, attempted);
  await page.goto('/');
  await expect(page.getByText(connectionRequired)).toBeVisible();

  await restore();
  await page.context().setOffline(false);
  await expect(page.getByRole('heading', { level: 2, name: 'Home' })).toBeVisible({ timeout: 40_000 });

  await page.context().setOffline(true);
  const restoreAgain = await cutNetwork(page, attempted);
  api.endSession();
  await page.goto('/');
  await expect(page.getByText(connectionRequired)).toBeVisible();
  await restoreAgain();
  await page.context().setOffline(false);
  await expect(page).toHaveURL(/\/sign-in/, { timeout: 40_000 });
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
});
