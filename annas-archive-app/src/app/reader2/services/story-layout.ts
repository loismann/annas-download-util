import { Actor, ActorEdge } from '../reader2.models';

/**
 * Where the relationship map draws things.
 *
 * <p>A pure function of the model, like {@link wordsPerPage} — no DOM, no
 * measurement, no component. A graph layout is arithmetic with a lot of edge
 * cases (an isolated actor, a cycle in a chain of command, two people who are
 * each other's rival), and every one of them is worth a test that does not need
 * a browser to run.</p>
 */

/** One person, placed. Coordinates are in an abstract unit the SVG scales. */
export interface PlacedActor {
  id: string;
  name: string;
  x: number;
  y: number;

  /** Depth in the chain of command, or 0 when there is no chain. */
  rank: number;
}

/**
 * One relationship, placed.
 *
 * `hierarchy` edges made the tree; `lateral` ones are drawn over it. Keeping
 * them apart is what stops a rivalry between two divisions being read as one
 * commanding the other.
 */
export interface PlacedEdge {
  from: PlacedActor;
  to: PlacedActor;
  type: string;
  kind: 'hierarchy' | 'lateral';
}

/** A set of people connected to each other and to nobody else. */
export interface Cluster {
  actors: PlacedActor[];
  edges: PlacedEdge[];
  width: number;
  height: number;
  x: number;
  y: number;
}

export interface StoryLayout {
  clusters: Cluster[];
  width: number;
  height: number;
}

/**
 * Relationship types that describe a chain of command rather than a tie between
 * equals.
 *
 * <p>The echelon tree is chosen from these rather than from the book's type: any
 * lens whose extraction reports a chain of command gets the tree, and a fourth
 * book type needs no change here. That is the same reason the panel reads its
 * column headings from the server's vocabulary instead of holding a table.</p>
 */
const HIERARCHY = ['commands', 'subordinate-to', 'reports-to', 'relieved-by'];

const COLUMN = 150;
const ROW = 90;
const GAP = 60;

/** How wide the map may run before clusters wrap onto another row. */
const MAX_WIDTH = 900;

export function isHierarchy(type: string): boolean {
  return HIERARCHY.includes(type.trim().toLowerCase());
}

/**
 * Lays out the cast.
 *
 * <p>Connected groups are placed side by side and never on top of each other —
 * overlapping them is what turns a readable diagram into a hairball, and the
 * whole point of the map is telling apart two households who do not know each
 * other yet.</p>
 */
export function layOutStory(actors: Actor[], edges: ActorEdge[]): StoryLayout {
  const present = new Map(actors.map(a => [a.id, a]));
  const live = edges.filter(e => present.has(e.from) && present.has(e.to));

  const clusters = group(actors, live)
    .map(members => place(members, live))
    .sort((a, b) => b.actors.length - a.actors.length || compare(a, b));

  return pack(clusters);
}

/** Biggest first, then by name, so a redraw of the same model is the same picture. */
function compare(a: Cluster, b: Cluster): number {
  return (a.actors[0]?.name ?? '').localeCompare(b.actors[0]?.name ?? '');
}

/** Connected components, by union-find over every edge regardless of direction. */
function group(actors: Actor[], edges: ActorEdge[]): Actor[][] {
  const parent = new Map(actors.map(a => [a.id, a.id]));

  const root = (id: string): string => {
    let at = id;
    while (parent.get(at) !== at) at = parent.get(at)!;
    return at;
  };

  for (const edge of edges) parent.set(root(edge.from), root(edge.to));

  const clusters = new Map<string, Actor[]>();
  for (const actor of actors) {
    const key = root(actor.id);
    clusters.set(key, [...(clusters.get(key) ?? []), actor]);
  }

  return [...clusters.values()];
}

/**
 * One cluster, laid out.
 *
 * <p>With a chain of command it is an echelon tree, deepest rank at the bottom.
 * Without one there is no meaningful "above", so the members go in a row rather
 * than being given an ordering the material does not support.</p>
 */
function place(members: Actor[], edges: ActorEdge[]): Cluster {
  const ids = new Set(members.map(a => a.id));
  const mine = edges.filter(e => ids.has(e.from) && ids.has(e.to));
  const ranks = rank(members, mine.filter(e => isHierarchy(e.type)));

  const rows = new Map<number, Actor[]>();
  for (const actor of members) {
    const at = ranks.get(actor.id)!;
    rows.set(at, [...(rows.get(at) ?? []), actor]);
  }

  const widest = Math.max(...[...rows.values()].map(r => r.length));
  const placed = new Map<string, PlacedActor>();

  for (const [at, row] of [...rows.entries()].sort(([a], [b]) => a - b)) {
    // Centred on the widest row, so a single commander sits over their subordinates.
    const offset = ((widest - row.length) * COLUMN) / 2;

    row.sort((a, b) => a.canonicalName.localeCompare(b.canonicalName));
    row.forEach((actor, i) => placed.set(actor.id, {
      id: actor.id,
      name: actor.canonicalName,
      x: offset + i * COLUMN + COLUMN / 2,
      y: at * ROW + ROW / 2,
      rank: at
    }));
  }

  return {
    actors: [...placed.values()],
    edges: mine.map(e => ({
      from: placed.get(e.from)!,
      to: placed.get(e.to)!,
      type: e.type,
      kind: isHierarchy(e.type) ? 'hierarchy' : 'lateral'
    })),
    width: widest * COLUMN,
    height: rows.size * ROW,
    x: 0,
    y: 0
  };
}

/**
 * How far down the chain of command each member sits.
 *
 * <p>Breadth-first from whoever nobody commands. A cycle — which a model reading
 * one chapter at a time will eventually report — leaves its members unvisited,
 * and they are placed at the top rather than dropped: a diagram missing somebody
 * is worse than one drawing them at the wrong height.</p>
 */
function rank(members: Actor[], hierarchy: ActorEdge[]): Map<string, number> {
  const below = new Map<string, string[]>();
  const commanded = new Set<string>();

  for (const edge of hierarchy) {
    const [over, under] = edge.type.trim().toLowerCase() === 'commands'
      ? [edge.from, edge.to]
      : [edge.to, edge.from];

    below.set(over, [...(below.get(over) ?? []), under]);
    commanded.add(under);
  }

  const ranks = new Map<string, number>();
  const queue = members.filter(a => !commanded.has(a.id)).map(a => a.id);
  for (const id of queue) ranks.set(id, 0);

  for (let i = 0; i < queue.length; i++) {
    const id = queue[i];

    for (const under of below.get(id) ?? []) {
      if (ranks.has(under)) continue;

      ranks.set(under, ranks.get(id)! + 1);
      queue.push(under);
    }
  }

  for (const actor of members) if (!ranks.has(actor.id)) ranks.set(actor.id, 0);

  return ranks;
}

/** Clusters placed left to right, wrapping, with a gap between each. */
function pack(clusters: Cluster[]): StoryLayout {
  let x = 0;
  let y = 0;
  let tallest = 0;
  let width = 0;

  for (const cluster of clusters) {
    if (x > 0 && x + cluster.width > MAX_WIDTH) {
      x = 0;
      y += tallest + GAP;
      tallest = 0;
    }

    cluster.x = x;
    cluster.y = y;

    x += cluster.width + GAP;
    tallest = Math.max(tallest, cluster.height);
    width = Math.max(width, x - GAP);
  }

  return { clusters, width, height: y + tallest };
}
