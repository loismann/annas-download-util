import { GraphEdge, GraphNode } from './story-graph';

/**
 * Where everybody goes on the map.
 *
 * <p><b>Ours, not the library's.</b> The vendored graph build exposes exactly two
 * layout settings — a type and an iteration count — and nothing to say how far
 * apart things should sit or what to do with somebody nobody has met yet. What it
 * did with those was put every unconnected person in a straight horizontal line
 * with their names written across each other, which is the one arrangement that
 * says nothing at all. Taking the positions over means the chart is told
 * <c>'fixed'</c> and handed coordinates.</p>
 *
 * <p>Two stages, because a cast is not one graph but several. Each connected set
 * is relaxed on its own, and then the sets are <i>packed</i> onto a spiral out
 * from the middle — largest first. A group of three who only know each other is
 * an island, and so is a single walk-on; both want to be near the map, not in a
 * queue beside it.</p>
 *
 * <p>Pure and deterministic: same cast, same picture, every time. That is what
 * makes it testable without a browser, and it is also what stops the map
 * rearranging itself under the reader every time a chapter is folded in.</p>
 */

/** Ideal distance between two connected people, before their sizes are added. */
const SPRING = 105;

/** Enough for a cast this size to settle; more is imperceptible. */
const PASSES = 400;

/** How much of the remaining heat a single pass may spend. */
const COOLING = 0.94;

/** Clear air between one island and the next, so names never run together. */
const CHANNEL = 34;

export interface Point {
  x: number;
  y: number;
}

/**
 * Half the room a node needs, label included.
 *
 * <p>A name is far wider than the circle under it, and packing by the circles
 * alone is why "Princess Thi-Congreve" and "Lord Sterling" ended up written on
 * top of each other. Estimated rather than measured: measuring means a DOM, and
 * a layout that needs a browser cannot be tested like this one.</p>
 */
export function room(node: GraphNode, fontSize: number): Point {
  return {
    x: Math.max(node.width / 2, (node.name.length * fontSize * 0.5) / 2),
    y: node.height / 2 + fontSize * 1.6
  };
}

/**
 * Positions for everybody, keyed by id.
 *
 * @param fontSize What the labels will be drawn at, so the packing can leave room
 *   for them. The chart and the layout have to agree on this or the spacing is
 *   computed for a picture nobody draws.
 */
export function positions(
  nodes: GraphNode[], edges: GraphEdge[], fontSize: number
): Map<string, Point> {
  const placed = new Map<string, Point>();
  const islands = split(nodes, edges);

  // Largest first: the principal cast takes the middle, and the map keeps the
  // same shape as walk-ons accumulate around it rather than being reshuffled.
  islands.sort((a, b) => b.nodes.length - a.nodes.length);

  let at = 0;
  const anchors: { centre: Point; radius: number }[] = [];

  for (const island of islands) {
    const local = relax(island.nodes, island.edges, fontSize);
    const radius = spanOf(island.nodes, local, fontSize);
    const centre = free(anchors, radius, at++);

    anchors.push({ centre, radius });

    for (const node of island.nodes) {
      const point = local.get(node.id)!;

      placed.set(node.id, { x: centre.x + point.x, y: centre.y + point.y });
    }
  }

  return placed;
}

/** One connected set and the edges inside it. */
interface Island {
  nodes: GraphNode[];
  edges: GraphEdge[];
}

function split(nodes: GraphNode[], edges: GraphEdge[]): Island[] {
  const parent = new Map(nodes.map(n => [n.id, n.id]));

  const root = (id: string): string => {
    let seek = id;
    while (parent.get(seek) !== seek) seek = parent.get(seek)!;

    return seek;
  };

  for (const edge of edges) parent.set(root(edge.from), root(edge.to));

  const found = new Map<string, Island>();
  for (const node of nodes) {
    const key = root(node.id);

    if (!found.has(key)) found.set(key, { nodes: [], edges: [] });
    found.get(key)!.nodes.push(node);
  }

  for (const edge of edges) found.get(root(edge.from))!.edges.push(edge);

  return [...found.values()];
}

/**
 * Fruchterman–Reingold, over one connected set.
 *
 * <p>Repulsion between every pair, attraction along every edge, and a
 * temperature that falls each pass so the arrangement settles instead of
 * oscillating. Started on a spiral rather than at the origin: from a single
 * point every repulsion vector is zero or undefined, and the first pass has
 * nothing to push apart.</p>
 *
 * <p><b>The ideal distance is per pair, not global.</b> A name is far wider than
 * the circle under it, so one rest length for everybody spaced "Jaks" and
 * "Finbar Charles Louis Griffin Jalgori-Tobu" identically and wrote the second
 * across whoever was beside it. Each pair is given the room its own two labels
 * need, which is why two short names sit close and two long ones do not.</p>
 */
function relax(nodes: GraphNode[], edges: GraphEdge[], fontSize: number): Map<string, Point> {
  const at = new Map<string, Point>(nodes.map((node, i) => [node.id, seed(i, nodes.length)]));

  if (nodes.length < 2) return centre(nodes, at);

  const half = new Map(nodes.map(node => [node.id, room(node, fontSize)]));
  const want = (a: string, b: string) => SPRING + half.get(a)!.x + half.get(b)!.x;

  let heat = SPRING * 1.5;

  for (let pass = 0; pass < PASSES; pass++) {
    const push = new Map<string, Point>(nodes.map(n => [n.id, { x: 0, y: 0 }]));

    for (let a = 0; a < nodes.length; a++) {
      for (let b = a + 1; b < nodes.length; b++) {
        const one = at.get(nodes[a].id)!;
        const two = at.get(nodes[b].id)!;
        const away = apart(one, two);
        const rest = want(nodes[a].id, nodes[b].id);
        const force = (rest * rest) / away.distance;

        shift(push.get(nodes[a].id)!, away.unit, force);
        shift(push.get(nodes[b].id)!, away.unit, -force);
      }
    }

    for (const edge of edges) {
      const from = at.get(edge.from);
      const to = at.get(edge.to);

      if (!from || !to) continue;

      const away = apart(from, to);
      const force = (away.distance * away.distance) / want(edge.from, edge.to);

      shift(push.get(edge.from)!, away.unit, -force);
      shift(push.get(edge.to)!, away.unit, force);
    }

    for (const node of nodes) {
      const move = push.get(node.id)!;
      const length = Math.hypot(move.x, move.y) || 1;
      const step = Math.min(length, heat) / length;
      const here = at.get(node.id)!;

      at.set(node.id, { x: here.x + move.x * step, y: here.y + move.y * step });
    }

    heat *= COOLING;
  }

  return centre(nodes, at);
}

/** A golden-angle spiral: even, and never two points on top of each other. */
function seed(at: number, of: number): Point {
  const angle = at * Math.PI * (3 - Math.sqrt(5));
  const radius = SPRING * Math.sqrt(at + 0.5);

  return { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius * (of > 1 ? 1 : 0) };
}

function apart(one: Point, two: Point): { unit: Point; distance: number } {
  const x = one.x - two.x;
  const y = one.y - two.y;
  const distance = Math.max(Math.hypot(x, y), 0.01);

  return { unit: { x: x / distance, y: y / distance }, distance };
}

function shift(point: Point, unit: Point, by: number): void {
  point.x += unit.x * by;
  point.y += unit.y * by;
}

/** Recentres an island on its own middle, so packing can treat it as a disc. */
function centre(nodes: GraphNode[], at: Map<string, Point>): Map<string, Point> {
  const mid = nodes.reduce(
    (sum, node) => ({ x: sum.x + at.get(node.id)!.x, y: sum.y + at.get(node.id)!.y }),
    { x: 0, y: 0 });

  mid.x /= nodes.length;
  mid.y /= nodes.length;

  return new Map([...at].map(([id, p]) => [id, { x: p.x - mid.x, y: p.y - mid.y }]));
}

/** How far an island reaches from its own middle, names included. */
function spanOf(nodes: GraphNode[], at: Map<string, Point>, fontSize: number): number {
  return nodes.reduce((most, node) => {
    const point = at.get(node.id)!;
    const needs = room(node, fontSize);

    return Math.max(most, Math.hypot(Math.abs(point.x) + needs.x, Math.abs(point.y) + needs.y));
  }, 0);
}

/**
 * Somewhere this island fits, spiralling out from the middle.
 *
 * <p>Walks outward until it finds air rather than computing a packing: the
 * counts here are small, the result is stable, and "the next one goes a little
 * further out and a little further round" is the arrangement being asked for
 * rather than an approximation of it.</p>
 */
function free(taken: { centre: Point; radius: number }[], radius: number, index: number): Point {
  if (taken.length === 0) return { x: 0, y: 0 };

  const golden = Math.PI * (3 - Math.sqrt(5));

  for (let step = 1; step < 4000; step++) {
    const angle = index * golden + step * 0.35;
    const out = step * 6;
    const spot = { x: Math.cos(angle) * out, y: Math.sin(angle) * out };

    if (taken.every(other =>
      Math.hypot(spot.x - other.centre.x, spot.y - other.centre.y)
        > radius + other.radius + CHANNEL)) {
      return spot;
    }
  }

  // Unreachable for any cast a reader will have; a picture with an overlap in it
  // is still better than one that hangs looking for a perfect arrangement.
  return { x: 0, y: 0 };
}

/**
 * How much room the whole arrangement needs, labels included.
 *
 * <p>What a true fit is computed from. The chart's own <c>fit()</c> is a reset to
 * scale 1 rather than a fit to the window — measured, not assumed — so fitting
 * means knowing the extent of the drawing and asking for the zoom that makes it
 * match the panel. Taken from the layout rather than measured off the DOM,
 * because the layout is what decided it.</p>
 */
export function extentOf(
  nodes: GraphNode[], at: Map<string, Point>, fontSize: number
): { width: number; height: number } {
  if (nodes.length === 0) return { width: 0, height: 0 };

  const edges = nodes.map(node => {
    const point = at.get(node.id) ?? { x: 0, y: 0 };
    const needs = room(node, fontSize);

    return {
      left: point.x - needs.x, right: point.x + needs.x,
      top: point.y - needs.y, bottom: point.y + needs.y
    };
  });

  return {
    width: Math.max(...edges.map(e => e.right)) - Math.min(...edges.map(e => e.left)),
    height: Math.max(...edges.map(e => e.bottom)) - Math.min(...edges.map(e => e.top))
  };
}
