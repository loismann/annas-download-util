import {
  matchesOwnerFilter,
  matchesFavoritesFilter,
  matchesOwnerAndFavorites,
  toggleInSet,
  OwnedItem
} from './owner-filters';

const owners = (...names: string[]) => new Set(names);

describe('owner-filters', () => {
  describe('matchesOwnerFilter', () => {
    it('keeps everything when no owner is selected', () => {
      expect(matchesOwnerFilter({ owners: ['Mom'] }, owners())).toBe(true);
      expect(matchesOwnerFilter({ owners: [] }, owners())).toBe(true);
    });

    it('keeps an item owned by a selected person', () => {
      expect(matchesOwnerFilter({ owners: ['Mom'] }, owners('Mom'))).toBe(true);
    });

    it('keeps an item owned by any one of several selected people', () => {
      expect(matchesOwnerFilter({ owners: ['Dad'] }, owners('Mom', 'Dad'))).toBe(true);
    });

    it('hides an item owned only by someone not selected', () => {
      expect(matchesOwnerFilter({ owners: ['Paul'] }, owners('Mom'))).toBe(false);
    });

    // The "Showing 990 of 992" bug. Owner tags are only written by the
    // reconciler, so anything reaching the library another way is untagged —
    // requiring a positive match makes it invisible forever.
    it('keeps an untagged item under every owner filter', () => {
      expect(matchesOwnerFilter({ owners: [] }, owners('Mom'))).toBe(true);
      expect(matchesOwnerFilter({}, owners('Mom'))).toBe(true);
      expect(matchesOwnerFilter({ owners: undefined }, owners('Paul', 'Mom', 'Dad'))).toBe(true);
    });
  });

  describe('matchesFavoritesFilter', () => {
    it('hides an item nobody has favourited', () => {
      expect(matchesFavoritesFilter({ favorites: [] }, owners())).toBe(false);
      expect(matchesFavoritesFilter({}, owners())).toBe(false);
    });

    it('keeps anything favourited by anyone when no owner is selected', () => {
      expect(matchesFavoritesFilter({ favorites: ['Dad'] }, owners())).toBe(true);
    });

    // The rule that stops "Mom's favourites" quietly meaning "everyone's".
    it('keeps only the selected people\'s favourites when an owner filter is active', () => {
      expect(matchesFavoritesFilter({ favorites: ['Mom'] }, owners('Mom'))).toBe(true);
      expect(matchesFavoritesFilter({ favorites: ['Paul'] }, owners('Mom'))).toBe(false);
    });

    // Deliberately asymmetric with the owner filter: "favourited by nobody" is a
    // real answer, where "owned by nobody" is a missing one.
    it('does not extend the untagged exemption to favourites', () => {
      expect(matchesFavoritesFilter({ favorites: [] }, owners('Mom'))).toBe(false);
    });
  });

  describe('matchesOwnerAndFavorites', () => {
    it('applies only the owner rule when favouritesOnly is off', () => {
      const notFavourited: OwnedItem = { owners: ['Mom'], favorites: [] };

      expect(matchesOwnerAndFavorites(notFavourited, owners('Mom'), false)).toBe(true);
      expect(matchesOwnerAndFavorites(notFavourited, owners('Mom'), true)).toBe(false);
    });

    it('requires both rules when favouritesOnly is on', () => {
      expect(matchesOwnerAndFavorites({ owners: ['Mom'], favorites: ['Mom'] }, owners('Mom'), true)).toBe(true);
      expect(matchesOwnerAndFavorites({ owners: ['Paul'], favorites: ['Paul'] }, owners('Mom'), true)).toBe(false);
    });

    // An untagged item is exempt from the owner rule but still has to clear the
    // favourites rule — the two exemptions must not be confused for each other.
    it('still requires a favourite from an untagged item', () => {
      expect(matchesOwnerAndFavorites({ favorites: ['Mom'] }, owners('Mom'), true)).toBe(true);
      expect(matchesOwnerAndFavorites({ favorites: ['Paul'] }, owners('Mom'), true)).toBe(false);
      expect(matchesOwnerAndFavorites({}, owners('Mom'), true)).toBe(false);
    });
  });

  describe('toggleInSet', () => {
    it('adds a value that is absent and removes one that is present', () => {
      const set = new Set<string>();

      toggleInSet(set, 'Mom');
      expect(set.has('Mom')).toBe(true);

      toggleInSet(set, 'Mom');
      expect(set.has('Mom')).toBe(false);
    });

    it('leaves other members alone', () => {
      const set = new Set(['Mom', 'Dad']);

      toggleInSet(set, 'Dad');
      expect(Array.from(set)).toEqual(['Mom']);
    });
  });
});
