import { expect, type Locator, type Page } from '@playwright/test';

const INTERACTIVE =
  'a[href], button, input:not([type="hidden"]), select, textarea, summary, [role="button"], [role="link"], [role="menuitem"], [role="tab"], [tabindex]';

type Reached = { index: number; focusVisible: boolean; description: string };

async function interactiveIndexes(scope: Locator): Promise<number[]> {
  return scope.locator(INTERACTIVE).evaluateAll((elements) =>
    elements.flatMap((el, index) => {
      const html = el as HTMLElement;
      const style = getComputedStyle(html);
      const disabled = (html as HTMLButtonElement).disabled === true || html.getAttribute('aria-disabled') === 'true';
      const hidden =
        style.visibility === 'hidden' || style.display === 'none' || html.getClientRects().length === 0;
      const skipped = html.tabIndex < 0;
      return disabled || hidden || skipped ? [] : [index];
    }),
  );
}

/**
 * Reaches every interactive element within `scope` using only the Tab key and checks that each
 * shows a visible focus indicator.
 */
export async function expectAllReachableByKeyboard(page: Page, scope: Locator = page.locator('body')): Promise<void> {
  const expected = await interactiveIndexes(scope);
  const elements = scope.locator(INTERACTIVE);
  const targets = new Set<number>(expected);
  const reached = new Map<number, Reached>();

  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  const limit = (await page.locator(INTERACTIVE).count()) + 5;

  for (let step = 0; step < limit && reached.size < targets.size; step++) {
    await page.keyboard.press('Tab');
    const hit = await elements.evaluateAll((all) => {
      const active = document.activeElement;
      const index = all.findIndex((el) => el === active);
      if (index < 0 || !active) return null;
      const style = getComputedStyle(active);
      const outline = style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0;
      const shadow = style.boxShadow !== 'none';
      const parentStyle = active.parentElement ? getComputedStyle(active.parentElement) : null;
      const wrapper =
        !!parentStyle && (parentStyle.outlineStyle !== 'none' || parentStyle.boxShadow !== 'none');
      const description = `${active.tagName.toLowerCase()}${active.id ? `#${active.id}` : ''} "${(active.getAttribute('aria-label') ?? active.textContent ?? '').trim().slice(0, 40)}"`;
      return { index, focusVisible: outline || shadow || wrapper, description };
    });
    if (hit && targets.has(hit.index) && !reached.has(hit.index)) {
      reached.set(hit.index, hit);
    }
  }

  const missed = [...targets].filter((index) => !reached.has(index));
  expect(missed, 'interactive elements not reachable with Tab').toEqual([]);
  const withoutFocus = [...reached.values()].filter((r) => !r.focusVisible).map((r) => r.description);
  expect(withoutFocus, 'elements without a visible focus indicator').toEqual([]);
}

/** Focuses the element with Tab navigation semantics and triggers it with the keyboard. */
export async function activateWithKeyboard(target: Locator, key: 'Enter' | 'Space' = 'Enter'): Promise<void> {
  await target.focus();
  await expect(target).toBeFocused();
  await target.press(key === 'Space' ? 'Space' : 'Enter');
}
