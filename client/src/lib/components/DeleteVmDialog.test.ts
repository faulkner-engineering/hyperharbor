import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import DeleteVmDialog from "./DeleteVmDialog.svelte";
import type { HostEntry, Vm, VmDeletePreview } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.2.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm: Vm = {
  id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
  name: "Dev Box",
  state: "off",
  generation: 2,
  rdpAvailable: false,
  ipAddresses: [],
  provisioned: false,
  remoteDesktop: { address: null, reachableFromHost: false },
  guestOs: { family: "windows", name: null },
  performanceMode: false,
};

function preview(overrides: Partial<VmDeletePreview> = {}): VmDeletePreview {
  return {
    vmId: vm.id,
    vmName: "Dev Box",
    state: "off",
    checkpointCount: 0,
    disks: ["C:\\VMs\\Dev Box.vhdx"],
    blockers: [],
    ...overrides,
  };
}

function respond(previewValue: VmDeletePreview) {
  invoke.mockImplementation((command: string) => {
    switch (command) {
      case "get_delete_preview":
        return Promise.resolve(previewValue);
      case "delete_vm":
        return Promise.resolve({
          id: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d",
          kind: "deleteVm",
          vmId: vm.id,
          state: "succeeded",
          step: "Deleting disks",
          percentComplete: 100,
          createdAt: "2026-10-03T12:00:00Z",
          updatedAt: "2026-10-03T12:00:00Z",
          error: null,
        });
      default:
        return Promise.reject(new Error(`unexpected ${command}`));
    }
  });
}

const deleteButton = () => screen.getByRole("button", { name: "Delete" }) as HTMLButtonElement;

async function typeName(name: string) {
  await fireEvent.input(await screen.findByLabelText(/to confirm/), { target: { value: name } });
}

beforeEach(() => {
  invoke.mockReset();
});

describe("DeleteVmDialog", () => {
  it("enables Delete only when the exact name is typed", async () => {
    respond(preview());
    render(DeleteVmDialog, { host, vm, onclose: vi.fn() });

    await typeName("dev box");
    expect(deleteButton().disabled).toBe(true);
    await typeName("Dev Box ");
    expect(deleteButton().disabled).toBe(true);
    await typeName("Dev Box");
    expect(deleteButton().disabled).toBe(false);
  });

  it("sends the choices and shows the finished job", async () => {
    respond(preview());
    const onclose = vi.fn();
    render(DeleteVmDialog, { host, vm, onclose });

    await fireEvent.click(await screen.findByLabelText(/Also delete its virtual hard disk/));
    await typeName("Dev Box");
    await fireEvent.click(deleteButton());

    await screen.findByText("Done.");
    expect(invoke).toHaveBeenCalledWith("delete_vm", {
      key: host.key,
      vmId: vm.id,
      request: { deleteDisks: true, deleteCheckpoints: false, confirmName: "Dev Box" },
    });
    await fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(onclose).toHaveBeenCalledWith(true);
  });

  it("requires checkpoint deletion when the VM has checkpoints", async () => {
    respond(preview({ checkpointCount: 2 }));
    render(DeleteVmDialog, { host, vm, onclose: vi.fn() });

    await typeName("Dev Box");
    expect(deleteButton().disabled).toBe(true);

    await fireEvent.click(screen.getByLabelText(/Delete its 2 checkpoints/));
    expect(deleteButton().disabled).toBe(false);
  });

  it("hides the checkpoint option when there are none", async () => {
    respond(preview());
    render(DeleteVmDialog, { host, vm, onclose: vi.fn() });

    await screen.findByLabelText(/to confirm/);
    expect(screen.queryByLabelText(/checkpoint/)).toBeNull();
  });

  it("blocks disk deletion for a shared disk, but not deleting the VM alone", async () => {
    respond(
      preview({
        blockers: [{ code: "sharedDisk", scope: "deleteDisksOrCheckpoints", message: "Dev Box.vhdx is also used by Web." }],
      }),
    );
    render(DeleteVmDialog, { host, vm, onclose: vi.fn() });

    await typeName("Dev Box");
    expect(deleteButton().disabled).toBe(false);
    expect(screen.queryByText(/also used by Web/)).toBeNull();

    await fireEvent.click(screen.getByLabelText(/Also delete its virtual hard disk/));
    expect(deleteButton().disabled).toBe(true);
    expect(screen.getByText(/also used by Web/)).not.toBeNull();
  });

  it("refuses a running VM", async () => {
    respond(
      preview({
        state: "running",
        blockers: [{ code: "notOff", scope: "always", message: "Dev Box is running. Shut it down or turn it off first." }],
      }),
    );
    render(DeleteVmDialog, { host, vm, onclose: vi.fn() });

    await typeName("Dev Box");

    expect(deleteButton().disabled).toBe(true);
    expect(screen.getByRole("alert").textContent).toContain("is running");
  });
});
