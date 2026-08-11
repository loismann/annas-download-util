import { StoryGraph } from './story-graph';
import { extentOf, positions } from './story-layout';

declare const anychart: any;

/** What a click landed on, as AnyChart reports it. */
export interface ChartTag {
  type?: string;
  id?: string;
}

/** Somebody, or a line, or the background. Never both. */
export interface Chosen {
  node: string | null;
  edge: number | null;
}

/**
 * What a click resolved to.
 *
 * <p>Here rather than in the component because the shape being decoded is
 * AnyChart's: it reports a clicked line as <c>edge_&lt;index&gt;</c> into the
 * array it was handed, and this file is where what the vendored build does is
 * written down. The index is why {@link graphOf}'s edge order is a contract.</p>
 *
 * <p>Null for a click on the background, which is not a deselection — a reader
 * missing a small circle should not lose the panel they were reading.</p>
 */
export function chosen(tag: ChartTag | undefined): Chosen | null {
  if (tag?.type === 'node' && tag.id) return { node: tag.id, edge: null };
  if (tag?.type !== 'edge') return null;

  // Matched whole rather than split on the underscore: splitting turned an id
  // with nothing after it into the number zero, which is a valid edge — so an
  // unreadable click opened the first relationship on the map as if it were the
  // one that had been pointed at.
  const at = /^edge_(\d+)$/.exec(String(tag.id ?? ''));

  return { node: null, edge: at ? Number(at[1]) : null };
}

/**
 * One configured relationship graph.
 *
 * <p>Apart from the component because it is a different kind of thing: every
 * line here is a statement about how the map should <i>look</i>, and none of it
 * is about when to draw, what is selected, or what to do about a failure. Kept
 * together, the two concerns made one file that was mostly colours.</p>
 *
 * <p>Everything below is the API surface the vendored build is known to
 * support, and the only documentation for the version actually on disk is the
 * build itself: `story-chart.spec.ts` draws against the real bundle, because a
 * stub answers every call the same way and passed a full suite while this file
 * was calling three settings that do not exist.</p>
 */

/** Big enough to read at rest, small enough that a full cast still fits. */
export const LABEL_SIZE = 12;

/** A configured chart, and how much room its drawing needs. See {@link fitTo}. */
export interface Drawn {
  chart: any;
  extent: { width: number; height: number };
}

export function buildChart(
  graph: StoryGraph, onClick: (tag: ChartTag | undefined) => void
): Drawn {
  // Ours, not the layout's — see `story-layout.ts`. Handing the chart fixed
  // coordinates is the only way to say how far apart things sit on a build
  // whose layout exposes nothing but a type and an iteration count.
  const where = positions(graph.nodes, graph.edges, LABEL_SIZE);

  // Primitives only. Each edge carries the whole `ActorEdge` it came from so a
  // click needs no lookup, and handing a charting library a nested object it
  // has no field for is asking it to do something surprising with it. The index
  // is what survives the trip, and the index is all the click needs.
  const chart = anychart.graph({
    nodes: graph.nodes.map(node => ({ ...node, ...where.get(node.id) })),
    edges: graph.edges.map(({ from, to, type }) => ({ from, to, type }))
  });

  chart.layout().type('fixed');
  paint(chart);

  chart.listen('click', (event: any) => onClick(event?.domTarget?.tag));

  return { chart, extent: extentOf(graph.nodes, where, LABEL_SIZE) };
}

function paint(chart: any): void {
  // A node's own `fill` travels in its data — the build supports fill, stroke,
  // labels, shape, height and width per node, which is what makes a colour each
  // possible without a group for every person on the map.
  chart.nodes().normal().stroke('#00000022', 1);
  chart.nodes().hovered().stroke('#1e1e1e', 2);
  chart.nodes().selected().stroke('#1e1e1e', 3);

  chart.nodes().labels().enabled(true);
  chart.nodes().labels().fontSize(LABEL_SIZE);
  chart.nodes().labels().fontColor('#3a332a');

  // Named explicitly: a graph node's label defaults to its id, and "a17" on
  // every circle is a map of nobody.
  chart.nodes().labels().format('{%name}');
  chart.nodes().tooltip().format('{%name}\n{%degree} connections');

  edges(chart);
}

/**
 * The lines, and how to hit one.
 *
 * <p>A line is the answer to "how do these two know each other", which is half
 * of what the map is for — but a 1.5px stroke is also its entire click target,
 * and pointing at one between two large circles caught the circle instead. Drawn
 * heavier, and thicker again on hover so it is clear which one is about to be
 * picked.</p>
 */
function edges(chart: any): void {
  chart.edges().normal().stroke('#8d8172', 2.5);
  chart.edges().hovered().stroke('#3a2f1c', 4);
  chart.edges().selected().stroke('#3a2f1c', 4);
  chart.edges().labels().enabled(false);
  chart.edges().tooltip().format('{%type}');

  const how = chart.interactivity();

  // Off by default, so without this a line could not be clicked at all.
  how.enabled(true);
  how.nodes(true);
  how.edges(true);

  // How near counts as on it. A 2.5px stroke is also its entire click target,
  // and a reader aiming at a line between two large circles hits neither — this
  // is the difference between edges being clickable in principle and in fact.
  how.hoverGap?.(HIT_GAP);

  // Both off, so the wheel is ours. Left to the build, the wheel scrolled the
  // drawing around rather than zooming it, which is the "panning from an origin
  // nowhere near the bubbles" it looked like from the outside.
  how.zoomOnMouseWheel?.(false);
  how.scrollOnMouseWheel?.(false);
}

/** Pixels of slack around a line before a click counts as missing it. */
const HIT_GAP = 12;

/**
 * Zoom and pan.
 *
 * <p>Wrapped rather than called directly so the component never has to know
 * whether the vendored build has a given method: a map that cannot fit is worse
 * with a broken button than without one.</p>
 */
/**
 * <p><c>zoom</c> and not <c>zoomIn</c>/<c>zoomOut</c>: measured against the real
 * build, those two ignore the factor they are handed and step by a fixed amount
 * of their own. That is why asking the wheel for a gentler step changed nothing
 * — every notch was a full button press whatever number it was given. Nothing
 * says so anywhere; <c>story-chart.spec.ts</c> is where it is written down.</p>
 */
export const zoom = {
  in: (chart: any, by: number = BUTTON_STEP) => chart?.zoom?.(by),
  out: (chart: any, by: number = BUTTON_STEP) => chart?.zoom?.(1 / by)
};

/** One press of + or −. Deliberate, so it is worth a whole step. */
const BUTTON_STEP = 1.3;

/**
 * One notch of the wheel: half a button press.
 *
 * <p>The square root rather than half the number, because zoom composes by
 * multiplication — two notches of √1.3 land on exactly 1.3, which is what "half
 * as much per notch" has to mean for it to be true of two notches as well as
 * one. Halving the factor itself (1.15) would make two notches 1.32, and ten
 * notches four times the intended scale.</p>
 *
 * <p>A wheel reports several notches for one flick of a finger, which is why a
 * step sized for a deliberate button press ran away under a mouse.</p>
 */
const WHEEL_STEP = Math.sqrt(BUTTON_STEP);

/**
 * Everything on screen at once.
 *
 * <p><b>The build's own <c>fit()</c> does not fit.</b> Measured against the real
 * bundle: it restores the drawing to scale 1, centred — which is a reset, and on
 * any cast bigger than the panel it leaves most of the map outside it. That is
 * why pressing Fit appeared to show only the middle cluster. So Fit resets and
 * then asks for the zoom that actually makes the extent match the panel.</p>
 *
 * <p>The extent comes from the layout rather than from measuring the DOM, and
 * <c>zoom</c> is relative to the current scale — both established against the
 * real build, because neither is written down anywhere else.</p>
 */
export function fitTo(drawn: Drawn | undefined, view: { width: number; height: number }): void {
  if (!drawn) return;

  drawn.chart?.fit?.();

  const { width, height } = drawn.extent;
  if (width <= 0 || height <= 0 || view.width <= 0 || view.height <= 0) return;

  // A little air, so nothing sits against the edge of the panel — and capped,
  // because a cast of two blown up to fill the window is not a map of anything.
  const scale = Math.min(view.width / width, view.height / height) * 0.9;

  if (Number.isFinite(scale) && scale > 0) drawn.chart?.zoom?.(Math.min(scale, LARGEST_FIT));
}

const LARGEST_FIT = 1.8;

/**
 * Which way the wheel zooms.
 *
 * <p>Ours because the build's own wheel handling is turned off above, and it has
 * to be somewhere the direction is one line rather than a library default. Away
 * from the reader zooms out and toward them zooms in — the reverse of the usual
 * convention, which is what was asked for.</p>
 *
 * <p>Gentler than the buttons: see {@link WHEEL_STEP}.</p>
 */
export function wheelZoom(drawn: Drawn | undefined, deltaY: number): void {
  if (!drawn || deltaY === 0) return;

  (deltaY > 0 ? zoom.in : zoom.out)(drawn.chart, WHEEL_STEP);
}
