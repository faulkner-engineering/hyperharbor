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
