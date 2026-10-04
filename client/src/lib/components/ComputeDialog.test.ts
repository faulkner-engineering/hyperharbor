import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import ComputeDialog from "./ComputeDialog.svelte";
import type { HostEntry, Vm, VmComputeSettings } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.2.0",
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
  rdpAvailable: false,
  ipAddresses: [],
  provisioned: false,
  remoteDesktop: { address: null, reachableFromHost: false },
  guestOs: { family: "linux", name: "Ubuntu" },
  performanceMode: false,
};

function settings(state: VmComputeSettings["state"]): VmComputeSettings {
  return {
    vmId: vm.id,
    state,
    processorCount: 2,
    startupMemoryMb: 768,
    maximumMemoryMb: 2048,
    dynamicMemory: true,
    nestedVirtualization: false,
    macAddressSpoofing: false,
    networkAdapterCount: 1,
    requiresOff: ["processorCount", "startupMemoryMb", "dynamicMemory", "nestedVirtualization"],
  };
}

function respond(current: VmComputeSettings) {
  invoke.mockImplementation((command: string) => {
    switch (command) {
      case "get_vm_compute":
        return Promise.resolve(current);
      case "get_host_resource":
        return Promise.resolve({
          logicalProcessorCount: 8,
          totalMemoryMb: 32768,
          availableMemoryMb: 20000,
          memoryReserveMb: 4096,
          virtualHardDiskFolder: "C:\\VMs",
          isoFolder: "C:\\ISOs",
        });
      case "update_vm_compute":
        return Promise.resolve({ settings: current, job: null });
      default:
        return Promise.reject(new Error(`unexpected ${command}`));
    }
  });
}

const saveButton = () => screen.getByRole("button", { name: "Save" }) as HTMLButtonElement;
const updates = () => invoke.mock.calls.filter(([command]) => command === "update_vm_compute").map(([, args]) => args.request);

beforeEach(() => {
  invoke.mockReset();
});

describe("ComputeDialog", () => {
  it("has nothing to save until something changes, even with memory that is not a whole gigabyte", async () => {
    respond(settings("off"));
    render(ComputeDialog, { host, vm, onclose: vi.fn() });

    await screen.findByRole("button", { name: "Developer mode" });

    expect(saveButton().disabled).toBe(true);
  });

  it("Developer mode turns on nested virtualization and MAC spoofing and turns off dynamic memory", async () => {
    respond(settings("off"));
    render(ComputeDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: "Developer mode" }));
    await fireEvent.click(saveButton());

    await screen.findByText("Saved.");
    expect(updates()[0]).toEqual({
      dynamicMemory: false,
      nestedVirtualization: true,
      macAddressSpoofing: true,
      shutDownToApply: false,
      acknowledgeWarnings: false,
    });
  });

  it("on a running VM, a change that needs it off waits for the shut-down option", async () => {
    respond(settings("running"));
    render(ComputeDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText(/Virtual processors/), { target: { value: "4" } });

    expect(screen.getByText(/needs the VM off/)).not.toBeNull();
    expect(saveButton().disabled).toBe(true);

    await fireEvent.click(screen.getByLabelText(/Shut Dev Box down, apply the changes/));
    await fireEvent.click(saveButton());

    await vi.waitFor(() => expect(updates()).toHaveLength(1));
    expect(updates()[0]).toEqual({ processorCount: 4, shutDownToApply: true, acknowledgeWarnings: false });
  });

  it("on a running VM, MAC spoofing applies without shutting down", async () => {
    respond(settings("running"));
    render(ComputeDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByLabelText(/MAC address spoofing/));
    await fireEvent.click(saveButton());

    await vi.waitFor(() => expect(updates()).toHaveLength(1));
    expect(updates()[0]).toEqual({ macAddressSpoofing: true, shutDownToApply: false, acknowledgeWarnings: false });
  });
});
