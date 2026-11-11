import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));

import HostUsage from "./HostUsage.svelte";
import type { HostEntry, HostResources } from "#lib/api/client.ts";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.38.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const resources = (overrides: Partial<HostResources> = {}): HostResources => ({
  logicalProcessorCount: 16,
  physicalCoreCount: 8,
  cpuUsagePercent: 23.4,
  totalMemoryMb: 32768,
  availableMemoryMb: 20480,
  memoryReserveMb: 4096,
  virtualHardDiskFolder: "C:\\VMs",
  isoFolder: "C:\\ISOs",
  ...overrides,
});

describe("HostUsage", () => {
  beforeEach(() => {
    invoke.mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("shows processor use, cores, and memory in use", async () => {
    invoke.mockResolvedValue(resources());
    render(HostUsage, { host });

    expect(await screen.findByText("23%")).toBeTruthy();
    expect(screen.getByText("8 cores, 16 logical processors")).toBeTruthy();
    expect(screen.getByText("38%")).toBeTruthy();
    expect(screen.getByText(/12 GB of 32 GB in use, 20 GB free/)).toBeTruthy();
    expect(screen.getByLabelText("Processor use").getAttribute("value")).toBe("23.4");
    expect(invoke).toHaveBeenCalledWith("get_host_resource", { key: "mdns:host", resource: "resources" });
  });

  it("shows only what a host before API 1.38.0 reports", async () => {
    invoke.mockResolvedValue(resources({ physicalCoreCount: undefined, cpuUsagePercent: undefined }));
    render(HostUsage, { host });

    expect(await screen.findByText("16 logical processors")).toBeTruthy();
    expect(screen.queryByLabelText("Processor use")).toBeNull();
    expect(screen.getByLabelText("Memory use")).toBeTruthy();
  });

  it("says when free memory is below the host's reserve", async () => {
    invoke.mockResolvedValue(resources({ availableMemoryMb: 2048 }));
    render(HostUsage, { host });

    expect(await screen.findByText(/below the 4 GB the host keeps for itself/)).toBeTruthy();
  });

  it("refreshes the figures while it is shown", async () => {
    vi.useFakeTimers();
    invoke.mockResolvedValueOnce(resources()).mockResolvedValue(resources({ cpuUsagePercent: 81 }));
    render(HostUsage, { host });
    await vi.advanceTimersByTimeAsync(0);
    expect(screen.getByText("23%")).toBeTruthy();

    await vi.advanceTimersByTimeAsync(5_000);

    expect(screen.getByText("81%")).toBeTruthy();
  });

  it("shows nothing when the host cannot answer", async () => {
    invoke.mockRejectedValue({ kind: "network", message: "unreachable" });
    const { container } = render(HostUsage, { host });
    await vi.waitFor(() => expect(invoke).toHaveBeenCalled());

    expect(container.querySelector("section")).toBeNull();
  });
});
