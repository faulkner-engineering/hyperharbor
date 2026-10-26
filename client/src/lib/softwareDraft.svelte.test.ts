import { describe, expect, it, vi } from "vitest";

vi.mock("@tauri-apps/api/core", () => ({ invoke: vi.fn() }));

import type { SetupProfileCatalog } from "$lib/api/client";
import { SoftwareDraft } from "./softwareDraft.svelte";

const catalog = {
  browsers: [{ id: "brave", name: "Brave", wingetId: "Brave.Brave", alias: "brave" }],
  policies: [],
  tweaks: [],
  appx: [],
} as unknown as SetupProfileCatalog;

describe("SoftwareDraft", () => {
  it("keeps makeDefault, and sends it only when it is on", () => {
    const draft = new SoftwareDraft({ browser: { app: { id: "Brave.Brave" }, makeDefault: true } });
    draft.catalog = catalog;

    expect(draft.makeDefault).toBe(true);
    expect(draft.toSoftware().browser?.makeDefault).toBe(true);

    draft.makeDefault = false;
    expect(draft.toSoftware().browser).not.toHaveProperty("makeDefault");
  });
});
