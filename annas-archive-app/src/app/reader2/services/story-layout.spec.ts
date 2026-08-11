import { positions, room } from './story-layout';
import { graphOf } from './story-graph';
import { actor, edge } from '../testing/cast';

/**
 * Where everybody goes.
 *
 * <p>Testable precisely because it is ours. The vendored build's layout has two
 * settings and no way to ask it for room between things, and what it produced
 * was every unconnected person in a horizontal line with their names written
 * across each other. These are the properties that arrangement failed.</p>
 */
describe('positions', () => {
  const SIZE = 12;

  function laid(actors: Parameters<typeof graphOf>[0], edges: Parameters<typeof graphOf>[1]) {
    const graph = graphOf(actors, edges);
    const at = positions(graph.nodes, graph.edges, SIZE);

    return {
      of: (id: string) => at.get(id)!,
      gap: (a: string, b: string) =>
        Math.hypot(at.get(a)!.x - at.get(b)!.x, at.get(a)!.y - at.get(b)!.y),
      nodes: graph.nodes,
      at
    };
  }

  const CAST = [
    actor('a1', 'Finn'), actor('a2', 'Ellie'), actor('a3', 'Josias'),
    actor('a4', 'Helena'), actor('a5', 'Bekket')
  ];

  it('places everybody', () => {
    const { at } = laid(CAST, [edge('a1', 'a2', 'travels-with')]);

    expect(at.size).toBe(CAST.length);
  });

  it('puts nobody on top of anybody else', () => {
    const { nodes, at } = laid(CAST, [edge('a1', 'a2', 'travels-with')]);
    const spots = nodes.map(n => `${Math.round(at.get(n.id)!.x)},${Math.round(at.get(n.id)!.y)}`);

    expect(new Set(spots).size).toBe(nodes.length);
  });

  /**
   * The complaint this file exists for. Unconnected nodes came out in a straight
   * horizontal row, which is both unreadable and a lie about them — they are not
   * a sequence.
   */
  it('does not lay the unconnected out in a line', () => {
    const alone = Array.from({ length: 8 }, (_, i) => actor(`a${i}`, `Person ${i}`));
    const { at } = laid(alone, []);
    const ys = new Set([...at.values()].map(p => Math.round(p.y / 10)));

    expect(ys.size).toBeGreaterThan(2);
  });

  it('spirals the islands out from the middle rather than stacking them', () => {
    const alone = Array.from({ length: 12 }, (_, i) => actor(`a${i}`, `Person ${i}`));
    const { at } = laid(alone, []);
    const out = [...at.values()].map(p => Math.hypot(p.x, p.y));

    // Something at the centre, something well out, and a spread in between.
    expect(Math.min(...out)).toBeLessThan(20);
    expect(Math.max(...out)).toBeGreaterThan(100);
  });

  /** Names are far wider than the circles under them, and names are the map. */
  it('leaves room for a long name, not just for the circle', () => {
    const long = laid([actor('a1', 'Finbar Charles Louis Griffin Jalgori-Tobu'), actor('a2', 'Jaks')], []);
    const short = laid([actor('a1', 'Finn'), actor('a2', 'Jaks')], []);

    expect(long.gap('a1', 'a2')).toBeGreaterThan(short.gap('a1', 'a2') * 1.5);
  });

  /**
   * The other half of the overlap complaint, and the one a fixed rest length
   * could never fix: two people who know each other are pulled together, so if
   * the spring does not know how wide their names are it pulls them until the
   * names collide.
   */
  it('gives two connected long names more room than two connected short ones', () => {
    const near = laid([actor('a1', 'Ann'), actor('a2', 'Bob')], [edge('a1', 'a2', 'x')]);
    const far = laid(
      [actor('a1', 'Finbar Charles Louis Griffin'), actor('a2', 'Princess Thi-Congreve')],
      [edge('a1', 'a2', 'x')]);

    expect(far.gap('a1', 'a2')).toBeGreaterThan(near.gap('a1', 'a2') * 1.5);
  });

  /** The complaint itself, stated as a property over a cast shaped like the real one. */
  it('leaves no two names written across each other', () => {
    const cast = [
      actor('a1', 'Finbar Charles Louis Griffin Jalgori-Tobu'), actor('a2', 'Eleanor'),
      actor('a3', 'Josias Aponi'), actor('a4', 'Yatras'), actor('a5', 'Kavee'),
      actor('a6', 'Lord Valdier-Mímir'), actor('a7', 'Helena-Chione'),
      actor('a8', 'Lord Gahiji-Calder'), actor('a9', 'Count Bekket'),
      actor('a10', 'Princess Thi-Congreve'), actor('a11', 'Lord Sterling'),
      actor('a12', 'Ramona-Iressa'), actor('a13', 'Marcela')
    ];

    const { nodes, at } = laid(cast, [
      edge('a1', 'a2', 'x'), edge('a1', 'a3', 'x'), edge('a1', 'a4', 'x'),
      edge('a1', 'a5', 'x'), edge('a2', 'a3', 'x'), edge('a1', 'a6', 'x'),
      edge('a7', 'a8', 'x'), edge('a7', 'a9', 'x')
    ]);

    const collisions = nodes.flatMap((one, i) => nodes.slice(i + 1).map(two => {
      const a = room(one, SIZE);
      const b = room(two, SIZE);
      const apart = { x: Math.abs(at.get(one.id)!.x - at.get(two.id)!.x),
        y: Math.abs(at.get(one.id)!.y - at.get(two.id)!.y) };

      return apart.x < a.x + b.x && apart.y < a.y + b.y ? `${one.name} / ${two.name}` : null;
    })).filter(Boolean);

    expect(collisions).toEqual([]);
  });

  /**
   * A map that rearranged itself every time a chapter was folded in would be a
   * different picture of the same book each time the reader looked at it.
   */
  it('is deterministic, so the same cast is the same picture', () => {
    const once = laid(CAST, [edge('a1', 'a2', 'x')]);
    const twice = laid(CAST, [edge('a1', 'a2', 'x')]);

    expect(once.of('a3')).toEqual(twice.of('a3'));
  });

  it('copes with nobody at all', () => {
    expect(positions([], [], SIZE).size).toBe(0);
  });

  it('puts a lone person at the middle rather than off in a corner', () => {
    const { of } = laid([actor('a1', 'Finn')], []);

    expect(of('a1')).toEqual({ x: 0, y: 0 });
  });
});

describe('room', () => {
  it('is wider for a longer name', () => {
    const wide = room({ name: 'Finbar Charles Louis Griffin', width: 20, height: 20 } as never, 12);
    const narrow = room({ name: 'Jaks', width: 20, height: 20 } as never, 12);

    expect(wide.x).toBeGreaterThan(narrow.x);
  });

  it('is never narrower than the circle itself', () => {
    expect(room({ name: 'A', width: 52, height: 52 } as never, 12).x).toBeGreaterThanOrEqual(26);
  });
});
