import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import DiskDialog from "./DiskDialog.svelte";
import type { HostEntry, Vm, VmDisk, VmJob } from "$lib/api/client";

const GB = 1024 ** 3;
const path = "D:\\VMs\\Dev\\Virtual Hard Disks\\Dev.vhdx";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.36.0",
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
  provisioned: true,
  remoteDesktop: { address: null, reachableFromHost: false },
  guestOs: { family: "windows", name: "Windows 11 Pro" },
  performanceMode: false,
};

const disk = (overrides: Partial<VmDisk> = {}): VmDisk => ({
  path,
  maximumSizeBytes: 64 * GB,
  fileSizeBytes: 20 * GB,
  growWhileRunning: true,
  growBlocker: null,
  ...overrides,
});

const job = (overrides: Partial<VmJob> = {}): VmJob => ({
  id: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d",
  kind: "growDisk",
  vmId: vm.id,
  state: "succeeded",
  step: "Done",
  percentComplete: 100,
  createdAt: "2026-10-26T12:00:00Z",
  updatedAt: "2026-10-26T12:00:05Z",
  error: null,
  diskGrowResult: { path, maximumSizeBytes: 128 * GB, partitionExtended: true, note: null },
  ...overrides,
});

function respond(disks: VmDisk[], grown: VmJob = job()) {
  invoke.mockImplementation((command: string) => {
    switch (command) {
      case "list_vm_disks":
        return Promise.resolve({ vmId: vm.id, disks });
      case "grow_vm_disk":
        return Promise.resolve(grown);
      default:
        return Promise.reject(new Error(`unexpected ${command}`));
    }
  });
}

const growButton = () => screen.getByRole("button", { name: "Grow disk" }) as HTMLButtonElement;
const requests = () => invoke.mock.calls.filter(([command]) => command === "grow_vm_disk").map(([, args]) => args.request);

beforeEach(() => {
  invoke.mockReset();
});

describe("DiskDialog", () => {
  it("grows a running VM's disk in place and says the partition uses the new space", async () => {
    respond([disk()]);
    render(DiskDialog, { host, vm, onclose: vi.fn() });

    const size = (await screen.findByLabelText("New maximum size (GB)")) as HTMLInputElement;
    expect(size.value).toBe("128");
    await fireEvent.click(growButton());

    await screen.findByText(/The disk is now 128 GB, and the partition uses the new space/);
    expect(requests()).toEqual([{ path, sizeGb: 128, extendPartition: true, shutDownToApply: false, acknowledgeWarnings: false }]);
  });

  it("needs the shut-down option before a disk that cannot grow while running", async () => {
    respond([disk({ growWhileRunning: false })]);
    render(DiskDialog, { host, vm, onclose: vi.fn() });

    await screen.findByText(/can grow only while Dev Box is off/);
    expect(growButton().disabled).toBe(true);

    await fireEvent.click(screen.getByRole("checkbox", { name: /Shut Dev Box down, grow the disk/ }));
    await fireEvent.click(growButton());

    await screen.findByText(/The disk is now 128 GB/);
    expect(requests()[0].shutDownToApply).toBe(true);
  });

  it("explains a disk that cannot grow, and offers nothing to grow", async () => {
    respond([disk({ growBlocker: "checkpoints" })]);
    render(DiskDialog, { host, vm, onclose: vi.fn() });

    await screen.findByText(/Merge its checkpoints first/);
    expect(screen.queryByLabelText("New maximum size (GB)")).toBeNull();
  });

  it("shows the host's note when the partition was not extended", async () => {
    const note = "In the VM, extend the last partition and its file system.";
    respond([disk()], job({ diskGrowResult: { path, maximumSizeBytes: 128 * GB, partitionExtended: false, note } }));
    render(DiskDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: "Grow disk" }));

    await screen.findByText(note);
    expect(screen.getByText(/The disk is now 128 GB\./)).toBeTruthy();
  });

  it("can be closed while the host never answers", async () => {
    invoke.mockImplementation(() => new Promise(() => {}));
    const onclose = vi.fn();
    render(DiskDialog, { host, vm, onclose });

    await screen.findByText("Reading the VM's disks…");
    await fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(onclose).toHaveBeenCalledWith(false);
  });
});
