import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn(async () => () => {}) }));

import Library from "./Library.svelte";
import type { HostEntry, Library as LibraryData } from "$lib/api/client";

const host = (key: string, hostId: string, displayName: string): HostEntry => ({
  key,
  displayName,
  hostId,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.33.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
});

const hosts = [host("mdns:a", "host-a", "TC-PC"), host("mdns:b", "host-b", "Den PC")];

const link = (hostId: string, hostName: string) => ({
  hostId,
  hostName,
  remoteId: "dev-box",
  sentHash: "h1",
  remoteUpdatedAt: "2026-10-23T10:00:00Z",
  syncedAt: 1,
});

let library: LibraryData;

beforeEach(() => {
  invoke.mockReset();
  library = {
    version: 1,
    isos: [{ id: "iso-1", name: "debian.iso", sizeBytes: 3 * 1024 ** 3, addedAt: 1, links: [] }],
    profiles: [
      {
        id: "p-1",
        name: "Dev box",
        os: "windows",
        description: null,
        hash: "h1",
        images: [],
        updatedAt: 1,
        links: [link("host-a", "TC-PC")],
      },
    ],
  };
  invoke.mockImplementation(async (command: string) => {
    if (command === "list_library") return library;
    if (command === "library_status") return [{ itemId: "p-1", hostId: "host-a", state: "hostNewer" }];
    if (command === "get_elevation") return { active: true, configured: true, expiresAt: null };
    if (command === "send_library_iso") return library.isos[0];
    if (command === "save_host_profile_to_library") return library.profiles[0];
    if (command === "get_library_profile") return { name: "Dev box", os: "windows" };
    if (command === "save_library_profile") return library.profiles[0];
    if (command === "get_host_resource") return [];
    if (command === "list_backup_targets") return [];
    throw new Error(`unexpected ${command}`);
  });
  vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, json: async () => ({}), text: async () => '<svg xmlns="http://www.w3.org/2000/svg"></svg>' })));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Library", () => {
  it("lists images and profiles with the state of each host's copy", async () => {
    render(Library, { hosts });

    expect(await screen.findByText("debian.iso")).toBeTruthy();
    expect(screen.getByText("3.0 GB")).toBeTruthy();
    expect(screen.getByText("Only on this device")).toBeTruthy();
    expect(await screen.findByText("TC-PC: Changed on host")).toBeTruthy();
    expect(invoke).toHaveBeenCalledWith("library_status", { key: "mdns:a" });
  });

  it("sends an image to the host chosen in the picker", async () => {
    render(Library, { hosts });

    const picker = await screen.findByRole("combobox", { name: "Send debian.iso to a host" });
    await fireEvent.change(picker, { target: { value: "mdns:b" } });

    await waitFor(() =>
      expect(invoke).toHaveBeenCalledWith("send_library_iso", expect.objectContaining({ key: "mdns:b", itemId: "iso-1", name: "debian.iso" })),
    );
  });

  it("updates the library from a host whose copy changed", async () => {
    render(Library, { hosts });

    await fireEvent.click(await screen.findByRole("button", { name: "Update library" }));

    await waitFor(() => expect(invoke).toHaveBeenCalledWith("save_host_profile_to_library", { key: "mdns:a", profileId: "dev-box" }));
  });

  it("offers a profile only to hosts that do not have it", async () => {
    render(Library, { hosts });

    const picker = await screen.findByRole("combobox", { name: "Send Dev box to a host" });
    const options = [...picker.querySelectorAll("option")].map((option) => option.textContent);
    expect(options).toEqual(["Send to…", "Den PC"]);
  });

  it("makes a new profile in the library with a host's catalogs", async () => {
    render(Library, { hosts });

    await fireEvent.click(await screen.findByRole("button", { name: "+ New profile" }));
    expect(await screen.findByText("New machine profile")).toBeTruthy();
    expect(screen.getByRole("combobox", { name: "Catalogs from" })).toBeTruthy();
  });

  it("opens a library profile in the editor", async () => {
    render(Library, { hosts });

    await fireEvent.click(await screen.findByRole("button", { name: "Edit" }));

    expect(await screen.findByText("Edit Dev box")).toBeTruthy();
    expect(invoke).toHaveBeenCalledWith("get_library_profile", { itemId: "p-1" });
  });

  it("shows a linked host it cannot reach", async () => {
    invoke.mockImplementation(async (command: string) => {
      if (command === "list_library") return library;
      if (command === "library_status") throw { code: "unreachable", message: "The host could not be reached." };
      throw new Error(`unexpected ${command}`);
    });

    render(Library, { hosts });

    expect(await screen.findByText("TC-PC: Not reachable")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Update library" })).toBeNull();
  });
});
