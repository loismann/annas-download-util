import { branches, placeTree, visibleRows } from './place-tree';
import { Place } from '../reader2.models';

function place(id: string, name: string, partOf = ''): Place {
  return {
    id, name, aliases: [], kind: 'Settlement', description: '', partOf,
    firstSeenChapter: 0, lastSeenChapter: 0
  };
}

/**
 * The chain a reader actually asks for: this palace, on this continent, on this
 * world, in this cluster. Pure, so every awkward shape can be stated here rather
 * than discovered in a panel.
 */
describe('placeTree', () => {
  const names = (rows: { place: Place }[]) => rows.map(r => r.place.name);

  it('roots a place nothing contains', () => {
    expect(names(placeTree([place('p1', 'Ravensmarch')]))).toEqual(['Ravensmarch']);
  });

  it('nests a chain as deep as the book describes it', () => {
    const tree = placeTree([
      place('p4', 'The Palace', 'p3'),
      place('p3', 'Anoosha', 'p2'),
      place('p2', 'The Reach', 'p1'),
      place('p1', 'Centauri Cluster')
    ]);

    expect(names(visibleRows(tree, new Set())))
      .toEqual(['Centauri Cluster', 'The Reach', 'Anoosha', 'The Palace']);
    expect(visibleRows(tree, new Set()).map(r => r.depth)).toEqual([0, 1, 2, 3]);
  });

  /** A container the reader has not reached is no container. */
  it('roots a place whose container is not in the list', () => {
    expect(names(placeTree([place('p1', 'The Palace', 'p99')]))).toEqual(['The Palace']);
  });

  it('roots a place that claims to contain itself', () => {
    expect(names(placeTree([place('p1', 'Ravensmarch', 'p1')]))).toEqual(['Ravensmarch']);
  });

  /**
   * The merge refuses cycles, but this renders whatever the server sent. A place
   * the reader cannot see is indistinguishable from one that was never recorded,
   * so both still appear.
   */
  it('shows both of two places that contain each other', () => {
    const tree = placeTree([place('p1', 'A', 'p2'), place('p2', 'B', 'p1')]);

    expect(names(visibleRows(tree, new Set())).sort()).toEqual(['A', 'B']);
  });

  /**
   * Bigger branches first: a cluster with nine worlds under it is what a reader
   * is looking for, and a stable order stops the list reshuffling when a chapter
   * adds one room.
   */
  it('puts the branch holding the most first', () => {
    const tree = placeTree([
      place('p1', 'Small'),
      place('p2', 'Big'),
      place('p3', 'One', 'p2'),
      place('p4', 'Two', 'p2')
    ]);

    expect(names(tree)).toEqual(['Big', 'Small']);
  });

  it('counts everything under a branch, at any depth', () => {
    const tree = placeTree([
      place('p1', 'Cluster'),
      place('p2', 'World', 'p1'),
      place('p3', 'City', 'p2')
    ]);

    expect(tree[0].total).toBe(2);
  });

  it('lists every place exactly once', () => {
    const tree = placeTree([
      place('p1', 'Cluster'), place('p2', 'A', 'p1'), place('p3', 'B', 'p1')
    ]);

    expect(visibleRows(tree, new Set()).length).toBe(3);
  });
});

describe('visibleRows', () => {
  const tree = () => placeTree([
    place('p1', 'Cluster'),
    place('p2', 'World', 'p1'),
    place('p3', 'City', 'p2')
  ]);

  it('emits nothing below a shut branch', () => {
    const rows = visibleRows(tree(), new Set(['p1']));

    expect(rows.map(r => r.place.name)).toEqual(['Cluster']);
  });

  it('shuts only the branch named, not the ones above it', () => {
    const rows = visibleRows(tree(), new Set(['p2']));

    expect(rows.map(r => r.place.name)).toEqual(['Cluster', 'World']);
  });
});

describe('branches', () => {
  it('names every place with something under it, and nothing else', () => {
    const tree = placeTree([
      place('p1', 'Cluster'), place('p2', 'World', 'p1'), place('p3', 'Lonely')
    ]);

    expect(branches(tree)).toEqual(['p1']);
  });
});
