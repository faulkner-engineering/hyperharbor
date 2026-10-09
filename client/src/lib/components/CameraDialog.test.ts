import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));

import CameraDialog from "./CameraDialog.svelte";
import type { CameraPrefs, CameraStatus, HostEntry, Vm } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: "8f3b2c1a-0000-4000-8000-000000000001",
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.17.0",
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

const idle: CameraStatus = {
  sharingAvailable: true,
  sharingMessage: null,
  sourceInstalled: true,
  camera: "idle",
  cameraMessage: null,
  device: null,
  localCamera: null,
  sessions: [],
};

function mockClient(
  options: { prefs?: CameraPrefs; status?: CameraStatus | (() => CameraStatus); shutter?: boolean; setup?: () => Promise<void> | void; remove?: () => Promise<void> | void; outdated?: () => boolean } = {},
) {
  invoke.mockImplementation(async (command: string) => {
    if (command === "camera_source_outdated") return options.outdated?.() ?? false;
    if (command === "get_camera_prefs") return options.prefs ?? { share: true, unfocused: "freeze" };
    if (command === "get_camera_status") {
      const status = options.status ?? idle;
      return typeof status === "function" ? status() : status;
    }
    if (command === "get_camera_shutter") return options.shutter ?? false;
    if (command === "set_camera_prefs" || command === "set_camera_shutter") return undefined;
    if (command === "setup_camera_sharing") return options.setup?.();
    if (command === "remove_camera_sharing") return options.remove?.();
    throw new Error(`unexpected command ${command}`);
  });
}

describe("CameraDialog", () => {
  beforeEach(() => {
    invoke.mockReset();
  });

  it("loads the VM's saved choice and saves a changed one", async () => {
    mockClient({ prefs: { share: true, unfocused: "freeze" } });
    const onclose = vi.fn();
    render(CameraDialog, { host, vm, onclose });

    const blur = (await screen.findByLabelText(/Blur the picture/)) as HTMLInputElement;
    expect((screen.getByLabelText(/Freeze the picture/) as HTMLInputElement).checked).toBe(true);
    await fireEvent.click(blur);
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(invoke).toHaveBeenCalledWith("get_camera_prefs", { key: host.key, vmId: vm.id });
    expect(invoke).toHaveBeenCalledWith("set_camera_prefs", {
      key: host.key,
      vmId: vm.id,
      prefs: { share: true, unfocused: "blur" },
    });
    expect(onclose).toHaveBeenCalled();
  });

  it("can stop sharing the camera with the VM, which disables the picture choices", async () => {
    mockClient();
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByLabelText("Share my camera with this VM"));

    expect(screen.getByLabelText(/Keep it live/).matches(":disabled")).toBe(true);
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(invoke).toHaveBeenCalledWith("set_camera_prefs", {
      key: host.key,
      vmId: vm.id,
      prefs: { share: false, unfocused: "freeze" },
    });
  });

  it("closes and opens the privacy shutter at once, without saving", async () => {
    mockClient();
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    const shutter = await screen.findByRole("button", { name: "Close shutter" });
    await fireEvent.click(shutter);

    expect(invoke).toHaveBeenCalledWith("set_camera_shutter", { key: host.key, vmId: vm.id, closed: true });
    const open = await screen.findByRole("button", { name: "Open shutter" });
    expect(open.getAttribute("aria-pressed")).toBe("true");
    expect(invoke).not.toHaveBeenCalledWith("set_camera_prefs", expect.anything());
  });

  it("shows a shutter that was closed earlier as closed", async () => {
    mockClient({ shutter: true });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByRole("button", { name: "Open shutter" })).toBeTruthy();
  });

  it("tells Windows 10 users that sharing needs Windows 11 and disables what it cannot do", async () => {
    mockClient({
      status: {
        ...idle,
        sharingAvailable: false,
        sharingMessage: "Camera sharing requires Windows 11 on this device. Only one VM at a time can use the camera here.",
      },
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/requires Windows 11 on this device/)).toBeTruthy();
    expect(screen.getByLabelText(/Blur the picture/).matches(":disabled")).toBe(true);
    expect((screen.getByRole("button", { name: "Close shutter" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("offers one-time setup when the camera source is not installed, and refreshes after it", async () => {
    let installed = false;
    mockClient({
      status: () => ({ ...idle, sourceInstalled: installed }),
      setup: () => {
        installed = true;
      },
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/not set up on this device yet/)).toBeTruthy();
    expect((screen.getByLabelText(/Blur the picture/) as HTMLInputElement).matches(":disabled")).toBe(true);
    await fireEvent.click(screen.getByRole("button", { name: "Set up camera sharing" }));

    expect(invoke).toHaveBeenCalledWith("setup_camera_sharing");
    await vi.waitFor(() => expect(screen.queryByText(/not set up on this device yet/)).toBeNull());
    expect((screen.getByLabelText(/Blur the picture/) as HTMLInputElement).matches(":disabled")).toBe(false);
  });

  it("offers to update an installed camera component that is older than the one shipped, once", async () => {
    let outdated = true;
    mockClient({
      outdated: () => outdated,
      setup: () => {
        outdated = false;
      },
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: "Update camera component" }));

    expect(invoke).toHaveBeenCalledWith("setup_camera_sharing");
    await vi.waitFor(() => expect(screen.queryByRole("button", { name: "Update camera component" })).toBeNull());
  });

  it("shows no update notice when the camera component is current", async () => {
    mockClient();
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    await screen.findByLabelText(/Blur the picture/);
    expect(screen.queryByRole("button", { name: "Update camera component" })).toBeNull();
  });

  it("shows why setup failed, for example when the permission prompt was declined", async () => {
    mockClient({
      status: { ...idle, sourceInstalled: false },
      setup: () => Promise.reject({ code: "rdpFailed", message: "Setup was cancelled.", status: null }),
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: "Set up camera sharing" }));

    expect(await screen.findByText("Setup was cancelled.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Set up camera sharing" })).toBeTruthy();
  });

  it("does not offer setup on Windows 10, where the camera is not shared at all", async () => {
    mockClient({
      status: { ...idle, sharingAvailable: false, sourceInstalled: false, sharingMessage: "Camera sharing requires Windows 11 on this device." },
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/requires Windows 11/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Set up camera sharing" })).toBeNull();
  });

  it("removes camera sharing only after a confirmation", async () => {
    let installed = true;
    mockClient({
      status: () => ({ ...idle, sourceInstalled: installed }),
      remove: () => {
        installed = false;
      },
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: /Remove camera sharing from this device/ }));
    expect(invoke).not.toHaveBeenCalledWith("remove_camera_sharing");
    expect(screen.getByText(/lose their camera until you set sharing up again/)).toBeTruthy();

    await fireEvent.click(screen.getByRole("button", { name: "Remove" }));

    expect(invoke).toHaveBeenCalledWith("remove_camera_sharing");
    expect(await screen.findByRole("button", { name: "Set up camera sharing" })).toBeTruthy();
  });

  it("keeps camera sharing when the confirmation is declined", async () => {
    mockClient();
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: /Remove camera sharing from this device/ }));
    await fireEvent.click(screen.getByRole("button", { name: "Keep it" }));

    expect(invoke).not.toHaveBeenCalledWith("remove_camera_sharing");
    expect(screen.getByRole("button", { name: /Remove camera sharing from this device/ })).toBeTruthy();
  });

  it("explains the camera for this PC when no VM is connected", async () => {
    mockClient();
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/HyperHarbor Camera \(This PC\)/)).toBeTruthy();
    expect(screen.getByText(/Windows has no default camera/)).toBeTruthy();
  });

  it("names the camera for this PC when it exists", async () => {
    mockClient({ status: { ...idle, camera: "active", localCamera: "HyperHarbor Camera (This PC)" } });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/Apps on this PC can use "HyperHarbor Camera \(This PC\)" now/)).toBeTruthy();
  });

  it("says when another application is using the camera", async () => {
    mockClient({
      status: {
        ...idle,
        camera: "busy",
        cameraMessage: "Another app is using your camera. Close it to share the camera with your VMs.",
      },
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("Another app is using your camera");
  });

  it("describes what this VM sees right now when it has an open session", async () => {
    mockClient({
      status: {
        ...idle,
        camera: "active",
        sessions: [
          {
            vmKey: `${host.hostId}/${vm.id}`,
            vmName: vm.name,
            shared: true,
            mode: "blurred",
            shutter: false,
            focused: false,
          },
        ],
      },
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/Right now this VM sees a blurred picture/)).toBeTruthy();
  });

  it("says so when the VM has the physical camera directly", async () => {
    mockClient({
      status: {
        ...idle,
        sessions: [
          { vmKey: `${host.hostId}/${vm.id}`, vmName: vm.name, shared: false, mode: null, shutter: false, focused: false },
        ],
      },
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText(/only one VM can use it at a time/)).toBeTruthy();
  });

  it("shows an error when the settings cannot be read", async () => {
    invoke.mockImplementation(async (command: string) => {
      if (command === "get_camera_status") return idle;
      throw { code: "rdpFailed", message: "The settings file is locked.", status: null };
    });
    render(CameraDialog, { host, vm, onclose: vi.fn() });

    expect(await screen.findByText("The settings file is locked.")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
  });
});
