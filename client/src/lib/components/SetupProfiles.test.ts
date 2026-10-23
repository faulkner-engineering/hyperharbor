import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import CaptureReview from "./CaptureReview.svelte";
import SetupProfiles from "./SetupProfiles.svelte";
import type { HostEntry, ProfileDraft, Vm } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.12.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm = {
  id: "807223df-0aeb-40a8-9d1c-223878114632",
  name: "Dev Box",
  state: "running",
  guestOs: { family: "windows", name: "Windows 11 Pro" },
} as Vm;

/** Answers commands by name; unknown commands reject so a test notices them. */
function answer(responses: Record<string, (args: Record<string, unknown>) => unknown>) {
  invoke.mockImplementation(async (command: string, args: Record<string, unknown>) => {
    const key = command === "get_host_resource" ? `resource:${args.resource as string}` : command;
    const respond = responses[key];
    if (!respond) throw new Error(`unexpected ${key}`);
    return respond(args);
  });
}

const calls = (command: string) => invoke.mock.calls.filter(([name]) => name === command).map(([, args]) => args);

beforeEach(() => {
  invoke.mockReset();
});

describe("CaptureReview", () => {
  const draft: ProfileDraft = {
    vmId: vm.id,
    vmName: "Dev Box",
    build: "10.0.26100.2033",
    edition: "Professional",
    baseline: null,
    install: [{ id: "Git.Git", name: "Git", selected: true, note: null }],
    removeAppx: [{ id: "Microsoft.BingNews", name: "Microsoft News", selected: true, note: null }],
    tweaks: [],
    browsers: [
      { app: "Brave.Brave", name: "Brave", selected: true, extensions: [{ id: "eimadpbcbfnmbkopoojfekhnkhdbieeh", name: "Dark Reader", selected: true, note: null }] },
    ],
    otherPrograms: [],
    warnings: ["There is no clean Appx baseline for Windows 10.0.26100.2033 Professional."],
  };

  it("shows three columns and saves what stays ticked", async () => {
    answer({
      capture_setup_profile: () => draft,
      save_setup_profile: (args) => ({ id: "dev-box-setup", updatedAt: "2026-10-05T12:00:00Z", profile: args.profile }),
    });
    const onclose = vi.fn();
    render(CaptureReview, { host, vm, onclose });

    expect(await screen.findByText(/no clean Appx baseline/)).toBeTruthy();
    await fireEvent.click(screen.getByRole("checkbox", { name: /Microsoft News/ }));
    await fireEvent.click(screen.getByRole("button", { name: "Save profile" }));

    await vi.waitFor(() => expect(onclose).toHaveBeenCalled());
    const [saved] = calls("save_setup_profile") as { profileId: string | null; profile: Record<string, unknown> }[];
    expect(saved.profileId).toBeNull();
    expect(saved.profile).toMatchObject({
      name: "Dev Box setup",
      install: [{ id: "Git.Git", name: "Git" }],
      remove: { appx: [] },
      browser: { app: { id: "Brave.Brave", name: "Brave" }, extensions: [{ id: "eimadpbcbfnmbkopoojfekhnkhdbieeh", name: "Dark Reader" }] },
    });
  });
});

describe("SetupProfiles", () => {
  const summary = {
    id: "dev-workstation",
    name: "Dev workstation",
    description: null,
    installCount: 4,
    removeCount: 2,
    tweakCount: 1,
    extensionCount: 1,
    browser: "Brave",
    updatedAt: "2026-10-05T12:00:00Z",
    error: null,
  };

  it("lists profiles, shows unreadable files, and exports one", async () => {
    answer({
      "resource:setupProfiles": () => [summary, { ...summary, id: "broken", name: "broken", error: "Line 3: oops" }],
      export_setup_profile: () => "C:\\Users\\me\\dev-workstation.yaml",
    });
    render(SetupProfiles, { host, vms: [] });

    const row = (await screen.findByText("Dev workstation")).closest("tr")!;
    expect(within(row).getByText(/4 to install, 2 to remove, 1 tweaks, Brave with 1 extensions/)).toBeTruthy();
    expect(screen.getByText(/does not read: Line 3: oops/)).toBeTruthy();

    await fireEvent.click(within(row).getByRole("button", { name: "Export…" }));
    await vi.waitFor(() => expect(calls("export_setup_profile")).toEqual([{ key: host.key, profileId: "dev-workstation" }]));
  });

  it("imports a file and lists it", async () => {
    let imported = false;
    answer({
      "resource:setupProfiles": () => (imported ? [summary] : []),
      import_setup_profile: () => {
        imported = true;
        return { id: "dev-workstation", updatedAt: "2026-10-05T12:00:00Z", profile: { name: "Dev workstation" } };
      },
    });
    render(SetupProfiles, { host, vms: [] });

    await fireEvent.click(await screen.findByRole("button", { name: "Import…" }));

    expect(await screen.findByText("Dev workstation")).toBeTruthy();
  });
});
