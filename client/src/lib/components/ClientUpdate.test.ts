import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn(() => Promise.resolve(() => {})) }));

import ClientUpdate from "./ClientUpdate.svelte";
import type { ClientUpdateStatus } from "$lib/api/client";

const status = (overrides: Partial<ClientUpdateStatus> = {}): ClientUpdateStatus => ({
  currentVersion: "0.1.7",
  installKind: "installed",
  releasesUrl: "https://example.test/releases",
  phase: { state: "idle" },
  ...overrides,
});

const available: ClientUpdateStatus["phase"] = { state: "available", version: "0.2.0", notes: "Faster start." };

function answer(current: ClientUpdateStatus, install: () => Promise<unknown> = () => Promise.resolve()) {
  invoke.mockImplementation((command: string) => {
    switch (command) {
      case "get_client_update":
      case "check_client_update":
        return Promise.resolve(current);
      case "get_update_settings":
        return Promise.resolve({ channel: "stable", checkAutomatically: true });
      case "install_client_update":
        return install();
      default:
        return Promise.reject(new Error(`unexpected ${command}`));
    }
  });
}

beforeEach(() => {
  invoke.mockReset();
});

describe("ClientUpdate", () => {
  it("shows the running version and checks on request", async () => {
    answer(status());
    render(ClientUpdate);
    expect(await screen.findByText("Version 0.1.7")).toBeTruthy();

    answer(status({ phase: { state: "upToDate" } }));
    await fireEvent.click(screen.getByRole("button", { name: "Check for updates" }));

    expect(await screen.findByText("You have the latest version.")).toBeTruthy();
  });

  it("offers an installed copy the update, with its notes", async () => {
    answer(status({ phase: available }));
    render(ClientUpdate);

    expect(await screen.findByText("Version 0.2.0 is available.")).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "What's new" }));
    expect(screen.getByText("Faster start.")).toBeTruthy();

    await fireEvent.click(screen.getByRole("button", { name: "Install and restart" }));
    expect(invoke).toHaveBeenCalledWith("install_client_update", { confirmed: false });
  });

  it("points a portable copy at the releases page instead of installing", async () => {
    answer(status({ installKind: "portable", phase: available }));
    render(ClientUpdate);

    expect(await screen.findByText("https://example.test/releases")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Install and restart" })).toBeNull();
  });

  it("asks before closing open sessions, then installs when confirmed", async () => {
    answer(status({ phase: available }), () =>
      Promise.reject({ code: "sessionsActive", message: "2 Remote Desktop or console window(s) are open." }),
    );
    render(ClientUpdate);
    await fireEvent.click(await screen.findByRole("button", { name: "Install and restart" }));

    expect(await screen.findByText(/2 Remote Desktop or console windows are open/)).toBeTruthy();

    answer(status({ phase: available }));
    const buttons = screen.getAllByRole("button", { name: "Install and restart" });
    await fireEvent.click(buttons[buttons.length - 1]);

    expect(invoke).toHaveBeenLastCalledWith("install_client_update", { confirmed: true });
  });

  it("shows a failed check", async () => {
    answer(status({ phase: { state: "failed", message: "offline" } }));
    render(ClientUpdate);
    expect((await screen.findByRole("alert")).textContent).toContain("offline");
  });

  it("saves the channel and the automatic check", async () => {
    answer(status());
    render(ClientUpdate);
    await fireEvent.click(await screen.findByRole("button", { name: "Settings" }));
    await fireEvent.change(screen.getByLabelText("Channel"), { target: { value: "beta" } });
    await fireEvent.click(screen.getByLabelText("Check for updates automatically"));

    invoke.mockImplementation((command: string) =>
      command === "set_update_settings" ? Promise.resolve(status()) : Promise.reject(new Error(command)),
    );
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(invoke).toHaveBeenLastCalledWith("set_update_settings", {
      settings: { channel: "beta", checkAutomatically: false },
    });
  });
});
