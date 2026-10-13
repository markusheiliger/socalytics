import { expect, test } from '../support/fixtures';

test('loads the app', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible();
});
