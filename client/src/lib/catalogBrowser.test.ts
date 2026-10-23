import { describe, expect, it } from "vitest";
import { ALL, categoryLabel, chipsFor, describeValue, filterEntries, looksLikeWingetId, POPULAR, type BrowseEntry } from "./catalogBrowser";

const entries: BrowseEntry[] = [
  { key: "Git.Git", name: "Git", category: "development", popular: true, description: "Version control." },
  { key: "Microsoft.VisualStudioCode", name: "Visual Studio Code", category: "development", popular: true, keywords: "vscode" },
  { key: "PuTTY.PuTTY", name: "PuTTY", category: "development", description: "SSH and telnet client." },
  { key: "VideoLAN.VLC", name: "VLC media player", category: "media" },
];

describe("chipsFor", () => {
  it("lists All, Popular, then categories by size", () => {
    expect(chipsFor(entries)).toEqual([
      { id: ALL, label: "All", count: 4 },
      { id: POPULAR, label: "Popular", count: 2 },
      { id: "development", label: "Development", count: 3 },
      { id: "media", label: "Media", count: 1 },
    ]);
  });

  it("leaves out Popular when nothing is popular, and follows a given order", () => {
    const plain = entries.map((entry) => ({ ...entry, popular: false }));
    expect(chipsFor(plain, ["media", "development"]).map((chip) => chip.id)).toEqual([ALL, "media", "development"]);
  });
});

describe("filterEntries", () => {
  it("keeps the chip's group", () => {
    expect(filterEntries(entries, "", POPULAR).map((entry) => entry.name)).toEqual(["Git", "Visual Studio Code"]);
    expect(filterEntries(entries, "", "media").map((entry) => entry.name)).toEqual(["VLC media player"]);
    expect(filterEntries(entries, "", ALL)).toHaveLength(4);
  });

  it("matches every word in the name, id, description, category, or keywords", () => {
    expect(filterEntries(entries, "ssh", ALL).map((entry) => entry.key)).toEqual(["PuTTY.PuTTY"]);
    expect(filterEntries(entries, "VSCODE", ALL).map((entry) => entry.key)).toEqual(["Microsoft.VisualStudioCode"]);
    expect(filterEntries(entries, "development git", ALL).map((entry) => entry.key)).toEqual(["Git.Git"]);
    expect(filterEntries(entries, "git media", ALL)).toEqual([]);
  });
});

describe("helpers", () => {
  it("recognizes winget ids", () => {
    expect(looksLikeWingetId("Valve.Steam")).toBe(true);
    expect(looksLikeWingetId("Notepad++.Notepad++")).toBe(true);
    expect(looksLikeWingetId("Microsoft.DotNet.SDK.8")).toBe(true);
    expect(looksLikeWingetId("steam")).toBe(false);
    expect(looksLikeWingetId("visual studio")).toBe(false);
  });

  it("labels categories", () => {
    expect(categoryLabel("development")).toBe("Development");
    expect(categoryLabel("")).toBe("Other");
  });

  it("prints a registry value with its clean-install value", () => {
    expect(describeValue({ key: "HKCU\\X", name: "HideFileExt", type: "dword", value: "0", default: "1" })).toBe(
      "HideFileExt = 0 (dword; clean install: 1)",
    );
    expect(describeValue({ key: "HKCU\\X", name: "", type: "string", value: "a", default: null })).toBe("(default) = a (string; clean install: absent)");
  });
});
