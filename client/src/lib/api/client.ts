import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import type { components } from "./types";

export type Vm = components["schemas"]["Vm"];
export type VmState = components["schemas"]["VmState"];
export type WakeReadiness = components["schemas"]["WakeReadiness"];
export type WakeCheck = components["schemas"]["WakeCheck"];
export type WakeTestScheduled = components["schemas"]["WakeTestScheduled"];
export type VmAction = components["schemas"]["VmAction"];
export type VmJob = components["schemas"]["VmJob"];
export type VmDeletePreview = components["schemas"]["VmDeletePreview"];
export type DeleteBlocker = components["schemas"]["DeleteBlocker"];
export type HostResources = components["schemas"]["HostResources"];
export type HostUpdateStatus = components["schemas"]["HostUpdateStatus"];
export type HostUpdateProgress = components["schemas"]["HostUpdateProgress"];
export type HostUpdateSettings = components["schemas"]["HostUpdateSettings"];
export type HostUpdateMode = components["schemas"]["HostUpdateMode"];
export type HostRemoteDesktop = components["schemas"]["HostRemoteDesktop"];
export type IsoImage = components["schemas"]["IsoImage"];
export type VirtualSwitch = components["schemas"]["VirtualSwitch"];
export type VmComputeSettings = components["schemas"]["VmComputeSettings"];
export type VmComputeUpdate = components["schemas"]["VmComputeUpdate"];
export type ComputeSetting = components["schemas"]["ComputeSetting"];
export type ValidationIssue = components["schemas"]["ValidationIssue"];
export type ElevationStatus = components["schemas"]["ElevationStatus"];
export type UnattendProfile = components["schemas"]["UnattendProfile"];
export type UnattendProfileRequest = components["schemas"]["UnattendProfileRequest"];
export type UnattendedInstallState = components["schemas"]["UnattendedInstallState"];
export type InstallOs = components["schemas"]["InstallOs"];
export type IsoInspection = components["schemas"]["IsoInspection"];
export type VmPerformance = components["schemas"]["VmPerformance"];
export type PerformanceSettings = components["schemas"]["PerformanceSettings"];
export type GuestDriverStatus = components["schemas"]["GuestDriverStatus"];
export type HostGpu = components["schemas"]["HostGpu"];
export type HostGpuDevice = components["schemas"]["HostGpuDevice"];
export type GpuDriverWarning = components["schemas"]["GpuDriverWarning"];
export type SetupProfile = components["schemas"]["SetupProfile"];
export type ProfileItem = components["schemas"]["ProfileItem"];
export type ProfileTweak = components["schemas"]["ProfileTweak"];
export type RegistryTweak = components["schemas"]["RegistryTweak"];
export type ProfileBrowser = components["schemas"]["ProfileBrowser"];
export type SetupProfileSummary = components["schemas"]["SetupProfileSummary"];
export type StoredSetupProfile = components["schemas"]["StoredSetupProfile"];
export type SetupProfileCatalog = components["schemas"]["SetupProfileCatalog"];
export type VmAppxInventory = components["schemas"]["VmAppxInventory"];
export type AppxPackage = components["schemas"]["AppxPackage"];
export type AppxBaselineInfo = components["schemas"]["AppxBaselineInfo"];
export type PackageSearchResult = components["schemas"]["PackageSearchResult"];
export type PackageCatalogItem = components["schemas"]["PackageCatalogItem"];
export type ExtensionCatalogItem = components["schemas"]["ExtensionCatalogItem"];
export type ResolvedExtension = components["schemas"]["ResolvedExtension"];
export type ProfileDraft = components["schemas"]["ProfileDraft"];
export type DraftItem = components["schemas"]["DraftItem"];
export type DraftBrowser = components["schemas"]["DraftBrowser"];

/** Request bodies, as the Rust side expects them. */
export interface DeleteVmRequest {
  deleteDisks: boolean;
  deleteCheckpoints: boolean;
  confirmName: string;
}

/** Every field is sent; `install` is left out for a manual install. */
export type CreateVmRequest = Required<
  Omit<components["schemas"]["CreateVmRequest"], "switchId" | "install">
> & { switchId: string | null; install?: UnattendedInstallRequest | null };

export type UnattendedInstallRequest = components["schemas"]["UnattendedInstallRequest"];
export type UnattendedInstallStatus = components["schemas"]["UnattendedInstallStatus"];
export type SetupProfileResult = components["schemas"]["SetupProfileResult"];

export type UpdateComputeRequest = {
  processorCount?: number;
  startupMemoryMb?: number;
  maximumMemoryMb?: number;
  dynamicMemory?: boolean;
  nestedVirtualization?: boolean;
  macAddressSpoofing?: boolean;
  shutDownToApply?: boolean;
  acknowledgeWarnings?: boolean;
};

/** Problem codes the host sends for errors the client handles specially. */
export const ProblemCodes = {
  elevationRequired: "elevationRequired",
  elevationUnavailable: "elevationUnavailable",
  incorrectPassphrase: "incorrectPassphrase",
  tooManyAttempts: "tooManyAttempts",
  resourceWarnings: "resourceWarnings",
  requiresShutdown: "requiresShutdown",
  vmMustBeOff: "vmMustBeOff",
  gpuUnavailable: "gpuUnavailable",
  credentialRequired: "credentialRequired",
  remoteDesktopUnsupported: "remoteDesktopUnsupported",
  requiresInstalledService: "requiresInstalledService",
  /** Set by the client: the host does not have the route, because it runs an older version. */
  hostOutdated: "hostOutdated",
  updateNotReady: "updateNotReady",
  wingetUnavailable: "wingetUnavailable",
} as const;

export interface WakeFixOutcome {
  status: "applied" | "awaitingApproval";
  readiness: WakeReadiness | null;
}

/** A host known to the client. Mirrors HostEntry in src-tauri/src/hosts.rs. */
export interface HostEntry {
  key: string;
  displayName: string;
  hostId: string | null;
  hostName: string | null;
  addresses: string[];
  port: number;
  apiVersion: string | null;
  /** "remembered": a paired host that is not announcing itself, for example while asleep. */
  source: "discovered" | "manual" | "remembered";
  isLocal: boolean;
  paired: boolean;
  /** Wake-on-LAN details are cached, so a wake signal can be sent while the host is asleep. */
  canWake: boolean;
}

/**
 * True when the host's API version is at least `minimum` ("major.minor.patch"). A host whose version
 * is unknown (added by address, or remembered while asleep) is assumed to support it; if it does
 * not, the request fails with problem code hostOutdated.
 */
export function hostSupports(host: Pick<HostEntry, "apiVersion">, minimum: string): boolean {
  const parse = (version: string) => {
    const parts = version.split("-")[0].split(".").map(Number);
    return parts.length === 3 && parts.every(Number.isInteger) ? parts : null;
  };
  const have = host.apiVersion ? parse(host.apiVersion) : null;
  const need = parse(minimum);
  if (!have || !need) return true;
  for (let i = 0; i < 3; i++) {
    if (have[i] !== need[i]) return have[i] > need[i];
  }
  return true;
}

/** Error returned by Tauri commands. Mirrors ClientError in src-tauri/src/error.rs. */
export interface ClientError {
  code:
    | "unknownHost"
    | "invalidAddress"
    | "invalidVmId"
    | "pairingRequired"
    | "noPendingPairing"
    | "pairingVerificationFailed"
    | "noWakeInfo"
    | "wakeFailed"
    | "rdpFailed"
    | "vmUnreachable"
    | "unreachable"
    | "api"
    | "cancelled"
    | "invalidRequest"
    | "invalidResponse"
    | "storage";
  message: string;
  /** HTTP status when code is "api". */
  status: number | null;
  /** The host's problem code when code is "api", for example "elevationRequired". */
  problemCode: string | null;
  /** Field errors (400) or resource warnings (409 with problemCode "resourceWarnings"). */
  issues: ValidationIssue[];
}

export interface PairingStarted {
  pairingId: string;
  expiresAt: string;
}

export function isClientError(value: unknown): value is ClientError {
  return (
    typeof value === "object" &&
    value !== null &&
    "code" in value &&
    "message" in value
  );
}

export function errorMessage(error: unknown): string {
  if (isClientError(error)) return error.message;
  if (error instanceof Error) return error.message;
  return String(error);
}

export const listHosts = () => invoke<HostEntry[]>("list_hosts");

export const addManualHost = (address: string) =>
  invoke<HostEntry>("add_manual_host", { address });

export const removeManualHost = (key: string) =>
  invoke<void>("remove_manual_host", { key });

export const listVms = (key: string) => invoke<Vm[]>("list_vms", { key });

export const startPairing = (key: string) =>
  invoke<PairingStarted>("start_pairing", { key });

export const completePairing = (key: string, pin: string) =>
  invoke<HostEntry>("complete_pairing", { key, pin });

export const cancelPairing = (key: string) => invoke<void>("cancel_pairing", { key });

export const unpair = (key: string) => invoke<void>("unpair", { key });

/** Sends Wake-on-LAN magic packets. Resolves to the number of datagrams sent. */
export const wakeHost = (key: string) => invoke<number>("wake_host", { key });

export const getWakeReadiness = (key: string) =>
  invoke<WakeReadiness>("get_wake_readiness", { key });

export const fixWake = (key: string, checkIds: string[]) =>
  invoke<WakeFixOutcome>("fix_wake", { key, checkIds });

export const startWakeTest = (key: string, delaySeconds: number) =>
  invoke<WakeTestScheduled>("start_wake_test", { key, delaySeconds });

export interface ProvisionOptions {
  enableRemoteDesktop: boolean;
  /** Linux only: install Xfce when the guest has no desktop environment. */
  installDesktop: boolean;
  /** Linux only: accept an SSH host key that differs from the one pinned at the last setup. */
  trustNewHostKey: boolean;
}

/** One-time setup of this User's account on a VM. The admin password is sent to the host over mTLS. */
export const provisionVm = (
  key: string,
  vmId: string,
  adminUserName: string,
  adminPassword: string,
  options: ProvisionOptions,
) =>
  invoke<components["schemas"]["VmProvisioning"]>("provision_vm", {
    key,
    vmId,
    adminUserName,
    adminPassword,
    options,
  });

/** Opens Remote Desktop to a provisioned VM. Resolves once mstsc has been launched. */
export const connectVm = (key: string, vmId: string, address: string) =>
  invoke<void>("connect_vm", { key, vmId, address });

/**
 * Opens the VM's console (its screen, also before an OS is installed) in mstsc through the host.
 * Resolves once mstsc has been launched. Fails with problem code consoleSetupRequired when the host
 * has not set up console access.
 */
export const openConsole = (key: string, vmId: string) => invoke<void>("open_console", { key, vmId });

/** A monitor of this device, in physical pixels. Mirrors Monitor in src-tauri/src/monitors.rs. */
export interface Monitor {
  /** Stable identity (the monitor's device path). */
  key: string;
  /** The ID `mstsc /l` shows. */
  mstscId: number;
  name: string;
  x: number;
  y: number;
  width: number;
  height: number;
  primary: boolean;
}

export type MonitorMode = "single" | "all" | "selected";

/** Which monitors Connect uses for a VM. `selected` holds monitor keys. */
export interface MonitorChoice {
  mode: MonitorMode;
  selected: string[];
}

export const listMonitors = () => invoke<Monitor[]>("list_monitors");

/** The VM's monitor choice, kept on this device. A VM without one uses one monitor. */
export const getMonitorChoice = (key: string, vmId: string) =>
  invoke<MonitorChoice>("get_monitor_choice", { key, vmId });

export const setMonitorChoice = (key: string, vmId: string, choice: MonitorChoice) =>
  invoke<void>("set_monitor_choice", { key, vmId, choice });

export const isOffline =(error: unknown) => isClientError(error) && error.code === "unreachable";

/** True when the host returned this problem code. */
export const hasProblemCode = (error: unknown, code: string) =>
  isClientError(error) && error.problemCode === code;

// Elevation. The token stays in the Rust side; the frontend only learns when it expires.

export const getElevation = (key: string) => invoke<ElevationStatus>("get_elevation", { key });

export const elevate = (key: string, passphrase: string) =>
  invoke<{ expiresAt: string }>("elevate", { key, passphrase });

export const dropElevation = (key: string) => invoke<void>("drop_elevation", { key });

// Power and lifecycle.

export const performVmAction = (key: string, vmId: string, action: VmAction) =>
  invoke<components["schemas"]["VmActionResult"]>("perform_vm_action", { key, vmId, action });

export const getDeletePreview = (key: string, vmId: string) =>
  invoke<VmDeletePreview>("get_delete_preview", { key, vmId });

export const deleteVm = (key: string, vmId: string, request: DeleteVmRequest) =>
  invoke<VmJob>("delete_vm", { key, vmId, request });

export const createVm = (key: string, request: CreateVmRequest) =>
  invoke<VmJob>("create_vm", { key, request });

export const getJob = (key: string, jobId: string) => invoke<VmJob>("get_job", { key, jobId });

export const getHostResources = (key: string) =>
  invoke<HostResources>("get_host_resource", { key, resource: "resources" });

export const listIsos = (key: string) => invoke<IsoImage[]>("get_host_resource", { key, resource: "isos" });

export const listSwitches = (key: string) =>
  invoke<VirtualSwitch[]>("get_host_resource", { key, resource: "switches" });

// Host updates. Only an installed host updates itself (status.supported).

export const getHostUpdate = (key: string) =>
  invoke<HostUpdateStatus>("get_host_resource", { key, resource: "update" });

export const checkHostUpdate = (key: string) => invoke<HostUpdateStatus>("check_host_update", { key });

/** The API version that added installing a ready update from a client. */
export const InstallUpdateApiVersion = "1.11.0";

/**
 * Installs the ready update now (status.activity "ready"); the host restarts. Needs elevation:
 * run it inside withElevation.
 */
export const installHostUpdate = (key: string) => invoke<HostUpdateStatus>("install_host_update", { key });

/** Needs elevation: run it inside withElevation. */
export const setHostUpdateSettings = (key: string, settings: HostUpdateSettings) =>
  invoke<HostUpdateStatus>("set_host_update_settings", { key, settings });

/** The API version that added Remote Desktop to the host. */
export const RemoteDesktopApiVersion = "1.10.0";

// Remote Desktop to the host itself. mstsc asks for the host's Windows account; no host
// credentials pass through HyperHarbor.

export const getHostRemoteDesktop = (key: string) =>
  invoke<HostRemoteDesktop>("get_host_resource", { key, resource: "remoteDesktop" });

/** Needs elevation: run it inside withElevation. */
export const enableHostRemoteDesktop = (key: string) =>
  invoke<HostRemoteDesktop>("enable_host_remote_desktop", { key });

/** The API version that added the host log download. */
export const HostLogsApiVersion = "1.16.0";

/**
 * Downloads the host's log files as a zip and asks where to save it. Resolves with the saved path, or null
 * when the user cancels the save dialog. Needs elevation: run it inside withElevation.
 */
export const downloadHostLogs = (key: string) => invoke<string | null>("download_host_logs", { key });

/** Opens Remote Desktop to the host. Resolves once mstsc has been launched. */
export const connectHost = (key: string) => invoke<void>("connect_host", { key });

// Setup profiles (API 1.12.0): YAML files on the host, built with pickers or captured from a VM.

/** The API version that added setup profiles, package search, and capture. */
export const SetupProfilesApiVersion = "1.12.0";

export const listSetupProfiles = (key: string) =>
  invoke<SetupProfileSummary[]>("get_host_resource", { key, resource: "setupProfiles" });

export const getSetupProfile = (key: string, profileId: string) =>
  invoke<StoredSetupProfile>("get_setup_profile", { key, profileId });

/** Creates a profile (null ID) or replaces one. Needs elevation: run it inside withElevation. */
export const saveSetupProfile = (key: string, profileId: string | null, profile: SetupProfile) =>
  invoke<StoredSetupProfile>("save_setup_profile", { key, profileId, profile });

/** Needs elevation: run it inside withElevation. */
export const deleteSetupProfile = (key: string, profileId: string) =>
  invoke<void>("delete_setup_profile", { key, profileId });

/** Saves the profile's YAML file where the user chooses; null when cancelled. */
export const exportSetupProfile = (key: string, profileId: string) =>
  invoke<string | null>("export_setup_profile", { key, profileId });

/** Picks a YAML file and saves it as a new profile; null when cancelled. Needs elevation. */
export const importSetupProfile = (key: string) => invoke<StoredSetupProfile | null>("import_setup_profile", { key });

export const getSetupProfileCatalog = (key: string) =>
  invoke<SetupProfileCatalog>("get_host_resource", { key, resource: "setupProfileCatalog" });

export const listPackageCatalog = (key: string) =>
  invoke<PackageCatalogItem[]>("get_host_resource", { key, resource: "packageCatalog" });

export const listExtensionCatalog = (key: string) =>
  invoke<ExtensionCatalogItem[]>("get_host_resource", { key, resource: "extensionCatalog" });

/** Fails with problem code wingetUnavailable until package search is set up on the host. */
export const searchPackages = (key: string, query: string) =>
  invoke<PackageSearchResult[]>("search_packages", { key, query });

export const resolveExtension = (key: string, input: string) =>
  invoke<ResolvedExtension>("resolve_extension", { key, input });

export const listVmAppx = (key: string, vmId: string) => invoke<VmAppxInventory>("list_vm_appx", { key, vmId });

export const recordAppxBaseline = (key: string, vmId: string) =>
  invoke<AppxBaselineInfo>("record_appx_baseline", { key, vmId });

/** Reads a running Windows VM (a few minutes) and returns a draft; nothing is saved. */
export const captureSetupProfile = (key: string, vmId: string) =>
  invoke<ProfileDraft>("capture_setup_profile", { key, vmId });

// ISO library. Files are chosen and read on the Rust side; the frontend only sees a pick ID.

export interface PickedIso {
  pickId: string;
  fileName: string;
  sizeBytes: number;
}

export interface IsoUploadProgress {
  pickId: string;
  sent: number;
  total: number;
}

/** Shows a file picker on this device. Resolves to null when the user cancels. */
export const pickIsoFile = () => invoke<PickedIso | null>("pick_iso_file");

export const uploadIso = (key: string, pickId: string, name: string) =>
  invoke<IsoImage>("upload_iso", { key, pickId, name });

export const cancelIsoUpload = (pickId: string) => invoke<void>("cancel_iso_upload", { pickId });

export const renameIso = (key: string, name: string, newName: string) =>
  invoke<IsoImage>("rename_iso", { key, name, newName });

export const deleteIso = (key: string, name: string) => invoke<void>("delete_iso", { key, name });

// Unattended installs. Saving or deleting a profile needs elevation.

export const listUnattendProfiles = (key: string) =>
  invoke<UnattendProfile[]>("get_host_resource", { key, resource: "unattendProfiles" });

/** Creates a profile when `profileId` is null, otherwise replaces it. */
export const saveUnattendProfile = (key: string, profileId: string | null, profile: UnattendProfileRequest) =>
  invoke<UnattendProfile>("save_unattend_profile", { key, profileId, profile });

export const deleteUnattendProfile = (key: string, profileId: string) =>
  invoke<void>("delete_unattend_profile", { key, profileId });

/** What an image in the library installs: its OS and, for Windows, its editions. */
export const inspectIso = (key: string, name: string) => invoke<IsoInspection>("inspect_iso", { key, name });

export const onIsoUploadProgress = (handler: (progress: IsoUploadProgress) => void): Promise<UnlistenFn> =>
  listen<IsoUploadProgress>("iso-upload-progress", (event) => handler(event.payload));

/** The API version that added choosing a setup profile for an unattended install. */
export const SetupProfileInstallApiVersion = "1.13.0";

/** The VM's unattended install and, once applied, its setup profile's result. */
export const getVmInstall = (key: string, vmId: string) =>
  invoke<UnattendedInstallStatus>("get_vm_install", { key, vmId });

/** The API version that added applying a setup profile to an existing VM. */
export const ApplySetupProfileApiVersion = "1.14.0";

/** Applies one of the User's setup profiles to a running Windows VM; returns the applySetupProfile job. */
export const applyVmSetupProfile = (key: string, vmId: string, profileId: string, restartIfNeeded: boolean) =>
  invoke<VmJob>("apply_vm_setup_profile", { key, vmId, request: { profileId, restartIfNeeded } });

export const getVmCompute = (key: string, vmId: string) =>
  invoke<VmComputeSettings>("get_vm_compute", { key, vmId });

export const updateVmCompute = (key: string, vmId: string, request: UpdateComputeRequest) =>
  invoke<VmComputeUpdate>("update_vm_compute", { key, vmId, request });

export const getVmPerformance = (key: string, vmId: string) =>
  invoke<VmPerformance>("get_vm_performance", { key, vmId });

export const applyVmPerformance = (key: string, vmId: string, settings: PerformanceSettings) =>
  invoke<VmJob>("apply_vm_performance", { key, vmId, settings });

export const removeVmPerformance = (key: string, vmId: string) =>
  invoke<void>("remove_vm_performance", { key, vmId });

export const setUpPerformanceGuest = (key: string, vmId: string, driversOnly: boolean) =>
  invoke<VmJob>("set_up_performance_guest", { key, vmId, driversOnly });

export const getHostGpu = (key: string) => invoke<HostGpu>("get_host_resource", { key, resource: "gpu" });

/** Copies an off VM's disks; without a folder the host uses the backup folder set in its tray. */
export const exportVmDisks = (key: string, vmId: string, destinationFolder: string | null = null) =>
  invoke<VmJob>("export_vm_disks", { key, vmId, destinationFolder });

export const onHostsChanged = (handler: () => void): Promise<UnlistenFn> =>
  listen("hosts-changed", handler);
