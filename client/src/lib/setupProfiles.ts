import type {
  DraftBrowser,
  DraftItem,
  ProfileDraft,
  ProfileItem,
  ProfileTweak,
  SetupProfile,
  UnattendedInstallStatus,
} from "$lib/api/client";

/** How many problems a ready toast lists before it says "and N more". */
export const SHOWN_PROBLEMS = 3;

/**
 * The toast for an install that finished with a setup profile: null when it had none. Problems are listed (the
 * first few), since the install is ready either way.
 */
export function setupResultMessage(vmName: string, install: UnattendedInstallStatus): { message: string; problem: boolean } | null {
  const result = install.setupResult;
  const profile = install.setupProfileName;
  if (!result || !profile) return null;
  if (result.problems.length === 0) {
    return { message: `${vmName} is ready; ${profile} applied. Press Connect.`, problem: false };
  }

  const shown = result.problems.slice(0, SHOWN_PROBLEMS).join("; ");
  const more = result.problems.length > SHOWN_PROBLEMS ? ` (and ${result.problems.length - SHOWN_PROBLEMS} more)` : "";
  const count = result.problems.length === 1 ? "1 item" : `${result.problems.length} items`;
  return { message: `${vmName} is ready, but ${count} of ${profile} could not be applied: ${shown}${more}`, problem: true };
}

/** A profile with nothing in it yet. */
export function emptyProfile(name = ""): SetupProfile {
  return { name, description: undefined, install: [], remove: { appx: [], capabilities: [], features: [] }, tweaks: [] };
}

/** Adds an item unless one with the same id (ignoring case) is there; returns the new list. */
export function addItem(items: ProfileItem[] | undefined, item: ProfileItem): ProfileItem[] {
  const list = items ?? [];
  return list.some((existing) => existing.id.toLowerCase() === item.id.toLowerCase()) ? list : [...list, item];
}

export function removeItem(items: ProfileItem[] | undefined, id: string): ProfileItem[] {
  return (items ?? []).filter((item) => item.id !== id);
}

/** What the capture review keeps: ticked items by column, and the chosen browser. */
export interface CaptureChoices {
  name: string;
  install: Set<string>;
  removeAppx: Set<string>;
  tweaks: Set<string>;
  /** The chosen browser's winget id, or null for none. */
  browser: string | null;
  extensions: Set<string>;
}

/** The choices the review screen starts with: whatever capture ticked, and the browser it chose. */
export function initialChoices(draft: ProfileDraft): CaptureChoices {
  const ticked = (items: DraftItem[]) => new Set(items.filter((item) => item.selected).map((item) => item.id));
  const browser = draft.browsers.find((item) => item.selected) ?? null;
  return {
    name: `${draft.vmName} setup`,
    install: ticked(draft.install),
    removeAppx: ticked(draft.removeAppx),
    tweaks: ticked(draft.tweaks),
    browser: browser?.app ?? null,
    extensions: ticked(browser?.extensions ?? []),
  };
}

/** The profile to save from a capture draft and the review screen's choices. */
export function draftToProfile(draft: ProfileDraft, choices: CaptureChoices): SetupProfile {
  const kept = (items: DraftItem[], ids: Set<string>): ProfileItem[] =>
    items.filter((item) => ids.has(item.id)).map((item) => ({ id: item.id, name: item.name }));
  const browser: DraftBrowser | undefined = draft.browsers.find((item) => item.app === choices.browser);
  const tweaks: ProfileTweak[] = kept(draft.tweaks, choices.tweaks);

  return {
    name: choices.name.trim(),
    description: `Captured from ${draft.vmName} (Windows ${draft.build} ${draft.edition}).`,
    install: kept(draft.install, choices.install),
    remove: { appx: kept(draft.removeAppx, choices.removeAppx), capabilities: [], features: [] },
    tweaks,
    browser: browser
      ? { app: { id: browser.app, name: browser.name }, extensions: kept(browser.extensions, choices.extensions), policies: {} }
      : undefined,
  };
}

/** A policy value as the profile stores it (text), from what a form control holds. */
export function policyValue(type: string, value: string | boolean | number): string {
  if (type === "boolean") return value === true || value === "true" ? "true" : "false";
  return String(value).trim();
}
