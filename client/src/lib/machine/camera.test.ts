import { describe, expect, it } from "vitest";
import {
  CameraRig,
  cameraPosition,
  easeInOut,
  FLY_MS,
  orbitSpeed,
  ORBIT_DEG_PER_S,
  rigFor,
  targetFor,
  type Rig,
  type TargetMap,
} from "./camera";

const targets: TargetMap = {
  overview: { node: "Case", lookAtOffset: [-0.25, 0.02, 0], distance: 1.6, elevation: 22, azimuth: 35 },
  firmware: { node: "TPM_LED", lookAtOffset: [0, 0, -0.01], distance: 0.16, elevation: 14, azimuth: -70 },
};

const overview: Rig = { lookAt: [0, 0, 0], distance: 1.6, elevation: 22, azimuth: 35 };
const close: Rig = { lookAt: [1, 2, 3], distance: 0.4, elevation: 10, azimuth: -100 };

describe("target lookup", () => {
  it("returns the section's target", () => {
    expect(targetFor(targets, "firmware")?.node).toBe("TPM_LED");
  });

  it("falls back to the overview for a section the model does not know", () => {
    expect(targetFor(targets, "tweaks")?.node).toBe("Case");
  });

  it("returns null when there is not even an overview", () => {
    expect(targetFor({}, "firmware")).toBeNull();
  });

  it("frames the node's center plus the look-at offset", () => {
    const rig = rigFor(targets.firmware, (node) => (node === "TPM_LED" ? [0.5, 0.4, -0.1] : undefined));
    expect(rig.lookAt[0]).toBeCloseTo(0.5);
    expect(rig.lookAt[1]).toBeCloseTo(0.4);
    expect(rig.lookAt[2]).toBeCloseTo(-0.11);
    expect(rig).toMatchObject({ distance: 0.16, elevation: 14, azimuth: -70 });
  });

  it("frames the origin plus the offset when the node is missing from the model", () => {
    expect(rigFor(targets.overview, () => undefined).lookAt).toEqual([-0.25, 0.02, 0]);
  });

  it("places the camera on the sphere, azimuth 0 in front (+Z)", () => {
    const [x, y, z] = cameraPosition({ lookAt: [0, 0, 0], distance: 2, elevation: 0, azimuth: 0 });
    expect(x).toBeCloseTo(0);
    expect(y).toBeCloseTo(0);
    expect(z).toBeCloseTo(2);
    const above = cameraPosition({ lookAt: [1, 1, 1], distance: 2, elevation: 90, azimuth: 0 });
    expect(above[1]).toBeCloseTo(3);
  });
});

describe("camera rig", () => {
  it("eases from start to end", () => {
    expect(easeInOut(0)).toBe(0);
    expect(easeInOut(0.5)).toBeCloseTo(0.5);
    expect(easeInOut(1)).toBe(1);
  });

  it("flies to the goal over 900 ms", () => {
    const rig = new CameraRig(overview, false, 0);
    rig.fly(close, 1000);
    const middle = rig.update(1000 + FLY_MS / 2);
    expect(middle.distance).toBeCloseTo((1.6 + 0.4) / 2);
    expect(rig.flying).toBe(true);
    const end = rig.update(1000 + FLY_MS);
    expect(end).toEqual(close);
    expect(rig.flying).toBe(false);
  });

  it("orbits at rest, slower when closer", () => {
    expect(orbitSpeed(1.6)).toBe(ORBIT_DEG_PER_S);
    expect(orbitSpeed(0.4)).toBeCloseTo(ORBIT_DEG_PER_S / 4);
    expect(orbitSpeed(3)).toBe(ORBIT_DEG_PER_S);

    const rig = new CameraRig(overview, false, 0);
    expect(rig.update(10_000).azimuth).toBeCloseTo(35 + 10 * ORBIT_DEG_PER_S);
    rig.fly(close, 10_000);
    rig.update(10_000 + FLY_MS);
    expect(rig.update(10_000 + FLY_MS + 10_000).azimuth).toBeCloseTo(-100 + 10 * orbitSpeed(0.4));
  });

  it("tilts the orbit: elevation rises and falls around the target", () => {
    const rig = new CameraRig(overview, false, 0);
    // A quarter turn at 9 degrees per second is 10 seconds.
    expect(rig.update(10_000).elevation).toBeGreaterThan(overview.elevation);
    expect(rig.update(30_000).elevation).toBeLessThan(overview.elevation);
  });

  it("takes the short way around after orbiting", () => {
    const rig = new CameraRig({ ...overview, azimuth: 0 }, false, 0);
    rig.update(40_000); // one full turn
    rig.fly({ ...overview, azimuth: 10 }, 40_000);
    const middle = rig.update(40_000 + FLY_MS / 2);
    expect(middle.azimuth).toBeGreaterThan(0);
    expect(middle.azimuth).toBeLessThan(10);
  });
});

describe("reduced motion", () => {
  it("cuts to the goal instead of flying", () => {
    const rig = new CameraRig(overview, true, 0);
    rig.fly(close, 1000);
    expect(rig.flying).toBe(false);
    expect(rig.update(1000)).toEqual(close);
  });

  it("does not orbit at rest", () => {
    const rig = new CameraRig(overview, true, 0);
    expect(rig.update(60_000)).toEqual(overview);
  });

  it("cuts a flight in progress when the preference turns on", () => {
    const rig = new CameraRig(overview, false, 0);
    rig.fly(close, 0);
    rig.update(100);
    rig.setReducedMotion(true, 100);
    expect(rig.update(100)).toEqual(close);
    expect(rig.update(20_000)).toEqual(close);
  });
});
