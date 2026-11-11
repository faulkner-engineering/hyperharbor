import type { components } from "./types.ts";
import type {
  DeleteVmRequest,
  ElevationStatus,
  GrowVmDiskRequest,
  HostResources,
  UpdateComputeRequest,
  Vm,
  VmAction,
  VmComputeSettings,
  VmComputeUpdate,
  VmDeletePreview,
  VmDiskList,
  VmJob,
} from "./model.ts";

/** What the shared components need to know about a host. The client's HostEntry has these fields. */
export interface HostRef {
  key: string;
  displayName: string;
}

/**
 * The host calls the shared components make. Each app provides one implementation at startup: the client sends them
 * through its Tauri commands to the selected host, and the host's tray window through the tray to its own host.
 * Every call takes the host's key (the tray has one host and ignores it) and throws a ClientError on failure.
 */
export interface HostApi {
  listVms(key: string): Promise<Vm[]>;
  performVmAction(key: string, vmId: string, action: VmAction): Promise<components["schemas"]["VmActionResult"]>;
  getVmCompute(key: string, vmId: string): Promise<VmComputeSettings>;
  updateVmCompute(key: string, vmId: string, request: UpdateComputeRequest): Promise<VmComputeUpdate>;
  listVmDisks(key: string, vmId: string): Promise<VmDiskList>;
  /** Grows a disk (and the guest's partition on it); returns the growDisk job. Needs elevation. */
  growVmDisk(key: string, vmId: string, request: GrowVmDiskRequest): Promise<VmJob>;
  getDeletePreview(key: string, vmId: string): Promise<VmDeletePreview>;
  deleteVm(key: string, vmId: string, request: DeleteVmRequest): Promise<VmJob>;
  getHostResources(key: string): Promise<HostResources>;
  getElevation(key: string): Promise<ElevationStatus>;
  elevate(key: string, passphrase: string): Promise<{ expiresAt: string }>;
  getJob(key: string, jobId: string): Promise<VmJob>;
}

let provided: HostApi | null = null;

/** Sets the app's implementation. Called once at startup, and by tests with a fake. */
export function provideHostApi(api: HostApi) {
  provided = api;
}

/** The app's implementation; throws when none was provided, which is a startup bug, not a host failure. */
export function hostApi(): HostApi {
  if (!provided) throw new Error("No HostApi was provided.");
  return provided;
}
