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

export const onHostsChanged = (handler: () => void): Promise<UnlistenFn> =>
  listen("hosts-changed", handler);
