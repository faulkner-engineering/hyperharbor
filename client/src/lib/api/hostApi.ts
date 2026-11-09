import { provideHostApi, type HostApi } from "@hyperharbor/ui/api/hostApi.ts";
import {
  deleteVm,
  elevate,
  getDeletePreview,
  getElevation,
  getHostResources,
  getJob,
  getVmCompute,
  growVmDisk,
  listVmDisks,
  listVms,
  performVmAction,
  updateVmCompute,
} from "./client.ts";

/** The shared components' host calls, sent through the client's Tauri commands to the host with that key. */
export const clientHostApi: HostApi = {
  listVms,
  performVmAction,
  getVmCompute,
  updateVmCompute,
  listVmDisks,
  growVmDisk,
  getDeletePreview,
  deleteVm,
  getHostResources,
  getElevation,
  elevate,
  getJob,
};

provideHostApi(clientHostApi);
