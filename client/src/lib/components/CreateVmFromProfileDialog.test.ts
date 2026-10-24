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

describe("CreateVmFromProfileDialog as New VM (API 1.32.0)", () => {
  const consolidated = { ...host, apiVersion: "1.32.0" };
  const stored = {
    id: "gaming-rig",
    updatedAt: "",
    profile: { name: "Gaming rig", os: "windows", compute: { processors: 8, memory: { startupMb: 16384, dynamic: false }, diskSizeGb: 256 }, network: { switch: "LAN" } },
  };
  const templates = [
    { id: "windows-burner", name: "Windows Burner", description: "For short-lived VMs.", profile: { name: "Windows Burner", os: "windows", account: { adminAccountName: "hhadmin" } } },
  ];
  const fromTemplate = () => invoke.mock.calls.filter(([command]) => command === "create_vm_from_template").map(([, args]) => args);

  beforeEach(() => {
    invoke.mockImplementation(async (command: string, args: Record<string, unknown>) => {
      if (command === "get_host_resource") {
        if (args.resource === "machineProfiles") return [profile];
        if (args.resource === "machineProfileTemplates") return templates;
        if (args.resource === "switches") return [{ id: "s1", name: "Default Switch", isDefault: true }, { id: "s2", name: "LAN", isDefault: false }];
        return [{ name: "Win11.iso", sizeBytes: 1, modifiedAt: "2026-10-01T00:00:00Z" }];
      }
      if (command === "get_profile") return stored;
      if (command === "inspect_iso") return { os: "windows", distribution: null, editions: ["Windows 11 Pro"] };
      if (command === "create_vm_from_profile" || command === "create_vm_from_template") return job;
      throw new Error(`unexpected ${command}`);
    });
  });

  it("starts from the first profile with its hardware, and sends only what changed", async () => {
    render(CreateVmFromProfileDialog, { host: consolidated, onclose: vi.fn() });

    const processors = (await screen.findByRole("spinbutton", { name: "Virtual processors" })) as HTMLInputElement;
    expect(processors.value).toBe("8");
    expect((screen.getByRole("spinbutton", { name: "Disk size (GB)" }) as HTMLInputElement).value).toBe("256");
    expect((screen.getByRole("combobox", { name: "Network" }) as HTMLSelectElement).value).toBe("LAN");

    await fireEvent.input(screen.getByRole("textbox", { name: "Name" }), { target: { value: "Rig 2" } });
    await fireEvent.input(processors, { target: { value: "4" } });
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));

    await screen.findByText(/Rig 2 is created/);
    const [args] = created() as { profileId: string; request: Record<string, unknown> }[];
    expect(args.profileId).toBe("gaming-rig");
    expect(args.request).toMatchObject({ name: "Rig 2", compute: { processors: 4, diskSizeGb: 256, memory: { startupMb: 16384, dynamic: false } } });
    expect(args.request).not.toHaveProperty("switch");
  });

  it("creates from a template without changing its hardware", async () => {
    render(CreateVmFromProfileDialog, { host: consolidated, onclose: vi.fn() });
    await screen.findByRole("spinbutton", { name: "Virtual processors" });

    await fireEvent.change(screen.getByRole("combobox", { name: "Start from" }), { target: { value: "template:windows-burner" } });
    expect(await screen.findByText(/For short-lived VMs\./)).toBeTruthy();
    expect((screen.getByRole("spinbutton", { name: "Virtual processors" }) as HTMLInputElement).value).toBe("2");
    await fireEvent.input(screen.getByRole("textbox", { name: "Name" }), { target: { value: "Scratch" } });
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));

    await screen.findByText(/Scratch is created/);
    const [args] = fromTemplate() as { templateId: string; request: Record<string, unknown> }[];
    expect(args.templateId).toBe("windows-burner");
    expect(args.request).not.toHaveProperty("compute");
  });
});
