import { provideHostApi, type HostApi } from "@hyperharbor/ui/api/hostApi.ts";
import type { ClientError, ValidationIssue } from "@hyperharbor/ui/api/model.ts";
import { listen, send, type HostMessage, type PageMessage } from "./bridge";

/** How long the page waits for an answer; the tray's own request timeout is 60 seconds. */
const ANSWER_TIMEOUT_MS = 90_000;

/** The host's problem response (RFC 9457), as the client's Rust side reads it (ProblemDetails in api.rs). */
interface Problem {
  title?: string;
  detail?: string;
  code?: string;
  errors?: ValidationIssue[];
  warnings?: ValidationIssue[];
}

function clientError(code: ClientError["code"], message: string, status: number | null = null): ClientError {
  return { code, message, status, problemCode: null, issues: [] };
}

/** The error the client makes from the same response (check in api.rs), so the shared dialogs read it the same way. */
export function problemError(status: number, body: string | undefined): ClientError {
  let problem: Problem | null = null;
  try {
    problem = body ? (JSON.parse(body) as Problem) : null;
  } catch {
    problem = null;
  }

  const message = problem?.detail || problem?.title || `The host answered ${status}.`;
  return {
    code: "api",
    message,
    status,
    problemCode: problem?.code ?? null,
    issues: [...(problem?.errors ?? []), ...(problem?.warnings ?? [])],
  };
}

/**
 * The shared components' host calls, for the tray window: each is an apiRequest message to the tray, which sends it to
 * this host's API as the tray's device (Api/TrayApiForwarder.cs) and answers with apiResponse. The host key is
 * ignored; the tray window has one host.
 */
export function createTrayHostApi(
  sendMessage: (message: PageMessage) => void = send,
  subscribe: (handler: (message: HostMessage) => void) => void = listen,
): HostApi {
  const pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: ClientError) => void }>();
  let next = 0;

  subscribe((message) => {
    if (message.type !== "apiResponse") return;
    const waiting = pending.get(message.id);
    if (!waiting) return;
    pending.delete(message.id);
    if (message.status === 0) {
      waiting.reject(clientError("unreachable", message.error ?? "The host did not answer."));
    } else if (message.status >= 200 && message.status < 300) {
      try {
        waiting.resolve(message.body ? JSON.parse(message.body) : undefined);
      } catch {
        waiting.reject(clientError("invalidResponse", "The host's answer could not be read.", message.status));
      }
    } else {
      waiting.reject(problemError(message.status, message.body));
    }
  });

  function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const id = `r${++next}`;
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        if (pending.delete(id)) reject(clientError("unreachable", "The host did not answer in time."));
      }, ANSWER_TIMEOUT_MS);
      pending.set(id, {
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value as T);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      });
      sendMessage(body === undefined ? { type: "apiRequest", id, method, path } : { type: "apiRequest", id, method, path, body });
    });
  }

  const vm = (vmId: string, rest = "") => `/vms/${encodeURIComponent(vmId)}${rest}`;

  return {
    listVms: () => request("GET", "/vms"),
    performVmAction: (_key, vmId, action) => request("POST", vm(vmId, "/actions"), { action }),
    getVmCompute: (_key, vmId) => request("GET", vm(vmId, "/compute")),
    updateVmCompute: (_key, vmId, body) => request("PATCH", vm(vmId, "/compute"), body),
    listVmDisks: (_key, vmId) => request("GET", vm(vmId, "/disks")),
    // As the client sends it: the guest's partition grows with the disk unless the request says otherwise.
    growVmDisk: (_key, vmId, body) => request("POST", vm(vmId, "/disks/grow"), { extendPartition: true, ...body }),
    getDeletePreview: (_key, vmId) => request("GET", vm(vmId, "/delete-preview")),
    deleteVm: (_key, vmId, body) => request("POST", vm(vmId, "/delete"), body),
    getHostResources: () => request("GET", "/host/resources"),
    listSwitches: () => request("GET", "/switches"),
    getElevation: () => request("GET", "/auth/elevation"),
    elevate: (_key, passphrase) => request("POST", "/auth/elevation", { passphrase }),
    getJob: (_key, jobId) => request("GET", `/jobs/${encodeURIComponent(jobId)}`),
  };
}

/** Provides the tray's implementation to the shared components; called once at startup (main.ts). */
export function provideTrayHostApi() {
  provideHostApi(createTrayHostApi());
}
