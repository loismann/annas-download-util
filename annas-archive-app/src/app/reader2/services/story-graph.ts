import { Actor, ActorEdge } from '../reader2.models';

/**
 * What the map draws, as data.
 *
 * <p>A pure function of the model, like `wordsPerPage` — no DOM, no chart, no
 * component. Which nodes exist, which lines are real, how big somebody is and
 * what colour they are; <i>where</i> they go is `story-layout.ts`, and drawing
 * them is `story-chart.ts`.</p>
 *
 * <p>The edge order is part of the contract. AnyChart identifies a clicked line
 * as <c>edge_&lt;index&gt;</c> into the array it was handed, so the array this
 * returns is what the map indexes back into to answer "how do these two know
 * each other". Each entry therefore carries the edge it came from rather than
 * leaving the caller to search for it.</p>
 */

/** One person, as the chart wants them. */
export interface GraphNode {
  id: string;
  name: string;

  /** How many live relationships they have. Drives the size, and the tooltip. */
  degree: number;

  height: number;
  width: number;

  /** Theirs alone. See {@link colourOf}. */
  fill: string;
}

export interface GraphEdge {
  from: string;
  to: string;
  type: string;

  /** The edge this was built from, so a click needs no lookup. */
  tie: ActorEdge;
}

export interface StoryGraph {
  nodes: GraphNode[];
  edges: GraphEdge[];
}

/**
 * Node size in chart units.
 *
 * <p>Bounded at both ends deliberately. A protagonist with thirty relationships
 * drawn to scale would cover the people they are connected to, and somebody with
 * none still has to be big enough to click.</p>
 *
 * <p>Half again as large as the first version, which read as dots. The layout
 * takes its spacing from these numbers, so the map opens out with them rather
 * than growing more crowded.</p>
 */
const SMALLEST = 21;
const GROWTH = 7.5;
const LARGEST = 78;

export function nodeSize(degree: number): number {
  return Math.min(SMALLEST + degree * GROWTH, LARGEST);
}

/**
 * A colour of somebody's own.
 *
 * <p>The map used to colour by connected set, on the argument that a household
 * sharing one colour is what the eye looks for. In a cast this size that put
 * fourteen people in one shade of orange and made the map harder to talk about,
 * not easier — so each person gets their own, and grouping is left to the thing
 * that shows it honestly, which is the lines.</p>
 *
 * <p>Hue by golden angle, so any number of people are as far apart on the wheel
 * as they can be and the tenth character is no harder to tell from the ninth
 * than the second was from the first. Saturation and lightness are held mid and
 * muted: the map inherits the reader's paper background, and a wheel of
 * saturated primaries would read as a dashboard dropped into a book.</p>
 */
export function colourOf(at: number): string {
  const hue = Math.round((at * 137.508) % 360);

  // Blues and violets read darker than yellows at one lightness; nudging the
  // cool half up keeps every label legible against every fill.
  const light = hue > 200 && hue < 300 ? 58 : 52;

  return `hsl(${hue}, 48%, ${light}%)`;
}

export function graphOf(actors: Actor[], edges: ActorEdge[]): StoryGraph {
  const present = new Map(actors.map(a => [a.id, a]));

  // An edge to somebody the reader has not met yet is not drawn: the model is
  // filtered to their position before it ever arrives, so a dangling end means
  // the far side is still ahead of them.
  const live = edges.filter(e =>
    present.has(e.from) && present.has(e.to) && e.from !== e.to);

  const degrees = new Map<string, number>();
  for (const edge of live) {
    degrees.set(edge.from, (degrees.get(edge.from) ?? 0) + 1);
    degrees.set(edge.to, (degrees.get(edge.to) ?? 0) + 1);
  }

  return {
    nodes: actors.map((actor, at) => {
      const degree = degrees.get(actor.id) ?? 0;

      return {
        id: actor.id,
        name: actor.canonicalName,
        degree,
        height: nodeSize(degree),
        width: nodeSize(degree),
        fill: colourOf(at)
      };
    }),
    edges: live.map(tie => ({ from: tie.from, to: tie.to, type: tie.type, tie }))
  };
}
