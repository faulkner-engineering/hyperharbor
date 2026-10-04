import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn(() => Promise.resolve(() => {})) }));

import IsoLibrary from "./IsoLibrary.svelte";
import type { HostEntry, IsoImage } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.3.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const images: IsoImage[] = [
  { name: "ubuntu.iso", sizeBytes: 3 * 1024 ** 3, modifiedAt: "2026-10-01T00:00:00Z", usedBy: [] },
  { name: "Win11.iso", sizeBytes: 6 * 1024 ** 3, modifiedAt: "2026-10-02T00:00:00Z", usedBy: ["Dev Box"] },
];

interface Overrides {
  picked?: unknown;
  upload?: () => Promise<unknown>;
  active?: boolean;
}

function respond({ picked = null, upload, active = true }: Overrides = {}) {
  invoke.mockImplementation((command: string, args: { resource?: string }) => {
    switch (command) {
      case "get_host_resource":
        return Promise.resolve(args.resource === "isos" ? images : { isoFolder: "D:\\ISOs" });
      case "pick_iso_file":
        return Promise.resolve(picked);
      case "get_elevation":
        return Promise.resolve({ configured: true, active, expiresAt: null });
      case "upload_iso":
        return upload ? upload() : Promise.resolve(images[0]);
      case "rename_iso":
      case "delete_iso":
        return Promise.resolve(images[0]);
      default:
        return Promise.reject(new Error(`unexpected ${command}`));
    }
  });
}

const calls = (command: string) => invoke.mock.calls.filter(([name]) => name === command).map(([, args]) => args);

function row(name: string) {
  return within(screen.getByText(name).closest("tr")!);
}

beforeEach(() => {
  invoke.mockReset();
});

describe("IsoLibrary", () => {
  it("lists the host's images, where they are stored, and which VMs use them", async () => {
    respond();
    render(IsoLibrary, { host });

    await screen.findByText("ubuntu.iso");
    expect(screen.getByText(/Stored in D:\\ISOs\./)).not.toBeNull();
    expect(row("ubuntu.iso").getByText("Not in use")).not.toBeNull();
    expect(row("Win11.iso").getByText("Dev Box")).not.toBeNull();
    expect((row("Win11.iso").getByRole("button", { name: "Delete" }) as HTMLButtonElement).disabled).toBe(true);
    expect((row("Win11.iso").getByRole("button", { name: "Rename" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("uploads a picked file under its own name", async () => {
    respond({ picked: { pickId: "pick-1", fileName: "debian.iso", sizeBytes: 1024 } });
    render(IsoLibrary, { host });
    await screen.findByText("ubuntu.iso");

    await fireEvent.click(screen.getByRole("button", { name: "+ Add ISO" }));

    await screen.findByText("Added debian.iso.");
    expect(calls("upload_iso")).toEqual([{ key: host.key, pickId: "pick-1", name: "debian.iso" }]);
  });

  it("asks for the passphrase before uploading when the device is not elevated", async () => {
    respond({ picked: { pickId: "pick-1", fileName: "debian.iso", sizeBytes: 1024 }, active: false });
    const { elevation } = await import("$lib/lifecycle.svelte");
    render(IsoLibrary, { host });
    await screen.findByText("ubuntu.iso");

    await fireEvent.click(screen.getByRole("button", { name: "+ Add ISO" }));
    await vi.waitFor(() => expect(elevation.pending?.hostKey).toBe(host.key));
    expect(calls("upload_iso")).toHaveLength(0);

    elevation.finish(true, "2026-10-03T12:05:00Z");
    await screen.findByText("Added debian.iso.");
  });

  it("refuses a file whose name is already in the library without uploading", async () => {
    respond({ picked: { pickId: "pick-1", fileName: "UBUNTU.ISO", sizeBytes: 1024 } });
    render(IsoLibrary, { host });
    await screen.findByText("ubuntu.iso");

    await fireEvent.click(screen.getByRole("button", { name: "+ Add ISO" }));

    expect((await screen.findByRole("alert")).textContent).toContain("already in the library");
    expect(calls("upload_iso")).toHaveLength(0);
  });

  it("reports a cancelled upload", async () => {
    respond({
      picked: { pickId: "pick-1", fileName: "debian.iso", sizeBytes: 1024 },
      upload: () => Promise.reject({ code: "cancelled", message: "Cancelled.", status: null, problemCode: null, issues: [] }),
    });
    render(IsoLibrary, { host });
    await screen.findByText("ubuntu.iso");

    await fireEvent.click(screen.getByRole("button", { name: "+ Add ISO" }));

    expect((await screen.findByRole("alert")).textContent).toContain("Nothing was added");
  });

  it("renames an image", async () => {
    respond();
    render(IsoLibrary, { host });
    await screen.findByText("ubuntu.iso");

    await fireEvent.click(row("ubuntu.iso").getByRole("button", { name: "Rename" }));
    await fireEvent.input(screen.getByLabelText("New name for ubuntu.iso"), { target: { value: "Ubuntu 24.04.iso" } });
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await screen.findByText("Renamed ubuntu.iso to Ubuntu 24.04.iso.");
    expect(calls("rename_iso")).toEqual([{ key: host.key, name: "ubuntu.iso", newName: "Ubuntu 24.04.iso" }]);
  });

  it("deletes an image after confirmation", async () => {
    respond();
    render(IsoLibrary, { host });
    await screen.findByText("ubuntu.iso");

    await fireEvent.click(row("ubuntu.iso").getByRole("button", { name: "Delete" }));
    expect(calls("delete_iso")).toHaveLength(0);
    await fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete" }));

    await screen.findByText("Deleted ubuntu.iso.");
    expect(calls("delete_iso")).toEqual([{ key: host.key, name: "ubuntu.iso" }]);
  });
});
