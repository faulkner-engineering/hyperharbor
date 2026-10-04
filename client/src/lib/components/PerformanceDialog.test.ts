import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import PerformanceDialog from "./PerformanceDialog.svelte";
import type { HostEntry, HostGpu, Vm, VmJob, VmPerformance, VmState } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.7.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm: Vm = {
  id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
  name: "Workstation",
  state: "off",
  generation: 2,
  rdpAvailable: false,
  ipAddresses: [],
  provisioned: true,
  remoteDesktop: { address: null, reachableFromHost: false },
  guestOs: { family: "windows", name: "Windows 11 Pro" },
  performanceMode: false,
};

const intelPath = "\\\\?\\PCI#VEN_8086&DEV_9BC4#3&11583659&0&10#{064092b3-625e-43bf-9eb5-dc845897dd59}";

const gpu: HostGpu = {
  gpus: [
    {
      name: "Intel(R) UHD Graphics",
      vendor: "intel",
      driverVersion: "30.0.101.1122",
      partitionable: true,
      partitionCount: 32,
      instancePath: intelPath,
    },
  ],
  warnings: [],
};

const off: VmPerformance = { vmId: vm.id, enabled: false, settings: null, gpuAttached: false, driver: null, warnings: [] };

const on = (drift: boolean): VmPerformance => ({
  vmId: vm.id,
  enabled: true,
  settings: {
    processorCount: 4,
    memoryMb: 8192,
    gpu: { instancePath: intelPath, vramPercent: 60, encodePercent: 50, decodePercent: 50, computePercent: 50 },
    mmio: { lowGapMb: 1024, highGapMb: 32768 },
    moveStorageTo: null,
    rdp: { hardwareEncoding: false },
    acknowledgeWarnings: false,
  },
  gpuAttached: true,
  driver: {
    vendor: "intel",
    hostVersion: drift ? "31.0.101.5000" : "30.0.101.1122",
    guestVersion: "30.0.101.1122",
    copiedAt: "2026-10-04T12:00:00Z",
    drift,
    rebootRequired: false,
  },
  warnings: [],
});

function job(kind: VmJob["kind"]): VmJob {
  return {
    id: "job-1",
    kind,
    vmId: vm.id,
    state: "succeeded",
    step: "Done",
    percentComplete: 100,
    createdAt: "2026-10-04T12:00:00Z",
    updatedAt: "2026-10-04T12:00:05Z",
    error: null,
  };
}

/** Answers the dialog's commands. An apply turns Performance mode on for the reads that follow. */
function respond(state: VmState, initial: VmPerformance, hostGpu: HostGpu = gpu) {
  let performance = initial;
  invoke.mockImplementation((command: string, args: { resource?: string }) => {
    switch (command) {
      case "get_vm_performance":
        return Promise.resolve(performance);
      case "get_vm_compute":
        return Promise.resolve({
          vmId: vm.id,
          state,
          processorCount: 2,
          startupMemoryMb: 2048,
          maximumMemoryMb: 8192,
          dynamicMemory: true,
          nestedVirtualization: false,
          macAddressSpoofing: false,
          networkAdapterCount: 1,
          requiresOff: [],
        });
      case "get_host_resource":
        if (args.resource === "gpu") return Promise.resolve(hostGpu);
        return Promise.resolve({
          logicalProcessorCount: 8,
          totalMemoryMb: 32768,
          availableMemoryMb: 20000,
          memoryReserveMb: 4096,
          virtualHardDiskFolder: "C:\\VMs",
          isoFolder: "C:\\ISOs",
        });
      case "apply_vm_performance":
        performance = on(false);
        return Promise.resolve(job("applyPerformance"));
      case "set_up_performance_guest":
        return Promise.resolve(job("performanceGuestSetup"));
      case "export_vm_disks":
        return Promise.resolve(job("exportDisks"));
      default:
        return Promise.reject(new Error(`unexpected ${command}`));
    }
  });
}

const applyButton = async () => (await screen.findByRole("button", { name: "Apply" })) as HTMLButtonElement;
const calls = (command: string) => invoke.mock.calls.filter(([name]) => name === command).map(([, args]) => args);

beforeEach(() => {
  invoke.mockReset();
});

describe("PerformanceDialog", () => {
  it("refuses to apply while the VM is running", async () => {
    respond("running", off);
    render(PerformanceDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/Shut Workstation down to change Performance mode/)).toBeTruthy();
    expect((await applyButton()).disabled).toBe(true);
  });

  it("applies to an off VM with the host's GPU, even shares, and hardware encoding off", async () => {
    respond("off", off);
    render(PerformanceDialog, { host, vm, onclose: vi.fn() });

    const hardware = (await screen.findByLabelText(/Hardware H.264 encoding/)) as HTMLInputElement;
    expect(hardware.checked).toBe(false);
    await fireEvent.click(await applyButton());

    await screen.findByText(/Performance mode is on/);
    expect(calls("apply_vm_performance")[0].settings).toEqual({
      processorCount: 2,
      memoryMb: 4096,
      gpu: { instancePath: intelPath, vramPercent: 50, encodePercent: 50, decodePercent: 50, computePercent: 50 },
      mmio: { lowGapMb: 1024, highGapMb: 32768 },
      moveStorageTo: null,
      rdp: { hardwareEncoding: false },
      acknowledgeWarnings: false,
    });
  });

  it("exports the disks before changing a VM that is already in Performance mode", async () => {
    respond("off", on(false));
    render(PerformanceDialog, { host, vm, onclose: vi.fn() });

    const exportFirst = (await screen.findByLabelText(/Export the disks/)) as HTMLInputElement;
    expect(exportFirst.checked).toBe(true);
    await fireEvent.click(await applyButton());

    await screen.findByText(/Performance mode is on/);
    const commands = invoke.mock.calls.map(([command]) => command).filter((command) => command === "export_vm_disks" || command === "apply_vm_performance");
    expect(commands).toEqual(["export_vm_disks", "apply_vm_performance"]);
    expect(calls("export_vm_disks")[0]).toEqual({ key: host.key, vmId: vm.id, destinationFolder: null });
  });

  it("does not export before the first apply", async () => {
    respond("off", off);
    render(PerformanceDialog, { host, vm, onclose: vi.fn() });

    expect(((await screen.findByLabelText(/Export the disks/)) as HTMLInputElement).checked).toBe(false);
  });

  it("explains that Performance mode is unavailable without a partitionable GPU", async () => {
    respond("off", off, { gpus: [{ ...gpu.gpus[0], partitionable: false, partitionCount: 0, instancePath: null }], warnings: [] });
    render(PerformanceDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/no GPU that Hyper-V can partition/)).toBeTruthy();
    expect((await applyButton()).disabled).toBe(true);
  });

  it("shows driver drift and re-syncs only the driver", async () => {
    respond("running", on(true));
    render(PerformanceDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/host's GPU driver changed/)).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "Re-sync drivers" }));

    await screen.findByText("Done.");
    expect(calls("set_up_performance_guest")).toEqual([{ key: host.key, vmId: vm.id, driversOnly: true }]);
  });

  it("offers no re-sync when the guest driver matches", async () => {
    respond("running", on(false));
    render(PerformanceDialog, { host, vm, onclose: vi.fn() });

    await screen.findByText(/Host driver 30.0.101.1122/);
    expect(screen.queryByRole("button", { name: "Re-sync drivers" })).toBeNull();
    expect((screen.getByRole("button", { name: "Set up the guest" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("asks for VM setup first when the host has no administrator credential", async () => {
    respond("running", on(false));
    const fallback = invoke.getMockImplementation()!;
    invoke.mockImplementation((command: string, args: unknown) =>
      command === "set_up_performance_guest"
        ? Promise.reject({ code: "api", message: "No credential.", status: 409, problemCode: "credentialRequired", issues: [] })
        : fallback(command, args),
    );
    render(PerformanceDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: "Set up the guest" }));

    expect(await screen.findByText(/no administrator account for Workstation yet/)).toBeTruthy();
  });
});
