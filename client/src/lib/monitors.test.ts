import { describe, expect, it } from "vitest";
import { areContiguous } from "./monitors";
import type { Monitor } from "$lib/api/client";

const monitor = (key: string, x: number, y: number, width: number, height: number): Monitor => ({
  key,
  mstscId: 0,
  name: key,
  x,
  y,
  width,
  height,
  primary: false,
});

// This development PC's layout, as mstsc /l reported it.
const laptop = monitor("laptop", 0, 0, 1920, 1080);
const samsung = monitor("samsung", -2877, -586, 2560, 1440);
const ultrawide = monitor("ultrawide", -317, -1080, 2560, 1080);

describe("areContiguous", () => {
  it("accepts zero or one monitor", () => {
    expect(areContiguous([])).toBe(true);
    expect(areContiguous([samsung])).toBe(true);
  });

  it("accepts monitors that share an edge, directly or through another", () => {
    expect(areContiguous([laptop, ultrawide])).toBe(true);
    expect(areContiguous([samsung, ultrawide])).toBe(true);
    expect(areContiguous([laptop, samsung, ultrawide])).toBe(true);
  });

  it("rejects monitors with a gap between them", () => {
    expect(areContiguous([laptop, samsung])).toBe(false);
  });

  it("rejects monitors that only meet at a corner", () => {
    expect(areContiguous([monitor("a", 0, 0, 100, 100), monitor("b", 100, 100, 100, 100)])).toBe(false);
  });
});
