import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import TailscalePanel from "./TailscalePanel.svelte";
import type { HostEntry } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: "ID-1",
  hostName: "TC-PC",
  addresses: ["192.168.0.235", "100.64.0.1"],
  port: 48443,
  apiVersion: "1.23.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: true,
};

const status = {
  state: "running",
  self: { nodeId: "n1", hostName: "LAPTOP", dnsName: "laptop.tail1.ts.net", addresses: ["100.64.0.9"], tags: [] },
  magicDnsSuffix: "tail1.ts.net",
  peers: [],
  error: null,
};

const hostInfo = {
  state: "running",
  ipv4: "100.64.0.1",
  dnsName: "tc-pc.tail1.ts.net",
  tagged: false,
  tag: "tag:hyperharbor-host",
  upCommand: "tailscale up --advertise-tags=tag:hyperharbor-host",
  config: { localApiPort: 41112, controlUrl: null },
};

function answer(overrides: Record<string, unknown> = {}) {
  const table: Record<string, unknown> = {
    get_tailnet_status: status,
    get_tailnet_settings: { localApiPort: 41112 },
    get_connection_route: {
      via: "tailscale",
      address: "100.64.0.1",
      path: "relayed",
      relay: "fra",
      hint: "This connection goes through a Tailscale relay. Use split tunneling.",
    },
    get_host_resource: hostInfo,
    ...overrides,
  };
  invoke.mockImplementation((command: string) => {
    if (!(command in table)) return Promise.reject(new Error(`unexpected ${command}`));
    const value = table[command];
    return value instanceof Error ? Promise.reject(value) : Promise.resolve(value);
  });
}

beforeEach(() => {
  invoke.mockReset();
});

describe("TailscalePanel", () => {
  it("shows this PC, the host, and a relayed connection with its hint", async () => {
    answer();
    render(TailscalePanel, { host, hostSupportsTailscale: true });

    expect(await screen.findByText(/Connected as laptop\.tail1\.ts\.net/)).toBeTruthy();
    expect(await screen.findByText(/but not tagged tag:hyperharbor-host/)).toBeTruthy();
    expect(screen.getByText(/Tailscale, relayed through fra/)).toBeTruthy();
    expect(screen.getByText(/Use split tunneling/)).toBeTruthy();
    expect(invoke).toHaveBeenCalledWith("get_host_resource", { key: host.key, resource: "tailscale" });
  });

  it("offers the tag command when the host is not tagged", async () => {
    answer();
    render(TailscalePanel, { host, hostSupportsTailscale: true });

    expect(await screen.findByText("tailscale up --advertise-tags=tag:hyperharbor-host")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Copy command" })).toBeTruthy();
  });

  it("does not ask an older host for its Tailscale state", async () => {
    answer();
    render(TailscalePanel, { host, hostSupportsTailscale: false });

    expect(await screen.findByText(/older HyperHarbor version/)).toBeTruthy();
    expect(invoke).not.toHaveBeenCalledWith("get_host_resource", expect.anything());
  });

  it("runs the reachability test and lists each step", async () => {
    answer({
      test_tailscale_reachability: [
        { id: "tailscale", title: "Tailscale on this PC", status: "pass", detail: null },
        { id: "peer", title: "Host on the tailnet", status: "fail", detail: "Tailscale on this PC does not list this host." },
      ],
    });
    render(TailscalePanel, { host, hostSupportsTailscale: true });
    await screen.findByText(/Connected as laptop/);

    await fireEvent.click(screen.getByRole("button", { name: "Test reachability" }));

    expect(await screen.findByText("Not reachable: host on the tailnet failed.")).toBeTruthy();
    expect(screen.getByText(/does not list this host/)).toBeTruthy();
    expect(invoke).toHaveBeenCalledWith("test_tailscale_reachability", { key: host.key });
  });

  it("shows a load failure instead of an empty panel", async () => {
    answer({ get_tailnet_status: new Error("boom") });
    render(TailscalePanel, { host, hostSupportsTailscale: true });

    expect(await screen.findByRole("alert")).toBeTruthy();
  });
});
