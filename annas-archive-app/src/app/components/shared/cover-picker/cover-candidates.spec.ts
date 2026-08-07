import { CoverCandidates, CoverProbe } from './cover-candidates';

/**
 * Cover lookup returns URLs from several sources and a good proportion of them
 * are dead, hotlink-blocked, or a tracking pixel. Only loading each one tells
 * you which — so this is the code that decides whether the user sees a real
 * cover, a broken image, or nothing.
 */
describe('CoverCandidates', () => {
  /** Probe that answers from a lookup table; anything absent "fails to load". */
  const probeFrom = (sizes: Record<string, [number, number]>): CoverProbe =>
    async (url: string) => {
      const size = sizes[url];
      return size ? { width: size[0], height: size[1] } : null;
    };

  describe('resolve', () => {
    it('sorts by pixel area, largest first', () => {
      // The biggest image is almost always the real cover; the small ones are
      // thumbnails of it.
      const probe = probeFrom({ small: [100, 150], big: [600, 900], mid: [300, 450] });

      return CoverCandidates.resolve(['small', 'big', 'mid'], probe).then(covers => {
        expect(covers.map(c => c.url)).toEqual(['big', 'mid', 'small']);
      });
    });

    it('drops anything that failed to load', async () => {
      const probe = probeFrom({ good: [400, 600] });

      const covers = await CoverCandidates.resolve(['good', 'dead', 'blocked'], probe);

      expect(covers.map(c => c.url)).toEqual(['good']);
    });

    it('drops a zero-sized image', async () => {
      // A 0×0 load "succeeds" and would render as a broken box.
      const probe = probeFrom({ pixel: [0, 0], real: [400, 600] });

      const covers = await CoverCandidates.resolve(['pixel', 'real'], probe);

      expect(covers.map(c => c.url)).toEqual(['real']);
    });

    it('records the aspect ratio', async () => {
      const covers = await CoverCandidates.resolve(['x'], probeFrom({ x: [400, 600] }));

      expect(covers[0].ratio).toBeCloseTo(1.5);
    });

    it('returns nothing when every candidate is dead', async () => {
      expect(await CoverCandidates.resolve(['a', 'b'], probeFrom({}))).toEqual([]);
    });

    it('handles an empty list', async () => {
      expect(await CoverCandidates.resolve([], probeFrom({}))).toEqual([]);
    });

    it('probes in parallel rather than one after another', async () => {
      // Serial probing over a dozen candidates, several of which hang until
      // they time out, is the difference between a picker that opens and one
      // that appears broken.
      let inFlight = 0;
      let peak = 0;
      const probe: CoverProbe = async () => {
        peak = Math.max(peak, ++inFlight);
        await Promise.resolve();
        inFlight--;
        return { width: 10, height: 10 };
      };

      await CoverCandidates.resolve(['a', 'b', 'c', 'd'], probe);

      expect(peak).toBeGreaterThan(1);
    });
  });

  describe('unique', () => {
    it('drops repeats while keeping order', () => {
      // The sources overlap; a duplicate would be probed twice and shown twice.
      expect(CoverCandidates.unique(['a', 'b', 'a', 'c', 'b'])).toEqual(['a', 'b', 'c']);
    });

    it('copes with nothing at all', () => {
      expect(CoverCandidates.unique(null)).toEqual([]);
      expect(CoverCandidates.unique(undefined)).toEqual([]);
      expect(CoverCandidates.unique([])).toEqual([]);
    });
  });
});
