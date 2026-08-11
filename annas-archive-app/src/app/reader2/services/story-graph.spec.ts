import { colourOf, graphOf, nodeSize } from './story-graph';
import { actor, edge } from '../testing/cast';

/**
 * What the map is given, decided without a chart.
 *
 * <p>Which nodes exist, which lines are real, how big somebody is, what colour
 * they are, and — because a clicked line is identified by its index — what order
 * the edges come out in. Where they all go is `story-layout.ts`.</p>
 */
describe('graphOf', () => {
  it('gives every actor a node, connected or not', () => {
    const graph = graphOf([actor('a1', 'Finn'), actor('a2', 'Ellie')], []);

    expect(graph.nodes.map(n => n.name)).toEqual(['Finn', 'Ellie']);
  });

  it('counts a live relationship at both ends', () => {
    const graph = graphOf(
      [actor('a1', 'Finn'), actor('a2', 'Ellie'), actor('a3', 'Josias')],
      [edge('a1', 'a2', 'travels-with'), edge('a1', 'a3', 'protects')]);

    expect(graph.nodes.find(n => n.id === 'a1')!.degree).toBe(2);
    expect(graph.nodes.find(n => n.id === 'a2')!.degree).toBe(1);
  });

  /** Somebody the reader has not met is filtered out before the map is drawn. */
  it('drops a line whose far end is not on the map', () => {
    const graph = graphOf([actor('a1', 'Finn')], [edge('a1', 'a99', 'rival')]);

    expect(graph.edges).toEqual([]);
    expect(graph.nodes.find(n => n.id === 'a1')!.degree).toBe(0);
  });

  it('drops a line from somebody to themselves', () => {
    const graph = graphOf([actor('a1', 'Finn')], [edge('a1', 'a1', 'rival')]);

    expect(graph.edges).toEqual([]);
  });

  /**
   * A clicked line is identified as `edge_<index>` into the array handed to the
   * chart, so each entry carries the edge it came from and the caller never has
   * to search for a pair — which is what made the old modal's edge lookup guess
   * between two people related in more than one way.
   */
  it('carries the edge each line came from, in the order the chart indexes', () => {
    const together = edge('a1', 'a2', 'travels-with');
    const owed = edge('a1', 'a2', 'owes');

    const graph = graphOf([actor('a1', 'Finn'), actor('a2', 'Ellie')], [together, owed]);

    expect(graph.edges[0].tie).toBe(together);
    expect(graph.edges[1].tie).toBe(owed);
  });

  it('keeps two people related in more than one way as two lines', () => {
    const graph = graphOf(
      [actor('a1', 'Finn'), actor('a2', 'Ellie')],
      [edge('a1', 'a2', 'travels-with'), edge('a1', 'a2', 'owes')]);

    expect(graph.edges.map(e => e.type)).toEqual(['travels-with', 'owes']);
  });
});

describe('nodeSize', () => {
  it('grows with connections', () => {
    expect(nodeSize(3)).toBeGreaterThan(nodeSize(0));
  });

  /** Somebody with nobody still has to be big enough to click. */
  it('never shrinks to nothing', () => {
    expect(nodeSize(0)).toBeGreaterThanOrEqual(14);
  });

  /**
   * A protagonist with thirty relationships drawn to scale would cover the
   * people they are connected to, which is the opposite of what the map is for.
   */
  it('stops growing before it swallows the map', () => {
    expect(nodeSize(200)).toBe(nodeSize(30));
  });
});

/**
 * A colour of somebody's own.
 *
 * <p>The map used to colour by connected set. In a cast this size that put
 * fourteen people in one shade of orange, so grouping was left to the thing that
 * shows it honestly — the lines — and colour was given back to the person.</p>
 */
describe('colourOf', () => {
  it('gives two people two different colours', () => {
    const graph = graphOf([actor('a1', 'Finn'), actor('a2', 'Ellie')], []);

    expect(graph.nodes[0].fill).not.toBe(graph.nodes[1].fill);
  });

  it('gives everybody a different colour, however many there are', () => {
    const many = Array.from({ length: 40 }, (_, i) => colourOf(i));

    expect(new Set(many).size).toBe(40);
  });

  /**
   * Golden angle rather than a fixed palette: the tenth character has to be as
   * easy to tell from the ninth as the second was from the first, and a palette
   * of eight starts repeating itself on any real cast.
   */
  it('separates neighbours as far as the wheel allows', () => {
    const hue = (at: number) => Number(/hsl\((\d+)/.exec(colourOf(at))![1]);

    expect(Math.abs(hue(0) - hue(1))).toBeGreaterThan(60);
    expect(Math.abs(hue(1) - hue(2))).toBeGreaterThan(60);
  });

  it('is stable, so a redraw of the same cast is the same picture', () => {
    expect(colourOf(5)).toBe(colourOf(5));
  });
});
