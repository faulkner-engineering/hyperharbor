import { beforeEach, describe, expect, it, vi } from "vitest";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import {
  connectVm,
  downloadHostLogs,
  errorMessage,
  getCameraPrefs,
  getCameraStatus,
  isClientError,
  isOffline,
  openConsole,
  provisionVm,
  setCameraPrefs,
  setCameraShutter,
} from "./client";

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

  it("connectVm passes the address to probe and the VM's name for its camera", async () => {
    await connectVm("mdns:host", "vm-1", "192.168.0.50", "Work");

    expect(invoke).toHaveBeenCalledWith("connect_vm", {
      key: "mdns:host",
      vmId: "vm-1",
      address: "192.168.0.50",
      vmName: "Work",
    });
  });

  it("connectVm returns the notice the client has for the user", async () => {
    invoke.mockResolvedValue("Camera sharing requires Windows 11 on this device.");

    expect(await connectVm("mdns:host", "vm-1", "192.168.0.50", "Work")).toBe(
      "Camera sharing requires Windows 11 on this device.",
    );
  });

  it("camera calls name the host key and VM", async () => {
    await getCameraPrefs("mdns:host", "vm-1");
    await setCameraPrefs("mdns:host", "vm-1", { share: true, unfocused: "blur" });
    await setCameraShutter("mdns:host", "vm-1", true);
    await getCameraStatus();

    expect(invoke).toHaveBeenCalledWith("get_camera_prefs", { key: "mdns:host", vmId: "vm-1" });
    expect(invoke).toHaveBeenCalledWith("set_camera_prefs", {
      key: "mdns:host",
      vmId: "vm-1",
      prefs: { share: true, unfocused: "blur" },
    });
    expect(invoke).toHaveBeenCalledWith("set_camera_shutter", { key: "mdns:host", vmId: "vm-1", closed: true });
    expect(invoke).toHaveBeenCalledWith("get_camera_status");
  });

  it("openConsole passes the host key and VM", async () => {
    await openConsole("mdns:host", "vm-1");

    expect(invoke).toHaveBeenCalledWith("open_console", { key: "mdns:host", vmId: "vm-1" });
  });
});

describe("downloadHostLogs", () => {
  it("asks the Tauri side to download and save the host logs, and returns the saved path", async () => {
    invoke.mockResolvedValue("C:\Users\me\hyperharbor-logs.zip");

    const path = await downloadHostLogs("mdns:host");

    expect(path).toBe("C:\Users\me\hyperharbor-logs.zip");
    expect(invoke).toHaveBeenCalledWith("download_host_logs", { key: "mdns:host" });
  });

  it("returns null when the save dialog is cancelled", async () => {
    invoke.mockResolvedValue(null);

    expect(await downloadHostLogs("mdns:host")).toBeNull();
  });
});
