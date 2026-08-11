import { Place } from '../reader2.models';

/**
 * The places, as the nesting the book describes.
 *
 * <p>A flat list of forty names answers nothing. The question a reader has is
 * "where was that", and the only useful answer is the chain: this palace, on this
 * continent, on this world, in this cluster. So <c>partOf</c> is walked into a
 * tree, and the tree is what the panel draws.</p>
 *
 * <p>Pure, so the shape is testable without a component — and the awkward cases
 * are all shape: a container the reader has not reached yet, two places that
 * claim to contain each other, a chain twelve deep.</p>
 */
export interface PlaceNode {
  place: Place;
  depth: number;

  /** Nested under this one, already sorted. */
  children: PlaceNode[];

  /** How many places sit under it at any depth. What the collapsed row reports. */
  total: number;
}

/**
 * The forest. Roots are places nothing contains, or whose container is not here.
 *
 * <p>Bigger branches first, then alphabetically. A cluster with nine worlds under
 * it is the thing a reader is looking for, and a stable order means the list does
 * not reshuffle when a chapter adds one room.</p>
 */
export function placeTree(places: Place[]): PlaceNode[] {
  const known = new Set(places.map(p => p.id));
  const children = new Map<string, Place[]>();

  for (const place of places) {
    // A container the reader has not reached is no container: the server clears
    // it, but this renders whatever it was sent.
    const parent = known.has(place.partOf) && place.partOf !== place.id ? place.partOf : '';

    children.set(parent, [...(children.get(parent) ?? []), place]);
  }

  const placed = new Set<string>();

  const build = (place: Place, depth: number): PlaceNode => {
    placed.add(place.id);

    const under = (children.get(place.id) ?? [])
      .filter(child => !placed.has(child.id))
      .map(child => build(child, depth + 1));

    return {
      place,
      depth,
      children: sort(under),
      total: under.reduce((sum, child) => sum + child.total + 1, 0)
    };
  };

  const roots = sort((children.get('') ?? []).map(place => build(place, 0)));

  // Anything a cycle kept out of the walk is still somewhere the book went. A
  // place the reader cannot see is indistinguishable from one never recorded.
  //
  // Checked as the loop runs rather than filtered first: building one of a pair
  // that contain each other places the other as its child, and a list decided up
  // front would then emit that other one a second time as a root of its own.
  const orphans: PlaceNode[] = [];
  for (const place of places) if (!placed.has(place.id)) orphans.push(build(place, 0));

  return [...roots, ...sort(orphans)];
}

function sort(nodes: PlaceNode[]): PlaceNode[] {
  return [...nodes].sort((a, b) =>
    b.total - a.total || a.place.name.localeCompare(b.place.name));
}

/**
 * The tree flattened to the rows actually on screen, honouring what is shut.
 *
 * <p>Flattened rather than rendered recursively: a recursive template needs a
 * component that includes itself, and this needs neither. Nothing below a shut
 * node is emitted, which is what makes a shut branch cheap as well as short.</p>
 */
export function visibleRows(tree: PlaceNode[], shut: ReadonlySet<string>): PlaceNode[] {
  const rows: PlaceNode[] = [];

  const walk = (nodes: PlaceNode[]): void => {
    for (const node of nodes) {
      rows.push(node);

      if (!shut.has(node.place.id)) walk(node.children);
    }
  };

  walk(tree);

  return rows;
}

/** Every place with something under it — what "collapse all" acts on. */
export function branches(tree: PlaceNode[]): string[] {
  return visibleRows(tree, new Set())
    .filter(node => node.children.length > 0)
    .map(node => node.place.id);
}
