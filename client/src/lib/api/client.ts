import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import type { components } from "./types";

export type Vm = components["schemas"]["Vm"];
export type VmState = components["schemas"]["VmState"];
export type WakeReadiness = components["schemas"]["WakeReadiness"];
export type WakeCheck = components["schemas"]["WakeCheck"];
export type WakeTestScheduled = components["schemas"]["WakeTestScheduled"];

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
    | "invalidResponse"
    | "storage";
  message: string;
  /** HTTP status when code is "api". */
  status: number | null;
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

export const isOffline =(error: unknown) => isClientError(error) && error.code === "unreachable";

export const onHostsChanged = (handler: () => void): Promise<UnlistenFn> =>
  listen("hosts-changed", handler);
