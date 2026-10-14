import { describe, expect, it } from "vitest";
import type { ConnectionRoute, HostTailscale, ReachabilityStep, TailnetStatus } from "$lib/api/client";
import { firstProblem, hostTailscaleSummary, reachabilityVerdict, routeText, tailnetSummary } from "./tailscale";

const status = (overrides: Partial<TailnetStatus> = {}): TailnetStatus => ({
  state: "running",
  self: { nodeId: "n1", hostName: "LAPTOP", dnsName: "laptop.tail1.ts.net", addresses: ["100.64.0.9"], tags: [] },
  magicDnsSuffix: "tail1.ts.net",
  peers: [
    { node: { nodeId: "n2", hostName: "pc", dnsName: "pc.tail1.ts.net", addresses: ["100.64.0.1"], tags: [] }, online: true, path: "direct", relay: null },
    { node: { nodeId: "n3", hostName: "old", dnsName: "old.tail1.ts.net", addresses: ["100.64.0.2"], tags: [] }, online: false, path: "unknown", relay: null },
  ],
  error: null,
  ...overrides,
});

const hostInfo = (overrides: Partial<HostTailscale> = {}): HostTailscale => ({
  state: "running",
  ipv4: "100.64.0.1",
  dnsName: "pc.tail1.ts.net",
  tagged: true,
  tag: "tag:hyperharbor-host",
  upCommand: "tailscale up --advertise-tags=tag:hyperharbor-host",
  config: { localApiPort: 41112 },
  ...overrides,
});

const step = (id: string, state: ReachabilityStep["status"]): ReachabilityStep => ({ id, title: `Step ${id}`, status: state, detail: null });

describe("tailscale text", () => {
  it("describes Tailscale on this PC in each state", () => {
    expect(tailnetSummary(null)).toBe("Reading…");
    expect(tailnetSummary(status({ state: "notInstalled", self: null }))).toContain("Not installed");
    expect(tailnetSummary(status({ state: "unreachable", self: null, error: "daemon not running" }))).toContain("daemon not running");
    expect(tailnetSummary(status({ state: "needsLogin", self: null }))).toContain("Sign in");
    expect(tailnetSummary(status({ state: "stopped", self: null }))).toContain("turned off");
    expect(tailnetSummary(status())).toBe("Connected as laptop.tail1.ts.net. 1 other device is online.");
  });

  it("describes the Tailscale of the host, with and without the tag", () => {
    expect(hostTailscaleSummary(null)).toBe("Reading…");
    expect(hostTailscaleSummary(hostInfo())).toBe("Connected as pc.tail1.ts.net, tagged tag:hyperharbor-host.");
    expect(hostTailscaleSummary(hostInfo({ tagged: false }))).toContain("but not tagged");
    expect(hostTailscaleSummary(hostInfo({ state: "notInstalled" }))).toContain("optional");
  });

  it("names the route a connection took", () => {
    const route = (overrides: Partial<ConnectionRoute>): ConnectionRoute => ({ via: "unknown", address: null, path: null, relay: null, hint: null, ...overrides });
    expect(routeText(null)).toBe("No connection made yet.");
    expect(routeText(route({}))).toBe("No connection made yet.");
    expect(routeText(route({ via: "lan", address: "192.168.0.5" }))).toBe("Local network (192.168.0.5)");
    expect(routeText(route({ via: "tailscale", address: "100.64.0.1", path: "direct" }))).toBe("Tailscale, direct (100.64.0.1)");
    expect(routeText(route({ via: "tailscale", address: "100.64.0.1", path: "relayed", relay: "fra" }))).toBe("Tailscale, relayed through fra (100.64.0.1)");
    expect(routeText(route({ via: "tailscale", path: "unknown" }))).toBe("Tailscale, path not decided yet");
  });

  it("gives a verdict from the first problem", () => {
    expect(reachabilityVerdict([step("a", "pass"), step("b", "pass")])).toBe("Reachable over Tailscale.");
    expect(reachabilityVerdict([step("a", "pass"), step("path", "warn")])).toBe("Reachable over Tailscale, but not by the fastest path.");
    expect(reachabilityVerdict([step("a", "pass"), step("peer", "fail")])).toBe("Not reachable: step peer failed.");
    expect(firstProblem([step("a", "warn"), step("b", "fail")])?.id).toBe("b");
    expect(firstProblem([step("a", "pass")])).toBeNull();
  });
});
