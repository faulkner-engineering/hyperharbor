import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import SetupProfileEditor from "./SetupProfileEditor.svelte";
import type { HostEntry } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.12.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const resources: Record<string, unknown> = {
  setupProfileCatalog: { browsers: [], policies: [], tweaks: [] },
  extensionCatalog: [],
  packageCatalog: [],
};

const machine = () => document.querySelector(".machine")!;
const hud = () => document.querySelector(".hud")!.textContent;

beforeEach(() => {
  invoke.mockReset();
  invoke.mockImplementation(async (command: string, args: Record<string, unknown>) => {
    if (command === "get_host_resource") return resources[args.resource as string];
    throw new Error(`unexpected ${command}`);
  });
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => ({ ok: true, status: 200, json: async () => ({}), text: async () => '<svg xmlns="http://www.w3.org/2000/svg"></svg>' })),
  );
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function open() {
  return render(SetupProfileEditor, {
    host,
    profileId: null,
    initial: { name: "Dev", install: [{ id: "Git.Git" }, { id: "Mozilla.Firefox" }] },
    vms: [],
    onclose: vi.fn(),
  });
}

describe("SetupProfileEditor machine view", () => {
  it("drives the machine and the form from the section list", async () => {
    open();

    expect(screen.getByRole("tab", { name: /Install/ }).getAttribute("aria-selected")).toBe("true");
    expect(machine().getAttribute("data-section")).toBe("packages");
    expect(screen.getByRole("tabpanel").textContent).toContain("In this profile");

    await fireEvent.click(screen.getByRole("tab", { name: /Remove/ }));
    expect(machine().getAttribute("data-section")).toBe("debloat");
    expect(screen.getByRole("tabpanel").textContent).toContain("Windows capabilities");

    await fireEvent.click(screen.getByRole("tab", { name: /Tweaks/ }));
    expect(machine().getAttribute("data-section")).toBe("overview");
  });

  it("shows the section and its counts in the HUD, and why the view is a still", async () => {
    open();

    await waitFor(() => expect(hud()).toContain("still: no WebGL"));
    expect(hud()).toContain("INSTALL · 2 packages, 2 of 4 drives lit");
  });

  it("remembers the palette and the motion setting", async () => {
    open();

    await fireEvent.click(screen.getByRole("checkbox", { name: "Green terminal" }));
    expect(document.querySelector(".editor")!.getAttribute("data-palette")).toBe("green");
    expect(localStorage.getItem("hh.machinePalette")).toBe("green");

    const motion = screen.getByRole("checkbox", { name: "Motion" }) as HTMLInputElement;
    await fireEvent.click(motion);
    expect(localStorage.getItem("hh.machineMotion")).toBe(motion.checked ? "on" : "off");
  });
});
