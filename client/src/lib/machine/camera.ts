// Camera targets for the profile builder's machine view, and the rig that flies between them.
// targets.json maps a profile section id to a node to frame; adding a section is a JSON entry, not scene code.

export type Vec3 = [number, number, number];

/** One entry of static/machine/targets.json. Angles are in degrees, distances in meters. */
export interface CameraTarget {
  node: string;
  lookAtOffset: Vec3;
  distance: number;
  elevation: number;
  azimuth: number;
}

export type TargetMap = Record<string, CameraTarget>;

/** A camera on a sphere around a look-at point. */
export interface Rig {
  lookAt: Vec3;
  distance: number;
  elevation: number;
  azimuth: number;
}

export const OVERVIEW = "overview";
/** How long a fly between sections takes. */
export const FLY_MS = 900;
/** Orbit speed at rest at the reference distance: a full turn in 40 seconds. */
export const ORBIT_DEG_PER_S = 9;
export const ORBIT_REFERENCE_DISTANCE = 1.6;
/** How far the orbit rises and falls from the target elevation, which tilts its plane. */
export const ORBIT_TILT_DEG = 4;

/** The target for a section, or the overview when the model has no target for it. */
export function targetFor(targets: TargetMap, section: string): CameraTarget | null {
  return targets[section] ?? targets[OVERVIEW] ?? null;
}

/** The rig that frames a target: the center of its node plus the offset. An unknown node frames the origin. */
export function rigFor(target: CameraTarget, centerOf: (node: string) => Vec3 | undefined): Rig {
  const center = centerOf(target.node) ?? [0, 0, 0];
  const [dx, dy, dz] = target.lookAtOffset;
  return {
    lookAt: [center[0] + dx, center[1] + dy, center[2] + dz],
    distance: target.distance,
    elevation: target.elevation,
    azimuth: target.azimuth,
  };
}

/** The camera position for a rig. Azimuth 0 looks from +Z, the front of the case and the monitor. */
export function cameraPosition(rig: Rig): Vec3 {
  const el = (rig.elevation * Math.PI) / 180;
  const az = (rig.azimuth * Math.PI) / 180;
  return [
    rig.lookAt[0] + rig.distance * Math.cos(el) * Math.sin(az),
    rig.lookAt[1] + rig.distance * Math.sin(el),
    rig.lookAt[2] + rig.distance * Math.cos(el) * Math.cos(az),
  ];
}

export function easeInOut(x: number): number {
  return x < 0.5 ? 4 * x * x * x : 1 - Math.pow(-2 * x + 2, 3) / 2;
}

/** Degrees per second of the rest orbit; closer views turn slower so the framed part stays readable. */
export function orbitSpeed(distance: number): number {
  return ORBIT_DEG_PER_S * Math.min(1, distance / ORBIT_REFERENCE_DISTANCE);
}

function wrap180(degrees: number): number {
  return ((((degrees + 180) % 360) + 360) % 360) - 180;
}

function lerp(a: number, b: number, k: number): number {
  return a + (b - a) * k;
}

function copy(rig: Rig): Rig {
  return { ...rig, lookAt: [...rig.lookAt] };
}

/**
 * Eases the camera to a goal over FLY_MS, then orbits slowly around it. With reduced motion it cuts to the
 * goal and holds still. Times are in milliseconds, as performance.now() reports them.
 */
export class CameraRig {
  private current: Rig;
  private goal: Rig;
  private flight: { start: number; from: Rig } | null = null;
  private restSince: number;

  constructor(
    initial: Rig,
    private reducedMotion = false,
    now = 0,
  ) {
    this.current = copy(initial);
    this.goal = copy(initial);
    this.restSince = now;
  }

  get flying(): boolean {
    return this.flight !== null;
  }

  fly(goal: Rig, now: number): void {
    this.goal = copy(goal);
    if (this.reducedMotion) {
      this.cut(now);
      return;
    }
    // Take the short way around: the orbit can leave the azimuth several turns from the goal.
    const from = copy(this.current);
    from.azimuth = goal.azimuth - wrap180(goal.azimuth - from.azimuth);
    this.flight = { start: now, from };
  }

  setReducedMotion(reduced: boolean, now: number): void {
    this.reducedMotion = reduced;
    if (reduced) this.cut(now);
  }

  update(now: number): Rig {
    if (this.flight) {
      const k = Math.min(1, (now - this.flight.start) / FLY_MS);
      const e = easeInOut(k);
      const { from } = this.flight;
      this.current = {
        lookAt: [lerp(from.lookAt[0], this.goal.lookAt[0], e), lerp(from.lookAt[1], this.goal.lookAt[1], e), lerp(from.lookAt[2], this.goal.lookAt[2], e)],
        distance: lerp(from.distance, this.goal.distance, e),
        elevation: lerp(from.elevation, this.goal.elevation, e),
        azimuth: lerp(from.azimuth, this.goal.azimuth, e),
      };
      if (k >= 1) {
        this.cut(now);
      }
    } else if (!this.reducedMotion) {
      const angle = ((now - this.restSince) / 1000) * orbitSpeed(this.goal.distance);
      this.current = {
        ...copy(this.goal),
        azimuth: this.goal.azimuth + angle,
        elevation: this.goal.elevation + ORBIT_TILT_DEG * Math.sin((angle * Math.PI) / 180),
      };
    }
    return copy(this.current);
  }

  private cut(now: number): void {
    this.flight = null;
    this.current = copy(this.goal);
    this.restSince = now;
  }
}
