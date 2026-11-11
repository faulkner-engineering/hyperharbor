import { describe, expect, it } from "vitest";
import type { ClientError } from "@hyperharbor/ui/api/model.ts";
import type { HostMessage, PageMessage } from "./bridge";
import { createTrayHostApi } from "./hostApi";

/** The tray's HostApi on a fake bridge: what it sends, and a way to answer as the tray. */
function harness() {
  const sent: Extract<PageMessage, { type: "apiRequest" }>[] = [];
  let deliver: (message: HostMessage) => void = () => {};
  const api = createTrayHostApi(
    (message) => {
      if (message.type === "apiRequest") sent.push(message);
    },
    (handler) => (deliver = handler),
  );
  const answer = (index: number, status: number, body?: unknown, error?: string) =>
    deliver({
      type: "apiResponse",
      id: sent[index].id,
      status,
      body: body === undefined ? undefined : JSON.stringify(body),
      error,
    });
  return { api, sent, answer };
}

describe("tray HostApi", () => {
  it("sends each call as an apiRequest and resolves with the answer's body", async () => {
    const { api, sent, answer } = harness();

    const vms = api.listVms("local");
    answer(0, 200, [{ id: "a" }]);

    expect(sent[0]).toMatchObject({ type: "apiRequest", method: "GET", path: "/vms" });
    expect(sent[0]).not.toHaveProperty("body");
    expect(await vms).toEqual([{ id: "a" }]);
  });

  it("matches answers to requests by id", async () => {
    const { api, answer } = harness();

    const first = api.getVmCompute("local", "vm-1");
    const second = api.getVmCompute("local", "vm-2");
    answer(1, 200, { processorCount: 2 });
    answer(0, 200, { processorCount: 8 });

    expect(await first).toEqual({ processorCount: 8 });
    expect(await second).toEqual({ processorCount: 2 });
  });

  it("sends the request bodies the API expects", () => {
    const { api, sent } = harness();

    void api.performVmAction("local", "vm-1", "shutdown");
    void api.updateVmCompute("local", "vm-1", { processorCount: 4, shutDownToApply: true });
    void api.growVmDisk("local", "vm-1", { path: "D:\\VMs\\a.vhdx", sizeGb: 64 });
    void api.deleteVm("local", "vm-1", { deleteDisks: true, deleteCheckpoints: false, confirmName: "Dev" });
    void api.elevate("local", "correct horse");

    expect(sent.map((request) => [request.method, request.path, request.body])).toEqual([
      ["POST", "/vms/vm-1/actions", { action: "shutdown" }],
      ["PATCH", "/vms/vm-1/compute", { processorCount: 4, shutDownToApply: true }],
      ["POST", "/vms/vm-1/disks/grow", { extendPartition: true, path: "D:\\VMs\\a.vhdx", sizeGb: 64 }],
      ["POST", "/vms/vm-1/delete", { deleteDisks: true, deleteCheckpoints: false, confirmName: "Dev" }],
      ["POST", "/auth/elevation", { passphrase: "correct horse" }],
    ]);
  });

  it("turns a problem response into the client's error, with its code and issues", async () => {
    const { api, answer } = harness();

    const update = api.updateVmCompute("local", "vm-1", { startupMemoryMb: 65536 });
    answer(0, 409, {
      title: "Check host resources",
      status: 409,
      code: "resourceWarnings",
      warnings: [{ field: "startupMemoryMb", message: "Low memory." }],
    });

    await expect(update).rejects.toEqual({
      code: "api",
      message: "Check host resources",
      status: 409,
      problemCode: "resourceWarnings",
      issues: [{ field: "startupMemoryMb", message: "Low memory." }],
    } satisfies ClientError);
  });

  it("prefers the problem's detail, as the client does", async () => {
    const { api, answer } = harness();

    const action = api.performVmAction("local", "vm-1", "start");
    answer(0, 403, { title: "Elevation required", detail: "Enter the passphrase.", code: "elevationRequired" });

    await expect(action).rejects.toMatchObject({ message: "Enter the passphrase.", problemCode: "elevationRequired" });
  });

  it("reports a request the tray could not send as unreachable, with the tray's reason", async () => {
    const { api, answer } = harness();

    const vms = api.listVms("local");
    answer(0, 0, undefined, "The HyperHarbor service is not running.");

    await expect(vms).rejects.toMatchObject({ code: "unreachable", message: "The HyperHarbor service is not running." });
  });
});
