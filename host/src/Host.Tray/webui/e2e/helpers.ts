import { readFileSync } from "node:fs";
import type { Page } from "@playwright/test";

// The first fixture is a full TrayViewState written by the .NET tests (HH_WRITE_TRAY_FIXTURES=1), so the pages render
// with devices, backups, Game ready cards and the rest.
const fixtures = JSON.parse(readFileSync(new URL("../src/bridge.fixtures.json", import.meta.url), "utf8")) as Array<Record<string, unknown>>;
export const fullState = fixtures[0];

/**
 * Opens the page the way the tray does: a stand-in for window.chrome.webview exists before the page's scripts run, and
 * the state is delivered as a message once the page has asked for it.
 */
export async function openTray(page: Page, hash = "#/host"): Promise<void> {
  await page.addInitScript(() => {
    const handlers: Array<(event: { data: unknown }) => void> = [];
    (window as unknown as { chrome: unknown }).chrome = {
      webview: {
        postMessage: () => {},
        addEventListener: (_type: string, handler: (event: { data: unknown }) => void) => handlers.push(handler),
      },
    };
    (window as unknown as { __deliver: (message: unknown) => void }).__deliver = (message) => handlers.forEach((handler) => handler({ data: message }));
  });
  await page.goto("/" + hash);
  await page.waitForFunction(() => typeof (window as unknown as { __deliver?: unknown }).__deliver === "function");
  if (hash === "#/host") {
    await page.evaluate((state) => (window as unknown as { __deliver: (m: unknown) => void }).__deliver(state), fullState);
    await page.waitForSelector("nav[aria-label='Sections']");
  }
}

/** The page buttons in the navigation, by label. */
export async function navLabels(page: Page): Promise<string[]> {
  return page.$$eval("nav[aria-label='Sections'] button", (buttons) => buttons.map((button) => button.textContent?.replace(/\d+$/, "").trim() ?? ""));
}

export interface ContrastProblem {
  text: string;
  selector: string;
  foreground: string;
  background: string;
  ratio: number;
}

/**
 * Checks every piece of visible text against the color it is actually drawn on: the nearest opaque background behind it,
 * found by walking up the tree. A global style that set light text on a light card, or dark text on a black page, shows
 * up here as a ratio near 1, which no unit test of the components could notice.
 */
export async function contrastProblems(page: Page, minimum = 3): Promise<ContrastProblem[]> {
  return page.evaluate((min) => {
    type Rgba = [number, number, number, number];
    const parse = (value: string): Rgba => {
      const match = value.match(/rgba?\(([^)]+)\)/);
      if (!match) return [0, 0, 0, 0];
      const parts = match[1].split(/[ ,/]+/).filter(Boolean).map(Number);
      return [parts[0], parts[1], parts[2], parts[3] ?? 1];
    };
    const over = (top: Rgba, below: Rgba): Rgba => {
      const alpha = top[3] + below[3] * (1 - top[3]);
      if (alpha === 0) return [0, 0, 0, 0];
      const mix = (a: number, b: number) => (a * top[3] + b * below[3] * (1 - top[3])) / alpha;
      return [mix(top[0], below[0]), mix(top[1], below[1]), mix(top[2], below[2]), alpha];
    };
    const luminance = ([r, g, b]: Rgba) => {
      const channel = (c: number) => ((c /= 255) <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
      return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
    };
    const ratioOf = (a: Rgba, b: Rgba) => {
      const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
      return (hi + 0.05) / (lo + 0.05);
    };
    const background = (element: Element): Rgba => {
      const layers: Rgba[] = [];
      for (let node: Element | null = element; node; node = node.parentElement) {
        layers.push(parse(getComputedStyle(node).backgroundColor));
      }
      // The canvas is white unless the page paints it; painted layers go on top of it.
      return layers.reduceRight<Rgba>((below, layer) => over(layer, below), [255, 255, 255, 1]);
    };
    const describe = (element: Element) => {
      const id = element.id ? "#" + element.id : "";
      const classes = [...element.classList].slice(0, 2).map((name) => "." + name).join("");
      return element.tagName.toLowerCase() + id + classes;
    };

    const problems: ContrastProblem[] = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim();
      const element = node.parentElement;
      if (!text || !element) continue;
      const style = getComputedStyle(element);
      const box = element.getBoundingClientRect();
      if (style.visibility === "hidden" || style.display === "none" || box.width === 0 || box.height === 0) continue;
      // Disabled controls are exempt from contrast rules, as in WCAG.
      if (element.closest("[disabled], [aria-disabled='true']")) continue;
      // A fade-in in progress is not the resting look.
      let opacity = 1;
      for (let node2: Element | null = element; node2; node2 = node2.parentElement) opacity *= Number(getComputedStyle(node2).opacity);
      const foreground = parse(style.color);
      const back = background(element);
      const effective = over([foreground[0], foreground[1], foreground[2], foreground[3] * opacity], back);
      const ratio = ratioOf(effective, back);
      if (ratio < min) {
        problems.push({
          text: text.slice(0, 40),
          selector: describe(element),
          foreground: style.color,
          background: `rgb(${back.slice(0, 3).map(Math.round).join(", ")})`,
          ratio: Math.round(ratio * 100) / 100,
        });
      }
    }
    return problems;
  }, minimum);
}
