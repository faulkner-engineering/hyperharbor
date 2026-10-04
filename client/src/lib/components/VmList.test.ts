import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

import VmList from "./VmList.svelte";
import type { Vm, VmState } from "$lib/api/client";

function vm(state: VmState, overrides: Partial<Vm> = {}): Vm {
  return {
    id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
    name: "Dev Box",
    state,
    generation: 2,
    rdpAvailable: false,
    ipAddresses: [],
    provisioned: false,
    remoteDesktop: { address: null, reachableFromHost: false },
    guestOs: { family: "unknown", name: null },
    ...overrides,
  };
}

describe("VmList console button", () => {
  it("is offered for a running VM that is not set up, and opens that VM's console", async () => {
    const onconsole = vi.fn();
    const running = vm("running");
    render(VmList, { vms: [running], onconsole });

    await fireEvent.click(screen.getByRole("button", { name: "Console" }));

    expect(onconsole).toHaveBeenCalledWith(running);
  });

  it("is offered for a paused VM", () => {
    render(VmList, { vms: [vm("paused")] });

    expect(screen.getByRole("button", { name: "Console" })).toBeTruthy();
  });

  it.each<VmState>(["off", "saved"])("is not offered while the VM is %s", (state) => {
    render(VmList, { vms: [vm(state)] });

    expect(screen.queryByRole("button", { name: "Console" })).toBeNull();
  });

  it("shows progress and disables every Console button while one opens", () => {
    const first = vm("running");
    const second = vm("running", { id: "1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f", name: "Other" });
    render(VmList, { vms: [first, second], consoleVmId: first.id });

    expect((screen.getByRole("button", { name: "Opening…" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Console" }) as HTMLButtonElement).disabled).toBe(true);
  });
});
