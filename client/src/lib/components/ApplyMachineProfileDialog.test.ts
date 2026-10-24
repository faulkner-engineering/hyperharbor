import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));
vi.mock("$lib/lifecycle.svelte", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$lib/lifecycle.svelte")>()),
  withElevation: <T,>(_key: string, run: () => Promise<T>) => run(),
}));

import ApplyMachineProfileDialog from "./ApplyMachineProfileDialog.svelte";
import type { HostEntry, Vm } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.29.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm = { id: "807223df-0aeb-40a8-9d1c-223878114632", name: "Dev Box", state: "running", guestOs: { family: "windows", name: "Windows 11 Pro" } } as Vm;

const preview = {
  vmId: vm.id,
  vmName: "Dev Box",
  vmState: "running",
  profileId: "gaming-rig",
  profileName: "Gaming rig",
  changes: [
    { part: "compute", setting: "Virtual processors", current: "2", wanted: "4", needsOff: true, needsGuest: false },
    { part: "software", setting: "Software and display", current: null, wanted: "1 package, 0 removals, 1 setting", needsOff: false, needsGuest: true },
  ],
  notes: [{ section: "account", message: "Account settings are used when the OS is installed, so they apply when creating a VM." }],
  warnings: [],
};

const job = {
  id: "j1",
  kind: "applyMachineProfile",
  vmId: vm.id,
  state: "succeeded",
  step: "Applying",
  percentComplete: 100,
  createdAt: "",
  updatedAt: "",
  error: null,
  setupResult: { applied: 3, problems: ["Git could not be installed: no network."], restarted: false, finishedAt: "" },
};

const applied = () => invoke.mock.calls.filter(([command]) => command === "apply_vm_machine_profile").map(([, args]) => args);

beforeEach(() => {
  invoke.mockReset();
  invoke.mockImplementation(async (command: string) => {
    if (command === "get_host_resource") return [{ id: "gaming-rig", name: "Gaming rig", error: null }];
    if (command === "preview_vm_machine_profile") return preview;
    if (command === "apply_vm_machine_profile") return job;
    throw new Error(`unexpected ${command}`);
  });
});

describe("ApplyMachineProfileDialog", () => {
  it("shows what changes, and needs the shutdown choice for parts that need the VM off", async () => {
    render(ApplyMachineProfileDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText("Virtual processors")).toBeTruthy();
    expect(screen.getByText(/apply when creating a VM/)).toBeTruthy();
    const apply = screen.getByRole("button", { name: "Apply" }) as HTMLButtonElement;
    expect(apply.disabled).toBe(true);

    await fireEvent.click(screen.getByRole("checkbox", { name: /Shut Dev Box down/ }));
    expect(apply.disabled).toBe(false);

    // Without compute, nothing needs the VM off.
    await fireEvent.click(screen.getByRole("checkbox", { name: "Compute" }));
    expect(screen.queryByRole("checkbox", { name: /Shut Dev Box down/ })).toBeNull();
  });

  it("applies the chosen parts and shows what could not be applied", async () => {
    render(ApplyMachineProfileDialog, { host, vm, onclose: vi.fn() });
    await screen.findByText("Virtual processors");

    await fireEvent.click(screen.getByRole("checkbox", { name: /Shut Dev Box down/ }));
    await fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(await screen.findByText(/Applied 3 items/)).toBeTruthy();
    expect(screen.getByText(/Git could not be installed/)).toBeTruthy();
    expect(applied()).toEqual([
      { key: host.key, vmId: vm.id, request: { profileId: "gaming-rig", parts: ["compute", "software"], shutDownToApply: true } },
    ]);
  });
});
