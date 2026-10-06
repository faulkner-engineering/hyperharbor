import { listen, send, type HostMessage, type PageMessage, type ToastKind, type TrayViewState } from "./bridge";

export type Page = "overview" | "devices" | "storage" | "updates" | "logs";

export interface Toast {
  id: number;
  kind: ToastKind;
  title: string;
  text: string;
}

const TOAST_MS = 6000;

/** What the window shows: the tray's latest state, the page, the open dialog, and the toasts. */
class TrayStore {
  state = $state<TrayViewState | null>(null);
  page = $state<Page>("overview");
  passphraseOpen = $state(false);
  toasts = $state<Toast[]>([]);
  /** Things this page asked for, until the tray reports them busy (so buttons react at once). */
  requested = $state<string[]>([]);

  #nextToast = 1;
  #send: (message: PageMessage) => void = send;

  /** Connects to the tray. Tests pass their own sender and feed messages to receive(). */
  start(sender: (message: PageMessage) => void = send, subscribe: (handler: (message: HostMessage) => void) => void = listen) {
    this.#send = sender;
    subscribe((message) => this.receive(message));
  }

  receive(message: HostMessage) {
    switch (message.type) {
      case "state":
        this.state = message.value;
        // Once the tray reports work as busy (or done), the page's own marker is no longer needed.
        this.requested = [];
        break;
      case "toast":
        this.toast(message.kind, message.title, message.text);
        break;
      case "navigate":
        if (message.page === "passphrase") this.passphraseOpen = true;
        else this.page = message.page as Page;
        break;
    }
  }

  send(message: PageMessage, busyKey?: string) {
    if (busyKey && !this.requested.includes(busyKey)) this.requested = [...this.requested, busyKey];
    this.#send(message);
  }

  isBusy(key: string): boolean {
    return (this.state?.busy.includes(key) ?? false) || this.requested.includes(key);
  }

  toast(kind: ToastKind, title: string, text: string) {
    const toast = { id: this.#nextToast++, kind, title, text };
    this.toasts = [...this.toasts, toast].slice(-4);
    setTimeout(() => this.dismiss(toast.id), kind === "error" ? TOAST_MS * 2 : TOAST_MS);
  }

  /** Back to the first-open state, for tests. */
  reset() {
    this.state = null;
    this.page = "overview";
    this.passphraseOpen = false;
    this.toasts = [];
    this.requested = [];
  }

  dismiss(id: number) {
    this.toasts = this.toasts.filter((toast) => toast.id !== id);
  }
}

export const tray = new TrayStore();
