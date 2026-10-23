import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, waitFor } from "@testing-library/svelte";
import { webglAvailable } from "$lib/machine/fallback";
import { createMachineScene, type MachineSceneOptions } from "$lib/machine/scene";
import MachineView from "./MachineView.svelte";

vi.mock("$lib/machine/fallback", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$lib/machine/fallback")>()),
  webglAvailable: vi.fn(() => false),
}));

vi.mock("$lib/machine/scene", () => ({ createMachineScene: vi.fn() }));

const targets = {
  overview: { node: "Case", lookAtOffset: [0, 0, 0], distance: 1.6, elevation: 22, azimuth: 35 },
  packages: { node: "DriveBay", lookAtOffset: [0, 0, 0], distance: 0.45, elevation: 18, azimuth: -50 },
  firmware: { node: "TPM_LED", lookAtOffset: [0, 0, 0], distance: 0.16, elevation: 14, azimuth: -70 },
};
const highlights = {
  packages: { nodes: ["Drive_1", "Drive_2", "Drive_3", "Drive_4"], perItem: "Drive_{n}" },
  firmware: { nodes: ["TPM_Chip", "TPM_LED"], blink: "TPM_LED" },
};
const svg =
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">' +
  ["Case", "Drive_1", "Drive_2", "Drive_3", "TPM_Chip", "TPM_LED"].map((node) => `<g data-node="${node}"><path d="M0 0L1 1"/></g>`).join("") +
  "</svg>";

function fakeScene() {
  return { setSection: vi.fn(), setControls: vi.fn(), setPalette: vi.fn(), setReducedMotion: vi.fn(), dispose: vi.fn() };
}

const mode = (container: HTMLElement) => container.querySelector(".machine")?.getAttribute("data-mode");
const node = (container: HTMLElement, name: string) => container.querySelector(`path[data-node="${name}"]`);

beforeEach(() => {
  vi.mocked(webglAvailable).mockReset();
  vi.mocked(webglAvailable).mockReturnValue(false);
  vi.mocked(createMachineScene).mockReset();
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) => {
      const body = url.endsWith("targets.json") ? targets : url.endsWith("highlights.json") ? highlights : null;
      return { ok: true, status: 200, json: async () => body, text: async () => svg };
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("MachineView fallback", () => {
  it("shows the exploded still when WebGL is unavailable", async () => {
    const { container } = render(MachineView, { section: "packages", controls: { items: 2 } });

    await waitFor(() => expect(node(container, "Drive_1")).not.toBeNull());
    expect(mode(container)).toBe("still");
    expect(createMachineScene).not.toHaveBeenCalled();
  });

  it("applies the same highlight states to the still", async () => {
    const { container, rerender } = render(MachineView, { section: "packages", controls: { items: 2 } });
    await waitFor(() => expect(node(container, "Drive_1")).not.toBeNull());

    expect(node(container, "Drive_1")!.classList.contains("glow")).toBe(true);
    expect(node(container, "Drive_2")!.classList.contains("glow")).toBe(true);
    expect(node(container, "Drive_3")!.classList.contains("glow")).toBe(false);
    expect(node(container, "Case")!.classList.contains("glow")).toBe(false);

    await rerender({ section: "firmware", controls: { tpmEnabled: true } });
    expect(node(container, "TPM_LED")!.classList.contains("blink")).toBe(true);
    expect(node(container, "Drive_1")!.classList.contains("glow")).toBe(false);

    await rerender({ section: "firmware", controls: { tpmEnabled: false } });
    expect(node(container, "TPM_LED")!.classList.contains("glow")).toBe(true);
    expect(node(container, "TPM_LED")!.classList.contains("blink")).toBe(false);
  });

  it("renders the 3D scene when WebGL is available and drives it from the section", async () => {
    vi.mocked(webglAvailable).mockReturnValue(true);
    const scene = fakeScene();
    vi.mocked(createMachineScene).mockResolvedValue(scene as never);
    const { container, rerender } = render(MachineView, { section: "packages", controls: { items: 1 } });

    await waitFor(() => expect(mode(container)).toBe("scene"));
    expect(scene.setSection).toHaveBeenLastCalledWith("packages");
    expect(scene.setControls).toHaveBeenLastCalledWith(expect.objectContaining({ items: 1 }));

    await rerender({ section: "firmware", controls: { items: 1 } });
    expect(scene.setSection).toHaveBeenLastCalledWith("firmware");
  });

  it("switches to the still when the frame rate stays low", async () => {
    vi.mocked(webglAvailable).mockReturnValue(true);
    const scene = fakeScene();
    let options: MachineSceneOptions | null = null;
    vi.mocked(createMachineScene).mockImplementation(async (_container, _data, opts) => {
      options = opts;
      return scene as never;
    });
    const { container } = render(MachineView, { section: "packages" });
    await waitFor(() => expect(mode(container)).toBe("scene"));

    options!.onslow();
    await waitFor(() => expect(mode(container)).toBe("still"));
    expect(scene.dispose).toHaveBeenCalled();
    await waitFor(() => expect(node(container, "Drive_1")).not.toBeNull());
  });

  it("switches to the still when the scene cannot start", async () => {
    vi.mocked(webglAvailable).mockReturnValue(true);
    vi.mocked(createMachineScene).mockRejectedValue(new Error("context lost"));
    const { container } = render(MachineView, { section: "packages" });

    await waitFor(() => expect(mode(container)).toBe("still"));
  });

  it("disposes the scene when the view goes away", async () => {
    vi.mocked(webglAvailable).mockReturnValue(true);
    const scene = fakeScene();
    vi.mocked(createMachineScene).mockResolvedValue(scene as never);
    const { container, unmount } = render(MachineView, { section: "packages" });
    await waitFor(() => expect(mode(container)).toBe("scene"));

    unmount();
    expect(scene.dispose).toHaveBeenCalled();
  });
});

describe("MachineView reduced motion", () => {
  function stubReducedMotion(matches: boolean) {
    let listener: ((event: { matches: boolean }) => void) | null = null;
    vi.stubGlobal(
      "matchMedia",
      vi.fn(() => ({ matches, addEventListener: (_: string, l: typeof listener) => (listener = l), removeEventListener: vi.fn() })),
    );
    return (next: boolean) => listener!({ matches: next });
  }

  it("starts the scene with reduced motion when the preference is set, and follows changes", async () => {
    const change = stubReducedMotion(true);
    vi.mocked(webglAvailable).mockReturnValue(true);
    const scene = fakeScene();
    vi.mocked(createMachineScene).mockResolvedValue(scene as never);
    const { container } = render(MachineView, { section: "packages" });
    await waitFor(() => expect(mode(container)).toBe("scene"));

    expect(vi.mocked(createMachineScene).mock.calls[0][2].reducedMotion).toBe(true);
    change(false);
    expect(scene.setReducedMotion).toHaveBeenLastCalledWith(false);
  });

  it("marks the still as reduced, keeping the highlight and blink", async () => {
    stubReducedMotion(true);
    const { container } = render(MachineView, { section: "firmware", controls: { tpmEnabled: true } });
    await waitFor(() => expect(node(container, "TPM_LED")).not.toBeNull());

    expect(container.querySelector(".still")!.classList.contains("reduced")).toBe(true);
    expect(node(container, "TPM_LED")!.classList.contains("glow")).toBe(true);
    expect(node(container, "TPM_LED")!.classList.contains("blink")).toBe(true);
  });
});
