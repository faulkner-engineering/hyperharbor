import { describe, expect, it } from "vitest";
import { applyHighlight, reactionFrame, resolveHighlight, type HighlightMap, type HighlightPart } from "./highlights";

const map: HighlightMap = {
  packages: { nodes: ["Drive_1", "Drive_2", "Drive_3", "Drive_4"], perItem: "Drive_{n}" },
  debloat: { nodes: ["Drive_1", "Drive_2", "Drive_3", "Drive_4"] },
  firmware: { nodes: ["TPM_Chip", "TPM_LED"], blink: "TPM_LED" },
  gpu: { nodes: ["GPU", "GPU_Fan_1", "GPU_Fan_2"], spin: ["GPU_Fan_1", "GPU_Fan_2"] },
  backup: { nodes: ["Backup_Disk", "Backup_LED"], pulse: "Backup_LED" },
};

type Material = "base" | "dim" | "glow" | "pulse";

function parts(...names: string[]): Map<string, HighlightPart<Material>> {
  return new Map(names.map((name) => [name, { object: { material: "base" as Material, visible: true }, base: (name === "Floor" ? "dim" : "base") as Material }]));
}

describe("resolveHighlight", () => {
  it("glows the section's nodes", () => {
    expect(resolveHighlight(map, "debloat", {}).glow).toEqual(["Drive_1", "Drive_2", "Drive_3", "Drive_4"]);
  });

  it("returns nothing for a section without highlights", () => {
    expect(resolveHighlight(map, "overview", { tpmEnabled: true })).toEqual({ glow: [], blink: null, pulse: null, spin: [] });
  });

  it("lights one drive per package", () => {
    expect(resolveHighlight(map, "packages", {}).glow).toEqual([]);
    expect(resolveHighlight(map, "packages", { items: 2 }).glow).toEqual(["Drive_1", "Drive_2"]);
    expect(resolveHighlight(map, "packages", { items: 9 }).glow).toHaveLength(4);
  });

  it("blinks the TPM LED only while TPM is enabled", () => {
    expect(resolveHighlight(map, "firmware", {}).blink).toBeNull();
    expect(resolveHighlight(map, "firmware", { tpmEnabled: true }).blink).toBe("TPM_LED");
  });

  it("pulses the backup LED only when a schedule is set", () => {
    expect(resolveHighlight(map, "backup", {}).pulse).toBeNull();
    expect(resolveHighlight(map, "backup", { backupScheduled: true }).pulse).toBe("Backup_LED");
  });

  it("spins the GPU fans only in performance mode", () => {
    expect(resolveHighlight(map, "gpu", {}).spin).toEqual([]);
    expect(resolveHighlight(map, "gpu", { performanceMode: true }).spin).toEqual(["GPU_Fan_1", "GPU_Fan_2"]);
  });
});

describe("applyHighlight", () => {
  it("puts the section's nodes on the glow material and leaves the rest on their base", () => {
    const all = parts("TPM_Chip", "TPM_LED", "CPU", "Floor");
    applyHighlight(all, resolveHighlight(map, "firmware", {}), "glow", "pulse");
    expect(all.get("TPM_Chip")!.object.material).toBe("glow");
    expect(all.get("TPM_LED")!.object.material).toBe("glow");
    expect(all.get("CPU")!.object.material).toBe("base");
    expect(all.get("Floor")!.object.material).toBe("dim");
  });

  it("clears the previous section and restores hidden parts", () => {
    const all = parts("TPM_LED", "CPU");
    applyHighlight(all, resolveHighlight(map, "firmware", {}), "glow", "pulse");
    all.get("TPM_LED")!.object.visible = false; // mid-blink
    applyHighlight(all, resolveHighlight(map, "gpu", {}), "glow", "pulse");
    expect(all.get("TPM_LED")!.object).toEqual({ material: "base", visible: true });
  });

  it("gives the pulsing node its own material", () => {
    const all = parts("Backup_Disk", "Backup_LED");
    applyHighlight(all, resolveHighlight(map, "backup", { backupScheduled: true }), "glow", "pulse");
    expect(all.get("Backup_Disk")!.object.material).toBe("glow");
    expect(all.get("Backup_LED")!.object.material).toBe("pulse");
  });

  it("skips nodes the model does not have", () => {
    const all = parts("Drive_1");
    applyHighlight(all, resolveHighlight(map, "debloat", {}), "glow", "pulse");
    expect(all.get("Drive_1")!.object.material).toBe("glow");
  });
});

describe("reactionFrame", () => {
  it("toggles blink visibility at 2 Hz", () => {
    expect(reactionFrame(0, false).blinkVisible).toBe(true);
    expect(reactionFrame(0.25, false).blinkVisible).toBe(true);
    expect(reactionFrame(0.5, false).blinkVisible).toBe(false);
    expect(reactionFrame(1.0, false).blinkVisible).toBe(true);
  });

  it("fades the pulse between dim and full", () => {
    const levels = [0, 0.25, 0.5, 0.75, 1, 1.5, 2].map((t) => reactionFrame(t, false).pulseOpacity);
    expect(Math.min(...levels)).toBeGreaterThanOrEqual(0.1);
    expect(Math.max(...levels)).toBeLessThanOrEqual(1);
    expect(new Set(levels).size).toBeGreaterThan(1);
  });

  it("spins over time, and not at all with reduced motion", () => {
    expect(reactionFrame(0.2, false).spinAngle).toBeGreaterThan(0);
    expect(reactionFrame(0.2, true).spinAngle).toBe(0);
  });
});
