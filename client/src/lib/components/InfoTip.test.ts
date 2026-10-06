import { describe, expect, it } from "vitest";
import { createRawSnippet } from "svelte";
import { fireEvent, render, screen } from "@testing-library/svelte";
import InfoTip from "./InfoTip.svelte";

const children = createRawSnippet(() => ({ render: () => "<span>Installed as the administrator.</span>" }));

const button = () => screen.getByRole("button", { name: "About admin installs" });

describe("InfoTip", () => {
  it("starts closed and opens and closes with a click", async () => {
    render(InfoTip, { label: "About admin installs", children });

    expect(button().getAttribute("aria-expanded")).toBe("false");
    await fireEvent.click(button());
    expect(button().getAttribute("aria-expanded")).toBe("true");
    expect(screen.getByRole("note").hasAttribute("hidden")).toBe(false);

    await fireEvent.click(button());
    expect(button().getAttribute("aria-expanded")).toBe("false");
  });

  it("closes on Escape and on a click elsewhere", async () => {
    render(InfoTip, { label: "About admin installs", children });

    await fireEvent.click(button());
    await fireEvent.keyDown(window, { key: "Escape" });
    expect(button().getAttribute("aria-expanded")).toBe("false");

    await fireEvent.click(button());
    await fireEvent.click(document.body);
    expect(button().getAttribute("aria-expanded")).toBe("false");
  });

  it("shows while hovered", async () => {
    render(InfoTip, { label: "About admin installs", children });

    await fireEvent.mouseEnter(button().parentElement as HTMLElement);
    expect(button().getAttribute("aria-expanded")).toBe("true");
    await fireEvent.mouseLeave(button().parentElement as HTMLElement);
    expect(button().getAttribute("aria-expanded")).toBe("false");
  });
});
