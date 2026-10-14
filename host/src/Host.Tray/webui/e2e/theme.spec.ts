import { expect, test } from "@playwright/test";
import { contrastProblems, navLabels, openTray } from "./helpers";

// The page follows the Windows app theme through prefers-color-scheme, so each page is checked in both.
for (const scheme of ["light", "dark"] as const) {
  test.describe(`${scheme} theme`, () => {
    test.use({ colorScheme: scheme });

    test("the page paints the theme colors, not another page's", async ({ page }) => {
      await openTray(page);

      const colors = await page.evaluate(() => {
        const root = getComputedStyle(document.documentElement);
        return {
          bg: root.getPropertyValue("--bg").trim(),
          html: getComputedStyle(document.documentElement).backgroundColor,
          body: getComputedStyle(document.body).backgroundColor,
          bodyColor: getComputedStyle(document.body).color,
          htmlColor: getComputedStyle(document.documentElement).color,
        };
      });

      // body paints nothing of its own: it shows the page color from html, and inherits its text color.
      expect(colors.body).toBe("rgba(0, 0, 0, 0)");
      expect(colors.bodyColor).toBe(colors.htmlColor);
      expect(colors.html).toBe(scheme === "dark" ? "rgb(13, 17, 23)" : "rgb(243, 241, 236)");
    });

    test("every page has readable text and fits the window", async ({ page }) => {
      await openTray(page);
      const labels = await navLabels(page);
      expect(labels.length).toBeGreaterThan(5);

      for (const label of labels) {
        await page.getByRole("navigation", { name: "Sections" }).getByRole("button", { name: new RegExp("^" + label) }).click();
        // Let page transitions settle: they fade and slide in.
        await page.waitForTimeout(450);

        const problems = await contrastProblems(page);
        expect(problems, `${label}: text that cannot be read`).toEqual([]);

        const overflow = await page.evaluate(() => ({
          x: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        }));
        expect(overflow.x, `${label}: the window scrolls sideways`).toBeLessThanOrEqual(0);
      }
    });
  });
}

test("the ambient screen is black, and only the ambient screen", async ({ page }) => {
  await openTray(page, "#/ambient");
  await expect(page.locator("body.ambient")).toHaveCount(1);
  expect(await page.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe("rgb(0, 0, 0)");

  // Leaving it must not leave the page black: the same bundle shows the host window.
  await page.evaluate(() => (location.hash = "#/host"));
  await page.reload();
  expect(await page.evaluate(() => document.body.classList.contains("ambient"))).toBe(false);
});
