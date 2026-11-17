import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import AdminCredentialDialog from "./AdminCredentialDialog.svelte";
import { elevation } from "#lib/lifecycle.svelte.ts";
import type { HostEntry, Vm, VmAdminCredential } from "#lib/api/client.ts";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.40.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm = (overrides: Partial<Vm> = {}): Vm => ({
  id: "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b",
  name: "Dev Box",
  state: "running",
  generation: 2,
  rdpAvailable: false,
  ipAddresses: [],
  provisioned: true,
  remoteDesktop: { address: null, reachableFromHost: false },
  guestOs: { family: "windows", name: "Windows 11 Pro" },
  performanceMode: false,
  ...overrides,
});

const credential = (overrides: Partial<VmAdminCredential> = {}): VmAdminCredential => ({
  userName: "hhadmin",
  managed: true,
  state: "working",
  checkedAt: "2026-11-17T09:13:00Z",
  passwordExpiresAt: null,
  detail: null,
  ...overrides,
});

const rejected = credential({ state: "rejected", passwordExpiresAt: "2026-11-16T22:00:00Z", detail: "The credential is invalid." });
const calls = (command: string) => invoke.mock.calls.filter(([name]) => name === command);

/** Answers each command with the given value, or with the next one of an array. */
function answer(replies: Record<string, unknown>) {
  invoke.mockImplementation((command: string) => {
    if (!(command in replies)) return Promise.reject(new Error(`unexpected ${command}`));
    const reply = replies[command];
    const next = Array.isArray(reply) ? reply.shift() : reply;
    return next instanceof Error || (typeof next === "object" && next !== null && "code" in next) ? Promise.reject(next) : Promise.resolve(next);
  });
}

beforeEach(() => {
  invoke.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
  elevation.finish(false);
});

describe("AdminCredentialDialog", () => {
  it("shows the stored account as the host last saw it, without asking the VM", async () => {
    answer({ get_vm_admin_credential: credential() });
    render(AdminCredentialDialog, { host, vm: vm(), onclose: vi.fn() });

    await screen.findByText("hhadmin");
    screen.getByText(/Dev Box accepted it when HyperHarbor last signed in/);
    screen.getByText("Its password does not expire.");
    expect(screen.queryByLabelText("Password")).toBeNull();
    expect(screen.queryByRole("button", { name: "Stop the password from expiring" })).toBeNull();
    expect(invoke.mock.calls).toEqual([["get_vm_admin_credential", { key: "mdns:host", vmId: vm().id }]]);
  });

  it("says a rejected account needs replacing, shows the form at once, and stores the working password", async () => {
    answer({ get_vm_admin_credential: rejected, set_vm_admin_credential: credential({ checkedAt: "2026-11-17T12:00:00Z" }) });
    const onclose = vi.fn();
    render(AdminCredentialDialog, { host, vm: vm(), onclose });

    await screen.findByText(/Dev Box did not accept it when HyperHarbor last tried/);
    screen.getByText("The credential is invalid.");
    screen.getByText(/Connect still works with your last password/);
    screen.getByText(/recover-vm-administrator\.md/);
    expect((screen.getByLabelText("Administrator user name") as HTMLInputElement).value).toBe("hhadmin");
    // HyperHarbor made this account and keeps it from expiring itself, so there is no choice to offer.
    expect(screen.queryByLabelText(/Stop this account's password from expiring/)).toBeNull();
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);

    await fireEvent.input(screen.getByLabelText("Password"), { target: { value: "Typed-Secret2!" } });
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await screen.findByText("HyperHarbor now signs in to Dev Box as hhadmin.");
    expect(calls("set_vm_admin_credential")).toEqual([
      ["set_vm_admin_credential", { key: "mdns:host", vmId: vm().id, userName: "hhadmin", password: "Typed-Secret2!", passwordNeverExpires: false }],
    ]);
    expect(screen.queryByLabelText("Password")).toBeNull();
    screen.getByText(/Dev Box accepted it when HyperHarbor last signed in/);

    await fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(onclose).toHaveBeenCalledWith(true);
  });

  it("keeps the typed password and says the VM refused it when the new one does not work either", async () => {
    answer({
      get_vm_admin_credential: [rejected, rejected],
      set_vm_admin_credential: { code: "api", message: "The credential is invalid.", status: 422, problemCode: "adminCredentialRejected", issues: [] },
    });
    render(AdminCredentialDialog, { host, vm: vm(), onclose: vi.fn() });

    await fireEvent.input(await screen.findByLabelText("Password"), { target: { value: "Wrong-Pass1!" } });
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await screen.findByText("Dev Box did not accept that user name and password. Nothing was changed. (The credential is invalid.)");
    expect((screen.getByLabelText("Password") as HTMLInputElement).value).toBe("Wrong-Pass1!");
  });

  it("offers to stop the password of the user's own account from expiring, with the stored password", async () => {
    const own = credential({ userName: "Tyler", managed: false, passwordExpiresAt: "2026-12-04T22:06:00Z" });
    answer({ get_vm_admin_credential: own, check_vm_admin_credential: { ...own, passwordExpiresAt: null } });
    render(AdminCredentialDialog, { host, vm: vm(), onclose: vi.fn() });

    await screen.findByText(/Its password expires on December 4, 2026/);
    screen.getByText(/You gave HyperHarbor this account/);
    await fireEvent.click(screen.getByRole("button", { name: "Stop the password from expiring" }));

    await screen.findByText("The password of Tyler no longer expires.");
    expect(calls("check_vm_admin_credential")).toEqual([["check_vm_admin_credential", { key: "mdns:host", vmId: vm().id, passwordNeverExpires: true }]]);
    screen.getByText("Its password does not expire.");
    expect(screen.queryByRole("button", { name: "Stop the password from expiring" })).toBeNull();
  });

  it("checks the stored account on request, and a refusal opens the form", async () => {
    answer({ get_vm_admin_credential: credential({ state: "unchecked", checkedAt: null }), check_vm_admin_credential: rejected });
    render(AdminCredentialDialog, { host, vm: vm(), onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: "Check now" }));

    await screen.findByText(/Dev Box did not accept it when HyperHarbor last tried/);
    expect(calls("check_vm_admin_credential")).toEqual([["check_vm_admin_credential", { key: "mdns:host", vmId: vm().id, passwordNeverExpires: false }]]);
    screen.getByLabelText("Password");
  });

  it("lets the user choose to stop expiry when entering another account", async () => {
    answer({ get_vm_admin_credential: credential(), set_vm_admin_credential: credential({ userName: "Tyler", managed: false }) });
    render(AdminCredentialDialog, { host, vm: vm(), onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: "Change…" }));
    await fireEvent.input(screen.getByLabelText("Administrator user name"), { target: { value: "Tyler" } });
    await fireEvent.input(screen.getByLabelText("Password"), { target: { value: "Typed-Secret2!" } });
    await fireEvent.click(screen.getByLabelText(/Stop this account's password from expiring/));
    await fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await screen.findByText("HyperHarbor now signs in to Dev Box as Tyler.");
    expect(calls("set_vm_admin_credential")[0][1]).toMatchObject({ userName: "Tyler", passwordNeverExpires: true });
  });

  it("does not offer the expiry choice for a Linux VM", async () => {
    answer({ get_vm_admin_credential: rejected });
    render(AdminCredentialDialog, { host, vm: vm({ guestOs: { family: "linux", name: "Ubuntu 24.04" } }), onclose: vi.fn() });

    await screen.findByLabelText("User name (with sudo)");
    expect(screen.queryByLabelText(/Stop this account's password from expiring/)).toBeNull();
  });

  it("asks for the VM to be started before it checks or saves", async () => {
    answer({ get_vm_admin_credential: rejected });
    render(AdminCredentialDialog, { host, vm: vm({ state: "off" }), onclose: vi.fn() });

    await screen.findByText("Start Dev Box to check the account or change what HyperHarbor has stored.");
    await fireEvent.input(screen.getByLabelText("Password"), { target: { value: "Typed-Secret2!" } });
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Check now" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("points to Set up when the host has no account for the VM", async () => {
    answer({ get_vm_admin_credential: credential({ userName: null, managed: false, state: "unchecked", checkedAt: null }) });
    render(AdminCredentialDialog, { host, vm: vm(), onclose: vi.fn() });

    await screen.findByText(/HyperHarbor has no administrator account stored for Dev Box/);
    expect(screen.queryByRole("button", { name: "Check now" })).toBeNull();
  });

  it("says a check takes long, closes at once while the host is still answering, and ignores the late answer", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let late: (value: unknown) => void = () => {};
    invoke.mockImplementation((command: string) =>
      command === "get_vm_admin_credential" ? Promise.resolve(credential()) : new Promise((resolve) => (late = resolve)),
    );
    const onclose = vi.fn();
    render(AdminCredentialDialog, { host, vm: vm(), onclose });

    await fireEvent.click(await screen.findByRole("button", { name: "Check now" }));
    await screen.findByRole("button", { name: "Checking…" });
    await vi.advanceTimersByTimeAsync(20_000);
    await screen.findByText(/This is taking longer than usual/);

    await fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(onclose).toHaveBeenCalledWith(false);

    late(rejected);
    await Promise.resolve();
    expect(screen.queryByText(/did not accept it/)).toBeNull();
  });

  it("leaves everything as it was when the passphrase prompt is cancelled", async () => {
    answer({
      get_vm_admin_credential: credential({ state: "unchecked", checkedAt: null }),
      check_vm_admin_credential: { code: "api", message: "Enter the passphrase.", status: 403, problemCode: "elevationRequired", issues: [] },
    });
    render(AdminCredentialDialog, { host, vm: vm(), onclose: vi.fn() });

    await fireEvent.click(await screen.findByRole("button", { name: "Check now" }));
    await waitFor(() => expect(elevation.pending).not.toBeNull());
    elevation.finish(false);

    await screen.findByRole("button", { name: "Check now" });
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
