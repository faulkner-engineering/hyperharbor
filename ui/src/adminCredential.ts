import type { VmAdminCredentialAlert } from "./api/model.ts";

/** How the VM list shows a stored administrator credential that needs attention. */
export interface AdminAlertText {
  label: string;
  /** The longer explanation, shown as the badge's tooltip. */
  title: string;
  /** "danger" when HyperHarbor can no longer manage the VM, "warn" while there is still time. */
  tone: "danger" | "warn";
}

/** "December 4" in the reader's language. */
export function formatAlertDate(iso: string): string {
  return new Date(iso).toLocaleDateString([], { month: "long", day: "numeric" });
}

/**
 * The badge for a VM whose stored administrator credential the guest refused, or whose password expires soon. Windows
 * expires a local account's password after 42 days unless told otherwise, and HyperHarbor cannot sign in with an
 * expired one.
 */
export function adminAlertText(alert: VmAdminCredentialAlert, now: number = Date.now()): AdminAlertText {
  const fix = "Choose Administrator account… in the VM's menu";
  const expires = alert.passwordExpiresAt;
  if (alert.kind === "rejected") {
    return {
      label: "Admin password rejected",
      title:
        `The VM did not accept the administrator account HyperHarbor has stored for it${expires && Date.parse(expires) <= now ? `; its password expired on ${formatAlertDate(expires)}` : ""}. ` +
        `HyperHarbor cannot manage the VM until it has a working one, and Connect reuses the last password. ${fix} to store one.`,
      tone: "danger",
    };
  }

  if (expires && Date.parse(expires) <= now) {
    return {
      label: "Admin password expired",
      title: `The password of the administrator account HyperHarbor uses in this VM expired on ${formatAlertDate(expires)}. ${fix} to check it or store a working one.`,
      tone: "danger",
    };
  }

  return {
    label: expires ? `Admin password expires ${formatAlertDate(expires)}` : "Admin password expires soon",
    title:
      `The password of the administrator account HyperHarbor uses in this VM expires${expires ? ` on ${formatAlertDate(expires)}` : " soon"}. ` +
      `After that HyperHarbor cannot manage the VM. ${fix} to turn expiry off.`,
    tone: "warn",
  };
}
