import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import ProvisionDialog from "./ProvisionDialog.svelte";
import type { HostEntry, Vm } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.1.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

function vm(family: "windows" | "linux", provisioned: boolean): Vm {
  return {
    id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
    name: "Dev VM",
    state: "running",
    generation: 2,
    rdpAvailable: true,
    ipAddresses: ["192.168.0.50"],
    provisioned,
    remoteDesktop: { address: "192.168.0.50", reachableFromHost: true },
    guestOs: { family, name: family === "linux" ? "Ubuntu" : "Windows 11" },
    performanceMode: false,
  };
}

const trustLabel = /Trust a new SSH host key/;

async function fillAndSubmit(user: string, password: string) {
  await fireEvent.input(screen.getByLabelText(/user name/i), { target: { value: user } });
  await fireEvent.input(screen.getByLabelText("Password"), { target: { value: password } });
  await fireEvent.click(screen.getByRole("button", { name: "Set up" }));
}

beforeEach(() => {
  invoke.mockReset();
});

describe("ProvisionDialog", () => {
  it("offers to trust a new host key only when a Linux VM is set up again", () => {
    render(ProvisionDialog, { host, vm: vm("linux", true), onclose: vi.fn() });
    expect(screen.queryByLabelText(trustLabel)).not.toBeNull();
  });

  it.each([
    ["linux", false],
    ["windows", true],
    ["windows", false],
  ] as const)("hides the host key option for %s (provisioned: %s)", (family, provisioned) => {
    render(ProvisionDialog, { host, vm: vm(family, provisioned), onclose: vi.fn() });
    expect(screen.queryByLabelText(trustLabel)).toBeNull();
  });

  it("sends trustNewHostKey only when ticked", async () => {
    invoke.mockResolvedValue({ vmId: "x", accountName: "hh-owner", provisionedAt: "2026-10-03T00:00:00Z" });
    const onclose = vi.fn();
    render(ProvisionDialog, { host, vm: vm("linux", true), onclose });

    await fireEvent.click(screen.getByLabelText(trustLabel));
    await fillAndSubmit("hhadmin", "Adm1n!");

    await vi.waitFor(() => expect(onclose).toHaveBeenCalledWith(true));
    expect(invoke).toHaveBeenCalledWith(
      "provision_vm",
      expect.objectContaining({ options: expect.objectContaining({ trustNewHostKey: true }) }),
    );
  });

  it("never sends trustNewHostKey for a first setup", async () => {
    invoke.mockResolvedValue({});
    render(ProvisionDialog, { host, vm: vm("linux", false), onclose: vi.fn() });

    await fillAndSubmit("hhadmin", "Adm1n!");

    await vi.waitFor(() => expect(invoke).toHaveBeenCalled());
    expect(invoke.mock.calls[0][1].options.trustNewHostKey).toBe(false);
  });

  it("shows the host's error and keeps the dialog open", async () => {
    invoke.mockRejectedValue({ code: "api", status: 409, message: "The VM's SSH host key changed." });
    const onclose = vi.fn();
    render(ProvisionDialog, { host, vm: vm("linux", true), onclose });

    await fillAndSubmit("hhadmin", "Adm1n!");

    expect(await screen.findByRole("alert")).toHaveProperty("textContent", "The VM's SSH host key changed.");
    expect(onclose).not.toHaveBeenCalled();
  });

  it("clears the password when cancelled", async () => {
    const onclose = vi.fn();
    render(ProvisionDialog, { host, vm: vm("windows", false), onclose });
    await fireEvent.input(screen.getByLabelText("Password"), { target: { value: "Adm1n!" } });

    await fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(onclose).toHaveBeenCalledWith(false);
    expect((screen.getByLabelText("Password") as HTMLInputElement).value).toBe("");
  });
});
