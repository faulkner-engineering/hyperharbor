import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));
vi.mock("$lib/lifecycle.svelte", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$lib/lifecycle.svelte")>()),
  withElevation: <T,>(_key: string, run: () => Promise<T>) => run(),
}));

import MachineProfileEditor from "./MachineProfileEditor.svelte";
import type { HostEntry } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.27.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const resources: Record<string, unknown> = {
  setupProfileCatalog: { browsers: [], policies: [], tweaks: [], appx: [] },
  extensionCatalog: [],
  packageCatalog: [],
  setupProfiles: [{ id: "dev", name: "Dev tools", error: null }],
  unattendProfiles: [],
  switches: [{ id: "s1", name: "Default Switch", isDefault: true }],
};

const saved = () => invoke.mock.calls.filter(([command]) => command === "save_profile").map(([, args]) => args);
const machine = () => document.querySelector(".machine")!;
const hud = () => document.querySelector(".hud")!.textContent;

beforeEach(() => {
  invoke.mockReset();
  invoke.mockImplementation(async (command: string, args: Record<string, unknown>) => {
    if (command === "get_host_resource") return resources[args.resource as string];
    if (command === "list_backup_targets") return [{ id: "t1", name: "NAS", schedule: { time: "02:00" } }];
    if (command === "get_profile") return { id: "dev", updatedAt: "", profile: { name: "Dev tools", install: [{ id: "Git.Git", name: "Git" }] } };
    if (command === "save_profile") return { id: "rig", updatedAt: "", profile: args.profile };
    throw new Error(`unexpected ${command}`);
  });
  vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, json: async () => ({}), text: async () => '<svg xmlns="http://www.w3.org/2000/svg"></svg>' })));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function open(onclose = vi.fn()) {
  render(MachineProfileEditor, { host, profileId: null, initial: null, vms: [], onclose });
  return onclose;
}

describe("MachineProfileEditor", () => {
  it("lists every part of the machine, grouped, and flies to each", async () => {
    open();

    expect(screen.getAllByRole("tab").map((tab) => tab.querySelector(".label")?.firstChild?.textContent?.trim())).toEqual([
      "Account", "Display", "Firmware", "Compute", "GPU", "Network", "Backups", "Install", "Remove", "Tweaks", "Browser",
    ]);
    expect(machine().getAttribute("data-section")).toBe("account");

    await fireEvent.click(screen.getByRole("tab", { name: /GPU/ }));
    expect(machine().getAttribute("data-section")).toBe("gpu");
    await fireEvent.click(screen.getByRole("tab", { name: /Backups/ }));
    expect(machine().getAttribute("data-section")).toBe("backup");
  });

  it("builds a profile from the sections and saves it as a machine profile", async () => {
    const onclose = open();
    await fireEvent.input(screen.getByRole("textbox", { name: "Name" }), { target: { value: "Gaming rig" } });

    await fireEvent.click(screen.getByRole("tab", { name: /Firmware/ }));
    await fireEvent.click(within("Virtual TPM").on);
    expect(hud()).toContain("TPM on");

    await fireEvent.click(screen.getByRole("tab", { name: /Compute/ }));
    await fireEvent.input(screen.getByRole("spinbutton", { name: "Virtual processors" }), { target: { value: "8" } });

    await fireEvent.click(screen.getByRole("tab", { name: /GPU/ }));
    await fireEvent.click(within("Performance mode").on);

    await fireEvent.click(screen.getByRole("tab", { name: /Backups/ }));
    await fireEvent.click(within("Backups").on);
    await waitFor(() => expect((screen.getByRole("combobox", { name: "Backup target" }) as HTMLInputElement).value).toBe("NAS"));

    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onclose).toHaveBeenCalled());
    const [args] = saved() as { kind: string; profileId: string | null; profile: Record<string, unknown> }[];
    expect(args.kind).toBe("machine");
    expect(args.profileId).toBeNull();
    expect(args.profile).toMatchObject({
      name: "Gaming rig",
      os: "windows",
      firmware: { tpm: true },
      compute: { processors: 8 },
      gpu: { enabled: true },
      backup: { enabled: true, target: "NAS" },
    });
  });

  it("saves backup retention and conditions that differ from the target's", async () => {
    const onclose = open();
    await fireEvent.input(screen.getByRole("textbox", { name: "Name" }), { target: { value: "Kept" } });
    await fireEvent.click(screen.getByRole("tab", { name: /Backups/ }));
    await fireEvent.click(within("Backups").on);

    await fireEvent.click(screen.getByRole("checkbox", { name: /Keep a different number of backups/ }));
    await fireEvent.input(screen.getByRole("spinbutton", { name: "Daily" }), { target: { value: "14" } });
    await fireEvent.click(screen.getByRole("checkbox", { name: /Use other conditions/ }));
    await fireEvent.click(screen.getByRole("checkbox", { name: /Wait while a game runs/ }));
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onclose).toHaveBeenCalled());
    const [args] = saved() as { profile: { backup: Record<string, unknown> } }[];
    expect(args.profile.backup).toMatchObject({
      retention: { daily: 14, weekly: 4, monthly: 6 },
      conditions: { noActiveSession: true, notInGame: false },
    });
  });

  it("fills the software sections from a setup profile", async () => {
    open();

    await fireEvent.click(screen.getByRole("tab", { name: /Install/ }));
    const fill = await screen.findByRole("combobox", { name: /Fill the software sections/ });
    await fireEvent.change(fill, { target: { value: "dev" } });

    await waitFor(() => expect(screen.getByRole("tab", { name: /Install/ }).textContent).toContain("1"));
  });

  it("keeps Performance mode and software to Windows", async () => {
    open();

    await fireEvent.click(screen.getByRole("radio", { name: "Linux" }));
    await fireEvent.click(screen.getByRole("tab", { name: /GPU/ }));
    expect(screen.getByText(/Performance mode \(a share of the host's GPU\) is for Windows VMs/)).toBeTruthy();
    await fireEvent.click(screen.getByRole("tab", { name: /Install/ }));
    expect(screen.getByText(/are for Windows VMs/)).toBeTruthy();
  });
});

/** The On button of a Choice group, found by the group's label. */
function within(label: string) {
  const group = screen.getByRole("radiogroup", { name: label });
  return { on: group.querySelector<HTMLButtonElement>("button:nth-child(2)")! };
}
