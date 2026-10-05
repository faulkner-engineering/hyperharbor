import { describe, expect, it, vi } from "vitest";

vi.mock("@tauri-apps/api/core", () => ({ invoke: vi.fn() }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import { hostSupports } from "./client";

const host = (apiVersion: string | null) => ({ apiVersion });

describe("hostSupports", () => {
  it("compares versions numerically, not as text", () => {
    expect(hostSupports(host("1.9.0"), "1.10.0")).toBe(false);
    expect(hostSupports(host("1.10.0"), "1.10.0")).toBe(true);
    expect(hostSupports(host("1.11.2"), "1.10.0")).toBe(true);
    expect(hostSupports(host("2.0.0"), "1.10.0")).toBe(true);
    expect(hostSupports(host("0.99.0"), "1.10.0")).toBe(false);
  });

  it("ignores a prerelease suffix", () => {
    expect(hostSupports(host("1.10.0-beta.1"), "1.10.0")).toBe(true);
  });

  it("assumes support when the version is unknown or unreadable", () => {
    expect(hostSupports(host(null), "1.10.0")).toBe(true);
    expect(hostSupports(host("one"), "1.10.0")).toBe(true);
  });
});
