// Pure helpers of the ambient screen (Ambient.svelte), kept apart so they can be tested.

export interface ClockParts {
  time: string;
  date: string;
}

/** The clock's two lines in the person's own locale: hours and minutes, and the weekday with the date. */
export function clockParts(now: Date): ClockParts {
  return {
    time: now.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" }),
    date: now.toLocaleDateString(undefined, { weekday: "long", month: "long", day: "numeric" }),
  };
}

/** How long a position holds before the clock jumps to the next one, in milliseconds. */
export const DRIFT_PERIOD_MS = 5 * 60_000;

// Offsets in vmin. Small enough that the clock stays centered to the eye, different enough that no pixel stays lit.
const POSITIONS: { x: number; y: number }[] = [
  { x: 0, y: 0 },
  { x: 4, y: -3 },
  { x: -4, y: 3 },
  { x: 3, y: 4 },
  { x: -3, y: -4 },
];

/** The clock's offset at a moment: it changes by a jump every <see cref="DRIFT_PERIOD_MS"/>, never in between. */
export function driftFor(now: Date): { x: number; y: number } {
  return POSITIONS[Math.floor(now.getTime() / DRIFT_PERIOD_MS) % POSITIONS.length];
}

/** The running VMs widget: names, a count when there are many, or that none run. */
export function vmLine(names: string[]): string {
  if (names.length === 0) return "No virtual machines running";
  if (names.length <= 3) return `Running: ${names.join(", ")}`;
  return `${names.length} virtual machines running`;
}
