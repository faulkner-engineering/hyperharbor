import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import ApplySetupProfileDialog from "./ApplySetupProfileDialog.svelte";
import type { HostEntry, Vm } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.14.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm: Vm = {
  id: "5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716",
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

const summary = (id: string, name: string, error: string | null = null) => ({
  id,
  name,
  description: null,
  updatedAt: "2026-10-05T12:00:00Z",
  installCount: 2,
  removeCount: 1,
  tweakCount: 1,
  extensionCount: 0,
  browser: null,
  error,
});

const job = (overrides: Record<string, unknown> = {}) => ({
  id: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d",
  kind: "applySetupProfile",
  vmId: vm.id,
  state: "succeeded",
  step: "Applying Workstation: writing settings",
  percentComplete: 100,
  createdAt: "2026-10-05T12:00:00Z",
  updatedAt: "2026-10-05T12:01:00Z",
  error: null,
  ...overrides,
});

function respond(finished: unknown) {
  invoke.mockImplementation((command: string, args: { resource?: string }) => {
    if (command === "get_host_resource" && args.resource === "setupProfiles")
      return Promise.resolve([summary("broken", "Broken", "Line 3: not YAML"), summary("workstation", "Workstation")]);
    if (command === "apply_vm_setup_profile") return Promise.resolve(finished);
    return Promise.reject(new Error(`unexpected ${command}`));
  });
}

const applyCalls = () => invoke.mock.calls.filter(([command]) => command === "apply_vm_setup_profile").map(([, args]) => args);

beforeEach(() => {
  invoke.mockReset();
});

describe("ApplySetupProfileDialog", () => {
  it("applies the chosen profile and shows what could not be applied", async () => {
    respond(
      job({
        setupResult: {
          applied: 3,
          problems: ["7-Zip: winget install failed (0x8A150011)."],
          restarted: true,
          restartPending: false,
          finishedAt: "2026-10-05T12:01:00Z",
        },
      }),
    );
    render(ApplySetupProfileDialog, { host, vm, onclose: vi.fn() });

    const select = (await screen.findByLabelText("Setup profile")) as HTMLSelectElement;
    expect([...select.options].map((option) => option.textContent)).toEqual(["Workstation"]);
    await fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(await screen.findByText(/3 items applied, and Dev Box was restarted/)).toBeTruthy();
    expect(screen.getByText("7-Zip: winget install failed (0x8A150011).")).toBeTruthy();
    expect(applyCalls()).toEqual([{ key: host.key, vmId: vm.id, request: { profileId: "workstation", restartIfNeeded: true } }]);
  });

  it("can leave the restart to the user", async () => {
    respond(
      job({ setupResult: { applied: 4, problems: [], restarted: false, restartPending: true, finishedAt: "2026-10-05T12:01:00Z" } }),
    );
    render(ApplySetupProfileDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByLabelText(/Restart Dev Box if a change needs it/));
    await fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(await screen.findByText("Restart Dev Box to finish.")).toBeTruthy();
    expect(applyCalls()[0].request.restartIfNeeded).toBe(false);
  });

  it("shows why the job failed", async () => {
    respond(job({ state: "failed", error: { title: "Applying Workstation failed", detail: "PowerShell Direct did not respond in time." } }));
    render(ApplySetupProfileDialog, { host, vm, onclose: vi.fn() });

    await screen.findByLabelText("Setup profile");
    await fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(await screen.findByText(/did not respond in time/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Close" })).toBeTruthy();
  });
});
