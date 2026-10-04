import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/svelte";

import Toasts from "$lib/components/Toasts.svelte";
import { MAX_TOASTS, TOAST_DURATION_MS, toasts } from "./toasts.svelte";

const messages = () => toasts.items.map((toast) => toast.message);

describe("toasts", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    toasts.clear();
    vi.useRealTimers();
  });

  it("closes a notification after five seconds", () => {
    toasts.show("Starting Dev Box…");

    vi.advanceTimersByTime(TOAST_DURATION_MS - 1);
    expect(messages()).toEqual(["Starting Dev Box…"]);
    vi.advanceTimersByTime(1);
    expect(messages()).toEqual([]);
  });

  it("keeps at most two, discarding the oldest", () => {
    toasts.show("first");
    toasts.show("second");
    toasts.error("third");

    expect(MAX_TOASTS).toBe(2);
    expect(messages()).toEqual(["second", "third"]);
    expect(toasts.items[1].kind).toBe("error");
  });

  it("gives each notification its own five seconds", () => {
    toasts.show("first");
    vi.advanceTimersByTime(3000);
    toasts.show("second");

    vi.advanceTimersByTime(2000);
    expect(messages()).toEqual(["second"]);
    vi.advanceTimersByTime(3000);
    expect(messages()).toEqual([]);
  });

  it("closes a notification early with its close button", async () => {
    render(Toasts);
    toasts.show("Opening the console of Dev Box…");
    await vi.advanceTimersByTimeAsync(0);

    await fireEvent.click(screen.getByRole("button", { name: "Close notification" }));

    expect(screen.queryByText("Opening the console of Dev Box…")).toBeNull();
    expect(messages()).toEqual([]);
  });

  it("shows errors as alerts with a progress bar that runs for the notification's lifetime", async () => {
    const { container } = render(Toasts);
    toasts.error("The host could not be reached.");
    await vi.advanceTimersByTimeAsync(0);

    expect(screen.getByRole("alert").textContent).toContain("The host could not be reached.");
    const progress = container.querySelector(".progress") as HTMLElement;
    expect(progress.style.animationDuration).toBe(`${TOAST_DURATION_MS}ms`);
  });
});
