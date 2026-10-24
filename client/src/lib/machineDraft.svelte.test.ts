import { describe, expect, it } from "vitest";
import { MachineDraft, machineSummary, prune, type MachineProfile } from "./machineDraft.svelte";

const sample: MachineProfile = {
  name: "Gaming rig",
  description: "Steam and a GPU share",
  os: "windows",
  account: {
    adminAccountName: "hhadmin",
    locale: "en-US",
    timeZone: null,
    computerName: "RIG",
    windows: {
      defaultEdition: "Windows 11 Pro",
      bypassHardwareChecks: false,
      disableTelemetry: true,
      disableAdvertisingId: true,
      disableLocation: true,
      disableConsumerFeatures: true,
    },
  },
  display: { guest: { theme: "dark" }, connection: { monitors: "all", redirect: { clipboard: true } } },
  firmware: { tpm: true },
  compute: { processors: 8, memory: { startupMb: 16384, dynamic: false } },
  gpu: { enabled: true, share: { vramPercent: 60, encodePercent: 50, decodePercent: 50, computePercent: 50 } },
  network: { switch: "LAN" },
  backup: { enabled: true, target: "NAS", triggers: { schedule: true, onShutdown: false, debounceMinutes: 10 } },
  install: [{ id: "Valve.Steam", name: "Steam" }],
};

describe("prune", () => {
  it("drops null, undefined, and empty objects, keeping false and zero", () => {
    expect(prune({ a: null, b: undefined, c: {}, d: { e: null }, f: false, g: 0, h: [] })).toEqual({ f: false, g: 0, h: [] });
    expect(prune({ a: null })).toBeUndefined();
  });
});

describe("MachineDraft", () => {
  it("round-trips a profile", () => {
    const profile = new MachineDraft(sample).toProfile();

    expect(profile).toMatchObject({
      name: "Gaming rig",
      os: "windows",
      firmware: { tpm: true },
      compute: { processors: 8, memory: { startupMb: 16384, dynamic: false } },
      gpu: { enabled: true },
      network: { switch: "LAN" },
      backup: { enabled: true, target: "NAS" },
      display: { guest: { theme: "dark" }, connection: { monitors: "all", redirect: { clipboard: true } } },
      install: [{ id: "Valve.Steam", name: "Steam" }],
    });
    expect(profile.account?.computerName).toBe("RIG");
  });

  it("leaves out sections that set nothing", () => {
    const draft = new MachineDraft();
    draft.name = "  Empty  ";

    const profile = draft.toProfile();

    expect(profile.name).toBe("Empty");
    expect([profile.account, profile.display, profile.firmware, profile.compute, profile.gpu, profile.network, profile.backup]).toEqual([
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
    ]);
  });

  it("drops Windows-only parts from a Linux profile", () => {
    const draft = new MachineDraft(sample);
    draft.os = "linux";

    const profile = draft.toProfile();

    expect(profile.gpu).toBeUndefined();
    expect(profile.install).toBeUndefined();
    expect(profile.account?.windows).toBeNull();
    expect(profile.account?.computerName).toBeNull();
    expect(profile.account?.linux).toEqual({ sshAuthorizedKeys: [], packages: [], installDesktop: true });
  });

  it("drives the machine's reactions from the settings", () => {
    const draft = new MachineDraft(sample);
    expect(draft.controls).toEqual({ items: 1, tpmEnabled: true, performanceMode: true, backupScheduled: true });

    draft.firmware.tpm = false;
    draft.gpu = null;
    draft.backup = { enabled: true, target: "NAS", triggers: { schedule: false, onShutdown: true, debounceMinutes: 10 } };
    expect(draft.controls).toEqual({ items: 1, tpmEnabled: false, performanceMode: false, backupScheduled: false });
  });

  it("lists the machine sections in groups, then the software", () => {
    const sections = new MachineDraft().sections();

    expect(sections.map((section) => section.id)).toEqual([
      "account", "display", "firmware", "compute", "gpu", "network", "backup", "packages", "remove", "tweaks", "browser",
    ]);
    expect(sections.filter((section) => section.group).map((section) => section.group)).toEqual(["Identity", "Hardware", "Care", "Software"]);
    expect(sections.find((section) => section.id === "firmware")?.section).toBe("firmware");
  });

  it("describes each section in the HUD", () => {
    const draft = new MachineDraft(sample);

    expect(draft.hud("compute")).toBe("8 processors, 16 GB");
    expect(draft.hud("firmware")).toBe("TPM on, Secure Boot as it is");
    expect(draft.hud("gpu")).toBe("Performance mode on, 60% video memory");
    expect(draft.hud("backup")).toBe("to NAS");
    expect(new MachineDraft().hud("compute")).toBe("host defaults");
  });

  it("fills the account from an unattended install profile, keeping the computer name", () => {
    const draft = new MachineDraft(sample);

    draft.fillAccount({ adminAccountName: "ops", locale: "de-DE", timeZone: "W. Europe Standard Time", windows: sample.account!.windows });

    expect(draft.account).toMatchObject({ adminAccountName: "ops", locale: "de-DE", computerName: "RIG", linux: null });
  });
});

describe("machineSummary", () => {
  it("summarizes a profile in one line", () => {
    expect(
      machineSummary({
        id: "rig",
        name: "Rig",
        description: null,
        os: "windows",
        processors: 8,
        memoryMb: 16384,
        gpu: true,
        tpm: true,
        backup: false,
        softwareCount: 1,
        updatedAt: "2026-10-23T12:00:00Z",
        error: null,
      }),
    ).toBe("Windows · 8 processors · 16 GB · GPU · TPM · 1 software item");
  });
});
