import { describe, expect, it } from "vitest";
import { render } from "@testing-library/svelte";
import { createRawSnippet } from "svelte";

import Dialog from "./Dialog.svelte";

const body = createRawSnippet(() => ({ render: () => "<p>Body</p>" }));

describe("Dialog", () => {
  it("puts a prompt another dialog waits on above the others", () => {
    const { container } = render(Dialog, { title: "Admin passphrase", above: true, children: body });

    expect(container.querySelector(".backdrop")?.classList.contains("above")).toBe(true);
  });

  it("leaves ordinary dialogs on the normal layer", () => {
    const { container } = render(Dialog, { title: "Add a backup target", children: body });

    expect(container.querySelector(".backdrop")?.classList.contains("above")).toBe(false);
  });
});
