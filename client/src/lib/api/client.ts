import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import type { components } from "./types";

export type Vm = components["schemas"]["Vm"];
export type VmState = components["schemas"]["VmState"];

/** A host known to the client. Mirrors HostEntry in src-tauri/src/hosts.rs. */
export interface HostEntry {
  key: string;
  displayName: string;
  hostId: string | null;
  hostName: string | null;
  addresses: string[];
  port: number;
  apiVersion: string | null;
  source: "discovered" | "manual";
  isLocal: boolean;
  paired: boolean;
}

/** Error returned by Tauri commands. Mirrors ClientError in src-tauri/src/error.rs. */
export interface ClientError {
  code:
    | "unknownHost"
    | "invalidAddress"
    | "pairingRequired"
    | "noPendingPairing"
    | "pairingVerificationFailed"
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

export const onHostsChanged = (handler: () => void): Promise<UnlistenFn> =>
  listen("hosts-changed", handler);
