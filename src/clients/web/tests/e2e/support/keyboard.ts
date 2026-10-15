import { expect, type Locator, type Page } from '@playwright/test';

const INTERACTIVE =
  'a[href], button, input:not([type="hidden"]), select, textarea, summary, [role="button"], [role="link"], [role="menuitem"], [role="tab"], [tabindex]';

type Reached = { index: number; focusVisible: boolean; description: string };

async function interactiveTargets(scope: Locator): Promise<{ index: number; description: string }[]> {
  return scope.locator(INTERACTIVE).evaluateAll((elements) =>
    elements.flatMap((el, index) => {
      const html = el as HTMLElement;
      const style = getComputedStyle(html);
      const disabled = (html as HTMLButtonElement).disabled === true || html.getAttribute('aria-disabled') === 'true';
      const hidden =
        style.visibility === 'hidden' || style.display === 'none' || html.getClientRects().length === 0;
      const skipped = html.tabIndex < 0;
      if (disabled || hidden || skipped) return [];
      const description = `${html.tagName.toLowerCase()}${html.id ? `#${html.id}` : ''} "${(html.getAttribute('aria-label') ?? html.textContent ?? '').trim().slice(0, 40)}"`;
      return [{ index, description }];
    }),
  );
}

/**
 * Reaches every interactive element within `scope` using only the Tab key and checks that each
 * shows a visible focus indicator.
 */
export async function expectAllReachableByKeyboard(page: Page, scope: Locator = page.locator('body')): Promise<void> {
  // A view that is still being replaced would leave targets that vanish; wait until the set is stable.
  let expected = await interactiveTargets(scope);
  for (let attempt = 0; attempt < 20; attempt++) {
    await page.waitForTimeout(150);
    const next = await interactiveTargets(scope);
    const stable = JSON.stringify(next) === JSON.stringify(expected);
    expected = next;
    if (stable) break;
  }
  const elements = scope.locator(INTERACTIVE);
  const targets = new Set<number>(expected.map((t) => t.index));
  const reached = new Map<number, Reached>();
  const descriptions = new Map<number, string>(expected.map((t) => [t.index, t.description]));

  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  // A date-time input takes one Tab stop per segment and one for its picker.
  const limit = (await page.locator(INTERACTIVE).count()) + 5 + (await page.locator('input[type="datetime-local"]').count()) * 8;

  for (let step = 0; step < limit && reached.size < targets.size; step++) {
    await page.evaluate(() => {
      (window as unknown as { __keyboardPrevious: Element | null }).__keyboardPrevious = document.activeElement;
    });
    await page.keyboard.press('Tab');
    // The last element or the end of the cycle does not change focus, so a timeout is tolerated.
    await page
      .waitForFunction(
        () => document.activeElement !== (window as unknown as { __keyboardPrevious: Element | null }).__keyboardPrevious,
        undefined,
        { timeout: 1000 },
      )
      .catch(() => undefined);
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

  const missed = [...targets].filter((index) => !reached.has(index)).map((index) => `#${index} ${descriptions.get(index)}`);
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
