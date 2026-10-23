import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import MachineProfiles from "./MachineProfiles.svelte";
import type { HostEntry } from "$lib/api/client";

const host = (apiVersion: string): HostEntry => ({
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion,
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
});

const templates = [
  { id: "ubuntu-dev-server", name: "Ubuntu Dev Server", description: "Ubuntu with a desktop.", profile: { name: "Ubuntu Dev Server", os: "linux", account: { adminAccountName: "hhadmin" } } },
];

beforeEach(() => {
  invoke.mockReset();
  invoke.mockImplementation(async (command: string, args: Record<string, unknown>) => {
    if (command === "get_host_resource" && args.resource === "machineProfiles") return [];
    if (command === "get_host_resource" && args.resource === "machineProfileTemplates") return templates;
    if (command === "get_host_resource") return [];
    if (command === "list_backup_targets") return [];
    throw new Error(`unexpected ${command}`);
  });
  vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, json: async () => ({}), text: async () => '<svg xmlns="http://www.w3.org/2000/svg"></svg>' })));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("MachineProfiles", () => {
  it("starts a new profile from a template on a host at 1.32.0", async () => {
    render(MachineProfiles, { host: host("1.32.0"), vms: [] });

    expect(await screen.findByText("No profiles yet. Make one here, or start from a template.")).toBeTruthy();
    const from = await screen.findByRole("combobox", { name: "New profile from a template" });
    await fireEvent.change(from, { target: { value: "ubuntu-dev-server" } });

    expect(((await screen.findByRole("textbox", { name: "Name" })) as HTMLInputElement).value).toBe("My Ubuntu Dev Server");
    expect(screen.getByRole("radio", { name: "Linux" }).getAttribute("aria-checked")).toBe("true");
  });

  it("offers no templates on an older host", async () => {
    render(MachineProfiles, { host: host("1.31.0"), vms: [] });

    await screen.findByText(/No machine profiles yet/);
    expect(screen.queryByRole("combobox", { name: "New profile from a template" })).toBeNull();
    expect(invoke.mock.calls.some(([, args]) => args?.resource === "machineProfileTemplates")).toBe(false);
  });
});
