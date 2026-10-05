import { beforeEach, describe, expect, it, vi } from "vitest";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import { openHostRemoteDesktop } from "./hostRemoteDesktop";
import { elevation } from "./lifecycle.svelte";
import type { HostRemoteDesktop } from "$lib/api/client";

const on: HostRemoteDesktop = { supported: true, enabled: true, port: 3389, firewallOpen: true, edition: "Windows 11 Pro" };
const off: HostRemoteDesktop = { ...on, enabled: false, firewallOpen: false };

/** Answers each command from `responses`; records the order of calls. */
function host(responses: Record<string, unknown>) {
  invoke.mockImplementation(async (command: string, args: { resource?: string }) => {
    const name = command === "get_host_resource" ? `get:${args.resource}` : command;
    if (!(name in responses)) throw new Error(`unexpected ${name}`);
    const response = responses[name];
    if (response instanceof Error || (typeof response === "object" && response && "problemCode" in response)) throw response;
    return response;
  });
}

const calls = () => invoke.mock.calls.map(([command]) => command);

beforeEach(() => {
  invoke.mockReset();
  elevation.finish(false);
});

describe("openHostRemoteDesktop", () => {
  it("connects at once when the host accepts Remote Desktop", async () => {
    host({ "get:remoteDesktop": on, connect_host: undefined });
    const confirm = vi.fn();

    const result = await openHostRemoteDesktop("h1", confirm);

    expect(result.outcome).toBe("opened");
    expect(confirm).not.toHaveBeenCalled();
    expect(calls()).toEqual(["get_host_resource", "connect_host"]);
  });

  it("asks before turning Remote Desktop on, then connects", async () => {
    host({ "get:remoteDesktop": off, enable_host_remote_desktop: on, connect_host: undefined });

    const result = await openHostRemoteDesktop("h1", async () => true);

    expect(result.outcome).toBe("opened");
    expect(calls()).toEqual(["get_host_resource", "enable_host_remote_desktop", "connect_host"]);
  });

  it("changes nothing when the user declines", async () => {
    host({ "get:remoteDesktop": off });

    const result = await openHostRemoteDesktop("h1", async () => false);

    expect(result.outcome).toBe("declined");
    expect(calls()).toEqual(["get_host_resource"]);
  });

  it("reports an edition without Remote Desktop", async () => {
    host({ "get:remoteDesktop": { ...off, supported: false, edition: "Windows 11 Home" } });
    const confirm = vi.fn();

    const result = await openHostRemoteDesktop("h1", confirm);

    expect(result.outcome).toBe("unsupported");
    expect(result.state.edition).toBe("Windows 11 Home");
    expect(confirm).not.toHaveBeenCalled();
  });
});
