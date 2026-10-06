import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/svelte";

import VmList from "./VmList.svelte";
import type { Vm, VmState } from "$lib/api/client";

function vm(state: VmState, overrides: Partial<Vm> = {}): Vm {
  return {
    id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
    name: "Dev Box",
    state,
    generation: 2,
    rdpAvailable: false,
    ipAddresses: [],
    provisioned: false,
    remoteDesktop: { address: null, reachableFromHost: false },
    guestOs: { family: "unknown", name: null },
    performanceMode: false,
    ...overrides,
  };
}

const provisioned = (state: VmState) =>
  vm(state, {
    provisioned: true,
    ipAddresses: ["192.168.0.50"],
    remoteDesktop: { address: "192.168.0.50", reachableFromHost: true },
    guestOs: { family: "windows", name: "Windows 11 Pro" },
  });

/** The buttons outside the actions menu. */
function rowButtons(): string[] {
  return screen
    .getAllByRole("button")
    .filter((button) => !button.closest(".menu-items"))
    .map((button) => button.textContent?.trim() ?? "");
}

/** Opens the actions menu and returns its items. */
async function openMenu(name = "Dev Box") {
  const summary = screen.getByLabelText(`Actions for ${name}`);
  await fireEvent.click(summary);
  return within(summary.closest("details")!.querySelector(".menu-items") as HTMLElement);
}

describe("VmList row actions", () => {
  it("shows Connect as the only button for a provisioned running VM, with the rest in the menu", async () => {
    render(VmList, { vms: [provisioned("running")] });

    expect(rowButtons()).toEqual(["Connect"]);
    const menu = await openMenu();
    for (const item of ["Console", "Shut down", "Restart", "Save state", "Force shut off…", "Settings…", "Delete…"]) {
      expect(menu.getByRole("button", { name: item })).toBeTruthy();
    }
  });

  it("offers Capture setup profile only for running Windows VMs, and only when the host has it", async () => {
    const oncapture = vi.fn();
    const running = provisioned("running");
    const { unmount } = render(VmList, { vms: [running], oncapture });

    await fireEvent.click((await openMenu()).getByRole("button", { name: "Capture setup profile…" }));
    expect(oncapture).toHaveBeenCalledWith(running);
    unmount();

    render(VmList, { vms: [provisioned("off")], oncapture });
    expect((await openMenu()).queryByRole("button", { name: "Capture setup profile…" })).toBeNull();
  });

  it("offers Apply setup profile for running Windows VMs that are not still installing", async () => {
    const onapplyprofile = vi.fn();
    const running = provisioned("running");
    const { unmount } = render(VmList, { vms: [running], onapplyprofile });

    await fireEvent.click((await openMenu()).getByRole("button", { name: "Apply setup profile…" }));
    expect(onapplyprofile).toHaveBeenCalledWith(running);
    unmount();

    const installing = render(VmList, { vms: [{ ...running, installState: "applyingProfile" }], onapplyprofile });
    expect((await openMenu()).queryByRole("button", { name: "Apply setup profile…" })).toBeNull();
    installing.unmount();

    // A host without the route gets no handler, so no item.
    render(VmList, { vms: [running] });
    expect((await openMenu()).queryByRole("button", { name: "Apply setup profile…" })).toBeNull();
  });

  it("leaves Capture setup profile out for hosts without setup profiles", async () => {
    render(VmList, { vms: [provisioned("running")] });

    expect((await openMenu()).queryByRole("button", { name: "Capture setup profile…" })).toBeNull();
  });

  it("offers Force shut off from the menu and reports the turnOff action", async () => {
    const onaction = vi.fn();
    const running = provisioned("running");
    render(VmList, { vms: [running], onaction });

    const menu = await openMenu();
    await fireEvent.click(menu.getByRole("button", { name: "Force shut off…" }));

    expect(onaction).toHaveBeenCalledWith(running, "turnOff");
  });

  it("starts an off VM from the row and offers no power actions in the menu", async () => {
    const onaction = vi.fn();
    const off = vm("off");
    render(VmList, { vms: [off], onaction });

    expect(rowButtons()).toEqual(["Start"]);
    await fireEvent.click(screen.getByRole("button", { name: "Start" }));
    expect(onaction).toHaveBeenCalledWith(off, "start");

    const menu = await openMenu();
    expect(menu.queryByRole("button", { name: "Force shut off…" })).toBeNull();
    expect(menu.queryByRole("button", { name: "Console" })).toBeNull();
    expect(menu.getByRole("button", { name: "Delete…" })).toBeTruthy();
  });

  it("resumes a paused VM and can still force it off", async () => {
    render(VmList, { vms: [provisioned("paused")] });

    expect(rowButtons()).toEqual(["Resume"]);
    const menu = await openMenu();
    expect(menu.getByRole("button", { name: "Console" })).toBeTruthy();
    expect(menu.getByRole("button", { name: "Force shut off…" })).toBeTruthy();
  });

  it("offers Set up for a running VM whose OS is known", () => {
    render(VmList, { vms: [vm("running", { guestOs: { family: "linux", name: "Ubuntu" } })] });

    expect(rowButtons()).toEqual(["Set up…"]);
  });
});

describe("VmList console", () => {
  it("is the row button while the OS is unknown, and opens that VM's console", async () => {
    const onconsole = vi.fn();
    const running = vm("running");
    render(VmList, { vms: [running], onconsole });

    expect(rowButtons()).toEqual(["Console"]);
    await fireEvent.click(screen.getByRole("button", { name: "Console" }));

    expect(onconsole).toHaveBeenCalledWith(running);
  });

  it("shows progress and disables other Console buttons while one opens", () => {
    const first = vm("running");
    const second = vm("running", { id: "1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f", name: "Other" });
    render(VmList, { vms: [first, second], consoleVmId: first.id });

    expect((screen.getByRole("button", { name: "Opening…" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Console" }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe("VmList OS detection", () => {
  it("says Detecting OS shortly after the VM starts", () => {
    render(VmList, { vms: [vm("running", { uptimeSeconds: 60 })] });

    expect(screen.getByText("Detecting OS…")).toBeTruthy();
  });

  it("stops detecting after a few minutes without a report", () => {
    render(VmList, { vms: [vm("running", { uptimeSeconds: 600 })] });

    expect(screen.queryByText("Detecting OS…")).toBeNull();
    expect(screen.getByText("OS not detected")).toBeTruthy();
  });

  it("says nothing about the OS while the VM is off", () => {
    render(VmList, { vms: [vm("off")] });

    expect(screen.queryByText("Detecting OS…")).toBeNull();
    expect(screen.queryByText("OS not detected")).toBeNull();
  });
});

describe("VmList unattended install", () => {
  it("shows the install step and keeps Console as the row button while it runs", () => {
    render(VmList, { vms: [vm("running", { installState: "waitingForRemoteAccess", guestOs: { family: "windows", name: "Windows 11 Pro" } })] });

    expect(screen.getByText("Waiting for Remote Desktop…")).toBeTruthy();
    expect(rowButtons()).toEqual(["Console"]);
  });

  it("asks Ubuntu installs to be confirmed in the console", () => {
    render(VmList, { vms: [vm("running", { installState: "awaitingConfirmation" })] });

    expect(screen.getByText("Installing (type yes in the console if asked)")).toBeTruthy();
  });

  it("offers manual setup again after an install failed", () => {
    render(VmList, { vms: [vm("running", { installState: "failed", guestOs: { family: "windows", name: "Windows 11 Pro" } })] });

    expect(screen.getByText("Install failed")).toBeTruthy();
    expect(rowButtons()).toEqual(["Set up…"]);
  });
});

describe("VmList Performance mode", () => {
  it("offers Performance mode for a Windows VM and reports which VM", async () => {
    const onperformance = vi.fn();
    const windows = provisioned("off");
    render(VmList, { vms: [windows], onperformance });

    const menu = await openMenu();
    await fireEvent.click(menu.getByRole("button", { name: "Performance mode…" }));

    expect(onperformance).toHaveBeenCalledWith(windows);
  });

  it("does not offer it for a Linux VM", async () => {
    render(VmList, { vms: [vm("off", { guestOs: { family: "linux", name: "Ubuntu 24.04" } })] });

    const menu = await openMenu();
    expect(menu.queryByRole("button", { name: "Performance mode…" })).toBeNull();
  });

  it("marks a VM in Performance mode with a GPU badge", () => {
    render(VmList, { vms: [vm("off", { performanceMode: true }), vm("off", { id: "other", name: "Plain" })] });

    expect(screen.getAllByText("GPU")).toHaveLength(1);
  });
});
