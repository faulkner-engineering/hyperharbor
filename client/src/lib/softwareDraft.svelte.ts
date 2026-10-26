// The software sections (install, remove, tweaks, browser) as one editable draft, shared by the setup profile
// editor and the machine profile editor.
import {
  errorMessage,
  getSetupProfileCatalog,
  listExtensionCatalog,
  type ExtensionCatalogItem,
  type ProfileItem,
  type RegistryTweak,
  type SetupProfile,
  type SetupProfileCatalog,
} from "$lib/api/client";
import { SvelteMap } from "svelte/reactivity";

/** The software part of a setup or machine profile. */
export type Software = Pick<SetupProfile, "install" | "remove" | "tweaks" | "browser">;

/** A section of the profile editor's list. section is the machine view's target (a key of targets.json). */
export interface WorkspaceSection {
  id: string;
  label: string;
  hint: string;
  section: string;
  count: number | null;
  /** A heading shown above the first section of each group. */
  group?: string;
}

export const SOFTWARE_TABS = ["packages", "remove", "tweaks", "browser"];

export function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? "" : "s"}`;
}

export class SoftwareDraft {
  install = $state<ProfileItem[]>([]);
  appx = $state<ProfileItem[]>([]);
  capabilities = $state<ProfileItem[]>([]);
  features = $state<ProfileItem[]>([]);
  tweakIds = $state<string[]>([]);
  custom = $state<{ registry: RegistryTweak; name?: string }[]>([]);
  browserApp = $state("");
  extensions = $state<ProfileItem[]>([]);
  /** Make the browser the default (hosts with DefaultBrowserApiVersion). */
  makeDefault = $state(false);
  readonly policies = new SvelteMap<string, string>();

  catalog = $state<SetupProfileCatalog | null>(null);
  extensionCatalog = $state<ExtensionCatalogItem[]>([]);

  readonly browser = $derived(
    this.catalog?.browsers.find((item) => item.wingetId === this.browserApp || item.alias === this.browserApp) ?? null,
  );
  readonly browserPolicies = $derived(
    this.browser ? (this.catalog?.policies ?? []).filter((policy) => policy.appliesTo.includes(this.browser!.id)) : [],
  );
  readonly removals = $derived(this.appx.length + this.capabilities.length + this.features.length);
  readonly tweakCount = $derived(this.tweakIds.length + this.custom.length);

  constructor(start: Software = {}) {
    this.fill(start);
  }

  /** Replaces every software section with the given ones ("Fill from" a setup profile). */
  fill(start: Software) {
    this.install = start.install ?? [];
    this.appx = start.remove?.appx ?? [];
    this.capabilities = start.remove?.capabilities ?? [];
    this.features = start.remove?.features ?? [];
    this.tweakIds = (start.tweaks ?? []).flatMap((tweak) => (tweak.id ? [tweak.id] : []));
    this.custom = (start.tweaks ?? []).flatMap((tweak) => (tweak.registry ? [{ registry: tweak.registry, name: tweak.name }] : []));
    this.browserApp = start.browser?.app.id ?? "";
    this.extensions = start.browser?.extensions ?? [];
    this.makeDefault = start.browser?.makeDefault ?? false;
    this.policies.clear();
    for (const [key, value] of Object.entries(start.browser?.policies ?? {})) this.policies.set(key, value);
  }

  /** Loads the host's catalogs; returns the error message, or null. */
  async load(hostKey: string): Promise<string | null> {
    try {
      [this.catalog, this.extensionCatalog] = await Promise.all([getSetupProfileCatalog(hostKey), listExtensionCatalog(hostKey)]);
      return null;
    } catch (e) {
      return errorMessage(e);
    }
  }

  get empty(): boolean {
    return this.install.length + this.removals + this.tweakCount === 0 && this.browserApp === "";
  }

  toSoftware(): Software {
    const tweakNames = new Map((this.catalog?.tweaks ?? []).map((tweak) => [tweak.id, tweak.name]));
    const browser = this.browser;
    return {
      install: this.install,
      remove: { appx: this.appx, capabilities: this.capabilities, features: this.features },
      tweaks: [
        ...this.tweakIds.map((id) => ({ id, name: tweakNames.get(id) })),
        ...this.custom.map((item) => ({ registry: item.registry, name: item.name })),
      ],
      browser: browser
        ? {
            app: { id: browser.wingetId, name: browser.name },
            extensions: this.extensions,
            policies: Object.fromEntries([...this.policies].filter(([key]) => this.browserPolicies.some((policy) => policy.key === key))),
            // Only when set: hosts before 1.36.0 do not know the field.
            ...(this.makeDefault ? { makeDefault: true } : {}),
          }
        : undefined,
    };
  }

  /** The four software sections of the editor's list. */
  sections(group?: string): WorkspaceSection[] {
    return [
      { id: "packages", label: "Install", hint: "Drive bay: one drive per app", section: "packages", count: this.install.length, group },
      { id: "remove", label: "Remove", hint: "Drive bay: debloat", section: "debloat", count: this.removals },
      { id: "tweaks", label: "Tweaks", hint: "The whole machine", section: "overview", count: this.tweakCount },
      { id: "browser", label: "Browser", hint: "Screen: tabs", section: "browser", count: this.browserApp ? this.extensions.length : null },
    ];
  }

  /** The HUD line's detail for a software section. */
  hud(tab: string): string {
    switch (tab) {
      case "packages":
        return `${plural(this.install.length, "package")}, ${Math.min(this.install.length, 4)} of 4 drives lit`;
      case "remove":
        return plural(this.removals, "removal");
      case "tweaks":
        return plural(this.tweakCount, "tweak");
      default:
        return this.browser ? `${this.browser.name}, ${plural(this.extensions.length, "extension")}` : "no browser";
    }
  }
}
