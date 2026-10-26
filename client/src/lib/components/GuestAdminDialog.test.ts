import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import GuestAdminDialog from "./GuestAdminDialog.svelte";
import { elevation } from "$lib/lifecycle.svelte";
import { toasts } from "$lib/toasts.svelte";
import { SLOW_AFTER_MS } from "$lib/guestAdmin";
import type { GuestAdminWindow, HostEntry, Vm } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: "8f3b2c1a-0000-4000-8000-000000000001",
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.35.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm: Vm = {
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

const problem = (problemCode: string, message = "Refused.") => ({ code: "api", message, status: 409, problemCode, issues: [] });
const elevationRequired = { code: "api", message: "Elevation required", status: 403, problemCode: "elevationRequired", issues: [] };

function window(minutes = 5): GuestAdminWindow {
  return {
    accountName: "hha-owner",
    passphrase: "Maple-River-Orbit-7",
    expiresAt: new Date(Date.now() + minutes * 60_000).toISOString(),
  };
}

/** Answers each command in turn from `responses[command]` (a value, a thrown problem, or a list of either). */
function mockHost(responses: Record<string, unknown[]>) {
  // The always-on-top card opens and closes without a host.
  const card: Record<string, unknown[]> = { show_guest_admin_overlay: [true], hide_guest_admin_overlay: [undefined] };
  invoke.mockImplementation(async (command: string) => {
    const queue = responses[command] ?? card[command];
    if (!queue?.length) throw new Error(`unexpected command ${command}`);
    const next = queue.length > 1 ? queue.shift() : queue[0];
    if (typeof next === "object" && next !== null && "problemCode" in next) throw next;
    return next;
  });
}

const calls = () => invoke.mock.calls.map(([command]) => command);

describe("GuestAdminDialog", () => {
  beforeEach(() => {
    invoke.mockReset();
    elevation.finish(false);
    toasts.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("asks for the admin passphrase, then shows the account and the passphrase split into its words", async () => {
    mockHost({ open_guest_admin: [elevationRequired, window()] });
    render(GuestAdminDialog, { host, vm, onclose: vi.fn() });

    await waitFor(() => expect(elevation.pending?.hostKey).toBe(host.key));
    elevation.finish(true);

    expect(await screen.findByText("hha-owner")).toBeTruthy();
    for (const part of ["Maple", "River", "Orbit", "7"]) {
      expect(screen.getByText(part)).toBeTruthy();
    }
    expect(screen.getByText(/choose/).textContent).toContain(
      "In the Windows prompt choose More choices, pick this account, and type the passphrase.",
    );
    expect(screen.getByRole("timer").textContent).toMatch(/Ends in (5:00|4:5\d)\./);
    expect(screen.queryByRole("button", { name: /copy/i })).toBeNull();
    expect(invoke).toHaveBeenCalledWith("open_guest_admin", { key: host.key, vmId: vm.id });
    expect(invoke).toHaveBeenCalledWith("show_guest_admin_overlay", {
      card: expect.objectContaining({ vmName: "Dev Box", accountName: "hha-owner", passphrase: "Maple-River-Orbit-7" }),
    });
  });

  it("ends the window with Done", async () => {
    mockHost({ open_guest_admin: [window()], close_guest_admin: [undefined] });
    const onclose = vi.fn();
    render(GuestAdminDialog, { host, vm, onclose });

    await fireEvent.click(await screen.findByRole("button", { name: "Done" }));

    expect(invoke).toHaveBeenCalledWith("close_guest_admin", { key: host.key, vmId: vm.id });
    expect(invoke).toHaveBeenCalledWith("hide_guest_admin_overlay");
    expect(onclose).toHaveBeenCalled();
  });

  it("says that closing leaves the window open, and leaves it open", async () => {
    mockHost({ open_guest_admin: [window()] });
    const onclose = vi.fn();
    render(GuestAdminDialog, { host, vm, onclose });

    expect((await screen.findByRole("timer")).textContent).toContain("Closing this dialog leaves administrator access open until");
    await fireEvent.click(screen.getByRole("button", { name: "Close" }));

    expect(onclose).toHaveBeenCalled();
    // The card stays up with the window, for the Remote Desktop session.
    expect(calls()).toEqual(["open_guest_admin", "show_guest_admin_overlay"]);
  });

  it("says when the window ended, hides the passphrase, and can request another", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "Date"] });
    mockHost({ open_guest_admin: [window(2), window()] });
    render(GuestAdminDialog, { host, vm, onclose: vi.fn() });
    expect(await screen.findByText("Maple")).toBeTruthy();

    vi.advanceTimersByTime(2 * 60_000 + 1000);

    expect(await screen.findByText(/has ended/)).toBeTruthy();
    expect(screen.queryByText("Maple")).toBeNull();
    await fireEvent.click(screen.getByRole("button", { name: "Request again" }));
    expect(await screen.findByText("Maple")).toBeTruthy();
    expect(calls()).toEqual([
      "open_guest_admin",
      "show_guest_admin_overlay",
      "hide_guest_admin_overlay",
      "open_guest_admin",
      "show_guest_admin_overlay",
    ]);
  });

  it("explains a Linux guest and a guest that is still starting", async () => {
    mockHost({ open_guest_admin: [problem("guestAdminUnsupported"), problem("guestUnavailable")] });
    render(GuestAdminDialog, { host, vm, onclose: vi.fn() });

    expect((await screen.findByRole("alert")).textContent).toContain("use sudo there");
    await fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("is not ready yet"));
  });

  it("closes without a word when the passphrase prompt is cancelled", async () => {
    mockHost({ open_guest_admin: [elevationRequired] });
    const onclose = vi.fn();
    render(GuestAdminDialog, { host, vm, onclose });

    await waitFor(() => expect(elevation.pending).not.toBeNull());
    elevation.finish(false);

    await waitFor(() => expect(onclose).toHaveBeenCalled());
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("closes at once on Done even when the host never answers, and hides the card", async () => {
    mockHost({ open_guest_admin: [window()] });
    const never = new Promise(() => {});
    const onclose = vi.fn();
    render(GuestAdminDialog, { host, vm, onclose });
    await screen.findByRole("button", { name: "Done" });
    invoke.mockImplementation(async (command: string) => (command === "close_guest_admin" ? never : undefined));

    await fireEvent.click(screen.getByRole("button", { name: "Done" }));

    expect(onclose).toHaveBeenCalled();
    expect(invoke).toHaveBeenCalledWith("hide_guest_admin_overlay");
    expect(invoke).toHaveBeenCalledWith("close_guest_admin", { key: host.key, vmId: vm.id });
  });

  it("reports a Done the host refused as a toast", async () => {
    mockHost({ open_guest_admin: [window()], close_guest_admin: [problem("guestUnavailable", "PowerShell Direct could not connect.")] });
    const onchanged = vi.fn();
    render(GuestAdminDialog, { host, vm, onclose: vi.fn(), onchanged });

    await fireEvent.click(await screen.findByRole("button", { name: "Done" }));

    await waitFor(() => expect(toasts.items.map((toast) => toast.message).join()).toContain("could not be ended"));
    expect(toasts.items[0].kind).toBe("error");
    await waitFor(() => expect(onchanged).toHaveBeenCalledTimes(2));
  });

  it("offers to close when opening is slow, and never shows the card for a late answer", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    let answer: (value: GuestAdminWindow) => void = () => {};
    invoke.mockImplementation((command: string) =>
      command === "open_guest_admin" ? new Promise<GuestAdminWindow>((resolve) => (answer = resolve)) : Promise.resolve(undefined),
    );
    const onclose = vi.fn();
    render(GuestAdminDialog, { host, vm, onclose });
    expect(screen.queryByRole("button", { name: "Close" })).toBeNull();

    await vi.advanceTimersByTimeAsync(SLOW_AFTER_MS);
    expect(screen.getByText(/taking longer than usual/)).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "Close" }));
    answer(window());
    await vi.advanceTimersByTimeAsync(10);

    expect(onclose).toHaveBeenCalled();
    expect(calls()).not.toContain("show_guest_admin_overlay");
  });
});
