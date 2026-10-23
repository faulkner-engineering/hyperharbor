import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));
vi.mock("$lib/lifecycle.svelte", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$lib/lifecycle.svelte")>()),
  withElevation: <T,>(_key: string, run: () => Promise<T>) => run(),
}));

import CreateVmFromProfileDialog from "./CreateVmFromProfileDialog.svelte";
import type { HostEntry, MachineProfileSummary } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.28.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const profile: MachineProfileSummary = {
  id: "gaming-rig",
  name: "Gaming rig",
  description: null,
  os: "windows",
  processors: 8,
  memoryMb: 16384,
  gpu: true,
  tpm: true,
  backup: true,
  softwareCount: 3,
  updatedAt: "2026-10-23T12:00:00Z",
  error: null,
};

const job = { id: "j1", kind: "createVm", vmId: "vm-1", state: "succeeded", step: "Finishing", percentComplete: 100, startedAt: "", updatedAt: "", error: null };
const created = () => invoke.mock.calls.filter(([command]) => command === "create_vm_from_profile").map(([, args]) => args);

function answer(create: () => unknown) {
  invoke.mockImplementation(async (command: string) => {
    if (command === "get_host_resource") return [{ name: "Win11.iso", sizeBytes: 1, modifiedAt: "2026-10-01T00:00:00Z" }];
    if (command === "inspect_iso") return { os: "windows", distribution: null, editions: ["Windows 11 Home", "Windows 11 Pro"] };
    if (command === "create_vm_from_profile") return create();
    throw new Error(`unexpected ${command}`);
  });
}

beforeEach(() => {
  invoke.mockReset();
});

describe("CreateVmFromProfileDialog", () => {
  it("creates the VM from the profile with the chosen name, image, and edition", async () => {
    answer(() => job);
    const onclose = vi.fn();
    render(CreateVmFromProfileDialog, { host, profile, onclose });

    expect(screen.getByText(/Windows · 8 processors · 16 GB · GPU · TPM · backups · 3 software items/)).toBeTruthy();
    await fireEvent.input(screen.getByRole("textbox", { name: "Name" }), { target: { value: "Rig" } });
    await fireEvent.change(await screen.findByRole("combobox", { name: "Edition" }), { target: { value: "Windows 11 Home" } });
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));

    expect(await screen.findByText(/Rig is created/)).toBeTruthy();
    expect(created()).toEqual([
      { key: host.key, profileId: "gaming-rig", request: { name: "Rig", isoName: "Win11.iso", windowsEdition: "Windows 11 Home", acknowledgeWarnings: false } },
    ]);
    await fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(onclose).toHaveBeenCalledWith(true);
  });

  it("shows the host's warnings and creates anyway when asked", async () => {
    const warned = {
      code: "api",
      message: "Check these warnings.",
      status: 409,
      problemCode: "resourceWarnings",
      issues: [{ field: "backup.target", message: "This host has no backup target named \"NAS\", so the VM is made without backups." }],
    };
    let calls = 0;
    answer(() => (++calls === 1 ? Promise.reject(warned) : job));
    render(CreateVmFromProfileDialog, { host, profile, onclose: vi.fn() });

    await fireEvent.input(screen.getByRole("textbox", { name: "Name" }), { target: { value: "Rig" } });
    await screen.findByRole("combobox", { name: "Edition" });
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));
    await screen.findByText(/no backup target named "NAS"/);
    await fireEvent.click(screen.getByRole("button", { name: "Create anyway" }));

    await screen.findByText(/Rig is created/);
    expect((created()[1] as { request: { acknowledgeWarnings: boolean } }).request.acknowledgeWarnings).toBe(true);
  });

  it("shows errors by the profile's field paths", async () => {
    answer(() =>
      Promise.reject({
        code: "api",
        message: "This host has no virtual switch named \"LAN\".",
        status: 400,
        issues: [{ field: "network.switch", message: "This host has no virtual switch named \"LAN\". Add one, or change the profile." }],
      }),
    );
    render(CreateVmFromProfileDialog, { host, profile, onclose: vi.fn() });

    await fireEvent.input(screen.getByRole("textbox", { name: "Name" }), { target: { value: "Rig" } });
    await screen.findByRole("combobox", { name: "Edition" });
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));

    expect(await screen.findByText("network.switch")).toBeTruthy();
  });
});
