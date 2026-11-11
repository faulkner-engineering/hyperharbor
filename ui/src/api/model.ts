// Types and error helpers shared by the client and the host's tray window. The schema types come from the
// generated types.ts (npm run gen:api in client/, from docs/api.yaml).
import type { components } from "./types.ts";

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
export type VmDisk = components["schemas"]["VmDisk"];
export type VmDiskList = components["schemas"]["VmDiskList"];
export type VmDiskEncryption = components["schemas"]["VmDiskEncryption"];
export type GuestVolumeEncryption = components["schemas"]["GuestVolumeEncryption"];
export type GrowVmDiskRequest = components["schemas"]["GrowVmDiskRequest"];
export type DiskGrowResult = components["schemas"]["DiskGrowResult"];
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
export type MachineProfile = components["schemas"]["MachineProfile"];
export type MachineProfileSummary = components["schemas"]["MachineProfileSummary"];
export type ProfileImage = components["schemas"]["ProfileImage"];
export type MachineProfileTemplate = components["schemas"]["MachineProfileTemplate"];
export type StoredMachineProfile = components["schemas"]["StoredMachineProfile"];
export type CreateVmFromProfileRequest = components["schemas"]["CreateVmFromProfileRequest"];
export type MachinePart = components["schemas"]["MachinePart"];
export type MachineProfileChange = components["schemas"]["MachineProfileChange"];
export type MachineProfilePreview = components["schemas"]["MachineProfilePreview"];
export type ApplyMachineProfileRequest = components["schemas"]["ApplyMachineProfileRequest"];
export type SetupProfileCatalog = components["schemas"]["SetupProfileCatalog"];
export type VmAppxInventory = components["schemas"]["VmAppxInventory"];
export type AppxPackage = components["schemas"]["AppxPackage"];
export type AppxBaselineInfo = components["schemas"]["AppxBaselineInfo"];
export type PackageSearchResult = components["schemas"]["PackageSearchResult"];
export type PackageCatalogItem = components["schemas"]["PackageCatalogItem"];
export type ExtensionCatalogItem = components["schemas"]["ExtensionCatalogItem"];
export type AppxCatalogItem = components["schemas"]["AppxCatalogItem"];
export type TweakCatalogItem = components["schemas"]["TweakCatalogItem"];
export type TweakCatalogValue = components["schemas"]["TweakCatalogValue"];
export type ResolvedExtension = components["schemas"]["ResolvedExtension"];
export type ProfileDraft = components["schemas"]["ProfileDraft"];
export type DraftItem = components["schemas"]["DraftItem"];
export type DraftBrowser = components["schemas"]["DraftBrowser"];
export type GuestAdminWindow = components["schemas"]["GuestAdminWindow"];
export type GuestAdminStatus = components["schemas"]["GuestAdminStatus"];

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
  switchId?: string;
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
  badRecoveryKey: "badRecoveryKey",
  recoverySetupExpired: "recoverySetupExpired",
  recoveryKeyConfirmed: "recoveryKeyConfirmed",
  noBackupsFound: "noBackupsFound",
  backupTargetNotFound: "backupTargetNotFound",
  backupFailed: "backupFailed",
  transferFailed: "transferFailed",
  transferInProgress: "transferInProgress",
  vmNotRunning: "vmNotRunning",
  vmNotProvisioned: "vmNotProvisioned",
  guestAdminUnsupported: "guestAdminUnsupported",
  guestUnavailable: "guestUnavailable",
} as const;

/**
 * Error thrown by every HostApi call. In the client it is the Tauri command's error (ClientError in
 * src-tauri/src/error.rs); the tray builds the same shape from the host's problem response.
 */
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
    | "noWakeRelay"
    | "rdpFailed"
    | "vmUnreachable"
    | "unreachable"
    | "api"
    | "cancelled"
    | "invalidRequest"
    | "invalidResponse"
    | "storage"
    | "updateFailed"
    | "sessionsActive";
  message: string;
  /** HTTP status when code is "api". */
  status: number | null;
  /** The host's problem code when code is "api", for example "elevationRequired". */
  problemCode: string | null;
  /** Field errors (400) or resource warnings (409 with problemCode "resourceWarnings"). */
  issues: ValidationIssue[];
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

export const hasProblemCode = (error: unknown, code: string) =>
  isClientError(error) && error.problemCode === code;
