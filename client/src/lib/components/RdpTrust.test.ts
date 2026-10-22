import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));

import RdpTrust from "./RdpTrust.svelte";

type State = "unsupported" | "notSetUp" | "ready";

function respond(initial: State, after: Partial<Record<string, () => Promise<unknown>>> = {}) {
  invoke.mockImplementation((command: string) => {
    if (command === "rdp_trust_status") return Promise.resolve({ state: initial });
    const handler = after[command];
    return handler ? handler() : Promise.reject(new Error(`unexpected ${command}`));
  });
}

beforeEach(() => {
  invoke.mockReset();
});

describe("RdpTrust", () => {
  it("offers to trust HyperHarbor's connections when they are not trusted yet", async () => {
    respond("notSetUp", { set_up_rdp_trust: () => Promise.resolve({ state: "ready" }) });
    render(RdpTrust);

    await fireEvent.click(await screen.findByRole("button", { name: "Trust HyperHarbor connections" }));

    await screen.findByText("Remote Desktop opens HyperHarbor's connections without asking.");
    expect(invoke).toHaveBeenCalledWith("set_up_rdp_trust");
  });

  it("shows why setup did not finish", async () => {
    respond("notSetUp", { set_up_rdp_trust: () => Promise.reject(new Error("Setup was cancelled.")) });
    render(RdpTrust);

    await fireEvent.click(await screen.findByRole("button", { name: "Trust HyperHarbor connections" }));

    expect((await screen.findByRole("alert")).textContent).toContain("Setup was cancelled.");
    expect(screen.getByRole("button", { name: "Trust HyperHarbor connections" })).not.toBeNull();
  });

  it("says so when the approval went through but Windows still does not trust them", async () => {
    respond("notSetUp", { set_up_rdp_trust: () => Promise.resolve({ state: "notSetUp" }) });
    render(RdpTrust);

    await fireEvent.click(await screen.findByRole("button", { name: "Trust HyperHarbor connections" }));

    expect((await screen.findByRole("alert")).textContent).toContain("policy on this PC");
  });

  it("can stop trusting them", async () => {
    respond("ready", { remove_rdp_trust: () => Promise.resolve({ state: "notSetUp" }) });
    render(RdpTrust);

    await fireEvent.click(await screen.findByRole("button", { name: "Stop trusting them" }));

    await screen.findByRole("button", { name: "Trust HyperHarbor connections" });
    expect(invoke).toHaveBeenCalledWith("remove_rdp_trust");
  });

  it("shows nothing where files cannot be signed", async () => {
    respond("unsupported");
    const { container } = render(RdpTrust);
    await vi.waitFor(() => expect(invoke).toHaveBeenCalledWith("rdp_trust_status"));
    await Promise.resolve();
    expect(container.querySelector("section")).toBeNull();
  });
});
