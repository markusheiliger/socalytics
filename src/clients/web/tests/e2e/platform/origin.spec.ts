import { expect, test } from '@playwright/test';

test('serves the app and the API through the Web UI origin', async ({ page, request }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible();

  const response = await request.get('/api/v1/session');
  expect(response.status()).toBe(401);
});
