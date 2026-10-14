import { describe, expect, it } from "vitest";
import { DRIFT_PERIOD_MS, driftFor, vmLine } from "./ambient";

describe("driftFor", () => {
  it("holds a position for the whole period and jumps at its end", () => {
    const start = new Date(10 * DRIFT_PERIOD_MS);
    const inside = new Date(10 * DRIFT_PERIOD_MS + DRIFT_PERIOD_MS - 1);
    const next = new Date(11 * DRIFT_PERIOD_MS);
    expect(driftFor(inside)).toEqual(driftFor(start));
    expect(driftFor(next)).not.toEqual(driftFor(start));
  });

  it("stays close to the center", () => {
    for (let period = 0; period < 20; period++) {
      const { x, y } = driftFor(new Date(period * DRIFT_PERIOD_MS));
      expect(Math.abs(x)).toBeLessThanOrEqual(5);
      expect(Math.abs(y)).toBeLessThanOrEqual(5);
    }
  });
});

describe("vmLine", () => {
  it("says when nothing runs", () => expect(vmLine([])).toBe("No virtual machines running"));
  it("lists a few names", () => expect(vmLine(["A", "B"])).toBe("Running: A, B"));
  it("counts many", () => expect(vmLine(["A", "B", "C", "D"])).toBe("4 virtual machines running"));
});

describe("the ambient page styles", () => {
  it("scope their global rules to the ambient route, so the host window keeps its theme", async () => {
    // A rule on plain html or body in this component reaches every window, because all pages share one bundle.
    const source = (await import("./Ambient.svelte?raw")).default as string;
    const globals = [...source.matchAll(/:global\(([^)]*)\)/g)].map((match) => match[1]);

    expect(globals.length).toBeGreaterThan(0);
    for (const selector of globals) {
      expect(selector, "global selector " + selector).toMatch(/\.ambient/);
    }
  });
});
