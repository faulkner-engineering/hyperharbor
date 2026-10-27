import { expect, test, type Page } from "@playwright/test";
import { fullState, openTray } from "./helpers";

// Reduced motion is also the default in many Remote Desktop sessions. A global rule that shortened every animation to
// 1 ms but left the infinite ones looping made the update spinner and sliding bar race at a thousand times their speed.

/** The Updates page while a downloaded package is checked: a spinner in the steps and a sliding bar of unknown length. */
async function openVerifying(page: Page): Promise<void> {
  await openTray(page);
  const state = structuredClone(fullState) as { value: { update: Record<string, unknown> } };
  state.value.update.progress = { step: "verifying", bytesDone: 0, bytesTotal: 0 };
  await page.evaluate((message) => (window as unknown as { __deliver: (m: unknown) => void }).__deliver(message), state);
  await page.getByRole("navigation", { name: "Sections" }).getByRole("button", { name: /^Updates/ }).click();
  await page.waitForSelector(".sliding");
}

const animation = (page: Page, selector: string) =>
  page.$eval(selector, (element) => {
    const style = getComputedStyle(element);
    return { name: style.animationName, duration: style.animationDuration, count: style.animationIterationCount };
  });

test("the spinner and the sliding bar move at their own speed", async ({ page }) => {
  await openVerifying(page);

  expect(await animation(page, ".steps .spinner")).toEqual({ name: "spin", duration: "0.8s", count: "infinite" });
  expect(await animation(page, ".sliding")).toMatchObject({ duration: "1.4s", count: "infinite" });
});

test.describe("with reduced motion", () => {
  test.use({ reducedMotion: "reduce" });

  test("nothing loops, and the bar still shows work in progress", async ({ page }) => {
    await openVerifying(page);

    // The spinner runs once and rests as an open ring.
    expect(await animation(page, ".steps .spinner")).toMatchObject({ count: "1" });

    // The bar does not slide off its track: it stays across it, fainter.
    expect((await animation(page, ".sliding")).name).toBe("none");
    const bar = await page.$eval(".sliding", (element) => {
      const track = element.parentElement!.getBoundingClientRect();
      const box = element.getBoundingClientRect();
      return { left: box.left - track.left, width: box.width, trackWidth: track.width, opacity: getComputedStyle(element).opacity };
    });
    expect(bar.left).toBe(0);
    expect(bar.width).toBeCloseTo(bar.trackWidth, 0);
    expect(Number(bar.opacity)).toBeLessThan(1);
  });
});
