import { describe, expect, it } from "vitest";
import type { GameAwareInfo, GameAwareSettings } from "$lib/api/client";
import { addPrograms, effectiveAction, gamingNotice, normalizeProgram, withVmAction } from "./gameAware";

const settings = (overrides: Partial<GameAwareSettings> = {}): GameAwareSettings => ({
  enabled: true,
  defaultAction: "pause",
  allowlist: [],
  ignoreList: [],
  vmPolicies: [],
  ...overrides,
});

const info = (overrides: Partial<GameAwareSettings> = {}): GameAwareInfo => ({
  settings: settings(overrides),
  state: { enabled: true, gaming: false, changedVms: [], sessionVms: [] },
  vms: [
    { vmId: "a", name: "Plain", gpuPartition: false },
    { vmId: "g", name: "Gpu", gpuPartition: true },
  ],
});

describe("program names", () => {
  it.each([
    ["Game.EXE", "game"],
    [" C:\\Games\\Foo\\foo.exe ", "foo"],
    ["/usr/bar", "bar"],
    ["", ""],
  ])("normalizes %s", (text, expected) => expect(normalizeProgram(text)).toBe(expected));

  it("adds a pasted list without duplicates or blanks", () => {
    expect(addPrograms(["foo"], "Foo.exe, bar;\nBaz.exe\n\n")).toEqual(["foo", "bar", "baz"]);
  });
});

describe("per VM actions", () => {
  it("stores a different action and drops the entry when it matches the default again", () => {
    const changed = withVmAction(settings(), "a", "save", false);
    expect(changed.vmPolicies).toEqual([{ vmId: "a", action: "save", alwaysPause: false }]);

    expect(withVmAction(changed, "a", "pause", false).vmPolicies).toEqual([]);
  });

  it("keeps an entry that only differs by always pause", () => {
    expect(withVmAction(settings(), "a", "pause", true).vmPolicies).toEqual([{ vmId: "a", action: "pause", alwaysPause: true }]);
  });

  it("shows pause for a GPU VM whatever was chosen", () => {
    const stored = info({ vmPolicies: [{ vmId: "g", action: "keep", alwaysPause: false }] });

    expect(effectiveAction(stored, "g")).toEqual({ action: "pause", alwaysPause: false, forced: true });
    expect(effectiveAction(stored, "a").action).toBe("pause");
  });
});

describe("gamingNotice", () => {
  const state = { enabled: true, gaming: true, process: "eldenring", changedVms: ["Dev"], sessionVms: ["Work"] };

  it("says nothing when no game runs", () => {
    expect(gamingNotice({ ...state, gaming: false })).toBeNull();
  });

  it("names the game, the changed VMs, and the VMs left running for a connected user", () => {
    expect(gamingNotice(state)).toBe(
      "The host is gaming (eldenring). Paused or saved: Dev. Still running because you are connected: Work. They may be slow.",
    );
  });
});
