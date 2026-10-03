import { beforeEach, describe, expect, it, vi } from "vitest";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import { elevation, ElevationCancelled, toMb, waitForJob, withElevation } from "./lifecycle.svelte";
import type { VmJob } from "$lib/api/client";

const elevationRequired = {
  code: "api",
  message: "This operation requires elevation.",
  status: 403,
  problemCode: "elevationRequired",
  issues: [],
};

function job(state: VmJob["state"], percentComplete: number): VmJob {
  return {
    id: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d",
    kind: "deleteVm",
    vmId: null,
    state,
    step: "Deleting disks",
    percentComplete,
    createdAt: "2026-10-03T12:00:00Z",
    updatedAt: "2026-10-03T12:00:00Z",
    error: null,
  };
}

beforeEach(() => {
  invoke.mockReset();
  elevation.finish(false);
});

describe("withElevation", () => {
  it("asks for the passphrase once and retries the action", async () => {
    const action = vi.fn().mockRejectedValueOnce(elevationRequired).mockResolvedValueOnce("done");

    const result = withElevation("host", action);
    await vi.waitFor(() => expect(elevation.pending?.hostKey).toBe("host"));
    elevation.finish(true, "2026-10-03T12:05:00Z");

    await expect(result).resolves.toBe("done");
    expect(action).toHaveBeenCalledTimes(2);
    expect(elevation.expiresAt["host"]).toBe("2026-10-03T12:05:00Z");
  });

  it("stops when the prompt is cancelled", async () => {
    const action = vi.fn().mockRejectedValue(elevationRequired);

    const result = withElevation("host", action);
    await vi.waitFor(() => expect(elevation.pending).not.toBeNull());
    elevation.finish(false);

    await expect(result).rejects.toBeInstanceOf(ElevationCancelled);
    expect(action).toHaveBeenCalledTimes(1);
  });

  it("passes other errors through without prompting", async () => {
    const conflict = { ...elevationRequired, status: 409, problemCode: null, message: "The VM is running." };

    await expect(withElevation("host", () => Promise.reject(conflict))).rejects.toBe(conflict);
    expect(elevation.pending).toBeNull();
  });
});

describe("waitForJob", () => {
  it("polls until the job finishes and reports each update", async () => {
    invoke.mockResolvedValueOnce(job("running", 60)).mockResolvedValueOnce(job("succeeded", 100));
    const updates: number[] = [];

    const finished = await waitForJob("host", job("running", 10), (update) => updates.push(update.percentComplete), 0);

    expect(finished.state).toBe("succeeded");
    expect(updates).toEqual([10, 60, 100]);
    expect(invoke).toHaveBeenCalledWith("get_job", { key: "host", jobId: job("running", 0).id });
  });
});

describe("toMb", () => {
  it("keeps exact gigabyte values and rounds to even megabytes", () => {
    expect(toMb(768 / 1024)).toBe(768);
    expect(toMb(4)).toBe(4096);
    expect(toMb(1.001)).toBe(1026);
  });
});
