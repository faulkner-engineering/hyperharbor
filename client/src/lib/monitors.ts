import type { Monitor } from "$lib/api/client";

/** True when two monitors share part of an edge. */
function touching(a: Monitor, b: Monitor): boolean {
  const overlapX = Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x);
  const overlapY = Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y);
  const besideX = a.x + a.width === b.x || b.x + b.width === a.x;
  const besideY = a.y + a.height === b.y || b.y + b.height === a.y;
  return (besideX && overlapY > 0) || (besideY && overlapX > 0);
}

/**
 * True when the monitors form one connected block. mstsc expects the selected monitors to be
 * next to each other; a gap between them may stop the session from using all of them.
 */
export function areContiguous(monitors: Monitor[]): boolean {
  if (monitors.length <= 1) return true;
  const reached = new Set<Monitor>([monitors[0]]);
  const queue = [monitors[0]];
  while (queue.length > 0) {
    const current = queue.shift()!;
    for (const other of monitors) {
      if (!reached.has(other) && touching(current, other)) {
        reached.add(other);
        queue.push(other);
      }
    }
  }
  return reached.size === monitors.length;
}
