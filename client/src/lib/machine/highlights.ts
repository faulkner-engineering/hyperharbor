// Section highlights for the machine view, from static/machine/highlights.json, and the per-control reactions
// (perItem, blink, pulse, spin). The same resolved state drives the 3D view and the exploded still.

/** One entry of highlights.json. perItem is a node name template with {n}, numbered from 1. */
export interface HighlightGroup {
  nodes: string[];
  perItem?: string;
  blink?: string;
  pulse?: string;
  spin?: string[];
}

export type HighlightMap = Record<string, HighlightGroup>;

/** Form state that the reactions follow. Anything left out counts as off. */
export interface ControlState {
  /** Items in the section's list, for perItem (packages light one drive each). */
  items?: number;
  tpmEnabled?: boolean;
  backupScheduled?: boolean;
  performanceMode?: boolean;
}

export interface ResolvedHighlight {
  glow: string[];
  blink: string | null;
  pulse: string | null;
  spin: string[];
}

export const NO_HIGHLIGHT: ResolvedHighlight = { glow: [], blink: null, pulse: null, spin: [] };

/** Visibility toggles at 2 Hz: on for half a second, off for half a second. */
export const BLINK_HZ = 2;
/** Pulse angular frequency in radians per second (one fade cycle in about 2.1 seconds). */
export const PULSE_RATE = 3;
/** Fan speed in radians per second. */
export const SPIN_RAD_PER_S = 2 * Math.PI * 0.75;

function itemPattern(template: string): RegExp {
  const [before, after] = template.split("{n}").map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  return new RegExp(`^${before}(\\d+)${after ?? ""}$`);
}

/** Which nodes glow and react for a section, given the form state. */
export function resolveHighlight(map: HighlightMap, section: string, state: ControlState): ResolvedHighlight {
  const group = map[section];
  if (!group) return NO_HIGHLIGHT;
  let glow = group.nodes;
  if (group.perItem?.includes("{n}")) {
    const pattern = itemPattern(group.perItem);
    const items = state.items ?? 0;
    glow = glow.filter((node) => {
      const match = pattern.exec(node);
      return !match || Number(match[1]) <= items;
    });
  }
  return {
    glow: [...glow],
    blink: state.tpmEnabled ? (group.blink ?? null) : null,
    pulse: state.backupScheduled ? (group.pulse ?? null) : null,
    spin: state.performanceMode ? [...(group.spin ?? [])] : [],
  };
}

/** A rendered part: the object on screen and the material it shows when not highlighted. */
export interface HighlightPart<M> {
  object: { material: M; visible: boolean };
  base: M;
}

/** Puts every part back on its base material, then the highlighted ones on the glow (or pulse) material. */
export function applyHighlight<M>(parts: Map<string, HighlightPart<M>>, highlight: ResolvedHighlight, glow: M, pulse: M): void {
  for (const part of parts.values()) {
    part.object.material = part.base;
    part.object.visible = true;
  }
  for (const node of highlight.glow) {
    const part = parts.get(node);
    if (part) part.object.material = node === highlight.pulse ? pulse : glow;
  }
}

export interface ReactionFrame {
  blinkVisible: boolean;
  pulseOpacity: number;
  spinAngle: number;
}

/** The reaction values at a time in seconds. Spin stops when motion is reduced. */
export function reactionFrame(seconds: number, reducedMotion: boolean): ReactionFrame {
  return {
    blinkVisible: Math.floor(seconds * BLINK_HZ) % 2 === 0,
    pulseOpacity: 0.55 + 0.45 * Math.sin(seconds * PULSE_RATE),
    spinAngle: reducedMotion ? 0 : (seconds * SPIN_RAD_PER_S) % (2 * Math.PI),
  };
}
