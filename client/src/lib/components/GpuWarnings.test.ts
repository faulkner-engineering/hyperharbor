import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import GpuWarnings from "./GpuWarnings.svelte";
import type { HostEntry } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.8.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

beforeEach(() => {
  invoke.mockReset();
});

describe("GpuWarnings", () => {
  it("summarizes the host's recent GPU driver errors", async () => {
    invoke.mockResolvedValue({
      gpus: [],
      warnings: [
        { provider: "nvlddmkm", eventId: 153, count: 3, lastSeen: "2026-10-04T10:00:00Z", message: "The GPU was reset." },
        { provider: "Display", eventId: 4101, count: 1, lastSeen: "2026-10-03T10:00:00Z", message: "Recovered." },
      ],
    });
    render(GpuWarnings, { host });

    expect(await screen.findByText(/reported 4 errors in the last seven days/)).toBeTruthy();
    expect(screen.getByText(/The GPU was reset\./)).toBeTruthy();
    expect(invoke).toHaveBeenCalledWith("get_host_resource", { key: host.key, resource: "gpu" });
  });

  it("shows nothing without warnings or when the host cannot answer", async () => {
    invoke.mockRejectedValue({ code: "api", message: "Not found.", status: 404, problemCode: null, issues: [] });
    const { container } = render(GpuWarnings, { host });

    await vi.waitFor(() => expect(invoke).toHaveBeenCalled());
    expect(container.querySelector("details")).toBeNull();
  });
});
