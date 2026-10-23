import { afterEach, describe, expect, it, vi } from "vitest";
import { FrameRateWatch, MIN_FPS, prefersReducedMotion, SLOW_WINDOW_MS, watchReducedMotion, webglAvailable } from "./fallback";

/** Feeds frames at a steady rate from start to end (ms); returns the first time the watch fired, or null. */
function run(watch: FrameRateWatch, fps: number, start: number, end: number): number | null {
  for (let now = start; now <= end; now += 1000 / fps) {
    if (watch.frame(now)) return now;
  }
  return null;
}

describe("FrameRateWatch", () => {
  it("does not fire at a healthy frame rate", () => {
    expect(run(new FrameRateWatch(), 60, 0, 20_000)).toBeNull();
  });

  it("fires after five seconds under 30 frames per second", () => {
    const fired = run(new FrameRateWatch(), 20, 0, 20_000);
    expect(fired).not.toBeNull();
    // The first second fills the window, then the rate must stay low for SLOW_WINDOW_MS.
    expect(fired!).toBeGreaterThanOrEqual(1000 + SLOW_WINDOW_MS);
    expect(fired!).toBeLessThan(1000 + SLOW_WINDOW_MS + 200);
  });

  it("starts over when the rate recovers before five seconds", () => {
    const watch = new FrameRateWatch();
    expect(run(watch, 20, 0, 4000)).toBeNull();
    expect(run(watch, 60, 4000, 6000)).toBeNull();
    const fired = run(watch, 20, 6000, 20_000);
    expect(fired!).toBeGreaterThanOrEqual(6000 + SLOW_WINDOW_MS);
  });

  it("does not count a hidden page as slow after reset", () => {
    const watch = new FrameRateWatch();
    expect(run(watch, 20, 0, 4000)).toBeNull();
    watch.reset();
    expect(run(watch, 60, 60_000, 70_000)).toBeNull();
  });

  it("uses 30 frames per second as the floor", () => {
    expect(MIN_FPS).toBe(30);
    expect(run(new FrameRateWatch(), 31, 0, 20_000)).toBeNull();
  });
});

describe("webglAvailable", () => {
  it("is false where there is no WebGL (jsdom)", () => {
    expect(webglAvailable()).toBe(false);
  });
});

describe("reduced motion preference", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("is off when matchMedia is missing", () => {
    vi.stubGlobal("matchMedia", undefined);
    expect(prefersReducedMotion()).toBe(false);
  });

  it("follows the media query and its changes", () => {
    let listener: ((event: { matches: boolean }) => void) | null = null;
    const query = {
      matches: true,
      addEventListener: vi.fn((_: string, l: typeof listener) => (listener = l)),
      removeEventListener: vi.fn(),
    };
    vi.stubGlobal("matchMedia", vi.fn(() => query));
    expect(prefersReducedMotion()).toBe(true);

    const seen: boolean[] = [];
    const stop = watchReducedMotion((reduced) => seen.push(reduced));
    listener!({ matches: false });
    expect(seen).toEqual([false]);
    stop();
    expect(query.removeEventListener).toHaveBeenCalled();
  });
});
