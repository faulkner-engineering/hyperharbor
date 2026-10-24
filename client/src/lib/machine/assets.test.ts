import { afterEach, describe, expect, it } from "vitest";
import { parseExplodedSvg, readPalette } from "./assets";

describe("parseExplodedSvg", () => {
  it("reads one path per node", () => {
    const still = parseExplodedSvg(
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50"><title>x</title>' +
        '<g data-node="CPU"><path d="M0 0L1 1"/></g><g data-node="TPM_LED"><path d="M2 2L3 3"/><path d="M4 4L5 5"/></g></svg>',
    );
    expect(still).toEqual({
      viewBox: "0 0 100 50",
      parts: [
        { node: "CPU", d: "M0 0L1 1" },
        { node: "TPM_LED", d: "M2 2L3 3M4 4L5 5" },
      ],
    });
  });

  it("rejects a document that is not SVG", () => {
    expect(() => parseExplodedSvg("not svg")).toThrow();
  });
});

describe("readPalette", () => {
  afterEach(() => {
    document.documentElement.removeAttribute("style");
  });

  it("reads the brand tokens", () => {
    document.documentElement.style.setProperty("--hh-crt-amber-glow", "#e39b2e");
    document.documentElement.style.setProperty("--hh-crt-green-glow", "#00ff00");
    expect(readPalette("amber").glow).toBe("#e39b2e");
    expect(readPalette("green").glow).toBe("#00ff00");
  });

  it("falls back when a token is missing", () => {
    expect(readPalette("green")).toEqual({ background: "#0b0d10", line: "#2a5a3a", lineDim: "#163322", glow: "#7cff9a" });
  });
});
