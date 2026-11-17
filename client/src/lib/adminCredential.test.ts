import { describe, expect, it } from "vitest";

import { accountOrigin, adminCredentialError, canStopExpiry, expiryLine, stateLine } from "#lib/adminCredential.ts";
import { adminAlertText } from "@hyperharbor/ui/adminCredential.ts";
import type { VmAdminCredential } from "#lib/api/client.ts";

const NOW = Date.parse("2026-11-17T12:00:00Z");
const windows = { guestOs: { family: "windows" as const, name: "Windows 11 Pro" } };
const linux = { guestOs: { family: "linux" as const, name: "Ubuntu 24.04" } };

const credential = (overrides: Partial<VmAdminCredential> = {}): VmAdminCredential => ({
  userName: "hhadmin",
  managed: true,
  state: "working",
  checkedAt: "2026-11-17T09:13:00Z",
  passwordExpiresAt: null,
  detail: null,
  ...overrides,
});

describe("the VM list's administrator badge", () => {
  it("counts down to the expiry date while there is time, as a warning", () => {
    const text = adminAlertText({ kind: "expiring", passwordExpiresAt: "2026-12-04T12:00:00Z" }, NOW);

    expect(text.label).toBe("Admin password expires December 4");
    expect(text.tone).toBe("warn");
    expect(text.title).toContain("Administrator account…");
  });

  it("says expired once the date has passed, and rejected once the VM refused the account", () => {
    const expired = adminAlertText({ kind: "expiring", passwordExpiresAt: "2026-11-16T12:00:00Z" }, NOW);
    const rejected = adminAlertText({ kind: "rejected", passwordExpiresAt: "2026-11-16T12:00:00Z" }, NOW);
    const unknown = adminAlertText({ kind: "rejected", passwordExpiresAt: null }, NOW);

    expect(expired).toMatchObject({ label: "Admin password expired", tone: "danger" });
    expect(rejected).toMatchObject({ label: "Admin password rejected", tone: "danger" });
    expect(rejected.title).toContain("its password expired on November 16");
    expect(unknown.title).not.toContain("expired on");
    expect(unknown.title).toContain("Connect reuses the last password");
  });
});

describe("the administrator account dialog's text", () => {
  it("says whose account it is", () => {
    expect(accountOrigin(credential())).toContain("only the host knows its password");
    expect(accountOrigin(credential({ managed: false }))).toContain("You gave HyperHarbor this account");
  });

  it("says what the host last saw", () => {
    expect(stateLine(credential(), "Dev Box")).toMatch(/^Dev Box accepted it when HyperHarbor last signed in \(/);
    expect(stateLine(credential({ state: "rejected" }), "Dev Box")).toMatch(/^Dev Box did not accept it/);
    expect(stateLine(credential({ state: "unchecked", checkedAt: null }), "Dev Box")).toContain("Check now");
  });

  it("gives the expiry date, and says never only for a Windows account it has read", () => {
    expect(expiryLine(credential({ passwordExpiresAt: "2026-12-04T12:00:00Z" }), windows, NOW)).toMatch(/^Its password expires on December 4, 2026/);
    expect(expiryLine(credential({ passwordExpiresAt: "2026-11-16T12:00:00Z" }), windows, NOW)).toMatch(/^Its password expired on November 16, 2026/);
    expect(expiryLine(credential(), windows, NOW)).toBe("Its password does not expire.");
    expect(expiryLine(credential(), linux, NOW)).toBeNull();
    expect(expiryLine(credential({ state: "unchecked" }), windows, NOW)).toBeNull();
  });

  it("offers to stop expiry only for a working Windows account that still has a date", () => {
    const expiring = credential({ managed: false, passwordExpiresAt: "2026-12-04T12:00:00Z" });

    expect(canStopExpiry(expiring, windows)).toBe(true);
    expect(canStopExpiry(expiring, linux)).toBe(false);
    expect(canStopExpiry(credential(), windows)).toBe(false);
    expect(canStopExpiry({ ...expiring, state: "rejected" }, windows)).toBe(false);
  });

  it("turns a refused credential into a sentence about the VM", () => {
    const refused = { code: "api", message: "The credential is invalid.", status: 422, problemCode: "adminCredentialRejected", issues: [] };
    const stopped = { code: "api", message: "Not running.", status: 409, problemCode: "vmNotRunning", issues: [] };

    expect(adminCredentialError(refused, "Dev Box")).toBe(
      "Dev Box did not accept that user name and password. Nothing was changed. (The credential is invalid.)",
    );
    expect(adminCredentialError(stopped, "Dev Box")).toBe("Dev Box is not running. Start it, then try again.");
    expect(adminCredentialError(new Error("boom"), "Dev Box")).toBe("boom");
  });
});
