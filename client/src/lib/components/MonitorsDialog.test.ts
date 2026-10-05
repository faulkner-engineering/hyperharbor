import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));

import MonitorsDialog from "./MonitorsDialog.svelte";
import type { HostEntry, Monitor, MonitorChoice, Vm } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: "8f3b2c1a-0000-4000-8000-000000000001",
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.9.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm: Vm = {
  id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
  name: "Dev Box",
  state: "running",
  generation: 2,
  rdpAvailable: true,
  ipAddresses: ["192.168.0.50"],
  provisioned: true,
  remoteDesktop: { address: "192.168.0.50", reachableFromHost: true },
  guestOs: { family: "windows", name: "Windows 11 Pro" },
  performanceMode: false,
};

const monitors: Monitor[] = [
  { key: "laptop", mstscId: 0, name: "Built-in display", x: 0, y: 0, width: 1920, height: 1080, primary: true },
  { key: "samsung", mstscId: 1, name: "LS32CG51x", x: -2877, y: -586, width: 2560, height: 1440, primary: false },
  { key: "lg", mstscId: 2, name: "LG ULTRAWIDE", x: -317, y: -1080, width: 2560, height: 1080, primary: false },
];

function mockHost(choice: MonitorChoice) {
  invoke.mockImplementation(async (command: string) => {
    if (command === "list_monitors") return monitors;
    if (command === "get_monitor_choice") return choice;
    if (command === "set_monitor_choice") return undefined;
    throw new Error(`unexpected command ${command}`);
  });
}

const monitorButton = (name: string) => screen.findByRole("button", { name: new RegExp(name) });

describe("MonitorsDialog", () => {
  beforeEach(() => {
    invoke.mockReset();
  });

  it("selects monitors by clicking them and saves their keys", async () => {
    mockHost({ mode: "single", selected: [] });
    const onclose = vi.fn();
    render(MonitorsDialog, { host, vm, onclose });

    await fireEvent.click(await monitorButton("LG ULTRAWIDE"));
    await fireEvent.click(await monitorButton("LS32CG51x"));

    expect(invoke).toHaveBeenCalledWith("get_monitor_choice", { key: host.key, vmId: vm.id });
    expect((screen.getByLabelText("Only the monitors I select") as HTMLInputElement).checked).toBe(true);
    expect((await monitorButton("LG ULTRAWIDE")).getAttribute("aria-pressed")).toBe("true");
    expect((await monitorButton("Built-in display")).getAttribute("aria-pressed")).toBe("false");

    await fireEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(invoke).toHaveBeenCalledWith("set_monitor_choice", {
      key: host.key,
      vmId: vm.id,
      choice: { mode: "selected", selected: ["lg", "samsung"] },
    });
    expect(onclose).toHaveBeenCalled();
  });

  it("starts a selection from all monitors when all were in use", async () => {
    mockHost({ mode: "all", selected: [] });
    render(MonitorsDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await monitorButton("Built-in display"));
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(invoke).toHaveBeenCalledWith("set_monitor_choice", {
      key: host.key,
      vmId: vm.id,
      choice: { mode: "selected", selected: ["samsung", "lg"] },
    });
  });

  it("warns about a gap and blocks an empty selection", async () => {
    mockHost({ mode: "selected", selected: ["laptop", "samsung"] });
    render(MonitorsDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/not next to each other/)).toBeTruthy();

    await fireEvent.click(await monitorButton("Built-in display"));
    await fireEvent.click(await monitorButton("LS32CG51x"));
    expect(screen.getByText(/Choose at least one monitor/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("keeps saved monitors that are not connected and drops them for other modes", async () => {
    mockHost({ mode: "selected", selected: ["lg", "unplugged"] });
    render(MonitorsDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/One chosen monitor is not\s+connected now/)).toBeTruthy();

    await fireEvent.click(screen.getByLabelText("All monitors"));
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(invoke).toHaveBeenCalledWith("set_monitor_choice", {
      key: host.key,
      vmId: vm.id,
      choice: { mode: "all", selected: [] },
    });
  });
});
