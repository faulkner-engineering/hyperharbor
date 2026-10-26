import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
const listen = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen }));

import Overlay from "./+page.svelte";
import type { GuestAdminCard } from "$lib/api/client";

function card(minutes = 5): GuestAdminCard {
  return {
    vmName: "Dev Box",
    accountName: "hha-owner",
    passphrase: "Maple-River-Orbit-7",
    expiresAt: new Date(Date.now() + minutes * 60_000).toISOString(),
  };
}

describe("passphrase card", () => {
  let changed: () => void;

  beforeEach(() => {
    invoke.mockReset();
    listen.mockReset();
    listen.mockImplementation(async (_event: string, handler: () => void) => {
      changed = handler;
      return () => {};
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("shows the VM, the passphrase in its parts, the account, and the time left", async () => {
    invoke.mockResolvedValue(card());
    render(Overlay);

    for (const part of ["Maple", "River", "Orbit", "7"]) {
      expect(await screen.findByText(part)).toBeTruthy();
    }
    expect(screen.getByText(/Dev Box/)).toBeTruthy();
    expect(screen.getByText(/hha-owner/)).toBeTruthy();
    expect(screen.getByRole("timer").textContent).toMatch(/^(5:00|4:5\d)$/);
    expect(screen.queryByRole("button", { name: /copy/i })).toBeNull();
  });

  it("reads the card again when the client opens a new window", async () => {
    invoke.mockResolvedValueOnce(card()).mockResolvedValueOnce({ ...card(), passphrase: "Cedar-Lantern-Comet-3" });
    render(Overlay);
    expect(await screen.findByText("Maple")).toBeTruthy();

    changed();

    expect(await screen.findByText("Lantern")).toBeTruthy();
  });

  it("hides itself when the window ends, and when the user hides it", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "Date"] });
    invoke.mockImplementation(async (command: string) => (command === "get_guest_admin_overlay" ? card(1) : undefined));
    render(Overlay);
    expect(await screen.findByText("Maple")).toBeTruthy();

    vi.advanceTimersByTime(61_000);

    await waitFor(() => expect(invoke).toHaveBeenCalledWith("hide_guest_admin_overlay"));
    expect(screen.queryByText("Maple")).toBeNull();
  });

  it("hides when the user closes it", async () => {
    invoke.mockImplementation(async (command: string) => (command === "get_guest_admin_overlay" ? card() : undefined));
    render(Overlay);

    await fireEvent.click(await screen.findByRole("button", { name: "Hide" }));

    expect(invoke).toHaveBeenCalledWith("hide_guest_admin_overlay");
    expect(screen.queryByText("Maple")).toBeNull();
  });
});
