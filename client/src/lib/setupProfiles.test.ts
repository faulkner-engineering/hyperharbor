import { describe, expect, it } from "vitest";
import type { AppxPackage, ProfileDraft } from "$lib/api/client";
import { addItem, debloatPreset, draftToProfile, groupByPublisher, initialChoices, policyValue, setupResultMessage } from "./setupProfiles";

const appx = (name: string, friendlyName: string, publisher: string, rating: AppxPackage["rating"]): AppxPackage => ({
  name,
  friendlyName,
  version: "1.0.0.0",
  publisher,
  rating,
  note: null,
  inBaseline: null,
});

const draft: ProfileDraft = {
  vmId: "807223df-0aeb-40a8-9d1c-223878114632",
  vmName: "Dev Box",
  build: "10.0.26100.2033",
  edition: "Professional",
  baseline: null,
  install: [
    { id: "Git.Git", name: "Git", selected: true, note: null },
    { id: "Microsoft.Edge", name: "Microsoft.Edge", selected: false, note: "Usually part of Windows or of another package." },
  ],
  removeAppx: [{ id: "Microsoft.BingNews", name: "Microsoft News", selected: true, note: null }],
  tweaks: [{ id: "explorer.showFileExtensions", name: "Show file name extensions", selected: true, note: null }],
  browsers: [
    {
      app: "Brave.Brave",
      name: "Brave",
      selected: true,
      extensions: [
        { id: "eimadpbcbfnmbkopoojfekhnkhdbieeh", name: "Dark Reader", selected: true, note: null },
        { id: "ghbmnnjooekpmoecnnnilnnbdlolhkhi", name: "Google Docs Offline", selected: false, note: "The browser installs this one by itself." },
      ],
    },
    { app: "Microsoft.Edge", name: "Microsoft Edge", selected: false, extensions: [] },
  ],
  otherPrograms: [],
  warnings: [],
};

describe("capture review", () => {
  it("starts from what capture ticked and the browser it chose", () => {
    const choices = initialChoices(draft);

    expect([...choices.install]).toEqual(["Git.Git"]);
    expect(choices.browser).toBe("Brave.Brave");
    expect([...choices.extensions]).toEqual(["eimadpbcbfnmbkopoojfekhnkhdbieeh"]);
    expect(choices.name).toBe("Dev Box setup");
  });

  it("builds the profile from the ticked items only", () => {
    const choices = initialChoices(draft);
    choices.install.add("Microsoft.Edge");
    choices.removeAppx.clear();
    choices.name = "  Dev workstation ";

    const profile = draftToProfile(draft, choices);

    expect(profile.name).toBe("Dev workstation");
    expect(profile.install).toEqual([
      { id: "Git.Git", name: "Git" },
      { id: "Microsoft.Edge", name: "Microsoft.Edge" },
    ]);
    expect(profile.remove?.appx).toEqual([]);
    expect(profile.tweaks).toEqual([{ id: "explorer.showFileExtensions", name: "Show file name extensions" }]);
    expect(profile.browser?.app).toEqual({ id: "Brave.Brave", name: "Brave" });
    expect(profile.browser?.extensions).toEqual([{ id: "eimadpbcbfnmbkopoojfekhnkhdbieeh", name: "Dark Reader" }]);
    expect(profile.description).toContain("Captured from Dev Box");
  });

  it("leaves the browser out when none is chosen", () => {
    const choices = initialChoices(draft);
    choices.browser = null;

    expect(draftToProfile(draft, choices).browser).toBeUndefined();
  });
});

describe("Appx helpers", () => {
  const packages = [
    appx("Clipchamp.Clipchamp", "Clipchamp", "Clipchamp", "safe"),
    appx("Microsoft.WindowsStore", "Microsoft Store", "Microsoft", "keep"),
    appx("Microsoft.BingNews", "Microsoft News", "Microsoft", "safe"),
    appx("Microsoft.Paint", "Paint", "Microsoft", "caution"),
    appx("Contoso.Tool", "Contoso.Tool", "Contoso", "unrated"),
  ];

  it("groups by publisher with Microsoft first and names sorted", () => {
    const groups = groupByPublisher(packages);

    expect(groups.map((group) => group.publisher)).toEqual(["Microsoft", "Clipchamp", "Contoso"]);
    expect(groups[0].packages.map((item) => item.friendlyName)).toEqual(["Microsoft News", "Microsoft Store", "Paint"]);
  });

  it("selects only safe packages for the debloat preset", () => {
    expect(debloatPreset(packages)).toEqual(["Clipchamp.Clipchamp", "Microsoft.BingNews"]);
  });
});

describe("small helpers", () => {
  it("adds an item once, ignoring case", () => {
    expect(addItem([{ id: "Git.Git" }], { id: "git.git" })).toEqual([{ id: "Git.Git" }]);
    expect(addItem(undefined, { id: "Git.Git", name: "Git" })).toEqual([{ id: "Git.Git", name: "Git" }]);
  });

  it("writes policy values as text", () => {
    expect(policyValue("boolean", true)).toBe("true");
    expect(policyValue("boolean", "false")).toBe("false");
    expect(policyValue("integer", 5)).toBe("5");
    expect(policyValue("string", " https://example.com ")).toBe("https://example.com");
  });
});

describe("setupResultMessage", () => {
  const install = {
    vmId: "5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716",
    profileId: "windows-workstation",
    os: "windows" as const,
    state: "ready" as const,
    step: "Ready",
    startedAt: "2026-10-05T12:00:00Z",
    updatedAt: "2026-10-05T12:40:00Z",
    error: null,
    setupProfileName: "Workstation",
  };
  const result = (problems: string[]) => ({ applied: 5, problems, restarted: true, finishedAt: "2026-10-05T12:40:00Z" });

  it("says the profile was applied", () => {
    expect(setupResultMessage("Dev Box", { ...install, setupResult: result([]) })).toEqual({
      message: "Dev Box is ready; Workstation applied. Press Connect.",
      problem: false,
    });
  });

  it("lists the first problems and counts the rest", () => {
    const message = setupResultMessage("Dev Box", { ...install, setupResult: result(["a: x", "b: y", "c: z", "d: w"]) });
    expect(message).toEqual({
      message: "Dev Box is ready, but 4 items of Workstation could not be applied: a: x; b: y; c: z (and 1 more)",
      problem: true,
    });
    expect(setupResultMessage("Dev Box", { ...install, setupResult: result(["a: x"]) })?.message).toContain("1 item of");
  });

  it("is null without a setup profile or before it ran", () => {
    expect(setupResultMessage("Dev Box", { ...install, setupProfileName: null, setupResult: null })).toBeNull();
    expect(setupResultMessage("Dev Box", { ...install })).toBeNull();
  });
});
