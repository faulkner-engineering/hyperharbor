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
    document.documentElement.style.setProperty("--hh-crt-amber-glow", "#ffaa00");
    expect(readPalette().glow).toBe("#ffaa00");
  });

  it("falls back when a token is missing", () => {
    expect(readPalette()).toEqual({ background: "#0b0d10", line: "#3a4250", lineDim: "#1f252d", glow: "#e39b2e" });
  });
});
