import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/svelte";

const appWindow = vi.hoisted(() => ({
  minimize: vi.fn(),
  toggleMaximize: vi.fn(),
  close: vi.fn(),
  isMaximized: vi.fn(),
  onResized: vi.fn(),
}));
vi.mock("@tauri-apps/api/window", () => ({ getCurrentWindow: () => appWindow }));

import TitleBar from "./TitleBar.svelte";

describe("TitleBar", () => {
  let resized: () => void;

  beforeEach(() => {
    vi.clearAllMocks();
    appWindow.isMaximized.mockResolvedValue(false);
    appWindow.onResized.mockImplementation(async (handler: () => void) => {
      resized = handler;
      return () => {};
    });
  });

  it("minimizes, maximizes, and closes the window", async () => {
    render(TitleBar);

    await fireEvent.click(screen.getByRole("button", { name: "Minimize" }));
    await fireEvent.click(screen.getByRole("button", { name: "Maximize" }));
    await fireEvent.click(screen.getByRole("button", { name: "Close" }));

    expect(appWindow.minimize).toHaveBeenCalledOnce();
    expect(appWindow.toggleMaximize).toHaveBeenCalledOnce();
    expect(appWindow.close).toHaveBeenCalledOnce();
  });

  it("offers Restore once the window is maximized", async () => {
    render(TitleBar);
    await waitFor(() => expect(appWindow.onResized).toHaveBeenCalled());

    appWindow.isMaximized.mockResolvedValue(true);
    resized();

    expect(await screen.findByRole("button", { name: "Restore" })).toBeTruthy();
  });

  it("drags the window from the bar and its brand, never from the buttons", () => {
    const { container } = render(TitleBar);

    expect(container.querySelector(".titlebar")?.hasAttribute("data-tauri-drag-region")).toBe(true);
    expect(container.querySelector(".brand")?.hasAttribute("data-tauri-drag-region")).toBe(true);
    for (const button of screen.getAllByRole("button")) {
      expect(button.hasAttribute("data-tauri-drag-region")).toBe(false);
    }
  });
});
