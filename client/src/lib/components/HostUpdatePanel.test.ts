import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import HostUpdatePanel from "./HostUpdatePanel.svelte";
import type { HostEntry, HostUpdateStatus } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.9.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const status = (overrides: Partial<HostUpdateStatus> = {}): HostUpdateStatus => ({
  supported: true,
  mode: "auto",
  channel: "stable",
  channels: ["beta", "stable"],
  maintenanceTime: "03:00",
  currentVersion: "0.1.0",
  availableVersion: null,
  notesUrl: null,
  activity: "idle",
  lastCheck: null,
  message: null,
  lastResult: null,
  rolledBack: [],
  ...overrides,
});

beforeEach(() => {
  invoke.mockReset();
});

describe("HostUpdatePanel", () => {
  it("shows the installed version, the channel, and a ready version", async () => {
    invoke.mockResolvedValue(status({ activity: "ready", availableVersion: "0.2.0", lastResult: "Updated from 0.0.9 to 0.1.0." }));
    render(HostUpdatePanel, { host });

    expect(await screen.findByText(/Version 0\.2\.0 is ready\. It installs when nothing is in progress/)).toBeTruthy();
    expect(screen.getByText(/Following the/).textContent).toContain("stable");
    expect(screen.getByText(/or after 03:00/)).toBeTruthy();
    expect(screen.getByText("Last update: Updated from 0.0.9 to 0.1.0.")).toBeTruthy();
    expect(invoke).toHaveBeenCalledWith("get_host_resource", { key: host.key, resource: "update" });
  });

  it("points to the host window when a ready version waits for the owner", async () => {
    invoke.mockResolvedValue(status({ mode: "notify", activity: "ready", availableVersion: "0.2.0" }));
    render(HostUpdatePanel, { host });

    expect(await screen.findByText(/Install it from the HyperHarbor Host window on the host\./)).toBeTruthy();
  });

  it("offers no Install now on a host that installs only from its tray", async () => {
    invoke.mockResolvedValue(status({ mode: "notify", activity: "ready", availableVersion: "0.2.0" }));
    render(HostUpdatePanel, { host });

    await screen.findByText(/is ready/);
    expect(screen.queryByRole("button", { name: /Install .* now/ })).toBeNull();
  });

  it("installs a ready version now on a newer host, then follows the install", async () => {
    const newer = { ...host, apiVersion: "1.11.0" };
    invoke
      .mockResolvedValueOnce(status({ mode: "notify", activity: "ready", availableVersion: "0.2.0" }))
      .mockResolvedValueOnce(status({ mode: "notify", activity: "ready", availableVersion: "0.2.0" }));
    render(HostUpdatePanel, { host: newer });

    expect(await screen.findByText("Version 0.2.0 is ready to install.", { exact: false })).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "Install 0.2.0 now" }));

    await vi.waitFor(() => expect(invoke).toHaveBeenCalledWith("install_host_update", { key: newer.key }));
    // While the install runs, the button is gone and the other actions wait.
    await vi.waitFor(() => expect(screen.queryByRole("button", { name: "Install 0.2.0 now" })).toBeNull());
    expect((screen.getByRole("button", { name: "Check now" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("checks now and says what the check found", async () => {
    const checked = "2026-10-05T20:00:00Z";
    // The host answers before the check runs (an older one still says idle), then reports the new check time.
    invoke
      .mockResolvedValueOnce(status({ lastCheck: "2026-10-05T08:00:00Z" }))
      .mockResolvedValueOnce(status({ lastCheck: "2026-10-05T08:00:00Z" }))
      .mockResolvedValue(status({ lastCheck: checked }));
    render(HostUpdatePanel, { host });

    await fireEvent.click(await screen.findByRole("button", { name: "Check now" }));

    expect(invoke).toHaveBeenCalledWith("check_host_update", { key: host.key });
    expect(await screen.findByText("Checking for updates…")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Checking…" }) as HTMLButtonElement).disabled).toBe(true);
    expect(await screen.findByText(/Up to date\. 0\.1\.0 is the newest version on the stable channel\./, {}, { timeout: 4000 })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Check now" })).toBeTruthy();
  });

  it("reports a failed check", async () => {
    invoke
      .mockResolvedValueOnce(status())
      .mockResolvedValueOnce(status({ activity: "checking" }))
      .mockResolvedValue(status({ lastCheck: "2026-10-05T20:00:00Z", message: "The update check failed: No such host is known." }));
    render(HostUpdatePanel, { host });

    await fireEvent.click(await screen.findByRole("button", { name: "Check now" }));

    const outcome = await screen.findByText(/The update check failed: No such host is known\./, { selector: "p[role=status]" }, { timeout: 4000 });
    expect(outcome.classList.contains("error")).toBe(true);
  });

  it("says when a newer version was found", async () => {
    invoke
      .mockResolvedValueOnce(status())
      .mockResolvedValueOnce(status({ activity: "checking" }))
      .mockResolvedValue(status({ lastCheck: "2026-10-05T20:00:00Z", activity: "preparing", availableVersion: "0.1.5" }));
    render(HostUpdatePanel, { host });

    await fireEvent.click(await screen.findByRole("button", { name: "Check now" }));

    expect(await screen.findByText("Found version 0.1.5.", {}, { timeout: 4000 })).toBeTruthy();
  });

  it("shows how far a new version has downloaded", async () => {
    invoke.mockResolvedValue(
      status({
        activity: "preparing",
        availableVersion: "0.1.5",
        progress: { step: "downloading", bytesDone: 50 * 1024 * 1024, bytesTotal: 200 * 1024 * 1024 },
      }),
    );
    render(HostUpdatePanel, { host });

    expect(await screen.findByText("Downloading: 50 of 200 MB (25%)")).toBeTruthy();
    expect((screen.getByRole("progressbar") as HTMLProgressElement).value).toBe(25);
  });

  it("saves settings, sending no maintenance time when the field is empty", async () => {
    invoke.mockResolvedValueOnce(status()).mockResolvedValueOnce(status({ channel: "beta", mode: "notify", maintenanceTime: null }));
    render(HostUpdatePanel, { host });

    await fireEvent.click(await screen.findByRole("button", { name: "Change settings…" }));
    await fireEvent.change(screen.getByLabelText("Channel"), { target: { value: "beta" } });
    await fireEvent.change(screen.getByLabelText("Mode"), { target: { value: "notify" } });
    await fireEvent.input(screen.getByLabelText(/Maintenance time/), { target: { value: "" } });
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await vi.waitFor(() =>
      expect(invoke).toHaveBeenCalledWith("set_host_update_settings", {
        key: host.key,
        settings: { channel: "beta", mode: "notify", maintenanceTime: null },
      }),
    );
  });

  it("explains that a host run without installing does not update itself", async () => {
    invoke.mockResolvedValue(
      status({ supported: false, message: "Updates apply to the installed host. This one runs without being installed." }),
    );
    render(HostUpdatePanel, { host });

    expect(await screen.findByText(/Updates apply to the installed host/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Check now" })).toBeNull();
  });
});
