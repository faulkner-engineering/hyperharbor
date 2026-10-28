import type {
  BackupItemStatus,
  BackupKind,
  BackupRun,
  BackupSchedule,
  BackupSnapshot,
  BackupTarget,
  NewBackupTarget,
  RestoreTestResult,
  RestoreTestStatus,
} from "$lib/api/client";

const GROUP = 4;
/** The host asks for this many groups of the key to confirm it was saved. */
export const CONFIRM_GROUPS = 4;

/** Upper case, no dashes or spaces: how the host compares what the user typed. */
export function normalizeKey(text: string): string {
  return text.replace(/[\s-]/g, "").toUpperCase();
}

/** The groups the user types back, shown as a hint of where they are in the key ("the last 4 groups"). */
export function keyGroups(key: string): string[] {
  return key.split("-").filter((group) => group.length > 0);
}

/** True once the typed text holds the groups asked for. The host does the real comparison. */
export function confirmationComplete(typed: string): boolean {
  return normalizeKey(typed).length === GROUP * CONFIRM_GROUPS;
}

/** Four-letter groups for a key typed or pasted without dashes. */
export function groupKey(text: string): string {
  const plain = normalizeKey(text);
  const groups: string[] = [];
  for (let i = 0; i < plain.length; i += GROUP) groups.push(plain.slice(i, i + GROUP));
  return groups.join("-");
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value >= 100 || Number.isInteger(value) ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

/** "3 hours ago", "yesterday", "5 days ago". */
export function ageText(iso: string, now: Date = new Date()): string {
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000));
  if (seconds < 60) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} ${minutes === 1 ? "minute" : "minutes"} ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} ${hours === 1 ? "hour" : "hours"} ago`;
  const days = Math.round(hours / 24);
  return days === 1 ? "yesterday" : `${days} days ago`;
}

export function scheduleText(schedule: BackupSchedule): string {
  const days = schedule.days ?? [];
  if (days.length === 0 || days.length === 7) return `every day at ${schedule.time}`;
  const names = days.map((day) => day.slice(0, 3).replace(/^./, (c) => c.toUpperCase()));
  return `${names.join(", ")} at ${schedule.time}`;
}

export const KIND_LABELS: Record<BackupKind, string> = {
  local: "Folder",
  s3: "S3-compatible storage",
  azure: "Azure Blob Storage",
};

/** Where the target keeps its backups, for one line in a list. */
export function targetLocation(target: BackupTarget): string {
  if (target.local) return target.local.path;
  if (target.s3) return `${target.s3.endpoint}/${target.s3.bucket}`;
  if (target.azure) return `${target.azure.account}/${target.azure.container}`;
  return "";
}

/** The item's name for a list: the VM's name, or "Host settings". */
export function itemName(status: Pick<BackupItemStatus, "item" | "vmName">): string {
  if (status.item === "host") return "Host settings";
  return status.vmName ?? "A virtual machine";
}

/** One sentence about how an item's last run went. */
export function runSummary(status: BackupItemStatus, now: Date = new Date()): string {
  const run = status.lastRun;
  if (!run) return "Not backed up yet.";
  switch (run.outcome) {
    case "succeeded":
      return `Backed up ${ageText(run.finishedAt, now)}${uploadSuffix(run)}.`;
    case "partial":
      return `Backed up ${ageText(run.finishedAt, now)}${uploadSuffix(run)}, with a problem to look at.`;
    case "skipped":
      return `Skipped ${ageText(run.finishedAt, now)}: ${reasonText(run)}`;
    case "failed":
      return status.lastSuccessAt
        ? `Failed ${ageText(run.finishedAt, now)}: ${reasonText(run)} Last good backup ${ageText(status.lastSuccessAt, now)}.`
        : `Failed ${ageText(run.finishedAt, now)}: ${reasonText(run)}`;
  }
}

/** "uploaded 1.2 GB of 40 GB", or null when the host did not measure the upload (hosts before API 1.37.0). */
export function uploadText(run: Pick<BackupRun, "uploadedBytes" | "totalBytes">): string | null {
  if (run.uploadedBytes == null) return null;
  return `uploaded ${formatBytes(run.uploadedBytes)} of ${formatBytes(run.totalBytes)}`;
}

function uploadSuffix(run: BackupRun): string {
  const text = uploadText(run);
  return text ? ` (${text})` : "";
}

/** The warnings of a run that stored a backup; a failed or skipped run explains itself in its summary. */
export function storedRunWarnings(status: BackupItemStatus): string[] {
  const run = status.lastRun;
  return run && (run.outcome === "succeeded" || run.outcome === "partial") ? run.warnings : [];
}

function reasonText(run: BackupRun): string {
  const reason = run.reason ?? run.warnings[0] ?? "";
  if (reason === "conditionsNotMet") return run.warnings[0] ?? "The conditions for running were not met.";
  return reason.endsWith(".") || reason === "" ? reason : `${reason}.`;
}

/** Whether a target can run: its recovery key was saved and confirmed. */
export const targetReady = (target: BackupTarget): boolean => target.keyState === "confirmed";

export interface TargetForm {
  name: string;
  kind: BackupKind;
  path: string;
  endpoint: string;
  bucket: string;
  region: string;
  prefix: string;
  useTls: boolean;
  accessKey: string;
  secretKey: string;
  azureAccount: string;
  azureContainer: string;
  azureKey: string;
  time: string;
  daily: number;
  weekly: number;
  monthly: number;
  limitKbps: number | null;
}

export const emptyTargetForm = (): TargetForm => ({
  name: "",
  kind: "local",
  path: "",
  endpoint: "",
  bucket: "",
  region: "",
  prefix: "",
  useTls: true,
  accessKey: "",
  secretKey: "",
  azureAccount: "",
  azureContainer: "",
  azureKey: "",
  time: "02:00",
  daily: 7,
  weekly: 4,
  monthly: 6,
  limitKbps: null,
});

const blank = (text: string): string | undefined => (text.trim() === "" ? undefined : text.trim());

/** The request body for a form: only the settings of the chosen kind, and no empty optional text. */
export function toNewTarget(form: TargetForm): NewBackupTarget {
  const request: NewBackupTarget = {
    name: form.name.trim(),
    kind: form.kind,
    schedule: { time: form.time },
    retention: { daily: form.daily, weekly: form.weekly, monthly: form.monthly },
  };
  if (form.limitKbps !== null && form.limitKbps > 0) request.bandwidthKbps = form.limitKbps;
  if (form.kind === "local") {
    request.local = { path: form.path.trim() };
  } else if (form.kind === "s3") {
    request.s3 = {
      endpoint: form.endpoint.trim(),
      bucket: form.bucket.trim(),
      region: blank(form.region),
      prefix: blank(form.prefix),
      useTls: form.useTls,
    };
    request.credentials = { s3AccessKey: form.accessKey, s3SecretKey: form.secretKey };
  } else {
    request.azure = { account: form.azureAccount.trim(), container: form.azureContainer.trim(), prefix: blank(form.prefix) };
    request.credentials = { azureKey: form.azureKey };
  }
  return request;
}

/** What is still missing in a form, or null when it can be sent. */
export function formProblem(form: TargetForm): string | null {
  if (form.name.trim() === "") return "Give the target a name.";
  if (form.kind === "local" && form.path.trim() === "") return "Choose a folder.";
  if (form.kind === "s3") {
    if (form.endpoint.trim() === "" || form.bucket.trim() === "") return "Enter the endpoint and the bucket.";
    if (form.accessKey === "" || form.secretKey === "") return "Enter the access key and the secret key.";
  }
  if (form.kind === "azure") {
    if (form.azureAccount.trim() === "" || form.azureContainer.trim() === "") return "Enter the storage account and the container.";
    if (form.azureKey === "") return "Enter the storage account key.";
  }
  if (!/^([01]\d|2[0-3]):[0-5]\d$/.test(form.time)) return "Enter the time as HH:mm, for example 02:00.";
  if (form.daily < 0 || form.weekly < 0 || form.monthly < 0 || form.daily + form.weekly + form.monthly === 0) {
    return "Keep at least one snapshot.";
  }
  return null;
}

/** "5 Oct 2026, 02:00", from the user's locale. */
export function snapshotLabel(snapshot: BackupSnapshot): string {
  const when = new Date(snapshot.startTime).toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
  return `${when} (${formatBytes(snapshot.totalSize)})`;
}

/** A path inside a backup volume: folders joined with backslashes, no leading separator. */
export function joinPath(parent: string, name: string): string {
  return parent === "" ? name : `${parent}\\${name}`;
}

/** The folder above `path`, or "" at the volume's root. */
export function parentPath(path: string): string {
  const cut = path.lastIndexOf("\\");
  return cut < 0 ? "" : path.slice(0, cut);
}

/** The folders leading to `path`, each with the path that opens it, for a breadcrumb row. */
export function crumbs(path: string): { name: string; path: string }[] {
  const parts = path.split("\\").filter((part) => part !== "");
  return parts.map((name, index) => ({ name, path: parts.slice(0, index + 1).join("\\") }));
}

/** "Windows (E:, 120 GB)" for a volume picker. */
export function volumeLabel(volume: { drive: string; label?: string | null; sizeBytes: number }): string {
  const name = volume.label ? `${volume.label} (` : "(";
  return `${name}${volume.drive}, ${formatBytes(volume.sizeBytes)})`;
}

/** One sentence about the restore test as a whole. */
export function restoreTestSummary(status: RestoreTestStatus, now: Date = new Date()): string {
  if (status.running) return status.current ? `Testing ${status.current}…` : "Testing…";
  if (status.testable === 0) return "Nothing to test yet: no virtual machine has a backup.";
  if (!status.settings.enabled) return "The restore test is off.";
  const last = status.results[0];
  const next = status.nextDue ? new Date(status.nextDue) : null;
  const due = next === null || next.getTime() <= now.getTime() ? "Next test at the next quiet time." : `Next test after ${next.toLocaleDateString()}.`;
  return last ? `Last tested ${ageText(last.finishedAt, now)}. ${due}` : `Never tested. ${due}`;
}

/** A result for a list: the VM, how it went, and why when it did not pass. */
export function restoreTestLine(result: RestoreTestResult, now: Date = new Date()): string {
  const verdict = { passed: "Passed", failed: "Failed", skipped: "Skipped" }[result.outcome];
  const reason = result.reason && result.outcome !== "passed" ? ` ${result.reason}` : "";
  return `${verdict}: ${result.vmName}, ${ageText(result.finishedAt, now)}.${reason}`;
}

/** The interval choices as words: "every day", "every 7 days", "every month". */
export function intervalText(days: number): string {
  if (days === 1) return "every day";
  if (days === 30) return "every month";
  return `every ${days} days`;
}
