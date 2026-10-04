import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import InstallProfiles from "./InstallProfiles.svelte";
import type { HostEntry } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.5.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const builtIn = {
  id: "ubuntu-dev-server",
  name: "Ubuntu Dev Server",
  builtIn: true,
  os: "linux",
  adminAccountName: "hhadmin",
  timeZone: "America/Chicago",
  locale: "en-US",
  windows: null,
  linux: { sshAuthorizedKeys: [], packages: ["git"], installDesktop: true },
};

const own = {
  ...builtIn,
  id: "3f2a9c1d-4b5e-4f70-8192-a3b4c5d6e7f8",
  name: "Build Server",
  builtIn: false,
  linux: { sshAuthorizedKeys: ["ssh-ed25519 AAAA user@laptop"], packages: [], installDesktop: false },
};

const saveCalls = () => invoke.mock.calls.filter(([command]) => command === "save_unattend_profile").map(([, args]) => args);

beforeEach(() => {
  invoke.mockReset();
  invoke.mockImplementation((command: string, args: { profile?: { name: string } }) => {
    if (command === "get_host_resource") return Promise.resolve([builtIn, own]);
    if (command === "save_unattend_profile") return Promise.resolve({ ...own, name: args.profile?.name });
    if (command === "delete_unattend_profile") return Promise.resolve();
    return Promise.reject(new Error(`unexpected ${command}`));
  });
});

describe("InstallProfiles", () => {
  it("lists profiles and lets only the User's own be edited or deleted", async () => {
    render(InstallProfiles, { host });

    const builtInRow = (await screen.findByText("Ubuntu Dev Server")).closest("tr")!;
    const ownRow = screen.getByText("Build Server").closest("tr")!;
    expect(within(builtInRow).getByText("Built in")).toBeTruthy();
    expect(within(builtInRow).queryByRole("button", { name: "Edit" })).toBeNull();
    expect(within(ownRow).getByRole("button", { name: "Edit" })).toBeTruthy();
    expect(within(ownRow).getByText("no desktop, 1 SSH key")).toBeTruthy();
  });

  it("copies a built-in profile into a new one", async () => {
    render(InstallProfiles, { host });

    const row = (await screen.findByText("Ubuntu Dev Server")).closest("tr")!;
    await fireEvent.click(within(row).getByRole("button", { name: "Copy" }));
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("Ubuntu Dev Server copy");
    await fireEvent.input(screen.getByLabelText("SSH public keys (one per line)"), {
      target: { value: "ssh-ed25519 AAAA one\n\n ssh-ed25519 BBBB two " },
    });
    await fireEvent.input(screen.getByLabelText("Extra packages"), { target: { value: "git, curl  htop" } });
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await vi.waitFor(() => expect(saveCalls()).toHaveLength(1));
    const call = saveCalls()[0];
    expect(call.profileId).toBeNull();
    expect(call.profile.linux).toEqual({
      sshAuthorizedKeys: ["ssh-ed25519 AAAA one", "ssh-ed25519 BBBB two"],
      packages: ["git", "curl", "htop"],
      installDesktop: true,
    });
  });

  it("shows field errors from the host", async () => {
    invoke.mockImplementation((command: string) => {
      if (command === "get_host_resource") return Promise.resolve([builtIn, own]);
      if (command === "save_unattend_profile")
        return Promise.reject({
          code: "api",
          message: "Invalid request",
          status: 400,
          problemCode: null,
          issues: [{ field: "adminAccountName", message: "\"root\" is reserved." }],
        });
      return Promise.reject(new Error(`unexpected ${command}`));
    });
    render(InstallProfiles, { host });

    await fireEvent.click(await screen.findByRole("button", { name: "New Linux profile" }));
    await fireEvent.input(screen.getByLabelText("Name"), { target: { value: "Bad" } });
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("\"root\" is reserved.")).toBeTruthy();
  });

  it("edits the User's own profile in place", async () => {
    render(InstallProfiles, { host });

    const row = (await screen.findByText("Build Server")).closest("tr")!;
    await fireEvent.click(within(row).getByRole("button", { name: "Edit" }));
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await vi.waitFor(() => expect(saveCalls()).toHaveLength(1));
    expect(saveCalls()[0].profileId).toBe(own.id);
  });
});
