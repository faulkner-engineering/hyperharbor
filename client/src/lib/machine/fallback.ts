// When the machine view shows the exploded still instead of the 3D scene: no WebGL, or a frame rate that
// stays too low. Also the reduced-motion preference, which the 3D view follows with cuts and no orbit.

export const MIN_FPS = 30;
export const SLOW_WINDOW_MS = 5000;

/** Whether this browser can create a WebGL context. Checks the constructor first so jsdom stays quiet. */
export function webglAvailable(): boolean {
  if (typeof window === "undefined" || typeof WebGLRenderingContext === "undefined") return false;
  try {
    const canvas = document.createElement("canvas");
    return (canvas.getContext("webgl2") ?? canvas.getContext("webgl")) !== null;
  } catch {
    return false;
  }
}

/**
 * Counts frames over the last second and reports when the rate has stayed under MIN_FPS for SLOW_WINDOW_MS.
 * The first second only fills the window. Call reset() when the page was hidden, since frames stop then.
 */
export class FrameRateWatch {
  private frames: number[] = [];
  private first: number | null = null;
  private slowSince: number | null = null;

  constructor(
    private readonly minFps = MIN_FPS,
    private readonly windowMs = SLOW_WINDOW_MS,
  ) {}

  /** Records a frame at a time in milliseconds; true once the view has been slow for the whole window. */
  frame(now: number): boolean {
    this.first ??= now;
    this.frames.push(now);
    while (this.frames.length > 0 && this.frames[0] <= now - 1000) this.frames.shift();
    if (now - this.first < 1000) return false;
    if (this.frames.length >= this.minFps) {
      this.slowSince = null;
      return false;
    }
    this.slowSince ??= now;
    return now - this.slowSince >= this.windowMs;
  }

  reset(): void {
    this.frames = [];
    this.first = null;
    this.slowSince = null;
  }
}

const REDUCED_MOTION = "(prefers-reduced-motion: reduce)";

export function prefersReducedMotion(): boolean {
  return typeof matchMedia === "function" && matchMedia(REDUCED_MOTION).matches;
}

/** Calls back when the reduced-motion preference changes; returns the unsubscribe function. */
export function watchReducedMotion(onchange: (reduced: boolean) => void): () => void {
  if (typeof matchMedia !== "function") return () => {};
  const query = matchMedia(REDUCED_MOTION);
  const listener = (event: MediaQueryListEvent) => onchange(event.matches);
  query.addEventListener("change", listener);
  return () => query.removeEventListener("change", listener);
}
