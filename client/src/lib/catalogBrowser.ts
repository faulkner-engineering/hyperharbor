// Discovery in the profile editor: one filterable list per section over the host's curated catalogs, with
// category chips, and the details shown on the machine view for the selected entry.
import type { TweakCatalogValue } from "$lib/api/client";

/** One row of a catalog list. key is what the profile stores (a winget id, an Appx name, a tweak id). */
export interface BrowseEntry {
  key: string;
  name: string;
  category: string;
  /** Shown under the name, one line. */
  description?: string | null;
  /** Shown as code beside the name, for example the winget id. */
  code?: string;
  popular?: boolean;
  /** Extra words the filter matches, for example an alias. */
  keywords?: string;
}

export const ALL = "all";
export const POPULAR = "popular";

export interface Chip {
  id: string;
  label: string;
  count: number;
}

/** "development" to "Development". */
export function categoryLabel(category: string): string {
  return category.length === 0 ? "Other" : category[0].toUpperCase() + category.slice(1);
}

/** All, then Popular when any entry is popular, then each category, the largest first. */
export function chipsFor(entries: BrowseEntry[], order?: string[]): Chip[] {
  const counts = new Map<string, number>();
  for (const entry of entries) counts.set(entry.category, (counts.get(entry.category) ?? 0) + 1);
  const categories = [...counts.keys()].sort((a, b) => {
    if (order) return order.indexOf(a) - order.indexOf(b);
    return counts.get(b)! - counts.get(a)! || a.localeCompare(b);
  });
  const popular = entries.filter((entry) => entry.popular).length;
  return [
    { id: ALL, label: "All", count: entries.length },
    ...(popular > 0 ? [{ id: POPULAR, label: "Popular", count: popular }] : []),
    ...categories.map((category) => ({ id: category, label: categoryLabel(category), count: counts.get(category)! })),
  ];
}

/** Entries in the chip's group whose text holds every word of the query, in any order and case. */
export function filterEntries(entries: BrowseEntry[], query: string, chip: string): BrowseEntry[] {
  const words = query.toLowerCase().split(/\s+/).filter((word) => word !== "");
  return entries.filter((entry) => {
    if (chip === POPULAR && !entry.popular) return false;
    if (chip !== ALL && chip !== POPULAR && entry.category !== chip) return false;
    if (words.length === 0) return true;
    const text = `${entry.name} ${entry.code ?? ""} ${entry.key} ${entry.description ?? ""} ${entry.category} ${entry.keywords ?? ""}`.toLowerCase();
    return words.every((word) => text.includes(word));
  });
}

/** Whether text could be a winget id such as Vendor.App, so the list can offer to add it as typed. */
export function looksLikeWingetId(text: string): boolean {
  return /^[A-Za-z0-9][A-Za-z0-9_+-]*(\.[A-Za-z0-9_+-]+)+$/.test(text.trim());
}

/** What the machine view's details card shows for the selected entry. */
export interface Detail {
  key: string;
  title: string;
  code?: string;
  category?: string;
  /** A short status word, for example an Appx rating. */
  badge?: string;
  description?: string | null;
  note?: string | null;
  /** The registry values a tweak writes. */
  values?: TweakCatalogValue[];
  /** Read in the template, so the card follows changes made from the list. */
  isOn: () => boolean;
  /** Null when the entry cannot be toggled from the card. */
  toggle: (() => void) | null;
  onLabel: string;
  offLabel: string;
}

/** A registry value as the details card prints it: the clean-install value when it differs. */
export function describeValue(value: TweakCatalogValue): string {
  const name = value.name === "" ? "(default)" : value.name;
  const before = value.default === null ? "absent" : value.default;
  return `${name} = ${value.value} (${value.type}; clean install: ${before})`;
}
