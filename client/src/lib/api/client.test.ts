import { beforeEach, describe, expect, it, vi } from "vitest";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import { connectVm, errorMessage, isClientError, isOffline, openConsole, provisionVm } from "./client";

beforeEach(() => {
  invoke.mockReset();
  invoke.mockResolvedValue(undefined);
});

describe("errors", () => {
  const offline = { code: "unreachable", message: "The host could not be reached: refused", status: null };

  it("recognizes ClientError values from Tauri commands", () => {
    expect(isClientError(offline)).toBe(true);
    expect(isClientError({ message: "no code" })).toBe(false);
    expect(isClientError(null)).toBe(false);
    expect(isClientError("text")).toBe(false);
  });

  it("shows the message of any error shape", () => {
    expect(errorMessage(offline)).toBe(offline.message);
    expect(errorMessage(new Error("boom"))).toBe("boom");
    expect(errorMessage("plain")).toBe("plain");
  });

  it("treats only unreachable as offline", () => {
    expect(isOffline(offline)).toBe(true);
    expect(isOffline({ ...offline, code: "api", status: 503 })).toBe(false);
    expect(isOffline(new Error("unreachable"))).toBe(false);
  });
});

describe("commands", () => {
  it("provisionVm sends the options the Rust command deserializes", async () => {
    await provisionVm("mdns:host", "vm-1", "hhadmin", "Adm1n!", {
      enableRemoteDesktop: true,
      installDesktop: false,
      trustNewHostKey: true,
    });

    expect(invoke).toHaveBeenCalledWith("provision_vm", {
      key: "mdns:host",
      vmId: "vm-1",
      adminUserName: "hhadmin",
      adminPassword: "Adm1n!",
      options: { enableRemoteDesktop: true, installDesktop: false, trustNewHostKey: true },
    });
  });

  it("connectVm passes the address to probe", async () => {
    await connectVm("mdns:host", "vm-1", "192.168.0.50");

    expect(invoke).toHaveBeenCalledWith("connect_vm", { key: "mdns:host", vmId: "vm-1", address: "192.168.0.50" });
  });

  it("openConsole passes the host key and VM", async () => {
    await openConsole("mdns:host", "vm-1");

    expect(invoke).toHaveBeenCalledWith("open_console", { key: "mdns:host", vmId: "vm-1" });
  });
});
