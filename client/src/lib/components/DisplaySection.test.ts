import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));
vi.mock("$lib/lifecycle.svelte", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$lib/lifecycle.svelte")>()),
  withElevation: <T,>(_key: string, run: () => Promise<T>) => run(),
}));

import DisplaySection from "./DisplaySection.svelte";
import { MachineDraft } from "$lib/machineDraft.svelte";
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

let images: { name: string; sizeBytes: number }[];

beforeEach(() => {
  invoke.mockReset();
  images = [{ name: "dunes.jpg", sizeBytes: 2_400_000 }];
  invoke.mockImplementation(async (command: string, args: Record<string, unknown>) => {
    if (command === "get_host_resource" && args.resource === "profileImages") return images;
    if (command === "upload_profile_image") return { name: "aurora.png", sizeBytes: 800_000 };
    if (command === "delete_profile_image") return undefined;
    throw new Error(`unexpected ${command}`);
  });
});

function open(apiVersion = "1.31.0", image = "") {
  const draft = new MachineDraft({ name: "Desk", os: "windows", display: { guest: { wallpaper: { image } } } });
  render(DisplaySection, { host: host(apiVersion), draft });
  return draft;
}

describe("DisplaySection wallpaper images", () => {
  it("lists the host's images and chooses one", async () => {
    const draft = open();

    await fireEvent.click(await screen.findByRole("radio", { name: /dunes\.jpg/ }));

    expect(draft.display.guest.wallpaper).toEqual({ image: "dunes.jpg" });
    expect(screen.getByRole("radio", { name: /dunes\.jpg/ }).getAttribute("aria-checked")).toBe("true");
    expect(screen.getByText("2.3 MB")).toBeTruthy();
  });

  it("adds an image from this device and chooses it", async () => {
    const draft = open();
    await screen.findByRole("radio", { name: /dunes\.jpg/ });

    await fireEvent.click(screen.getByRole("button", { name: "Add image…" }));

    await waitFor(() => expect(draft.display.guest.wallpaper).toEqual({ image: "aurora.png" }));
    expect(screen.getAllByRole("radio", { name: /\.(jpg|png)/ }).map((radio) => radio.querySelector(".name")?.textContent)).toEqual(["aurora.png", "dunes.jpg"]);
  });

  it("deletes an image and says when the chosen one is missing", async () => {
    open("1.31.0", "dunes.jpg");
    await screen.findByRole("radio", { name: /dunes\.jpg/ });

    await fireEvent.click(screen.getByRole("button", { name: "Delete dunes.jpg" }));

    await waitFor(() => expect(screen.getByText(/This host has no image named dunes\.jpg/)).toBeTruthy());
    expect(invoke).toHaveBeenCalledWith("delete_profile_image", { key: "mdns:host", name: "dunes.jpg" });
  });

  it("keeps a typed name on a host that does not keep images", async () => {
    const draft = open("1.30.0", "harbor.jpg");

    await fireEvent.input(screen.getByRole("textbox", { name: "Image file name" }), { target: { value: "night.png" } });

    expect(draft.display.guest.wallpaper).toEqual({ image: "night.png" });
    expect(invoke).not.toHaveBeenCalled();
  });
});
