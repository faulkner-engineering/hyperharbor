import { describe, expect, it } from "vitest";
import type { BackupItemStatus, BackupTarget, RestoreTestResult, RestoreTestStatus } from "$lib/api/client";
import {
  ageText,
  confirmationComplete,
  emptyTargetForm,
  formProblem,
  formatBytes,
  groupKey,
  intervalText,
  itemName,
  normalizeKey,
  restoreTestLine,
  restoreTestSummary,
  runSummary,
  scheduleText,
  targetLocation,
  toNewTarget,
} from "./backup";

const now = new Date("2026-10-12T12:00:00Z");

const status = (overrides: Partial<BackupItemStatus> = {}): BackupItemStatus => ({
  item: "vm:1",
  targetId: "t1",
  targetName: "Local",
  overdue: false,
  vmName: "Dev",
  ...overrides,
});

const run = (outcome: "succeeded" | "failed" | "skipped" | "partial", extra = {}) => ({
  runId: "r1",
  item: "vm:1",
  targetId: "t1",
  trigger: "schedule" as const,
  startedAt: "2026-10-12T09:00:00Z",
  finishedAt: "2026-10-12T09:10:00Z",
  outcome,
  totalBytes: 1,
  warnings: [],
  ...extra,
});

describe("recovery key helpers", () => {
  it("ignores case, dashes, and spaces", () => {
    expect(normalizeKey("ab cd-ef")).toBe("ABCDEF");
  });

  it("is complete at four groups", () => {
    expect(confirmationComplete("ABCD-EFGH-IJKL")).toBe(false);
    expect(confirmationComplete("abcd efgh ijkl mnop")).toBe(true);
    expect(confirmationComplete("ABCDEFGHIJKLMNOP")).toBe(true);
  });

  it("regroups a key typed without dashes", () => {
    expect(groupKey("abcdefghij")).toBe("ABCD-EFGH-IJ");
  });
});

describe("formatting", () => {
  it.each([
    [0, "0 B"],
    [1023, "1023 B"],
    [1024, "1 KB"],
    [1536, "1.5 KB"],
    [64 * 1024 ** 3, "64 GB"],
  ])("formats %i bytes as %s", (bytes, text) => expect(formatBytes(bytes)).toBe(text));

  it.each([
    ["2026-10-12T11:59:40Z", "just now"],
    ["2026-10-12T11:30:00Z", "30 minutes ago"],
    ["2026-10-12T11:00:00Z", "1 hour ago"],
    ["2026-10-11T12:00:00Z", "yesterday"],
    ["2026-10-07T12:00:00Z", "5 days ago"],
  ])("says %s is %s", (iso, text) => expect(ageText(iso, now)).toBe(text));

  it("describes a schedule", () => {
    expect(scheduleText({ time: "02:00" })).toBe("every day at 02:00");
    expect(scheduleText({ time: "03:30", days: ["monday", "thursday"] })).toBe("Mon, Thu at 03:30");
  });
});

describe("status text", () => {
  it("names the host and VMs", () => {
    expect(itemName({ item: "host" })).toBe("Host settings");
    expect(itemName({ item: "vm:1", vmName: "Dev" })).toBe("Dev");
    expect(itemName({ item: "vm:1" })).toBe("A virtual machine");
  });

  it("covers every outcome", () => {
    expect(runSummary(status(), now)).toBe("Not backed up yet.");
    expect(runSummary(status({ lastRun: run("succeeded") }), now)).toBe("Backed up 3 hours ago.");
    expect(runSummary(status({ lastRun: run("partial") }), now)).toContain("problem to look at");
    expect(runSummary(status({ lastRun: run("skipped", { reason: "needsOff" }) }), now)).toBe("Skipped 3 hours ago: needsOff.");
  });

  it("puts the last good backup beside a failure", () => {
    const text = runSummary(
      status({ lastRun: run("failed", { reason: "The disk is full." }), lastSuccessAt: "2026-10-10T12:00:00Z" }),
      now,
    );

    expect(text).toBe("Failed 3 hours ago: The disk is full. Last good backup 2 days ago.");
  });

  it("explains a run skipped for its conditions with the reason the host gave", () => {
    const text = runSummary(status({ lastRun: run("skipped", { reason: "conditionsNotMet", warnings: ["A game was running."] }) }), now);

    expect(text).toBe("Skipped 3 hours ago: A game was running.");
  });
});

describe("target form", () => {
  const filled = () => ({ ...emptyTargetForm(), name: "Cloud" });

  it("needs the settings of the chosen kind", () => {
    expect(formProblem({ ...filled(), kind: "local" })).toBe("Choose a folder.");
    expect(formProblem({ ...filled(), kind: "s3", endpoint: "s3.example.com", bucket: "b" })).toContain("access key");
    expect(formProblem({ ...filled(), kind: "azure", azureAccount: "a", azureContainer: "c" })).toContain("account key");
    expect(formProblem({ ...filled(), kind: "local", path: "D:\\B" })).toBeNull();
  });

  it("checks the time and the retention", () => {
    const form = { ...filled(), path: "D:\\B" };

    expect(formProblem({ ...form, time: "25:00" })).toContain("HH:mm");
    expect(formProblem({ ...form, daily: 0, weekly: 0, monthly: 0 })).toContain("at least one");
  });

  it("builds only the chosen kind's settings, without empty optional text", () => {
    const request = toNewTarget({
      ...filled(),
      kind: "s3",
      endpoint: " s3.example.com ",
      bucket: "b",
      accessKey: "A",
      secretKey: "S",
      limitKbps: 2000,
    });

    expect(request.s3).toEqual({ endpoint: "s3.example.com", bucket: "b", region: undefined, prefix: undefined, useTls: true });
    expect(request.local).toBeUndefined();
    expect(request.azure).toBeUndefined();
    expect(request.credentials).toEqual({ s3AccessKey: "A", s3SecretKey: "S" });
    expect(request.bandwidthKbps).toBe(2000);
  });

  it("sends no credentials for a folder", () => {
    const request = toNewTarget({ ...filled(), path: "D:\\B" });

    expect(request.credentials).toBeUndefined();
    expect(request.local).toEqual({ path: "D:\\B" });
  });
});

describe("targetLocation", () => {
  const target = (extra: Partial<BackupTarget>): BackupTarget =>
    ({ id: "t", name: "n", kind: "local", enabled: true, keyState: "confirmed", ...extra }) as BackupTarget;

  it("shows where each kind keeps its backups", () => {
    expect(targetLocation(target({ local: { path: "D:\\B" } }))).toBe("D:\\B");
    expect(targetLocation(target({ s3: { endpoint: "s3.example.com", bucket: "b", useTls: true } }))).toBe("s3.example.com/b");
    expect(targetLocation(target({ azure: { account: "a", container: "c" } }))).toBe("a/c");
  });
});

describe("restore test text", () => {
  const result = (overrides: Partial<RestoreTestResult> = {}): RestoreTestResult => ({
    runId: "r1",
    vmName: "Dev",
    targetId: "t1",
    trigger: "schedule",
    startedAt: "2026-10-12T08:50:00Z",
    finishedAt: "2026-10-12T09:00:00Z",
    outcome: "passed",
    warnings: [],
    ...overrides,
  });
  const test = (overrides: Partial<RestoreTestStatus> = {}): RestoreTestStatus => ({
    settings: { enabled: true, intervalDays: 30, window: { start: "03:00", end: "06:00" } },
    running: false,
    testable: 2,
    results: [result()],
    nextDue: "2026-11-11T09:00:00Z",
    ...overrides,
  });

  it("says what is happening, and why nothing is", () => {
    expect(restoreTestSummary(test({ running: true, current: "Dev" }), now)).toBe("Testing Dev…");
    expect(restoreTestSummary(test({ running: true }), now)).toBe("Testing…");
    expect(restoreTestSummary(test({ testable: 0 }), now)).toMatch(/no virtual machine has a backup/);
    expect(restoreTestSummary(test({ settings: { enabled: false, intervalDays: 30, window: { start: "03:00", end: "06:00" } } }), now)).toBe("The restore test is off.");
  });

  it("gives the last test and when the next one is due", () => {
    expect(restoreTestSummary(test(), now)).toMatch(/^Last tested 3 hours ago. Next test after /);
    expect(restoreTestSummary(test({ results: [], nextDue: "2026-10-01T00:00:00Z" }), now)).toBe("Never tested. Next test at the next quiet time.");
  });

  it("describes a result with its reason only when it did not pass", () => {
    expect(restoreTestLine(result(), now)).toBe("Passed: Dev, 3 hours ago.");
    expect(restoreTestLine(result({ outcome: "failed", reason: "The guest did not report in." }), now)).toBe("Failed: Dev, 3 hours ago. The guest did not report in.");
    expect(restoreTestLine(result({ outcome: "skipped", reason: "A game started." }), now)).toContain("Skipped: Dev");
  });

  it("words the interval", () => {
    expect(intervalText(1)).toBe("every day");
    expect(intervalText(30)).toBe("every month");
    expect(intervalText(7)).toBe("every 7 days");
  });
});
