// A machine profile being edited: the seven machine sections plus the software sections (SoftwareDraft).
// Each section keeps every field, with null meaning "leave as it is"; toProfile drops what is not set.
import type { components } from "$lib/api/types";
import type { ControlState } from "$lib/machine/highlights";
import { SoftwareDraft, plural, type WorkspaceSection } from "$lib/softwareDraft.svelte";

type Schemas = components["schemas"];
export type MachineProfile = Schemas["MachineProfile"];
export type MachineAccount = Schemas["MachineAccount"];
export type MachineDisplay = Schemas["MachineDisplay"];
export type MachineGuestDisplay = Schemas["MachineGuestDisplay"];
export type MachineConnection = Schemas["MachineConnection"];
export type MachineRedirect = Schemas["MachineRedirect"];
export type MachineFirmware = Schemas["MachineFirmware"];
export type MachineCompute = Schemas["MachineCompute"];
export type MachineMemory = Schemas["MachineMemory"];
export type MachineGpu = Schemas["MachineGpu"];
export type MachineGpuShare = Schemas["MachineGpuShare"];
export type MachineNetwork = Schemas["MachineNetwork"];
export type MachineBackup = Schemas["MachineBackup"];
export type InstallOs = Schemas["InstallOs"];

export const MACHINE_TABS = ["account", "display", "firmware", "compute", "gpu", "network", "backup"];
export const DEFAULT_EDITION = "Windows 11 Pro";
export type WindowsSettings = Schemas["WindowsInstallSettings"];
export type LinuxSettings = Schemas["LinuxInstallSettings"];

/** Windows Setup settings as the host defaults them. */
export function defaultWindows(): WindowsSettings {
  return {
    defaultEdition: DEFAULT_EDITION,
    bypassHardwareChecks: false,
    disableTelemetry: true,
    disableAdvertisingId: true,
    disableLocation: true,
    disableConsumerFeatures: true,
  };
}

export function defaultLinux(): LinuxSettings {
  return { sshAuthorizedKeys: [], packages: [], installDesktop: true };
}
export const DEFAULT_SHARE: Required<MachineGpuShare> = { vramPercent: 50, encodePercent: 50, decodePercent: 50, computePercent: 50 };

/** A copy without null, undefined, and empty objects; undefined when nothing is left. */
export function prune<T>(value: T): T | undefined {
  if (value === null || value === undefined) return undefined;
  if (Array.isArray(value)) return value;
  if (typeof value !== "object") return value;
  const kept = Object.entries(value as Record<string, unknown>)
    .map(([key, child]) => [key, prune(child)] as const)
    .filter(([, child]) => child !== undefined);
  return kept.length === 0 ? undefined : (Object.fromEntries(kept) as T);
}

/** One line for the profile list: "Windows · 8 processors · 16 GB · GPU · TPM · backups · 12 software items". */
export function machineSummary(profile: Schemas["MachineProfileSummary"]): string {
  const parts = [profile.os === "linux" ? "Linux" : "Windows"];
  if (profile.processors) parts.push(plural(profile.processors, "processor"));
  if (profile.memoryMb) parts.push(gigabytes(profile.memoryMb));
  if (profile.gpu) parts.push("GPU");
  if (profile.tpm) parts.push("TPM");
  if (profile.backup) parts.push("backups");
  if (profile.softwareCount > 0) parts.push(plural(profile.softwareCount, "software item"));
  return parts.join(" · ");
}

function gigabytes(megabytes: number): string {
  const value = megabytes / 1024;
  return `${Number.isInteger(value) ? value : value.toFixed(1)} GB`;
}

export class MachineDraft {
  name = $state("");
  description = $state("");
  os = $state<InstallOs>("windows");
  /** Null: no unattended install settings (the OS is installed from the console). */
  account = $state<MachineAccount | null>(null);
  display = $state<{ guest: MachineGuestDisplay; connection: MachineConnection & { redirect: MachineRedirect } }>({
    guest: {},
    connection: { redirect: {} },
  });
  firmware = $state<MachineFirmware>({});
  compute = $state<MachineCompute>({});
  /** Null: leave Performance mode as it is. */
  gpu = $state<MachineGpu | null>(null);
  network = $state<MachineNetwork>({});
  /** Null: leave backups as they are. */
  backup = $state<MachineBackup | null>(null);
  readonly software = new SoftwareDraft();

  readonly controls: ControlState = $derived({
    items: this.software.install.length,
    tpmEnabled: this.firmware.tpm === true,
    performanceMode: this.gpu?.enabled === true,
    backupScheduled: this.backup?.enabled === true && this.backup.triggers?.schedule !== false,
  });

  constructor(from?: MachineProfile) {
    if (!from) return;
    // The profile may be reactive state (a proxy), which structuredClone cannot copy; work from a plain copy.
    const start = $state.snapshot(from) as MachineProfile;
    this.software.fill(start);
    this.name = start.name;
    this.description = start.description ?? "";
    this.os = start.os;
    this.account = start.account ? structuredClone(start.account) : null;
    this.display = {
      guest: { ...start.display?.guest },
      connection: { ...start.display?.connection, redirect: { ...start.display?.connection?.redirect } },
    };
    this.firmware = { ...start.firmware };
    this.compute = { ...start.compute, memory: start.compute?.memory ? { ...start.compute.memory } : null };
    this.gpu = start.gpu ? { ...start.gpu, share: start.gpu.share ? { ...start.gpu.share } : null } : null;
    this.network = { ...start.network };
    this.backup = start.backup ? structuredClone(start.backup) : null;
  }

  /** Fills the account section from an unattended install profile's settings. */
  fillAccount(from: Omit<MachineAccount, "computerName">) {
    this.account = {
      adminAccountName: from.adminAccountName,
      timeZone: from.timeZone,
      locale: from.locale,
      computerName: this.account?.computerName ?? null,
      windows: this.os === "windows" ? (from.windows ?? null) : null,
      linux: this.os === "linux" ? (from.linux ?? null) : null,
    };
  }

  toProfile(): MachineProfile {
    const windows = this.os === "windows";
    const software = windows ? this.software.toSoftware() : {};
    return {
      name: this.name.trim(),
      description: this.description.trim() || undefined,
      os: this.os,
      account: this.account
        ? {
            ...this.account,
            windows: windows ? (this.account.windows ?? defaultWindows()) : null,
            linux: windows ? null : (this.account.linux ?? defaultLinux()),
            computerName: windows ? this.account.computerName || null : null,
          }
        : undefined,
      display: prune(this.display),
      firmware: prune(this.firmware),
      compute: prune(this.compute),
      gpu: windows && this.gpu ? this.gpu : undefined,
      network: prune(this.network),
      backup: this.backup ?? undefined,
      install: software.install,
      remove: software.remove,
      tweaks: software.tweaks,
      browser: software.browser,
    };
  }

  sections(): WorkspaceSection[] {
    return [
      { id: "account", label: "Account", hint: "Screen: login prompt", section: "account", count: null, group: "Identity" },
      { id: "display", label: "Display", hint: "Screen", section: "display", count: null },
      { id: "firmware", label: "Firmware", hint: "Board: the TPM", section: "firmware", count: null, group: "Hardware" },
      { id: "compute", label: "Compute", hint: "CPU and RAM", section: "compute", count: null },
      { id: "gpu", label: "GPU", hint: "Graphics card", section: "gpu", count: null },
      { id: "network", label: "Network", hint: "Rear I/O", section: "network", count: null },
      { id: "backup", label: "Backups", hint: "Cable to the external disk", section: "backup", count: null, group: "Care" },
      ...this.software.sections("Software"),
    ];
  }

  hud(tab: string): string {
    switch (tab) {
      case "account":
        return this.account ? `${this.account.adminAccountName || "hhadmin"}, ${this.account.locale || "en-US"}` : "installed from the console";
      case "display": {
        const guest = this.display.guest;
        const parts = [guest.theme ? `${guest.theme} theme` : null, this.display.connection.monitors === "all" ? "all monitors" : null];
        return parts.filter(Boolean).join(", ") || "as installed";
      }
      case "firmware": {
        const state = (value: boolean | null | undefined) => (value === true ? "on" : value === false ? "off" : "as it is");
        return `TPM ${state(this.firmware.tpm)}, Secure Boot ${state(this.firmware.secureBoot)}`;
      }
      case "compute": {
        const parts = [
          this.compute.processors ? plural(this.compute.processors, "processor") : null,
          this.compute.memory ? gigabytes(this.compute.memory.startupMb) : null,
          this.compute.diskSizeGb ? `${this.compute.diskSizeGb} GB disk` : null,
        ];
        return parts.filter(Boolean).join(", ") || "host defaults";
      }
      case "gpu":
        if (this.os !== "windows") return "Windows only";
        if (!this.gpu) return "as it is";
        return this.gpu.enabled ? `Performance mode on, ${(this.gpu.share ?? DEFAULT_SHARE).vramPercent}% video memory` : "Performance mode off";
      case "network":
        return this.network.switch ?? "the default switch";
      case "backup":
        if (!this.backup) return "as it is";
        return this.backup.enabled ? `to ${this.backup.target || "a target"}` : "off";
      default:
        return this.os === "windows" ? this.software.hud(tab) : "Windows only";
    }
  }
}
