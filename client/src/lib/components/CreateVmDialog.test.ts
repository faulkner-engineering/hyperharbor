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
  it("offers the ISO library when it is empty", async () => {
    respond([]);
    const onopenlibrary = vi.fn();
    render(CreateVmDialog, { host, onclose: vi.fn(), onopenlibrary });

    await screen.findByText(/ISO library is empty/);
    expect((screen.getByRole("button", { name: "Create" }) as HTMLButtonElement).disabled).toBe(true);

    await fireEvent.click(screen.getByRole("button", { name: "Open ISO library" }));
    expect(onopenlibrary).toHaveBeenCalled();
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
      install: null,
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

const profiles = [
  {
    id: "windows-workstation",
    name: "Windows Workstation",
    builtIn: true,
    os: "windows",
    adminAccountName: "hhadmin",
    timeZone: "Central Standard Time",
    locale: "en-US",
    windows: { defaultEdition: "Windows 11 Pro" },
    linux: null,
  },
  {
    id: "ubuntu-dev-server",
    name: "Ubuntu Dev Server",
    builtIn: true,
    os: "linux",
    adminAccountName: "hhadmin",
    timeZone: "America/Chicago",
    locale: "en-US",
    windows: null,
    linux: { installDesktop: true },
  },
];

/** A host with profiles that inspects images as `inspection` describes. */
function respondUnattended(inspection: unknown) {
  invoke.mockImplementation((command: string, args: { resource?: string }) => {
    if (command === "get_host_resource") {
      if (args.resource === "resources") return Promise.resolve(resources);
      if (args.resource === "isos")
        return Promise.resolve([{ name: "Win11.iso", sizeBytes: 6_000_000_000, modifiedAt: "2026-10-01T00:00:00Z" }]);
      if (args.resource === "unattendProfiles") return Promise.resolve(profiles);
      if (args.resource === "setupProfiles") return Promise.resolve(setupProfiles);
      return Promise.resolve([{ id: "C08CB7B8-9B3C-408E-8E30-5E16A3AEB444", name: "Default Switch", isDefault: true }]);
    }
    if (command === "inspect_iso") return Promise.resolve(inspection);
    if (command === "create_vm") return Promise.resolve(job);
    if (command === "open_console") return Promise.resolve();
    return Promise.reject(new Error(`unexpected ${command}`));
  });
}

const setupProfiles = [
  { id: "workstation", name: "Workstation", description: null, updatedAt: "2026-10-05T12:00:00Z", installCount: 2, removeCount: 1, tweakCount: 1, extensionCount: 0, browser: null, error: null },
  { id: "broken", name: "broken", description: null, updatedAt: "2026-10-05T12:00:00Z", installCount: 0, removeCount: 0, tweakCount: 0, extensionCount: 0, browser: null, error: "Line 3: not YAML" },
];

const windowsMedia = { os: "windows", distribution: "Windows", editions: ["Windows 11 Home", "Windows 11 Pro"] };

describe("CreateVmDialog unattended install", () => {
  it("offers only profiles for what the image installs and sends the install", async () => {
    respondUnattended(windowsMedia);
    render(CreateVmDialog, { host, onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText("Name"), { target: { value: "Dev Box" } });
    await fireEvent.click(screen.getByLabelText("Automatically with a profile"));

    const profile = (await screen.findByLabelText("Profile")) as HTMLSelectElement;
    expect([...profile.options].map((option) => option.value)).toEqual(["windows-workstation"]);
    expect((screen.getByLabelText("Edition") as HTMLSelectElement).value).toBe("Windows 11 Pro");
    expect((screen.getByLabelText("Computer name") as HTMLInputElement).placeholder).toBe("DEV-BOX");

    await fireEvent.change(screen.getByLabelText("Edition"), { target: { value: "Windows 11 Home" } });
    await fireEvent.input(screen.getByLabelText("Computer name"), { target: { value: " DEVBOX1 " } });
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));

    await screen.findByText("Done.");
    expect(createCalls()[0].install).toEqual({
      profileId: "windows-workstation",
      windowsEdition: "Windows 11 Home",
      computerName: "DEVBOX1",
    });
  });

  it("opens the new VM's console once it is created", async () => {
    respondUnattended(windowsMedia);
    render(CreateVmDialog, { host, onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText("Name"), { target: { value: "Dev Box" } });
    await fireEvent.click(screen.getByLabelText("Automatically with a profile"));
    await screen.findByLabelText("Profile");
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));
    await fireEvent.click(await screen.findByRole("button", { name: "Console" }));

    expect(invoke).toHaveBeenCalledWith("open_console", { key: host.key, vmId: job.vmId });
  });

  it("explains when the image cannot be installed automatically and blocks Create", async () => {
    respondUnattended({ os: null, distribution: null, editions: [] });
    render(CreateVmDialog, { host, onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText("Name"), { target: { value: "Dev Box" } });
    await fireEvent.click(screen.getByLabelText("Automatically with a profile"));

    expect(await screen.findByText(/cannot tell what Win11\.iso installs/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Create" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("sends the chosen setup profile to a host that applies them", async () => {
    respondUnattended(windowsMedia);
    render(CreateVmDialog, { host: { ...host, apiVersion: "1.13.0" }, onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText("Name"), { target: { value: "Dev Box" } });
    await fireEvent.click(screen.getByLabelText("Automatically with a profile"));
    const select = (await screen.findByLabelText("Setup profile")) as HTMLSelectElement;
    // Profiles whose file does not read on the host are not offered.
    expect([...select.options].map((option) => option.textContent)).toEqual(["None", "Workstation"]);
    expect(select.value).toBe("");

    await fireEvent.change(select, { target: { value: "workstation" } });
    expect(screen.getByText(/restarts the VM if needed/)).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));

    await screen.findByText("Done.");
    expect(createCalls()[0].install).toEqual({
      profileId: "windows-workstation",
      windowsEdition: "Windows 11 Pro",
      computerName: null,
      setupProfileId: "workstation",
    });
  });

  it("leaves the setup profile out for None", async () => {
    respondUnattended(windowsMedia);
    render(CreateVmDialog, { host: { ...host, apiVersion: "1.13.0" }, onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText("Name"), { target: { value: "Dev Box" } });
    await fireEvent.click(screen.getByLabelText("Automatically with a profile"));
    await screen.findByLabelText("Setup profile");
    await fireEvent.click(screen.getByRole("button", { name: "Create" }));
    await screen.findByText("Done.");
    expect(createCalls()[0].install).not.toHaveProperty("setupProfileId");

    expect(invoke.mock.calls.filter(([, args]) => args?.resource === "setupProfiles")).toHaveLength(1);
  });

  it("does not offer setup profiles on a host that cannot apply them", async () => {
    respondUnattended(windowsMedia);
    render(CreateVmDialog, { host: { ...host, apiVersion: "1.12.0" }, onclose: vi.fn() });

    await fireEvent.click(await screen.findByLabelText("Automatically with a profile"));
    await screen.findByLabelText("Edition");

    expect(screen.queryByLabelText("Setup profile")).toBeNull();
    expect(invoke.mock.calls.some(([, args]) => args?.resource === "setupProfiles")).toBe(false);
  });

  it("asks Ubuntu users to confirm in the console", async () => {
    respondUnattended({ os: "linux", distribution: "Ubuntu-Server 24.04.1 LTS", editions: [] });
    render(CreateVmDialog, { host, onclose: vi.fn() });

    await fireEvent.click(await screen.findByLabelText("Automatically with a profile"));

    expect(await screen.findByText(/type yes/)).toBeTruthy();
    expect(screen.queryByLabelText("Edition")).toBeNull();
  });
});
