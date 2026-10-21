import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import TransferVmDialog from "./TransferVmDialog.svelte";
import type { HostEntry, TransferPreview, Vm } from "$lib/api/client";

function entry(key: string, displayName: string, apiVersion: string | null, paired = true): HostEntry {
  return {
    key,
    displayName,
    hostId: key,
    hostName: null,
    addresses: ["192.168.0.10"],
    port: 48443,
    apiVersion,
    source: "discovered",
    isLocal: false,
    paired,
    canWake: false,
  };
}

const desktop = entry("mdns:desktop", "DESKTOP", "1.24.0");
const laptop = entry("mdns:laptop", "LAPTOP", "1.24.0");
const old = entry("mdns:old", "OLD-PC", "1.23.0");
const unknown = entry("manual:10.0.0.9", "10.0.0.9", null);
const stranger = entry("mdns:stranger", "STRANGER", "1.24.0", false);

const vm: Vm = {
  id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
  name: "Web",
  state: "off",
  generation: 2,
  rdpAvailable: false,
  ipAddresses: [],
  provisioned: true,
  remoteDesktop: { address: null, reachableFromHost: false },
  guestOs: { family: "windows", name: null },
  performanceMode: false,
};

const copyId = "9c8b7a65-4321-4fed-8cba-0987654321ab";
const transferId = "5a0c1d2e-3f40-4152-8364-758697a8b9ca";

function preview(overrides: Partial<TransferPreview> = {}): TransferPreview {
  return {
    vmId: vm.id,
    name: "Web",
    state: "off",
    generation: 2,
    configurationVersion: "12.0",
    compute: { processorCount: 2, startupMemoryMb: 4096, maximumMemoryMb: 4096, dynamicMemory: false, nestedVirtualization: false, macAddressSpoofing: false },
    secureBoot: true,
    secureBootTemplateId: null,
    tpmEnabled: true,
    hasGpuPartition: false,
    adapters: [],
    disks: [{ path: "D:\\VMs\\Web.vhdx", sizeBytes: 21474836480, controllerType: "scsi", controllerNumber: 0, lun: 0 }],
    totalBytes: 21474836480,
    checkpointCount: 0,
    blockers: [],
    warnings: [],
    ...overrides,
  };
}

const offer = { transferId, name: "Web (copy)", vmFolder: "E:\\VMs\\Web (copy)", vmId: null, warnings: [] };

const job = {
  id: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d",
  kind: "transferVm",
  vmId: vm.id,
  state: "succeeded",
  step: "Completing the transfer",
  percentComplete: 100,
  createdAt: "2026-10-21T12:00:00Z",
  updatedAt: "2026-10-21T12:00:00Z",
  error: null,
  transferResult: { targetVmId: copyId, targetName: "Web (copy)", sourceRemoved: false, warnings: [] },
};

function respond(previewValue: TransferPreview, offerResults: Array<() => Promise<unknown>> = [() => Promise.resolve(offer)]) {
  invoke.mockImplementation((command: string) => {
    switch (command) {
      case "get_transfer_preview":
        return Promise.resolve(previewValue);
      case "offer_transfer":
        return (offerResults.shift() ?? (() => Promise.resolve(offer)))();
      case "start_transfer":
        return Promise.resolve(job);
      case "carry_vm_settings":
      case "forget_transfer_offer":
        return Promise.resolve();
      default:
        return Promise.reject(new Error(`unexpected ${command}`));
    }
  });
}

function show() {
  render(TransferVmDialog, { host: desktop, vm, hosts: [desktop, laptop, old, unknown, stranger], onclose: vi.fn() });
}

const calls = (command: string) => invoke.mock.calls.filter(([name]) => name === command).map(([, args]) => args);

async function chooseCopy() {
  await fireEvent.click(await screen.findByLabelText(/^Copy/));
}

beforeEach(() => {
  invoke.mockReset();
});

describe("TransferVmDialog", () => {
  it("offers only paired hosts, and only those known to have transfers", async () => {
    respond(preview());
    show();

    const select = (await screen.findByLabelText("To")) as HTMLSelectElement;
    const options = [...select.options].map((option) => [option.textContent?.trim(), option.disabled]);
    expect(options).toEqual([
      ["LAPTOP", false],
      ["OLD-PC (update it first)", true],
      ["10.0.0.9 (connect to it first)", true],
    ]);
    expect(select.value).toBe("mdns:laptop");
  });

  it("shows blockers and does not let the transfer start", async () => {
    respond(preview({ blockers: [{ code: "notOff", message: "Web is running. Shut it down first." }] }));
    show();
    await chooseCopy();

    expect(await screen.findByText("Web is running. Shut it down first.")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Copy" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("a move needs the VM's name typed", async () => {
    respond(preview());
    show();

    const move = (await screen.findByRole("button", { name: "Move" })) as HTMLButtonElement;
    expect(move.disabled).toBe(true);
    await fireEvent.input(screen.getByLabelText(/to confirm the move/), { target: { value: "Web" } });
    expect(move.disabled).toBe(false);
  });

  it("copies: asks the other host, starts on this one, and carries this device's settings to the copy", async () => {
    respond(preview());
    show();
    await chooseCopy();

    await fireEvent.click(screen.getByRole("button", { name: "Copy" }));

    expect(await screen.findByText(/Copied to LAPTOP as Web \(copy\)/)).toBeTruthy();
    expect(calls("offer_transfer")).toEqual([
      { sourceKey: "mdns:desktop", targetKey: "mdns:laptop", preview: preview(), mode: "copy", acknowledgeWarnings: false },
    ]);
    expect(calls("start_transfer")).toEqual([{ vmId: vm.id, transferId, confirmName: null }]);
    await waitFor(() =>
      expect(calls("carry_vm_settings")).toEqual([
        { sourceKey: "mdns:desktop", targetKey: "mdns:laptop", sourceVmId: vm.id, targetVmId: copyId, moved: false },
      ]),
    );
  });

  it("shows the other host's warnings and goes on only when the user does", async () => {
    const warned = {
      code: "api",
      message: "Check these warnings.",
      status: 409,
      problemCode: "resourceWarnings",
      issues: [{ field: "name", message: "A VM named \"Web\" already exists on this host, so this one is named \"Web (copy)\"." }],
    };
    respond(preview(), [() => Promise.reject(warned), () => Promise.resolve(offer)]);
    show();
    await chooseCopy();

    await fireEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(await screen.findByText(/so this one is named/)).toBeTruthy();
    expect(calls("start_transfer")).toEqual([]);

    await fireEvent.click(screen.getByRole("button", { name: "Continue anyway" }));
    await screen.findByText(/Copied to LAPTOP/);
    expect(calls("offer_transfer").map((args) => (args as { acknowledgeWarnings: boolean }).acknowledgeWarnings)).toEqual([false, true]);
  });
});
