import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/svelte";
import { SvelteMap } from "svelte/reactivity";

const invoke = vi.hoisted(() => vi.fn());
vi.mock("@tauri-apps/api/core", () => ({ invoke }));
vi.mock("@tauri-apps/api/event", () => ({ listen: vi.fn() }));

import BrowserSection from "./BrowserSection.svelte";
import DetailCard from "./DetailCard.svelte";
import InstallSection from "./InstallSection.svelte";
import RemoveSection from "./RemoveSection.svelte";
import TweaksSection from "./TweaksSection.svelte";
import type { Detail } from "$lib/catalogBrowser";
import type { HostEntry, ProfileItem, Vm } from "$lib/api/client";

const host: HostEntry = {
  key: "mdns:host",
  displayName: "TC-PC",
  hostId: null,
  hostName: null,
  addresses: [],
  port: 48443,
  apiVersion: "1.26.0",
  source: "discovered",
  isLocal: false,
  paired: true,
  canWake: false,
};

const vm = { id: "807223df-0aeb-40a8-9d1c-223878114632", name: "Dev Box", state: "running", guestOs: { family: "windows", name: "Windows 11 Pro" } } as Vm;

/** Answers commands by name; unknown commands reject so a test notices them. */
function answer(responses: Record<string, (args: Record<string, unknown>) => unknown>) {
  invoke.mockImplementation(async (command: string, args: Record<string, unknown>) => {
    const key = command === "get_host_resource" ? `resource:${args.resource as string}` : command;
    const respond = responses[key];
    if (!respond) throw new Error(`unexpected ${key}`);
    return respond(args);
  });
}

const calls = (command: string) => invoke.mock.calls.filter(([name]) => name === command).map(([, args]) => args);
const rows = (list: string) => within(screen.getByRole("list", { name: list })).getAllByRole("listitem");

beforeEach(() => {
  invoke.mockReset();
});

describe("InstallSection", () => {
  const catalog = [
    { alias: "git", id: "Git.Git", name: "Git", description: "Version control.", category: "development", popular: true },
    { alias: "putty", id: "PuTTY.PuTTY", name: "PuTTY", description: "SSH and telnet client.", category: "development", popular: false },
    { alias: "vlc", id: "VideoLAN.VLC", name: "VLC media player", description: null, category: "media", popular: false },
  ];

  function open(install: ProfileItem[] = []) {
    const onselect = vi.fn();
    const props = $state({ host, install, selected: null, onselect });
    render(InstallSection, props);
    return { props, onselect };
  }

  it("lists the whole catalog, not only the popular packages, and filters by chip and text", async () => {
    answer({ "resource:packageCatalog": () => catalog });
    open();

    await waitFor(() => expect(rows("Package catalog")).toHaveLength(3));
    await fireEvent.click(screen.getByRole("radio", { name: /Popular/ }));
    expect(rows("Package catalog").map((row) => row.textContent)).toEqual([expect.stringContaining("Git")]);

    await fireEvent.click(screen.getByRole("radio", { name: /All/ }));
    await fireEvent.input(screen.getByRole("searchbox", { name: "Package catalog" }), { target: { value: "ssh" } });
    expect(rows("Package catalog")).toHaveLength(1);
    expect(screen.getByText("SSH and telnet client.")).toBeTruthy();
  });

  it("adds and removes from the list, shows chips, and opens details", async () => {
    answer({ "resource:packageCatalog": () => catalog });
    const { props, onselect } = open([{ id: "git" }]);
    await waitFor(() => expect(rows("Package catalog")).toHaveLength(3));

    // The profile lists Git by its alias; the catalog row still shows it as added.
    expect(within(screen.getByRole("list", { name: "Package catalog" })).getByRole("button", { name: "Remove Git" })).toBeTruthy();
    expect(within(screen.getByRole("list", { name: "In this profile" })).getByText("Git")).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "Add PuTTY" }));
    expect(props.install.map((item) => item.id)).toEqual(["git", "PuTTY.PuTTY"]);
    expect(within(screen.getByRole("list", { name: "In this profile" })).getByText("PuTTY")).toBeTruthy();

    await fireEvent.click(screen.getByRole("button", { name: /^VLC media player/ }));
    const detail = onselect.mock.lastCall![0] as Detail;
    expect([detail.title, detail.code, detail.isOn()]).toEqual(["VLC media player", "VideoLAN.VLC", false]);
    detail.toggle!();
    expect(props.install.at(-1)).toEqual({ id: "VideoLAN.VLC", name: "VLC media player" });
  });

  it("adds a typed winget id, and searches winget for what the catalog lacks", async () => {
    answer({
      "resource:packageCatalog": () => catalog,
      search_packages: () => {
        throw { code: "api", message: "not set up", status: 409, problemCode: "wingetUnavailable", issues: [] };
      },
    });
    const { props } = open();
    await waitFor(() => expect(rows("Package catalog")).toHaveLength(3));

    await fireEvent.input(screen.getByRole("searchbox", { name: "Package catalog" }), { target: { value: "Valve.Steam" } });
    expect(screen.getByText(/Nothing in the catalog matches/)).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "Add Valve.Steam by id" }));
    expect(props.install).toEqual([{ id: "Valve.Steam", name: undefined }]);

    await fireEvent.click(screen.getByRole("button", { name: 'Search winget for "Valve.Steam"' }));
    expect(await screen.findByText(/Set up package search/)).toBeTruthy();
    expect(calls("search_packages")).toEqual([{ key: host.key, query: "Valve.Steam" }]);
  });

  it("lists winget results in the same place", async () => {
    answer({
      "resource:packageCatalog": () => catalog,
      search_packages: () => [{ id: "Valve.Steam", name: "Steam", version: "2.10", source: "winget" }],
    });
    const { props } = open();
    await waitFor(() => expect(rows("Package catalog")).toHaveLength(3));

    await fireEvent.input(screen.getByRole("searchbox", { name: "Package catalog" }), { target: { value: "steam" } });
    await fireEvent.click(screen.getByRole("button", { name: 'Search winget for "steam"' }));
    await fireEvent.click(await screen.findByRole("button", { name: "Add Steam" }));
    expect(props.install).toEqual([{ id: "Valve.Steam", name: "Steam" }]);
  });
});

describe("RemoveSection", () => {
  const catalog = [
    { name: "Microsoft.BingNews", friendlyName: "Microsoft News", publisher: "Microsoft", rating: "safe" as const, note: null },
    { name: "Clipchamp.Clipchamp", friendlyName: "Clipchamp", publisher: "Clipchamp", rating: "safe" as const, note: null },
    { name: "Microsoft.Paint", friendlyName: "Paint", publisher: "Microsoft", rating: "caution" as const, note: null },
    { name: "Microsoft.WindowsStore", friendlyName: "Microsoft Store", publisher: "Microsoft", rating: "keep" as const, note: "Apps need it." },
  ];

  function open(vms: Vm[] = []) {
    const props = $state({ host, vms, catalog, appx: [] as ProfileItem[], capabilities: [] as ProfileItem[], features: [] as ProfileItem[], selected: null, onselect: vi.fn() });
    render(RemoveSection, props);
    return props;
  }

  it("browses the rated catalog without a VM, and removes all safe apps at once", async () => {
    const props = open();

    expect(rows("Built-in apps")).toHaveLength(4);
    await fireEvent.click(screen.getByRole("radio", { name: /Caution/ }));
    expect(rows("Built-in apps")).toHaveLength(1);

    await fireEvent.click(screen.getByRole("button", { name: "Remove all safe" }));
    expect(props.appx.map((item) => item.id).sort()).toEqual(["Clipchamp.Clipchamp", "Microsoft.BingNews"]);
  });

  it("locks apps rated keep until allowed", async () => {
    const props = open();
    const store = () => screen.getByRole("button", { name: "Remove Microsoft Store" }) as HTMLButtonElement;

    expect(store().disabled).toBe(true);
    await fireEvent.click(screen.getByRole("checkbox", { name: /Allow removing apps rated keep/ }));
    await fireEvent.click(store());
    expect(props.appx).toEqual([{ id: "Microsoft.WindowsStore", name: "Microsoft Store" }]);
  });

  it("marks what a read VM has", async () => {
    answer({
      list_vm_appx: () => ({
        vmId: vm.id,
        build: "10.0.26100.2033",
        edition: "Professional",
        baseline: null,
        removedFromBaseline: [],
        packages: [{ name: "Microsoft.BingNews", friendlyName: "Microsoft News", version: "1", publisher: "Microsoft", rating: "safe", note: null, inBaseline: null }],
      }),
    });
    open([vm]);

    await fireEvent.click(screen.getByRole("button", { name: "Read its apps" }));
    expect(await screen.findByText(/No clean baseline/)).toBeTruthy();
    expect(screen.getByText("Microsoft · in this VM")).toBeTruthy();
    expect(screen.getAllByText("Microsoft · not in this VM").length).toBeGreaterThan(0);
  });
});

describe("TweaksSection and DetailCard", () => {
  const catalog = [
    {
      id: "explorer.showFileExtensions",
      name: "Show file name extensions",
      category: "explorer",
      note: null,
      values: [{ key: "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", name: "HideFileExt", type: "dword" as const, value: "0", default: "1" }],
    },
  ];

  it("shows what a tweak writes and turns it on from the details", async () => {
    const onselect = vi.fn();
    const props = $state({ catalog, tweakIds: [] as string[], custom: [], selected: null, onselect });
    render(TweaksSection, props);

    await fireEvent.click(screen.getByRole("button", { name: /^Show file name extensions/ }));
    const detail = onselect.mock.lastCall![0] as Detail;
    render(DetailCard, { detail, onclose: vi.fn() });

    expect(screen.getByText("HideFileExt = 0 (dword; clean install: 1)")).toBeTruthy();
    await fireEvent.click(screen.getByRole("button", { name: "Turn on" }));
    expect(props.tweakIds).toEqual(["explorer.showFileExtensions"]);
    expect(screen.getByText("In this profile")).toBeTruthy();
  });
});

describe("BrowserSection", () => {
  const browsers = [
    { id: "chrome", name: "Google Chrome", wingetId: "Google.Chrome", alias: "chrome" },
    { id: "edge", name: "Microsoft Edge", wingetId: "Microsoft.Edge", alias: "edge" },
  ];

  function open(browserApp = "Google.Chrome") {
    const props = $state({
      host,
      browsers,
      browser: browsers.find((item) => item.wingetId === browserApp) ?? null,
      browserPolicies: [],
      extensionCatalog: [],
      browserApp,
      extensions: [] as ProfileItem[],
      policies: new SvelteMap<string, string>(),
      selected: null,
      onselect: vi.fn(),
    });
    render(BrowserSection, props);
    return props;
  }

  it("looks up a pasted store link from the filter box", async () => {
    answer({
      resolve_extension: () => ({
        store: "chrome",
        id: "ghbmnnjooekpmoecnnnilnnbdlolhkhi",
        profileId: "ghbmnnjooekpmoecnnnilnnbdlolhkhi",
        name: "Google Docs Offline",
        iconDataUrl: null,
        inCatalog: false,
      }),
    });
    const props = open();
    const link = "https://chromewebstore.google.com/detail/x/ghbmnnjooekpmoecnnnilnnbdlolhkhi";

    await fireEvent.input(screen.getByRole("searchbox", { name: "Extension catalog" }), { target: { value: link } });
    await fireEvent.click(screen.getByRole("button", { name: "Look up this extension" }));

    await waitFor(() => expect(props.extensions).toEqual([{ id: "ghbmnnjooekpmoecnnnilnnbdlolhkhi", name: "Google Docs Offline" }]));
    expect(calls("resolve_extension")).toEqual([{ key: host.key, input: link }]);
  });

  it("refuses an Edge-only extension for another browser", async () => {
    answer({
      resolve_extension: () => ({
        store: "edge",
        id: "odfafepnkmbhccpbejgmiehpchacaeak",
        profileId: "edge:odfafepnkmbhccpbejgmiehpchacaeak",
        name: "uBlock Origin",
        iconDataUrl: null,
        inCatalog: true,
      }),
    });
    const props = open();

    await fireEvent.input(screen.getByRole("searchbox", { name: "Extension catalog" }), { target: { value: "edge:odfafepnkmbhccpbejgmiehpchacaeak" } });
    await fireEvent.click(screen.getByRole("button", { name: "Look up this extension" }));

    expect(await screen.findByText(/works only in Microsoft Edge/)).toBeTruthy();
    expect(props.extensions).toEqual([]);
  });

  it("chooses the browser from the segmented list", async () => {
    const props = open("");

    expect(screen.getByText(/Choose a browser/)).toBeTruthy();
    await fireEvent.click(screen.getByRole("radio", { name: "Microsoft Edge" }));
    expect(props.browserApp).toBe("Microsoft.Edge");
  });
});
