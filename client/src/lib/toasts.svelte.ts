/**
 * Short notifications shown at the bottom of the window. Each closes itself after
 * TOAST_DURATION_MS, or earlier with its close button. At most MAX_TOASTS are shown; a new one
 * discards the oldest.
 */

export type ToastKind = "info" | "error";

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
}

export const TOAST_DURATION_MS = 5000;
export const MAX_TOASTS = 2;

class Toasts {
  items = $state<Toast[]>([]);

  #timers = new Map<number, ReturnType<typeof setTimeout>>();
  #nextId = 1;

  /** Shows a notification and returns its ID. */
  show(message: string, kind: ToastKind = "info"): number {
    const toast: Toast = { id: this.#nextId++, kind, message };
    const shown = [...this.items, toast];
    for (const discarded of shown.slice(0, Math.max(0, shown.length - MAX_TOASTS))) {
      this.#clearTimer(discarded.id);
    }
    this.items = shown.slice(-MAX_TOASTS);
    this.#timers.set(
      toast.id,
      setTimeout(() => this.dismiss(toast.id), TOAST_DURATION_MS),
    );
    return toast.id;
  }

  error(message: string): number {
    return this.show(message, "error");
  }

  dismiss(id: number) {
    this.#clearTimer(id);
    this.items = this.items.filter((toast) => toast.id !== id);
  }

  clear() {
    for (const id of this.#timers.keys()) {
      clearTimeout(this.#timers.get(id));
    }
    this.#timers.clear();
    this.items = [];
  }

  #clearTimer(id: number) {
    clearTimeout(this.#timers.get(id));
    this.#timers.delete(id);
  }
}

export const toasts = new Toasts();
