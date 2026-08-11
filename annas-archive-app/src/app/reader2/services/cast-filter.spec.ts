import { Actor, ActorTier } from '../reader2.models';
import { DEFAULT_FILTER, NO_FILTER, filterCast, onTheMap, toggleTier } from './cast-filter';
import { actor, thread } from '../testing/cast';

/** A long novel's worth: 20 majors, 80 secondary, 400 walk-ons. */
function bigCast(): Actor[] {
  const tier = (i: number): ActorTier => i < 20 ? 'Major' : i < 100 ? 'Secondary' : 'Minor';

  return Array.from({ length: 500 }, (_, i) =>
    actor(`a${i}`, `Person ${i}`, tier(i), {
      groupIds: i % 2 === 0 ? ['g1'] : [],
      lastSeenChapter: i % 10
    }));
}

const THREADS = [thread('t1', 'The duel', 'Active', { participantIds: ['a0', 'a1'] })];

/**
 * Who is shown, decided in one place.
 *
 * <p>These were the character table's tests. They moved when the map needed the
 * same answer: the rule is about the book, not about which tab is open, and two
 * copies of it would drift the moment one gained a control the other did not.</p>
 */
describe('filterCast', () => {
  /** The whole reason for the default: 500 entries is the wall of names, not the cure. */
  it('opens on major and secondary, at 500 actors', () => {
    expect(filterCast(bigCast(), THREADS, DEFAULT_FILTER, 5).length).toBe(100);
  });

  it('shows everybody when nothing is filtered', () => {
    expect(filterCast(bigCast(), THREADS, NO_FILTER, 5).length).toBe(500);
  });

  it('narrows by tier', () => {
    const majors = filterCast(bigCast(), THREADS, { ...NO_FILTER, tiers: ['Major'] }, 5);

    expect(majors.length).toBe(20);
    expect(majors.every(a => a.tier === 'Major')).toBeTrue();
  });

  it('narrows by faction', () => {
    const rostovs = filterCast(bigCast(), THREADS, { ...NO_FILTER, groupId: 'g1' }, 5);

    expect(rostovs.every(a => a.groupIds.includes('g1'))).toBeTrue();
    expect(rostovs.length).toBe(250);
  });

  it('narrows to the participants of one plot thread', () => {
    const duel = filterCast(bigCast(), THREADS, { ...NO_FILTER, threadId: 't1' }, 5);

    expect(duel.map(a => a.id)).toEqual(['a0', 'a1']);
  });

  it('narrows to who was last seen in this chapter', () => {
    const here = filterCast(bigCast(), THREADS, { ...NO_FILTER, hereOnly: true }, 5);

    expect(here.every(a => a.lastSeenChapter === 5)).toBeTrue();
    expect(here.length).toBe(50);
  });

  it('applies every filter at once rather than the last one set', () => {
    const both = filterCast(
      bigCast(), THREADS, { ...NO_FILTER, tiers: ['Major'], groupId: 'g1' }, 5);

    expect(both.every(a => a.tier === 'Major' && a.groupIds.includes('g1'))).toBeTrue();
    expect(both.length).toBe(10);
  });

  it('can hide everybody, which is a filter working rather than an empty book', () => {
    expect(filterCast(bigCast(), THREADS, { ...NO_FILTER, tiers: [] }, 5)).toEqual([]);
  });
});

describe('toggleTier', () => {
  it('adds a tier that is off', () => {
    expect(toggleTier({ ...NO_FILTER, tiers: ['Major'] }, 'Minor').tiers).toEqual(['Major', 'Minor']);
  });

  it('removes a tier that is on', () => {
    expect(toggleTier({ ...NO_FILTER, tiers: ['Major', 'Minor'] }, 'Major').tiers).toEqual(['Minor']);
  });

  it('leaves the rest of the filter alone', () => {
    const narrowed = toggleTier({ ...NO_FILTER, groupId: 'g1', hereOnly: true }, 'Major');

    expect(narrowed.groupId).toBe('g1');
    expect(narrowed.hereOnly).toBeTrue();
  });
});

/**
 * Hiding, which the list and the map answer differently on purpose.
 *
 * <p>The list is the record and keeps everybody — the extraction did find them
 * in the book. The map is a picture, and a picture with forty walk-ons on it
 * shows nothing.</p>
 */
describe('hiding', () => {
  const CAST = [
    actor('a1', 'Finn', 'Major'),
    actor('a2', 'Jaks', 'Major', { hidden: true })
  ];

  it('keeps a hidden character in the list', () => {
    expect(filterCast(CAST, [], DEFAULT_FILTER, 0).map(a => a.id)).toEqual(['a1', 'a2']);
  });

  it('takes a hidden character off the map', () => {
    expect(onTheMap(filterCast(CAST, [], DEFAULT_FILTER, 0)).map(a => a.id)).toEqual(['a1']);
  });

  /**
   * On its own, ignoring every other control: a hidden walk-on is below the
   * default tiers too, so combining them would show an empty list and no way
   * back.
   */
  it('shows exactly the hidden ones when reviewing, whatever else is set', () => {
    const reviewing = { ...DEFAULT_FILTER, tiers: [], groupId: 'nobody', hiddenOnly: true };

    expect(filterCast(CAST, [], reviewing, 0).map(a => a.id)).toEqual(['a2']);
  });
});
