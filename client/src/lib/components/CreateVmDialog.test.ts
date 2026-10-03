import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import CreateVmDialog from "./CreateVmDialog.svelte";
import type { HostEntry } from "$lib/api/client";

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

const resources = {
  logicalProcessorCount: 8,
  totalMemoryMb: 32768,
  availableMemoryMb: 20000,
  memoryReserveMb: 4096,
  virtualHardDiskFolder: "C:\\VMs",
  isoFolder: "C:\\Users\\Public\\Documents\\HyperHarbor ISOs",
};

const job = {
  id: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d",
  kind: "createVm",
  vmId: "5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716",
  state: "succeeded",
  step: "Finishing",
  percentComplete: 100,
  createdAt: "2026-10-03T12:00:00Z",
  updatedAt: "2026-10-03T12:00:00Z",
  error: null,
};

function respond(isos: unknown[], createResults: Array<() => Promise<unknown>> = []) {
  invoke.mockImplementation((command: string, args: { resource?: string }) => {
    if (command === "get_host_resource") {
      if (args.resource === "resources") return Promise.resolve(resources);
      if (args.resource === "isos") return Promise.resolve(isos);
      return Promise.resolve([{ id: "C08CB7B8-9B3C-408E-8E30-5E16A3AEB444", name: "Default Switch", isDefault: true }]);
    }
    if (command === "create_vm") return (createResults.shift() ?? (() => Promise.resolve(job)))();
    return Promise.reject(new Error(`unexpected ${command}`));
  });
}

const createCalls = () => invoke.mock.calls.filter(([command]) => command === "create_vm").map(([, args]) => args.request);

beforeEach(() => {
  invoke.mockReset();
});

describe("CreateVmDialog", () => {
  it("points to the ISO folder when the library is empty", async () => {
    respond([]);
    render(CreateVmDialog, { host, onclose: vi.fn() });

    expect((await screen.findByText(/ISO library is empty/)).textContent).toContain("HyperHarbor ISOs");
    expect((screen.getByRole("button", { name: "Create" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("sends the defaults with the Default Switch and a TPM", async () => {
    respond([{ name: "Win11.iso", sizeBytes: 6_000_000_000, modifiedAt: "2026-10-01T00:00:00Z" }]);
    render(CreateVmDialog, { host, onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText("Name"), { target: { value: " Win11 Dev " } });
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));

    await screen.findByText("Done.");
    expect(createCalls()[0]).toEqual({
      name: "Win11 Dev",
      isoName: "Win11.iso",
      diskSizeGb: 64,
      processorCount: 2,
      startupMemoryMb: 4096,
      maximumMemoryMb: 8192,
      dynamicMemory: true,
      switchId: "C08CB7B8-9B3C-408E-8E30-5E16A3AEB444",
      enableTpm: true,
      acknowledgeWarnings: false,
    });
  });

  it("shows resource warnings and sends acknowledgeWarnings when the user continues", async () => {
    const warned = {
      code: "api",
      message: "Check these warnings.",
      status: 409,
      problemCode: "resourceWarnings",
      issues: [{ field: "startupMemoryMb", message: "Starting this VM would leave the host about 1 GB free." }],
    };
    respond([{ name: "Win11.iso", sizeBytes: 1, modifiedAt: "2026-10-01T00:00:00Z" }], [() => Promise.reject(warned)]);
    render(CreateVmDialog, { host, onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText("Name"), { target: { value: "Win11 Dev" } });
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));
    await screen.findByText(/leave the host about 1 GB free/);
    await fireEvent.click(screen.getByRole("button", { name: "Create anyway" }));

    await screen.findByText("Done.");
    expect(createCalls().map((request) => request.acknowledgeWarnings)).toEqual([false, true]);
  });

  it("shows field errors from the host next to the field", async () => {
    const invalid = {
      code: "api",
      message: "A virtual machine named \"Dev\" already exists.",
      status: 400,
      problemCode: null,
      issues: [{ field: "name", message: "A virtual machine named \"Dev\" already exists." }],
    };
    respond([{ name: "Win11.iso", sizeBytes: 1, modifiedAt: "2026-10-01T00:00:00Z" }], [() => Promise.reject(invalid)]);
    render(CreateVmDialog, { host, onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText("Name"), { target: { value: "Dev" } });
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));

    expect((await screen.findAllByText(/already exists/)).length).toBeGreaterThan(0);
    expect((screen.getByRole("button", { name: "Create" }) as HTMLButtonElement).disabled).toBe(false);
  });
});
