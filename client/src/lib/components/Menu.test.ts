import { describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";
import { createRawSnippet } from "svelte";

import Menu from "./Menu.svelte";

const items = createRawSnippet(() => ({
  render: () => `<div><button type="button">Refresh</button><button type="button" disabled>Unpair</button></div>`,
}));

function details() {
  return screen.getByText("Host ▾").closest("details")!;
}

async function openMenu() {
  render(Menu, { label: "Host ▾", ariaLabel: "Host actions", variant: "button", children: items });
  await fireEvent.click(screen.getByText("Host ▾"));
  expect(details().open).toBe(true);
}

describe("Menu", () => {
  it("closes when an item is chosen", async () => {
    await openMenu();

    await fireEvent.click(screen.getByRole("button", { name: "Refresh" }));

    expect(details().open).toBe(false);
  });

  it("stays open when a disabled item is clicked", async () => {
    await openMenu();

    await fireEvent.click(screen.getByRole("button", { name: "Unpair" }));

    expect(details().open).toBe(true);
  });

  it("closes on Escape and on a click elsewhere", async () => {
    await openMenu();
    await fireEvent.keyDown(window, { key: "Escape" });
    expect(details().open).toBe(false);

    await fireEvent.click(screen.getByText("Host ▾"));
    expect(details().open).toBe(true);
    await fireEvent.click(document.body);
    expect(details().open).toBe(false);
  });
});
