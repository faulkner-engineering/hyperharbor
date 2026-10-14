// Words for the Tailscale panel and the connection line. Pure functions, so tests check them directly.

import type { ConnectionRoute, HostTailscale, ReachabilityStep, TailnetStatus } from "$lib/api/client";

/** One line on Tailscale on this PC. */
export function tailnetSummary(status: TailnetStatus | null): string {
  if (status === null) return "Reading…";
  switch (status.state) {
    case "notInstalled":
      return "Not installed on this PC. Install Tailscale to reach hosts away from home.";
    case "unreachable":
      return status.error ? `Installed, but it did not answer: ${status.error}` : "Installed, but it did not answer.";
    case "needsLogin":
      return "Installed. Sign in to Tailscale to use it.";
    case "stopped":
      return "Installed and turned off.";
    case "running": {
      const name = status.self?.dnsName || status.self?.hostName || "this PC";
      const peers = status.peers.filter((peer) => peer.online).length;
      return `Connected as ${name}. ${peers === 1 ? "1 other device is" : `${peers} other devices are`} online.`;
    }
    default:
      return "Starting, or waiting for approval.";
  }
}

/** One line on the host's Tailscale, from its own report. */
export function hostTailscaleSummary(info: HostTailscale | null): string {
  if (info === null) return "Reading…";
  switch (info.state) {
    case "notInstalled":
      return "Tailscale is not installed on the host. It is optional.";
    case "unreachable":
      return "Tailscale is installed on the host but did not answer.";
    case "needsLogin":
      return "Tailscale on the host needs a sign-in.";
    case "stopped":
      return "Tailscale on the host is turned off.";
    case "running": {
      const where = info.dnsName ?? info.ipv4 ?? "the tailnet";
      return info.tagged ? `Connected as ${where}, tagged ${info.tag}.` : `Connected as ${where}, but not tagged ${info.tag}.`;
    }
    default:
      return "Tailscale on the host is starting.";
  }
}

/** Where the last request went: "Local network", "Tailscale, direct", "Tailscale, relayed through fra". */
export function routeText(route: ConnectionRoute | null): string {
  if (route === null || route.via === "unknown") return "No connection made yet.";
  if (route.via === "lan") return `Local network${route.address ? ` (${route.address})` : ""}`;
  // No path means there is none to report: the host is this PC, or this PC has not seen it as a peer.
  const path = route.path === "direct" ? ", direct" : route.path === "relayed" ? `, relayed${route.relay ? ` through ${route.relay}` : ""}` : route.path === "unknown" ? ", path not decided yet" : "";
  return `Tailscale${path}${route.address ? ` (${route.address})` : ""}`;
}

export function stepMark(status: ReachabilityStep["status"]): string {
  return status === "pass" ? "OK" : status === "warn" ? "Check" : "Fix";
}

/** The first step that did not pass, or null when every step did. */
export function firstProblem(steps: ReachabilityStep[]): ReachabilityStep | null {
  return steps.find((step) => step.status === "fail") ?? steps.find((step) => step.status === "warn") ?? null;
}

/** "Reachable over Tailscale", "Reachable, but relayed", or the first thing that failed. */
export function reachabilityVerdict(steps: ReachabilityStep[]): string {
  const problem = firstProblem(steps);
  if (problem === null) return "Reachable over Tailscale.";
  if (problem.status === "warn") return problem.id === "path" ? "Reachable over Tailscale, but not by the fastest path." : "Reachable over Tailscale, with a note.";
  return `Not reachable: ${problem.title.toLowerCase()} failed.`;
}
