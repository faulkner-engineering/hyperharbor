import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/svelte";
import { provideHostApi, type HostApi } from "@hyperharbor/ui/api/hostApi.ts";
import type { ClientError, Vm } from "@hyperharbor/ui/api/model.ts";
import { elevation } from "@hyperharbor/ui/lifecycle.svelte.ts";
import type { TrayViewState } from "../bridge";
import { tray } from "../store.svelte";
import Vms from "./Vms.svelte";

const running: Vm = {
  id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
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

const off: Vm = { ...running, id: "6f1d2c3b-4a59-4e8d-9c7b-1a2b3c4d5e6f", name: "Old Box", state: "off" };

const view = { connected: true } as TrayViewState;

const elevationRequired: ClientError = {
  code: "api",
  message: "Enter the passphrase.",
  status: 403,
  problemCode: "elevationRequired",
  issues: [],
};

/** A HostApi whose calls are mocks; each test sets what it needs. */
function fakeApi(): { [K in keyof HostApi]: ReturnType<typeof vi.fn> } {
  return {
    listVms: vi.fn(async () => [running, off]),
    performVmAction: vi.fn(async () => ({})),
    getVmCompute: vi.fn(),
    updateVmCompute: vi.fn(),
    listVmDisks: vi.fn(),
    growVmDisk: vi.fn(),
    getDeletePreview: vi.fn(),
    deleteVm: vi.fn(),
    getHostResources: vi.fn(),
    getElevation: vi.fn(),
    elevate: vi.fn(async () => ({ expiresAt: "2026-11-11T12:05:00Z" })),
    getJob: vi.fn(),
  };
}

let api = fakeApi();

beforeEach(() => {
  api = fakeApi();
  provideHostApi(api as unknown as HostApi);
  elevation.finish(false);
  tray.reset();
});

/** Opens a VM's actions menu and returns its items. */
async function openMenu(name: string) {
  const summary = await screen.findByLabelText(`Actions for ${name}`);
  await fireEvent.click(summary);
  return within(summary.closest("details")!.querySelector(".menu-items") as HTMLElement);
}

describe("tray VM page", () => {
  it("lists this host's VMs with the actions the tray offers, and none it cannot do", async () => {
    render(Vms, { view });

    expect(await screen.findByText("Dev Box")).toBeTruthy();
    // No Remote Desktop, console, or setup from the tray, so no button leads to one.
    expect(screen.queryByRole("button", { name: "Connect" })).toBeNull();
    expect(screen.getByRole("button", { name: "Start" })).toBeTruthy();

    const menu = await openMenu("Dev Box");
    for (const item of ["Shut down", "Restart", "Save state", "Force shut off…", "Settings…", "Disk size…", "Delete…"]) {
      expect(menu.getByRole("button", { name: item })).toBeTruthy();
    }
    for (const item of ["Console", "Monitors…", "Performance mode…", "Administrator access…"]) {
      expect(menu.queryByRole("button", { name: item })).toBeNull();
    }
  });

  it("says why the list could not be read, and reads it again on request", async () => {
    api.listVms.mockRejectedValueOnce({ code: "unreachable", message: "The HyperHarbor service is not running.", status: null, problemCode: null, issues: [] });
    render(Vms, { view });

    expect(await screen.findByText(/The HyperHarbor service is not running\./)).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(await screen.findByText("Dev Box")).toBeTruthy();
    expect(api.listVms).toHaveBeenCalledTimes(2);
  });

  it("asks for the admin passphrase when the host requires it, then repeats the action", async () => {
    api.performVmAction.mockRejectedValueOnce(elevationRequired);
    render(Vms, { view });

    await fireEvent.click(await screen.findByRole("button", { name: "Start" }));
    const input = await screen.findByLabelText("Passphrase");
    await fireEvent.input(input, { target: { value: "correct horse" } });
    await fireEvent.click(screen.getByRole("button", { name: "Continue" }));

    await waitFor(() => expect(api.performVmAction).toHaveBeenCalledTimes(2));
    expect(api.elevate).toHaveBeenCalledWith("local", "correct horse");
    expect(api.performVmAction).toHaveBeenLastCalledWith("local", off.id, "start");
    expect(tray.toasts.at(-1)?.title).toBe("Starting Old Box");
  });

  it("confirms before forcing a VM off", async () => {
    render(Vms, { view });

    const menu = await openMenu("Dev Box");
    await fireEvent.click(menu.getByRole("button", { name: "Force shut off…" }));
    expect(api.performVmAction).not.toHaveBeenCalled();

    await fireEvent.click(screen.getByRole("button", { name: "Force shut off" }));
    await waitFor(() => expect(api.performVmAction).toHaveBeenCalledWith("local", running.id, "turnOff"));
  });

  it("reports a failed action without leaving the page", async () => {
    api.performVmAction.mockRejectedValueOnce({ ...elevationRequired, problemCode: null, status: 409, message: "The VM is busy." });
    render(Vms, { view });

    await fireEvent.click(await screen.findByRole("button", { name: "Start" }));

    await waitFor(() => expect(tray.toasts.at(-1)).toMatchObject({ kind: "error", text: "The VM is busy." }));
    expect(screen.getByText("Dev Box")).toBeTruthy();
  });
});
