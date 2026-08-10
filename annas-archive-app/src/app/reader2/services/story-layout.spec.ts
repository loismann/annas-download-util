import { layOutStory } from './story-layout';
import { actor, edge } from '../testing/cast';

describe('layOutStory', () => {
  it('lays disconnected groups side by side rather than on top of each other', () => {
    const laid = layOutStory(
      [actor('a1', 'Pierre'), actor('a2', 'Natasha'), actor('a3', 'Kutuzov'), actor('a4', 'Bagration')],
      [edge('a1', 'a2', 'family'), edge('a3', 'a4', 'commands')]);

    expect(laid.clusters.length).toBe(2);

    const [first, second] = laid.clusters;
    const apart = first.x + first.width <= second.x || second.x + second.width <= first.x
      || first.y + first.height <= second.y || second.y + second.height <= first.y;

    expect(apart).toBeTrue();
  });

  it('stacks a chain of command into ranks, commander on top', () => {
    const laid = layOutStory(
      [actor('a1', 'Kutuzov'), actor('a2', 'Bagration'), actor('a3', 'Tushin')],
      [edge('a1', 'a2', 'commands'), edge('a3', 'a2', 'subordinate-to')]);

    const placed = laid.clusters[0].actors;
    const at = (name: string) => placed.find(p => p.name === name)!;

    expect(at('Kutuzov').rank).toBe(0);
    expect(at('Bagration').rank).toBe(1);
    expect(at('Tushin').rank).toBe(2);
    expect(at('Kutuzov').y).toBeLessThan(at('Tushin').y);
  });

  it('keeps rivalry lateral rather than folding it into the tree', () => {
    const laid = layOutStory(
      [actor('a1', 'Kutuzov'), actor('a2', 'Bennigsen')],
      [edge('a1', 'a2', 'rival')]);

    expect(laid.clusters[0].edges[0].kind).toBe('lateral');
    expect(laid.clusters[0].actors.every(a => a.rank === 0))
      .withContext('a rivalry says nothing about who is above whom')
      .toBeTrue();
  });

  /** A model reading one chapter at a time will eventually report a cycle. */
  it('places every member of a command cycle rather than dropping them', () => {
    const laid = layOutStory(
      [actor('a1', 'A'), actor('a2', 'B')],
      [edge('a1', 'a2', 'commands'), edge('a2', 'a1', 'commands')]);

    expect(laid.clusters[0].actors.length)
      .withContext('a diagram missing somebody is worse than one drawing them at the wrong height')
      .toBe(2);
  });

  it('drops an edge whose end the filter has hidden', () => {
    const laid = layOutStory([actor('a1', 'Pierre')], [edge('a1', 'a9', 'loves')]);

    expect(laid.clusters[0].edges).toEqual([]);
    expect(laid.clusters[0].actors.length).toBe(1);
  });

  it('draws the same model as the same picture every time', () => {
    const cast = [actor('a2', 'B'), actor('a1', 'A'), actor('a3', 'C')];

    expect(layOutStory(cast, [])).toEqual(layOutStory([...cast].reverse(), []));
  });

  it('wraps a wide cast onto another row instead of running off the page', () => {
    const cast = Array.from({ length: 30 }, (_, i) => actor(`a${i}`, `Person ${i}`));

    const laid = layOutStory(cast, []);

    expect(laid.width).toBeLessThanOrEqual(900 + 150);
    expect(laid.height).toBeGreaterThan(90);
  });
});
